using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using WhyLag.Core;

namespace WhyLag.AI;

public sealed record ValidationResult(AiExplanation? Explanation, List<string> Problems)
{
    public bool IsClean => Explanation is not null && Problems.Count == 0;
}

/// <summary>
/// Checks a model answer against the incident. The model may only talk about existing findings,
/// must cite existing evidence and must not introduce numbers that were never measured.
/// Everything that fails is removed and reported, never silently shown.
/// </summary>
public static partial class ExplanationValidator
{
    private static readonly HashSet<string> Likelihoods = new(StringComparer.OrdinalIgnoreCase) { "high", "medium", "low" };

    [GeneratedRegex(@"(?<![\p{L}\d.,])\d+(?:[.,]\d+)?")]
    private static partial Regex NumberPattern();

    public static ValidationResult Validate(string raw, Incident incident, string model)
    {
        var problems = new List<string>();
        AnswerDto? answer;
        try
        {
            answer = JsonSerializer.Deserialize<AnswerDto>(raw, DtoOptions);
        }
        catch (JsonException ex)
        {
            return new ValidationResult(null, [$"The answer was not valid JSON ({ex.Message})."]);
        }
        if (answer is null) return new ValidationResult(null, ["The answer was empty."]);

        var findings = incident.Findings.ToDictionary(f => f.Id, StringComparer.OrdinalIgnoreCase);
        var evidence = incident.AllEvidence.ToDictionary(e => e.Id, StringComparer.OrdinalIgnoreCase);
        var allowed = AllowedNumbers(incident);

        var items = new List<AiExplanationItem>();
        foreach (var item in answer.Ranking ?? [])
        {
            var findingId = item.FindingId?.Trim() ?? "";
            if (!findings.ContainsKey(findingId))
            {
                problems.Add($"Unknown finding id '{findingId}' was removed.");
                continue;
            }
            var cited = (item.EvidenceIds ?? []).Select(e => e.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var unknown = cited.Where(e => !evidence.ContainsKey(e)).ToList();
            if (unknown.Count > 0) problems.Add($"{findingId}: unknown evidence ids {string.Join(", ", unknown)} were removed.");
            cited = cited.Except(unknown, StringComparer.OrdinalIgnoreCase).ToList();
            if (cited.Count == 0)
            {
                problems.Add($"{findingId}: the explanation cited no valid evidence and was removed.");
                continue;
            }

            var text = item.Explanation?.Trim() ?? "";
            var steps = (item.NextSteps ?? []).Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
            var invented = InventedNumbers(text, allowed).Concat(steps.SelectMany(s => InventedNumbers(s, allowed))).Distinct().ToList();
            if (invented.Count > 0)
            {
                problems.Add($"{findingId}: contained numbers that were never measured ({string.Join(", ", invented)}) and was removed.");
                continue;
            }
            if (text.Length == 0)
            {
                problems.Add($"{findingId}: empty explanation was removed.");
                continue;
            }

            items.Add(new AiExplanationItem
            {
                FindingId = findings[findingId].Id,
                Likelihood = Likelihoods.Contains(item.Likelihood ?? "") ? item.Likelihood!.ToLowerInvariant() : "low",
                Explanation = text,
                EvidenceIds = cited.Select(c => evidence[c].Id).ToList(),
                NextSteps = steps.Take(4).ToList(),
            });
        }

        if (items.Count == 0 && incident.Findings.Count > 0) problems.Add("No explanation item could be verified.");

        var summary = answer.Summary?.Trim() ?? "";
        var summaryInvented = InventedNumbers(summary, allowed).ToList();
        if (summaryInvented.Count > 0)
        {
            problems.Add($"Summary contained numbers that were never measured ({string.Join(", ", summaryInvented)}) and was removed.");
            summary = "";
        }

        var caveats = (answer.Caveats ?? []).Select(c => c.Trim())
            .Where(c => c.Length > 0 && !InventedNumbers(c, allowed).Any())
            .Take(3).ToList();

        var explanation = new AiExplanation
        {
            Model = model,
            CreatedAt = DateTimeOffset.Now,
            Summary = summary,
            Items = items.DistinctBy(i => i.FindingId).ToList(),
            Caveats = caveats,
        };
        return new ValidationResult(explanation, problems);
    }

    /// <summary>Every number that appears anywhere in the facts the model was given.</summary>
    internal static List<double> AllowedNumbers(Incident incident)
    {
        var numbers = new List<double>();
        for (var i = 0; i <= 10; i++) numbers.Add(i); // counts and list positions
        void AddFrom(string? text)
        {
            if (string.IsNullOrEmpty(text)) return;
            foreach (Match m in NumberPattern().Matches(text))
                if (Parse(m.Value) is { } v) numbers.Add(v);
        }

        foreach (var f in incident.Findings)
        {
            AddFrom(f.Id[1..]);
            AddFrom(f.Title);
            AddFrom(f.Summary);
            f.Checks.ForEach(AddFrom);
            numbers.Add(Math.Round(f.Confidence * 100));
            foreach (var e in f.Evidence)
            {
                AddFrom(e.Id[1..]);
                AddFrom(e.Description);
                AddFrom(e.Timestamp.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture));
                if (e.Value is { } v) numbers.Add(v);
            }
        }
        AddFrom(incident.TriggeredAt.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture));
        AddFrom(incident.TriggerDetail);
        return numbers;
    }

    internal static IEnumerable<string> InventedNumbers(string text, List<double> allowed)
    {
        foreach (Match m in NumberPattern().Matches(text))
        {
            if (Parse(m.Value) is not { } n) continue;
            // Allow rounding by the model ("97.4 %" -> "97 %").
            var ok = allowed.Any(a => Math.Abs(a - n) <= Math.Max(0.6, Math.Abs(a) * 0.02));
            if (!ok) yield return m.Value;
        }
    }

    private static double? Parse(string s) =>
        double.TryParse(s.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static readonly JsonSerializerOptions DtoOptions = new() { PropertyNameCaseInsensitive = true };

    private sealed class AnswerDto
    {
        public string? Summary { get; set; }
        public List<ItemDto>? Ranking { get; set; }
        public List<string>? Caveats { get; set; }
    }

    private sealed class ItemDto
    {
        [System.Text.Json.Serialization.JsonPropertyName("finding_id")] public string? FindingId { get; set; }
        public string? Likelihood { get; set; }
        public string? Explanation { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("evidence_ids")] public List<string>? EvidenceIds { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("next_steps")] public List<string>? NextSteps { get; set; }
    }
}
