using System.Diagnostics;

namespace CamoRip;

/// <summary>Small per-stage counters. No per-frame log writes or UI work.</summary>
internal sealed class StageTiming
{
    private readonly object _sync = new();
    private long _count, _total, _maximum;

    public void Record(long started)
    {
        var elapsed = Stopwatch.GetTimestamp() - started;
        lock (_sync)
        {
            _count++;
            _total += elapsed;
            _maximum = Math.Max(_maximum, elapsed);
        }
    }

    public string TakeSnapshot()
    {
        lock (_sync)
        {
            var averageMs = _count == 0 ? 0 : _total * 1000.0 / Stopwatch.Frequency / _count;
            var maximumMs = _maximum * 1000.0 / Stopwatch.Frequency;
            var result = $"{averageMs:F2}/{maximumMs:F2}ms({_count})";
            _count = _total = _maximum = 0;
            return result;
        }
    }
}
