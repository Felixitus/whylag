namespace WhyLag.Core;

public static class Stats
{
    public static double Median(IReadOnlyList<double> values)
    {
        if (values.Count == 0) return double.NaN;
        var sorted = values.OrderBy(v => v).ToArray();
        var mid = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }

    /// <summary>Median absolute deviation, a spread measure that ignores the spikes we want to find.</summary>
    public static double Mad(IReadOnlyList<double> values)
    {
        var median = Median(values);
        if (double.IsNaN(median)) return double.NaN;
        return Median(values.Select(v => Math.Abs(v - median)).ToList());
    }

    public static double Round(double value) => Math.Abs(value) >= 100 ? Math.Round(value) : Math.Round(value, 1);
}
