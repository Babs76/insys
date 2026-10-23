using System.Diagnostics;

namespace Insys.Testing;

/// <summary>Latency percentiles of a smoke test, against the NFR targets (NFR-002 standard API, NFR-003 rating).</summary>
public sealed record LatencyReport(string Name, int Count, TimeSpan P50, TimeSpan P95, TimeSpan Max)
{
    public override string ToString() =>
        $"{Name}: {Count} calls, p50 {P50.TotalMilliseconds:0} ms, p95 {P95.TotalMilliseconds:0} ms, max {Max.TotalMilliseconds:0} ms";
}

public static class Latency
{
    /// <summary>Times calls one after another, after a warm-up that is not counted.</summary>
    public static async Task<LatencyReport> Sequential(string name, int warmUp, int count, Func<int, Task> call)
    {
        for (var i = 0; i < warmUp; i++)
        {
            await call(-1 - i);
        }

        var timings = new List<TimeSpan>(count);
        for (var i = 0; i < count; i++)
        {
            var started = Stopwatch.GetTimestamp();
            await call(i);
            timings.Add(Stopwatch.GetElapsedTime(started));
        }

        return Report(name, timings);
    }

    /// <summary>Times calls made concurrently, at most <paramref name="parallelism"/> at a time.</summary>
    public static async Task<LatencyReport> Concurrent(string name, int count, int parallelism, Func<int, Task> call)
    {
        var timings = new System.Collections.Concurrent.ConcurrentBag<TimeSpan>();
        await Parallel.ForEachAsync(Enumerable.Range(0, count), new ParallelOptions { MaxDegreeOfParallelism = parallelism }, async (i, _) =>
        {
            var started = Stopwatch.GetTimestamp();
            await call(i);
            timings.Add(Stopwatch.GetElapsedTime(started));
        });
        return Report(name, [.. timings]);
    }

    private static LatencyReport Report(string name, List<TimeSpan> timings)
    {
        var sorted = timings.OrderBy(t => t).ToList();
        TimeSpan At(double percentile) => sorted[Math.Clamp((int)Math.Ceiling(percentile * sorted.Count) - 1, 0, sorted.Count - 1)];
        var report = new LatencyReport(name, sorted.Count, At(0.50), At(0.95), sorted[^1]);
        Xunit.TestContext.Current.TestOutputHelper?.WriteLine(report.ToString());
        if (Environment.GetEnvironmentVariable("INSYS_LATENCY_LOG") is { Length: > 0 } log)
        {
            File.AppendAllText(log, report + Environment.NewLine);
        }

        return report;
    }
}
