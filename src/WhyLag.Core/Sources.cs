namespace WhyLag.Core;

/// <summary>Adds metrics to each tick. Collect is called once per second and must return quickly.</summary>
public interface IMetricSource : IDisposable
{
    string Name { get; }
    void Start();
    void Collect(Tick tick);

    /// <summary>Non-fatal problems found during Start (e.g. missing counters), shown to the user.</summary>
    IReadOnlyList<string> Warnings => [];
}

/// <summary>Pushes discrete events (e.g. event log entries) as they happen.</summary>
public interface IEventSource : IDisposable
{
    string Name { get; }
    void Start(Action<SystemEvent> sink);

    IReadOnlyList<string> Warnings => [];
}

public enum SourceState { Starting, Running, Degraded, Failed }

public sealed record SourceStatus(string Name, SourceState State, string? Message);
