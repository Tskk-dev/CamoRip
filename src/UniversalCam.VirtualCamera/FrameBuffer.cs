using System;

namespace UniversalCam.VirtualCamera;

/// <summary>
/// Thread-safe ring buffer for NV12 frames.
/// Drops the oldest frame if capacity is exceeded (for real-time streaming).
/// </summary>
public sealed class FrameBuffer
{
    private readonly NV12Frame?[] _buffer;
    private readonly object _lock = new();
    private int _head;
    private int _count;

    public int Capacity { get; }

    /// <summary>
    /// Creates a frame buffer with the specified capacity.
    /// </summary>
    public FrameBuffer(int capacity = 4)
    {
        if (capacity <= 0)
            throw new ArgumentException("Capacity must be > 0", nameof(capacity));

        Capacity = capacity;
        _buffer = new NV12Frame?[capacity];
        _head = 0;
        _count = 0;
    }

    /// <summary>
    /// Enqueues a frame (may drop oldest if buffer is full).
    /// </summary>
    public void Enqueue(NV12Frame frame)
    {
        lock (_lock)
        {
            int idx = (_head + _count) % Capacity;
            _buffer[idx] = frame;

            if (_count < Capacity)
            {
                _count++;
            }
            else
            {
                // Buffer is full; drop oldest by advancing head
                _head = (_head + 1) % Capacity;
            }
        }
    }

    /// <summary>
    /// Attempts to dequeue the oldest frame.
    /// </summary>
    public bool TryDequeue(out NV12Frame frame)
    {
        lock (_lock)
        {
            if (_count == 0)
            {
                frame = default!;
                return false;
            }

            frame = _buffer[_head]!;
            _buffer[_head] = null;
            _head = (_head + 1) % Capacity;
            _count--;
            return true;
        }
    }

    /// <summary>
    /// Gets the current number of frames in the buffer.
    /// </summary>
    public int Count
    {
        get
        {
            lock (_lock)
                return _count;
        }
    }

    /// <summary>
    /// Clears all frames from the buffer.
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            Array.Clear(_buffer, 0, _buffer.Length);
            _head = 0;
            _count = 0;
        }
    }
}
