using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using WhyLag.Core;

namespace WhyLag.AI.Tests;

internal static class Sample
{
    public static readonly DateTimeOffset At = new(2026, 10, 1, 20, 14, 3, TimeSpan.Zero);

    public static Incident Incident() => new()
    {
        Id = "test",
        TriggeredAt = At,
        Trigger = TriggerKind.Hotkey,
        Findings =
        [
            new Finding
            {
                Id = "F1", RuleId = "disk.saturation", Category = FindingCategory.Disk, Title = "Storage overloaded, mainly by MsMpEng",
                Summary = "The drive was busy.", Confidence = 0.75,
                Evidence =
                [
                    new Evidence("E1", At, "Disk busy 97.4 % of the time", 97.4, "%"),
                    new Evidence("E2", At, "Most I/O by MsMpEng (peak 85 MB/s)", 85, "MB/s"),
                ],
                Checks = ["Open Resource Monitor."],
            },
            new Finding
            {
                Id = "F2", RuleId = "cpu.saturation", Category = FindingCategory.Cpu, Title = "CPU fully loaded",
                Summary = "Busy.", Confidence = 0.55,
                Evidence = [new Evidence("E3", At, "Total CPU load 93 %", 93, "%")],
            },
        ],
    };

    public static string Answer(string explanation = "Defender scanned the disk at 97 %.", string evidence = "\"E1\",\"E2\"", string finding = "F1") => $$"""
        {"summary":"Most likely a virus scan.","ranking":[
          {"finding_id":"{{finding}}","likelihood":"high","explanation":"{{explanation}}","evidence_ids":[{{evidence}}],"next_steps":["Schedule the scan."]}
        ],"caveats":[]}
        """;
}

public class ValidatorTests
{
    [Fact]
    public void Accepts_answer_that_cites_existing_evidence()
    {
        var result = ExplanationValidator.Validate(Sample.Answer(), Sample.Incident(), "m");

        Assert.True(result.IsClean);
        var item = Assert.Single(result.Explanation!.Items);
        Assert.Equal(["E1", "E2"], item.EvidenceIds);
        Assert.Equal("high", item.Likelihood);
    }

    [Fact]
    public void Removes_unknown_finding()
    {
        var result = ExplanationValidator.Validate(Sample.Answer(finding: "F9"), Sample.Incident(), "m");

        Assert.Empty(result.Explanation!.Items);
        Assert.Contains(result.Problems, p => p.Contains("F9"));
    }

    [Fact]
    public void Drops_unknown_evidence_but_keeps_valid_citations()
    {
        var result = ExplanationValidator.Validate(Sample.Answer(evidence: "\"E1\",\"E7\""), Sample.Incident(), "m");

        Assert.Equal(["E1"], Assert.Single(result.Explanation!.Items).EvidenceIds);
        Assert.False(result.IsClean);
    }

    [Fact]
    public void Item_without_any_valid_evidence_is_removed()
    {
        var result = ExplanationValidator.Validate(Sample.Answer(evidence: "\"E7\""), Sample.Incident(), "m");
        Assert.Empty(result.Explanation!.Items);
    }

    [Theory]
    [InlineData("The disk ran at 63 % because of 12 updates.")]
    [InlineData("It took 4500 ms.")]
    public void Rejects_numbers_that_were_never_measured(string explanation)
    {
        var result = ExplanationValidator.Validate(Sample.Answer(explanation), Sample.Incident(), "m");

        Assert.Empty(result.Explanation!.Items);
        Assert.Contains(result.Problems, p => p.Contains("never measured"));
    }

    [Theory]
    [InlineData("Die Festplatte war zu 97 % ausgelastet.")]
    [InlineData("Peak was 85 MB/s at 20:14:03 (rule confidence 75 %).")]
    [InlineData("Two programs were involved.")]
    public void Allows_measured_and_rounded_numbers(string explanation)
    {
        var incident = Sample.Incident();
        incident = new Incident { Id = incident.Id, TriggeredAt = incident.TriggeredAt.ToLocalTime(), Findings = incident.Findings };
        var answer = Sample.Answer(explanation.Replace("20:14:03", Sample.At.ToLocalTime().ToString("HH:mm:ss")));

        var result = ExplanationValidator.Validate(answer, incident, "m");

        Assert.True(result.IsClean, string.Join("; ", result.Problems));
    }

    [Fact]
    public void Invalid_json_is_reported()
    {
        var result = ExplanationValidator.Validate("not json", Sample.Incident(), "m");
        Assert.Null(result.Explanation);
        Assert.NotEmpty(result.Problems);
    }

    [Fact]
    public void Summary_with_invented_number_is_dropped()
    {
        var answer = Sample.Answer().Replace("Most likely a virus scan.", "Your PC has 3000 viruses.");
        var result = ExplanationValidator.Validate(answer, Sample.Incident(), "m");

        Assert.Equal("", result.Explanation!.Summary);
        Assert.Single(result.Explanation.Items);
    }
}

public class ExplainerTests
{
    private sealed class FakeOllama(params string[] chatAnswers) : HttpMessageHandler
    {
        private int _chat;
        public List<JsonNode> ChatRequests { get; } = new();
        public bool Offline { get; init; }
        public string Tags { get; init; } = """{"models":[{"name":"qwen3:4b","size":2500000000,"details":{"family":"qwen3","parameter_size":"4B"}}]}""";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (Offline) throw new HttpRequestException("connection refused");
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/api/tags")) return Json(Tags);
            if (path.EndsWith("/api/chat"))
            {
                ChatRequests.Add(JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!);
                var content = chatAnswers[Math.Min(_chat++, chatAnswers.Length - 1)];
                return Json(new JsonObject { ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = content } }.ToJsonString());
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private static IncidentExplainer Explainer(FakeOllama fake, string? model = null) =>
        new(new OllamaClient(new HttpClient(fake), new Uri("http://127.0.0.1:11434/")), new AiSettings { Enabled = true, Model = model, Language = "de" });

    [Fact]
    public async Task Valid_answer_is_returned_and_model_is_unloaded()
    {
        var fake = new FakeOllama(Sample.Answer());
        var result = await Explainer(fake).ExplainAsync(Sample.Incident());

        Assert.Equal(ExplainStatus.Ok, result.Status);
        Assert.Equal("qwen3:4b", result.Explanation!.Model);
        var request = Assert.Single(fake.ChatRequests);
        Assert.Equal(0, request["keep_alive"]!.GetValue<int>());
        Assert.Equal("object", request["format"]!["type"]!.GetValue<string>());
        Assert.Contains("German", request["messages"]![0]!["content"]!.GetValue<string>());
        Assert.DoesNotContain("Ticks", request["messages"]![1]!["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task Invalid_answer_gets_one_retry_with_feedback()
    {
        var fake = new FakeOllama(Sample.Answer("Load was 12 %."), Sample.Answer());
        var result = await Explainer(fake).ExplainAsync(Sample.Incident());

        Assert.Equal(ExplainStatus.Ok, result.Status);
        Assert.Equal(2, fake.ChatRequests.Count);
        Assert.Contains("problems", fake.ChatRequests[1]["messages"]!.AsArray().Last()!["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task Answer_that_never_verifies_is_rejected()
    {
        var fake = new FakeOllama(Sample.Answer(evidence: "\"E99\""));
        var result = await Explainer(fake).ExplainAsync(Sample.Incident());

        Assert.Equal(ExplainStatus.Rejected, result.Status);
        Assert.Null(result.Explanation);
    }

    [Fact]
    public async Task No_findings_means_no_ai_call()
    {
        var fake = new FakeOllama(Sample.Answer());
        var result = await Explainer(fake).ExplainAsync(new Incident { Id = "x" });

        Assert.Equal(ExplainStatus.NothingToExplain, result.Status);
        Assert.Empty(fake.ChatRequests);
    }

    [Fact]
    public async Task Offline_ollama_is_reported_not_thrown()
    {
        var result = await Explainer(new FakeOllama { Offline = true }).ExplainAsync(Sample.Incident());
        Assert.Equal(ExplainStatus.Unavailable, result.Status);
    }

    [Fact]
    public async Task Missing_model_is_reported()
    {
        var result = await Explainer(new FakeOllama { Tags = """{"models":[]}""" }).ExplainAsync(Sample.Incident());
        Assert.Equal(ExplainStatus.NoModel, result.Status);
    }

    [Fact]
    public async Task Configured_cloud_model_is_refused()
    {
        var fake = new FakeOllama(Sample.Answer()) { Tags = """{"models":[{"name":"glm-5.2:cloud","size":1}]}""" };
        var result = await Explainer(fake, model: "glm-5.2:cloud").ExplainAsync(Sample.Incident());

        Assert.Equal(ExplainStatus.NoModel, result.Status);
        Assert.Empty(fake.ChatRequests);
    }

    [Fact]
    public async Task Configured_model_is_used_when_installed()
    {
        var fake = new FakeOllama(Sample.Answer())
        {
            Tags = """{"models":[{"name":"qwen3:4b","size":1},{"name":"llama3.2:latest","size":1}]}""",
        };
        await Explainer(fake, model: "llama3.2").ExplainAsync(Sample.Incident());

        Assert.Equal("llama3.2", fake.ChatRequests[0]["model"]!.GetValue<string>());
    }
}

public class ModelAdvisorTests
{
    [Theory]
    [InlineData(24576, 32768, "qwen3:8b")]
    [InlineData(6144, 16384, "qwen3:4b")]
    [InlineData(0, 16384, "qwen3:4b")]
    [InlineData(0, 8192, "qwen3:1.7b")]
    public void Recommends_by_hardware(double vramMb, double ramMb, string expected) =>
        Assert.Equal(expected, ModelAdvisor.Recommend(vramMb, ramMb)?.Name);

    [Fact]
    public void Weak_pc_gets_no_recommendation() => Assert.Null(ModelAdvisor.Recommend(0, 4096));

    [Fact]
    public void Prefers_catalog_models_and_skips_embeddings()
    {
        var installed = new List<OllamaModel>
        {
            new("nomic-embed-text:latest", 1, null, "nomic-bert", null),
            new("mistral:latest", 4, null, "llama", null),
            new("qwen3:4b", 3, null, "qwen3", null),
        };
        Assert.Equal("qwen3:4b", ModelAdvisor.PickInstalled(installed));
        Assert.Equal("mistral:latest", ModelAdvisor.PickInstalled(installed.Take(2).ToList()));
    }

    [Fact]
    public void Latest_tag_is_implicit() => Assert.True(ModelAdvisor.SameModel("llama3.2", "llama3.2:latest"));

    [Theory]
    [InlineData("glm-5.2:cloud", true)]
    [InlineData("gpt-oss:120b-cloud", true)]
    [InlineData("qwen3:4b", false)]
    [InlineData("cloudy", false)]
    public void Detects_cloud_models(string name, bool cloud) => Assert.Equal(cloud, ModelAdvisor.IsCloudModel(name));

    [Fact]
    public void Never_picks_a_cloud_model()
    {
        var installed = new List<OllamaModel> { new("glm-5.2:cloud", 300, null, null, null), new("deepseek-r1:8b", 5_000_000_000, null, "qwen3", null) };
        Assert.Equal("deepseek-r1:8b", ModelAdvisor.PickInstalled(installed));
    }
}
