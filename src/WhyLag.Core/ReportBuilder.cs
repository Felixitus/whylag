using System.Text;
using static WhyLag.Core.Localization.Loc;

namespace WhyLag.Core;

/// <summary>Plain Markdown report that can be pasted into forums or support tickets.</summary>
public static class ReportBuilder
{
    public static string Markdown(Incident incident, bool anonymize = false)
    {
        var sb = new StringBuilder();
        var time = incident.TriggeredAt.ToLocalTime();
        sb.AppendLine($"# {T("report.title", time.ToString("yyyy-MM-dd HH:mm:ss", Culture))}");
        sb.AppendLine();
        sb.AppendLine($"- {T("report.trigger", TriggerText(incident))}");
        sb.AppendLine($"- {T("report.history", incident.Ticks.Count, incident.Events.Count)}");
        if (!anonymize)
        {
            sb.AppendLine($"- {T("report.system", incident.Machine.OsVersion, incident.Machine.LogicalCores, Math.Round(incident.Machine.TotalMemoryMb / 1024))}");
            if (incident.Machine.Gpus.Count > 0) sb.AppendLine($"- GPU: {string.Join(", ", incident.Machine.Gpus)}");
        }
        var fg = incident.Ticks.LastOrDefault(t => t.ForegroundProcess is not null)?.ForegroundProcess;
        if (fg is not null) sb.AppendLine($"- {T("report.foreground", fg)}");
        sb.AppendLine($"- WhyLag {incident.AppVersion}");
        sb.AppendLine();

        if (incident.Findings.Count == 0)
        {
            sb.AppendLine($"## {T("report.none.title")}");
            sb.AppendLine();
            sb.AppendLine(T("report.none.text"));
        }
        else
        {
            sb.AppendLine($"## {T("report.findings")}");
            sb.AppendLine();
            foreach (var f in incident.Findings)
            {
                sb.AppendLine($"### {f.Id}: {f.Title}");
                sb.AppendLine($"*{T("report.rule", f.RuleId, f.Confidence.ToString("P0", Culture))}*");
                sb.AppendLine();
                sb.AppendLine(f.Summary);
                sb.AppendLine();
                sb.AppendLine($"| {T("report.col.evidence")} | {T("report.col.time")} | {T("report.col.measured")} |");
                sb.AppendLine("|---|---|---|");
                foreach (var e in f.Evidence)
                    sb.AppendLine($"| {e.Id} | {e.Timestamp.ToLocalTime():HH:mm:ss} | {Escape(e.Description)} |");
                sb.AppendLine();
                if (f.Checks.Count > 0)
                {
                    sb.AppendLine(T("report.checks"));
                    foreach (var c in f.Checks) sb.AppendLine($"- {c}");
                    sb.AppendLine();
                }
            }
        }

        if (incident.Explanation is { } ai)
        {
            sb.AppendLine($"## {T("report.ai.title", ai.Model)}");
            sb.AppendLine();
            sb.AppendLine($"> {T("report.ai.note")}");
            sb.AppendLine();
            sb.AppendLine(ai.Summary);
            sb.AppendLine();
            foreach (var item in ai.Items)
            {
                sb.AppendLine($"- **{item.FindingId}** ({T($"likelihood.{item.Likelihood}")}): {item.Explanation} _[{string.Join(", ", item.EvidenceIds)}]_");
                foreach (var step in item.NextSteps) sb.AppendLine($"  - {step}");
            }
            foreach (var c in ai.Caveats) sb.AppendLine($"- _{c}_");
            sb.AppendLine();
        }

        sb.AppendLine("---");
        sb.AppendLine(T("report.disclaimer"));
        var text = sb.ToString();
        return anonymize ? Anonymize(text) : text;
    }

    public static string TriggerText(Incident incident) => incident.Trigger switch
    {
        TriggerKind.Hotkey => T("trigger.hotkey"),
        TriggerKind.Automatic => T("trigger.auto", incident.TriggerDetail),
        _ when incident.Id.EndsWith("-demo", StringComparison.Ordinal) => T("trigger.demo"),
        _ => T("trigger.manual"),
    };

    /// <summary>Removes the user and machine name, which can appear in event log messages.</summary>
    public static string Anonymize(string text)
    {
        foreach (var secret in new[] { Environment.UserName, Environment.MachineName })
        {
            if (secret.Length >= 3) text = text.Replace(secret, "<redacted>", StringComparison.OrdinalIgnoreCase);
        }
        return text;
    }

    private static string Escape(string s) => s.Replace("|", "\\|").ReplaceLineEndings(" ");
}
