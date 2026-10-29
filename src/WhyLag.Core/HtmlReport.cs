using System.Globalization;
using System.Net;
using System.Text;
using static WhyLag.Core.Localization.Loc;

namespace WhyLag.Core;

/// <summary>Self-contained HTML report (inline CSS and SVG timeline) for sharing in forums or by mail.</summary>
public static class HtmlReport
{
    private static readonly (string Key, string Label, string Color, double Scale)[] Lanes =
    [
        (MetricKeys.FramesMaxMs, "chart.frames", "#ec4899", 50),
        (MetricKeys.CpuTotal, "chart.cpu", "#3b82f6", 100),
        (MetricKeys.DiskActive, "chart.disk", "#f59e0b", 100),
        (MetricKeys.Gpu3D, "chart.gpu", "#8b5cf6", 100),
        (MetricKeys.PingTargetMs, "chart.ping", "#14b8a6", 100),
    ];

    public static string Build(Incident incident, bool anonymize = true)
    {
        static string E(string? s) => WebUtility.HtmlEncode(s ?? "");
        var sb = new StringBuilder();
        var time = incident.TriggeredAt.ToLocalTime();
        sb.Append($$"""
            <!doctype html><html lang="{{Language}}"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
            <title>{{E(T("report.title", time.ToString("g", Culture)))}}</title>
            <style>
            :root{color-scheme:light dark;--bg:#f6f7f9;--card:#fff;--text:#1c2330;--muted:#5e6878;--line:#e2e5ea}
            @media(prefers-color-scheme:dark){:root{--bg:#1e1f22;--card:#2b2d31;--text:#eee;--muted:#a0a4ab;--line:#3a3c42} }
            body{font-family:Segoe UI,system-ui,sans-serif;background:var(--bg);color:var(--text);margin:0;padding:24px}
            main{max-width:960px;margin:auto} h1{margin:.2em 0} .muted{color:var(--muted)}
            .card{background:var(--card);border:1px solid var(--line);border-radius:8px;padding:16px 20px;margin:12px 0}
            .chip{display:inline-block;border-radius:10px;padding:1px 8px;font-size:12px;font-weight:600;background:#3b82f622;color:#3b82f6}
            table{border-collapse:collapse;width:100%;font-size:13px} td,th{text-align:left;padding:4px 8px;border-bottom:1px solid var(--line);vertical-align:top}
            td.mono{font-family:Cascadia Mono,Consolas,monospace;white-space:nowrap;color:var(--muted)} svg{width:100%;height:auto}
            </style></head><body><main>
            """);
        sb.Append($"<p class=\"muted\">WhyLag · {E(time.ToString("F", Culture))} · {E(ReportBuilder.TriggerText(incident))}</p>");
        sb.Append($"<h1>{E(incident.Findings.FirstOrDefault()?.Title ?? T("report.none.title"))}</h1>");
        if (!string.IsNullOrWhiteSpace(incident.Note)) sb.Append($"<div class=\"card\"><b>{E(T("report.note"))}:</b> {E(incident.Note)}</div>");
        sb.Append($"<div class=\"card\">{Timeline(incident)}</div>");

        foreach (var f in incident.Findings)
        {
            sb.Append($"<div class=\"card\"><span class=\"chip\">{E(T($"category.{f.Category}"))}</span> <span class=\"muted\">{E(f.Id)} · {E(T("ui.rulematch", f.Confidence.ToString("P0", Culture)))}</span>");
            sb.Append($"<h3>{E(f.Title)}</h3><p>{E(f.Summary)}</p><table>");
            foreach (var e in f.Evidence) sb.Append($"<tr><td class=\"mono\">{E(e.Id)}</td><td class=\"mono\">{e.Timestamp.ToLocalTime():HH:mm:ss}</td><td>{E(e.Description)}</td></tr>");
            sb.Append("</table>");
            if (f.Checks.Count > 0) sb.Append("<ul>").Append(string.Concat(f.Checks.Select(c => $"<li>{E(c)}</li>"))).Append("</ul>");
            sb.Append("</div>");
        }
        if (incident.Findings.Count == 0) sb.Append($"<div class=\"card\">{E(T("report.none.text"))}</div>");

        if (incident.Events.Count > 0)
        {
            sb.Append($"<div class=\"card\"><h3>{E(T("ui.events.title"))}</h3><table>");
            foreach (var ev in incident.Events.OrderBy(e => e.Timestamp))
                sb.Append($"<tr><td class=\"mono\">{ev.Timestamp.ToLocalTime():HH:mm:ss}</td><td>{E(T($"level.{ev.Level}"))}</td><td>{E(ev.Provider)} ({ev.EventId})</td><td>{E(ev.Message)}</td></tr>");
            sb.Append("</table></div>");
        }

        if (incident.Explanation is { } ai)
        {
            sb.Append($"<div class=\"card\"><h3>{E(T("report.ai.title", ai.Model))}</h3><p class=\"muted\">{E(T("report.ai.note"))}</p><p><b>{E(ai.Summary)}</b></p><ul>");
            foreach (var item in ai.Items) sb.Append($"<li><b>{E(item.FindingId)}</b>: {E(item.Explanation)} <span class=\"muted\">[{E(string.Join(", ", item.EvidenceIds))}]</span></li>");
            sb.Append("</ul></div>");
        }
        sb.Append($"<p class=\"muted\">{E(T("report.disclaimer"))}</p></main></body></html>");
        var html = sb.ToString();
        return anonymize ? ReportBuilder.Anonymize(html) : html;
    }

    private static string Timeline(Incident incident)
    {
        var ticks = incident.Ticks;
        var lanes = Lanes.Where(l => ticks.Any(t => t.Get(l.Key).HasValue)).ToList();
        if (ticks.Count < 2 || lanes.Count == 0) return WebUtility.HtmlEncode(T("chart.nodata"));

        const double width = 900, laneHeight = 46, labelWidth = 80;
        var start = ticks[0].Timestamp;
        var span = Math.Max(1, (ticks[^1].Timestamp - start).TotalSeconds);
        double X(DateTimeOffset t) => labelWidth + (t - start).TotalSeconds / span * (width - labelWidth);
        var height = lanes.Count * laneHeight + 18;
        var sb = new StringBuilder($"<svg viewBox=\"0 0 {width} {height}\" xmlns=\"http://www.w3.org/2000/svg\" font-family=\"Segoe UI\" font-size=\"12\">");
        var trigger = X(incident.TriggeredAt);
        for (var i = 0; i < lanes.Count; i++)
        {
            var (key, label, color, scale) = lanes[i];
            var top = i * laneHeight;
            var max = Math.Max(scale, ticks.Max(t => t.Get(key) ?? 0));
            var points = string.Join(" ", ticks.Where(t => t.Get(key).HasValue).Select(t =>
                $"{X(t.Timestamp).ToString("0.#", CultureInfo.InvariantCulture)},{(top + laneHeight - 4 - Math.Min(t.Get(key)!.Value, max) / max * (laneHeight - 8)).ToString("0.#", CultureInfo.InvariantCulture)}"));
            sb.Append($"<text x=\"0\" y=\"{top + laneHeight / 2 + 4}\" fill=\"currentColor\" opacity=\".7\">{WebUtility.HtmlEncode(T(label))}</text>");
            sb.Append($"<polyline fill=\"none\" stroke=\"{color}\" stroke-width=\"1.6\" points=\"{points}\"/>");
            sb.Append($"<line x1=\"{labelWidth}\" x2=\"{width}\" y1=\"{top + laneHeight}\" y2=\"{top + laneHeight}\" stroke=\"currentColor\" opacity=\".15\"/>");
        }
        sb.Append($"<line x1=\"{trigger:0.#}\" x2=\"{trigger:0.#}\" y1=\"0\" y2=\"{lanes.Count * laneHeight}\" stroke=\"#ef4444\" stroke-dasharray=\"4 3\"/>");
        sb.Append($"<text x=\"{labelWidth}\" y=\"{height - 2}\" fill=\"currentColor\" opacity=\".6\" font-size=\"10\">{start.ToLocalTime():HH:mm:ss}</text>");
        sb.Append($"<text x=\"{width}\" y=\"{height - 2}\" fill=\"currentColor\" opacity=\".6\" font-size=\"10\" text-anchor=\"end\">{ticks[^1].Timestamp.ToLocalTime():HH:mm:ss}</text>");
        return sb.Append("</svg>").ToString();
    }
}
