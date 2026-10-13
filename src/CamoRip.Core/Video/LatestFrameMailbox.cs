namespace CamoRip.Core.Video;

/// <summary>A single pending decoded frame, consumed by a dedicated worker.</summary>
public sealed class LatestFrameMailbox<T>(Action<T> discarded)
{
    private readonly object _sync = new();
    private T _pending = default!;
    private bool _hasPending, _completed;

    public void Publish(T frame)
    {
        T previous = default!;
        bool discard;
        lock (_sync)
        {
            discard = _completed || _hasPending;
            previous = _completed ? frame : _pending;
            if (!_completed)
            {
                _pending = frame;
                _hasPending = true;
                Monitor.Pulse(_sync);
            }
        }
        if (discard) discarded(previous);
    }

    public IEnumerable<T> ReadAll()
    {
        while (true)
        {
            T frame;
            lock (_sync)
            {
                while (!_hasPending)
                {
                    if (_completed) yield break;
                    Monitor.Wait(_sync);
                }
                frame = _pending;
                _pending = default!;
                _hasPending = false;
            }
            yield return frame;
        }
    }

    public void Complete()
    {
        lock (_sync) { _completed = true; Monitor.PulseAll(_sync); }
    }
}
