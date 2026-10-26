using System.Diagnostics.Eventing.Reader;
using System.Text.RegularExpressions;
using WhyLag.Core;

namespace WhyLag.Collectors;

/// <summary>Reads recent driver updates and Windows updates from the Windows event logs.</summary>
public static partial class SystemChangeReader
{
    [GeneratedRegex(@"^[0-9A-Z]{12}-")]
    private static partial Regex StoreApp();

    public static List<SystemChange> Read(TimeSpan span) => [.. ReadDriverUpdates(span), .. ReadWindowsUpdates(span)];

    /// <summary>
    /// Kernel-PnP event 400 is logged whenever a device is configured. A driver update is a device that shows up
    /// again with a different driver version. Windows' own inbox drivers are skipped.
    /// </summary>
    private static IEnumerable<SystemChange> ReadDriverUpdates(TimeSpan span)
    {
        var lastVersion = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<SystemChange>();
        foreach (var record in Query("Microsoft-Windows-Kernel-PnP/Configuration", $"EventID=400 and TimeCreated[timediff(@SystemTime) <= {(long)span.TotalMilliseconds}]"))
        {
            using (record)
            {
                var p = record.Properties.Select(x => x.Value?.ToString() ?? "").ToList();
                if (p.Count < 6 || record.TimeCreated is not { } time) continue;
                var (device, inf, version, provider) = (p[0], p[1], p[4], p[5]);
                if (provider.Length == 0 || provider == "null" || provider.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase)) continue;
                if (lastVersion.TryGetValue(device, out var previous) && previous != version)
                    result.Add(new SystemChange(new DateTimeOffset(time), ChangeKind.Driver, $"{provider}|{version}"));
                lastVersion[device] = version;
            }
        }
        // A driver package updates many devices at once: one change per manufacturer and hour.
        return result
            .GroupBy(c => (Provider: c.Description.Split('|')[0], Hour: c.At.ToLocalTime().ToString("yyyyMMddHH")))
            .Select(g =>
            {
                var version = g.GroupBy(c => c.Description.Split('|')[1]).MaxBy(v => v.Count())!.Key;
                return new SystemChange(g.Min(c => c.At), ChangeKind.Driver, $"{g.Key.Provider} {version}");
            })
            .ToList();
    }

    /// <summary>Installed Windows updates, without the daily Defender signatures and Store app updates.</summary>
    private static IEnumerable<SystemChange> ReadWindowsUpdates(TimeSpan span)
    {
        var result = new List<SystemChange>();
        foreach (var record in Query("System", $"Provider[@Name='Microsoft-Windows-WindowsUpdateClient'] and EventID=19 and TimeCreated[timediff(@SystemTime) <= {(long)span.TotalMilliseconds}]"))
        {
            using (record)
            {
                var title = record.Properties.FirstOrDefault()?.Value?.ToString() ?? "";
                if (record.TimeCreated is not { } time || title.Length == 0 || StoreApp().IsMatch(title)
                    || title.Contains("Defender", StringComparison.OrdinalIgnoreCase) || title.Contains("KB890830", StringComparison.Ordinal)) continue;
                result.Add(new SystemChange(new DateTimeOffset(time), ChangeKind.WindowsUpdate, title));
            }
        }
        return result;
    }

    private static IEnumerable<EventRecord> Query(string log, string condition)
    {
        EventLogReader reader;
        try
        {
            // Oldest first, so driver versions can be compared in order.
            reader = new EventLogReader(new EventLogQuery(log, PathType.LogName, $"*[System[{condition}]]") { ReverseDirection = false });
        }
        catch (EventLogException)
        {
            yield break;
        }
        using (reader)
        {
            while (true)
            {
                EventRecord? record;
                try
                {
                    record = reader.ReadEvent();
                }
                catch (EventLogException)
                {
                    yield break;
                }
                if (record is null) yield break;
                yield return record;
            }
        }
    }
}
