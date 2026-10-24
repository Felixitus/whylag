using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using WhyLag.Core;
using static WhyLag.Core.Localization.Loc;

namespace WhyLag.AI;

public enum ExplainStatus { Ok, NothingToExplain, Unavailable, NoModel, Rejected, Failed }

public sealed record ExplainResult(ExplainStatus Status, AiExplanation? Explanation, string Message);

/// <summary>
/// Optional: asks a local model to explain the rule findings in plain language. The model only
/// receives the findings and their evidence (no raw logs), and its answer is validated before use.
/// </summary>
public sealed class IncidentExplainer(OllamaClient client, AiSettings settings)
{
    private const int MaxAttempts = 2;

    internal static readonly JsonNode Schema = JsonNode.Parse("""
        {
          "type": "object",
          "properties": {
            "summary": { "type": "string" },
            "ranking": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "finding_id": { "type": "string" },
                  "likelihood": { "type": "string", "enum": ["high", "medium", "low"] },
                  "explanation": { "type": "string" },
                  "evidence_ids": { "type": "array", "items": { "type": "string" } },
                  "next_steps": { "type": "array", "items": { "type": "string" } }
                },
                "required": ["finding_id", "likelihood", "explanation", "evidence_ids", "next_steps"]
              }
            },
            "caveats": { "type": "array", "items": { "type": "string" } }
          },
          "required": ["summary", "ranking", "caveats"]
        }
        """)!;

    public async Task<ExplainResult> ExplainAsync(Incident incident, CancellationToken ct = default)
    {
        if (incident.Findings.Count == 0)
            return new ExplainResult(ExplainStatus.NothingToExplain, null, T("ai.nothing"));

        List<OllamaModel> installed;
        try
        {
            installed = await client.ListModelsAsync(ct).ConfigureAwait(false);
        }
        catch (OllamaException ex)
        {
            return new ExplainResult(ExplainStatus.Unavailable, null, ex.Message);
        }

        if (settings.Model is { Length: > 0 } chosen && ModelAdvisor.IsCloudModel(chosen))
            return new ExplainResult(ExplainStatus.NoModel, null, T("ai.cloud", chosen));

        var model = settings.Model is { Length: > 0 } configured && installed.Any(m => ModelAdvisor.SameModel(m.Name, configured))
            ? configured
            : ModelAdvisor.PickInstalled(installed);
        if (model is null)
            return new ExplainResult(ExplainStatus.NoModel, null, T("ai.nomodel"));

        var messages = new List<ChatMessage>
        {
            new("system", SystemPrompt(LanguageName(settings.Language is "auto" or "" ? WhyLag.Core.Localization.Loc.Language : settings.Language))),
            new("user", FactsJson(incident)),
        };

        var watch = Stopwatch.StartNew();
        ValidationResult? last = null;
        try
        {
            for (var attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                var raw = await client.ChatAsync(model, messages, Schema, ct).ConfigureAwait(false);
                last = ExplanationValidator.Validate(raw, incident, model);
                if (last.IsClean) break;

                // Give the model one chance to fix exactly what the validator rejected.
                messages.Add(new ChatMessage("assistant", raw));
                messages.Add(new ChatMessage("user",
                    "Your answer had these problems:\n- " + string.Join("\n- ", last.Problems) +
                    "\nAnswer again. Only use finding ids and evidence ids from the facts and only numbers that appear in the facts."));
            }
        }
        catch (OllamaException ex)
        {
            return new ExplainResult(ExplainStatus.Failed, null, ex.Message);
        }

        if (last?.Explanation is not { Items.Count: > 0 } explanation)
            return new ExplainResult(ExplainStatus.Rejected, null,
                T("ai.rejected"));

        // Small models sometimes echo the instructions as caveats; those are not information for the user.
        var prompt = messages[0].Content;
        var caveats = explanation.Caveats
            .Where(c => !prompt.Contains(c.TrimEnd('.'), StringComparison.OrdinalIgnoreCase)
                        && !c.Contains("evidence id", StringComparison.OrdinalIgnoreCase)
                        && !c.Contains("finding id", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var final = new AiExplanation
        {
            Model = explanation.Model,
            CreatedAt = explanation.CreatedAt,
            DurationMs = watch.ElapsedMilliseconds,
            Summary = explanation.Summary,
            Items = explanation.Items,
            Caveats = caveats,
            ValidationNotes = last.Problems,
        };
        return new ExplainResult(ExplainStatus.Ok, final, last.Problems.Count == 0 ? T("ai.ok") : T("ai.partial"));
    }

    internal static string SystemPrompt(string language) => $"""
        You explain PC performance problems to non-technical users for the tool WhyLag.
        You receive findings that deterministic rules computed from measurements, each with evidence ids.
        Rules:
        - Only talk about the given findings. Do not invent causes, programs, numbers or times.
        - Every ranking item must cite the evidence ids it is based on.
        - Only use numbers that appear in the facts. Prefer describing magnitudes in words.
        - The findings show what happened at the same time; say "likely" or "possibly", never claim proof.
        - Rank the findings by how likely they explain the problem the user noticed, most likely first.
        - Next steps must be safe, reversible actions a normal user can do. Never suggest disabling security software, editing the registry or deleting system files.
        - Write short, friendly sentences in {language}. Keep finding ids and evidence ids unchanged.
        - Answer with JSON that matches the given schema:
          summary: one or two sentences for the user about the most likely cause.
          ranking: one item per relevant finding, most likely first.
          caveats: limits of this analysis that matter for the user (may be empty). Do not repeat these instructions.
        """;

    internal static string FactsJson(Incident incident)
    {
        var fg = incident.Ticks.LastOrDefault(t => t.ForegroundProcess is not null)?.ForegroundProcess;
        var facts = new JsonObject
        {
            ["problem_noticed_at"] = incident.TriggeredAt.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture),
            ["trigger"] = incident.Trigger switch
            {
                TriggerKind.Hotkey => "The user pressed the hotkey because something lagged or stuttered.",
                TriggerKind.Automatic => $"WhyLag detected an anomaly automatically: {incident.TriggerDetail}",
                _ => "The user asked for an analysis of this moment.",
            },
            ["foreground_program"] = fg,
            ["findings"] = new JsonArray(incident.Findings.Select(f => (JsonNode)new JsonObject
            {
                ["id"] = f.Id,
                ["category"] = f.Category.ToString(),
                ["title"] = f.Title,
                ["summary"] = f.Summary,
                ["rule_confidence_percent"] = Math.Round(f.Confidence * 100),
                ["evidence"] = new JsonArray(f.Evidence.Select(e => (JsonNode)new JsonObject
                {
                    ["id"] = e.Id,
                    ["time"] = e.Timestamp.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture),
                    ["fact"] = e.Description,
                }).ToArray()),
                ["suggested_checks"] = new JsonArray(f.Checks.Select(c => (JsonNode)JsonValue.Create(c)!).ToArray()),
            }).ToArray()),
        };
        return facts.ToJsonString(new JsonSerializerOptions { WriteIndented = false, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }

    internal static string LanguageName(string setting)
    {
        var code = setting is "auto" or "" ? CultureInfo.CurrentUICulture.TwoLetterISOLanguageName : setting;
        try
        {
            return CultureInfo.GetCultureInfo(code).EnglishName.Split(' ')[0];
        }
        catch (CultureNotFoundException)
        {
            return "English";
        }
    }
}
