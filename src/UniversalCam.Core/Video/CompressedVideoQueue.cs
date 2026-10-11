using System.Diagnostics;
using System.Threading.Channels;
using UniversalCam.Core.Protocol;

namespace UniversalCam.Core.Video;

/// <summary>
/// Bounds compressed video by frames, bytes and age. H.264 interframes depend on
/// earlier frames: an overflow discards the old GOP once, then waits for an IDR.
/// Never apply DropOldest to compressed frames; it would break that dependency.
/// </summary>
public sealed class CompressedVideoQueue
{
    private readonly object _sync = new();
    private readonly Queue<QueuedVideoFrame> _frames = new();
    private readonly Channel<bool> _signal = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.DropWrite });
    private readonly int _capacity, _maxBytes;
    private readonly TimeSpan _maxAge;
    private readonly Func<long> _clock;
    private int _bytes;
    private long _generation, _drops, _recoveries, _keyframeSkips;
    private bool _waitingForKeyframe = true, _completed;
    private string _resetReason = "initial connection";

    public CompressedVideoQueue(int capacity = 32, int maxBytes = 16 * 1024 * 1024,
        TimeSpan? maxAge = null, Func<long>? clock = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBytes, 1);
        _capacity = capacity;
        _maxBytes = maxBytes;
        _maxAge = maxAge ?? TimeSpan.FromMilliseconds(250);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_maxAge, TimeSpan.Zero);
        _clock = clock ?? Stopwatch.GetTimestamp;
    }

    public long Generation { get { lock (_sync) return _generation; } }

    public bool Enqueue(MediaFrame frame)
    {
        lock (_sync)
        {
            if (_completed || !frame.IsVideo) return false;
            var now = _clock();
            if (_frames.Count >= _capacity || (long)_bytes + frame.Payload.Length > _maxBytes ||
                (_frames.TryPeek(out var oldest) && Stopwatch.GetElapsedTime(oldest.EnqueuedTicks, now) > _maxAge))
            {
                ResetCore("compressed queue limit");
                _recoveries++;
            }
            if (frame.Payload.Length > _maxBytes)
            {
                _drops++;
                return false;
            }
            if (_waitingForKeyframe && !frame.Header.IsKeyframe)
            {
                _keyframeSkips++;
                _drops++;
                return false;
            }
            _waitingForKeyframe = false;
            _frames.Enqueue(new(frame, _generation, now, _resetReason));
            _bytes += frame.Payload.Length;
            _signal.Writer.TryWrite(true);
            return true;
        }
    }

    public void Reset(string reason)
    {
        lock (_sync) ResetCore(reason);
    }

    private void ResetCore(string reason)
    {
        _drops += _frames.Count;
        _frames.Clear();
        _bytes = 0;
        _generation++;
        _waitingForKeyframe = true;
        _resetReason = reason;
    }

    public async IAsyncEnumerable<QueuedVideoFrame> ReadAllAsync()
    {
        await foreach (var _ in _signal.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            while (TryDequeue(out var frame)) yield return frame;
        }
    }

    private bool TryDequeue(out QueuedVideoFrame frame)
    {
        lock (_sync)
        {
            if (_frames.TryPeek(out var oldest) && Stopwatch.GetElapsedTime(oldest.EnqueuedTicks, _clock()) > _maxAge)
            {
                ResetCore("compressed queue age");
                _recoveries++;
            }
            if (_frames.TryDequeue(out frame))
            {
                _bytes -= frame.Frame.Payload.Length;
                return true;
            }
            frame = default;
            return false;
        }
    }

    public VideoQueueSnapshot Snapshot()
    {
        lock (_sync)
            return new(_frames.Count, _bytes, _drops, _recoveries, _keyframeSkips, _waitingForKeyframe,
                _frames.TryPeek(out var oldest) ? Stopwatch.GetElapsedTime(oldest.EnqueuedTicks, _clock()).TotalMilliseconds : 0);
    }

    public void Complete()
    {
        lock (_sync)
        {
            _completed = true;
            _signal.Writer.TryComplete();
        }
    }
}

public readonly record struct QueuedVideoFrame(MediaFrame Frame, long Generation, long EnqueuedTicks, string ResetReason);
public readonly record struct VideoQueueSnapshot(int Depth, int Bytes, long Drops, long Recoveries, long KeyframeSkips,
    bool WaitingForKeyframe, double OldestAgeMs);
