namespace UniversalCam.Core.Video;

/// <summary>Matches remote PTS to local complete-packet arrival time. Bounded even when a decoder drops output.</summary>
public sealed class FrameArrivalTracker
{
    private readonly object _sync = new();
    private readonly Dictionary<long, long> _arrivals = new();
    private readonly Queue<long> _order = new();
    private readonly int _capacity;
    public FrameArrivalTracker(int capacity = 256)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _capacity = capacity;
    }
    public void Record(long ptsUs, long ticks)
    {
        lock (_sync) {
            if (_arrivals.ContainsKey(ptsUs)) return;
            while (_order.Count >= _capacity) _arrivals.Remove(_order.Dequeue());
            _arrivals[ptsUs] = ticks; _order.Enqueue(ptsUs);
        }
    }
    public long Take(long ptsUs)
    {
        lock (_sync) return _arrivals.Remove(ptsUs, out var ticks) ? ticks : 0;
    }
    public void Reset() { lock (_sync) { _arrivals.Clear(); _order.Clear(); } }
}
