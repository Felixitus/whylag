using WhyLag.Core;

namespace WhyLag.Collectors;

public static class DefaultSources
{
    /// <param name="traceSession">Name of the frame-time trace session; the CLI uses its own so it does not stop the app's.</param>
    public static (List<IMetricSource> Metrics, List<IEventSource> Events) Create(WhyLagSettings settings, MachineInfo machine, string traceSession = "WhyLag")
    {
        var metrics = new List<IMetricSource>
        {
            new PerformanceSource(),
            new ForegroundSource(), // before PresentMon: it uses the foreground program to pick the app
            new WifiSource(),
        };
        if (settings.FrameTimesEnabled) metrics.Add(new PresentMonSource(sessionName: traceSession));
        if (settings.PingEnabled) metrics.Add(new PingSource(settings.PingTarget));
        if (settings.DriverLatencyHelper) metrics.Add(new LatencyHelperSource());
        if (settings.Temperatures) metrics.Add(new TemperatureSource());
        if (settings.RouterTraffic) metrics.Add(new RouterSource());
        return (metrics, [new EventLogSource()]);
    }
}
