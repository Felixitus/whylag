namespace WhyLag.Collectors;

/// <summary>Reads any counter path twice (rate counters need two samples). Used by "whylag diag".</summary>
public static class CounterDiagnostics
{
    public static List<(string Instance, double Value)>? Read(string path, TimeSpan interval)
    {
        using var q = new PdhQuery();
        if (!q.TryAdd(path)) return null;
        q.Collect();
        Thread.Sleep(interval);
        q.Collect();
        var values = q.Values(path);
        if (values.Count == 0 && q.Value(path) is { } single) values.Add(("", single));
        return values;
    }
}
