using System.Diagnostics.Eventing.Reader;
using WhyLag.Core;
using static WhyLag.Core.Localization.Loc;
using EventLevel = WhyLag.Core.EventLevel;

namespace WhyLag.Collectors;

/// <summary>
/// Subscribes to new Windows event log entries that are relevant for stutters and freezes.
/// Only warnings/errors from System and Application plus a few specific operational events.
/// </summary>
public sealed class EventLogSource : IEventSource
{
    private const string WarningsAndErrors = "*[System[(Level=1 or Level=2 or Level=3)]]";

    private static readonly (string Channel, string Query)[] Subscriptions =
    [
        ("System", WarningsAndErrors),
        ("Application", WarningsAndErrors),
        // Wi-Fi connect/disconnect and roaming related events
        ("Microsoft-Windows-WLAN-AutoConfig/Operational", "*[System[(EventID=8001 or EventID=8002 or EventID=8003 or EventID=11004 or EventID=11005 or EventID=11010)]]"),
        // Windows Update download/installation started/finished/failed
        ("Microsoft-Windows-WindowsUpdateClient/Operational", "*[System[(EventID=19 or EventID=20 or EventID=43 or EventID=44)]]"),
    ];

    private readonly List<EventLogWatcher> _watchers = new();

    public string Name => WhyLag.Core.Localization.Loc.T("source.eventlog");

    /// <summary>Channels that could not be subscribed to (missing or access denied).</summary>
    public List<string> UnavailableChannels { get; } = new();

    public void Start(Action<SystemEvent> sink)
    {
        foreach (var (channel, query) in Subscriptions)
        {
            try
            {
                var watcher = new EventLogWatcher(new EventLogQuery(channel, PathType.LogName, query));
                watcher.EventRecordWritten += (_, args) =>
                {
                    if (args.EventRecord is { } record)
                    {
                        using (record)
                        {
                            if (Convert(channel, record) is { } e) sink(e);
                        }
                    }
                };
                watcher.Enabled = true;
                _watchers.Add(watcher);
            }
            catch (Exception ex) when (ex is EventLogException or UnauthorizedAccessException)
            {
                UnavailableChannels.Add(channel);
            }
        }
        if (_watchers.Count == 0) throw new InvalidOperationException(T("err.eventlog"));
    }

    private static SystemEvent? Convert(string channel, EventRecord record)
    {
        string message;
        try
        {
            message = record.FormatDescription() ?? "";
        }
        catch (EventLogException)
        {
            message = "";
        }
        if (message.Length > 500) message = message[..500];

        var level = record.Level is >= 1 and <= 4 ? (EventLevel)record.Level.Value : EventLevel.Information;
        return new SystemEvent(
            record.TimeCreated is { } t ? new DateTimeOffset(t) : DateTimeOffset.Now,
            channel,
            record.ProviderName ?? "",
            record.Id,
            level,
            message.Trim());
    }

    public void Dispose()
    {
        foreach (var w in _watchers)
        {
            w.Enabled = false;
            w.Dispose();
        }
        _watchers.Clear();
    }
}
