using WhyLag.Core;

namespace WhyLag.Collectors;

public static class DefaultSources
{
    public static (List<IMetricSource> Metrics, List<IEventSource> Events) Create(WhyLagSettings settings, MachineInfo machine)
    {
        var metrics = new List<IMetricSource>
        {
            new PerformanceSource(),
            new ForegroundSource(), // before PresentMon: it uses the foreground program to pick the app
            new WifiSource(),
        };
        if (settings.FrameTimesEnabled) metrics.Add(new PresentMonSource());
        if (settings.PingEnabled) metrics.Add(new PingSource(settings.PingTarget));
        if (settings.DriverLatencyHelper) metrics.Add(new LatencyHelperSource());
        return (metrics, [new EventLogSource()]);
    }
}
