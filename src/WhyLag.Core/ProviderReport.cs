using System.Text;
using static WhyLag.Core.Localization.Loc;

namespace WhyLag.Core;

/// <summary>
/// A report to hand to an internet provider: every recorded moment in which the line behind the router was slow while
/// the router itself answered fast. Moments with a local cause (Wi-Fi, own upload) do not count, so the report stays
/// fair and hard to dismiss.
/// </summary>
public static class ProviderReport
{
    public const string RemoteRule = "net.remote";

    public static string Markdown(IEnumerable<Incident> incidents, DateTimeOffset now, int days = 30)
    {
        var since = now.AddDays(-days);
        var hits = incidents
            .Where(i => i.TriggeredAt >= since && !i.Id.EndsWith("-demo", StringComparison.Ordinal))
            .Select(i => (Incident: i, Finding: i.Findings.FirstOrDefault(f => f.RuleId == RemoteRule)))
            .Where(x => x.Finding is not null)
            .OrderBy(x => x.Incident.TriggeredAt)
            .ToList();

        var sb = new StringBuilder();
        sb.AppendLine($"# {T("provider.title")}").AppendLine();
        sb.AppendLine(T("provider.intro")).AppendLine();
        sb.AppendLine(T("provider.period", since.ToLocalTime().ToString("d", Culture), now.ToLocalTime().ToString("d", Culture))).AppendLine();
        if (hits.Count == 0)
        {
            sb.AppendLine(T("provider.none"));
            return ReportBuilder.Anonymize(sb.ToString());
        }

        var byDay = hits.GroupBy(x => x.Incident.TriggeredAt.ToLocalTime().Date).ToList();
        sb.AppendLine(T("provider.total", hits.Count, byDay.Count)).AppendLine();
        sb.AppendLine($"| {T("provider.col.day")} | {T("provider.col.count")} |").AppendLine("|---|---|");
        foreach (var day in byDay) sb.AppendLine($"| {day.Key.ToString("d", Culture)} | {day.Count()} |");
        sb.AppendLine().AppendLine($"## {T("provider.details")}").AppendLine();
        foreach (var (incident, finding) in hits)
        {
            sb.AppendLine($"### {incident.TriggeredAt.ToLocalTime().ToString("g", Culture)}");
            foreach (var evidence in finding!.Evidence) sb.AppendLine($"- {evidence.Description}");
            sb.AppendLine();
        }
        sb.AppendLine("---").AppendLine(T("report.disclaimer"));
        return ReportBuilder.Anonymize(sb.ToString());
    }
}
