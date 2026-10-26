using System.Text.Json;
using System.Text.Json.Nodes;
using WhyLag.Core.Rules;

namespace WhyLag.Core;

/// <summary>What a recorded real case must (and must not) produce. Stored next to the data in each fixture file.</summary>
public sealed class FixtureExpectation
{
    public string Description { get; set; } = "";
    /// <summary>Rule ids that must be found.</summary>
    public List<string> Expect { get; set; } = [];
    /// <summary>Rule ids that must not be found (e.g. a known false alarm).</summary>
    public List<string> Forbid { get; set; } = [];
    /// <summary>Whether the spike detector should start an automatic incident; null = not checked.</summary>
    public bool? AutoTrigger { get; set; }
}

/// <summary>
/// Turns recorded incidents into regression fixtures: the measured data stays, everything derived (findings, AI
/// text) and everything personal (machine details, process ids, user and machine name) is removed.
/// </summary>
public static class Fixtures
{
    public static string Export(Incident incident, FixtureExpectation expectation)
    {
        var node = JsonSerializer.SerializeToNode(incident, Json.Options)!.AsObject();
        foreach (var key in new[] { "findings", "explanation", "allEvidence", "note", "machine", "longTerm" }) node.Remove(key);
        foreach (var tick in node["ticks"]!.AsArray())
        {
            if (tick?["metrics"] is JsonObject metrics)
            {
                foreach (var (key, value) in metrics.ToList())
                    if (value is not null && value.GetValueKind() == JsonValueKind.Number) metrics[key] = Math.Round(value.GetValue<double>(), 2);
            }
            if (tick?["topProcesses"] is JsonArray processes)
            {
                while (processes.Count > 5) processes.RemoveAt(processes.Count - 1);
                foreach (var p in processes) p?.AsObject().Remove("pid");
            }
        }
        node["fixture"] = JsonSerializer.SerializeToNode(expectation, Json.Options);
        return ReportBuilder.Anonymize(node.ToJsonString(Json.Options));
    }

    public static (FixtureExpectation Expectation, Incident Incident) Load(string path)
    {
        var text = File.ReadAllText(path);
        var node = JsonNode.Parse(text)!.AsObject();
        var expectation = node["fixture"]?.Deserialize<FixtureExpectation>(Json.Options) ?? new();
        var incident = JsonSerializer.Deserialize<Incident>(text, Json.Options)!;
        return (expectation, incident);
    }

    /// <summary>Runs the rules and the spike detector on a fixture and lists every mismatch (empty = passed).</summary>
    public static List<string> Check(FixtureExpectation expectation, Incident incident)
    {
        var problems = new List<string>();
        var ctx = new IncidentContext(incident.Ticks, incident.Events, incident.TriggeredAt, incident.Trigger);
        var found = RuleEngine.Evaluate(ctx, RuleEngine.CodeRules.Concat(JsonRules.BuiltIn)).Select(f => f.RuleId).ToHashSet();
        problems.AddRange(expectation.Expect.Where(id => !found.Contains(id)).Select(id => $"expected {id}, found [{string.Join(", ", found)}]"));
        problems.AddRange(expectation.Forbid.Where(found.Contains).Select(id => $"{id} must not be found"));

        if (expectation.AutoTrigger is { } auto)
        {
            var detector = new SpikeDetector();
            var fired = incident.Ticks.Select(detector.Feed).FirstOrDefault(s => s is not null);
            if (auto != fired is not null) problems.Add(auto ? "spike detector did not fire" : $"spike detector fired: {fired!.Description}");
        }
        return problems;
    }
}
