namespace UniversalCam.Core.Video;

/// <summary>Bounded, exact-size NV12 storage. A resolution change drops cached old-size buffers.</summary>
internal sealed class VideoBufferPool
{
    private readonly object _sync = new();
    private readonly Stack<byte[]> _buffers = new();
    private int _length;

    public byte[] Rent(int length)
    {
        lock (_sync)
        {
            if (_length != length) { _buffers.Clear(); _length = length; }
            return _buffers.TryPop(out var buffer) ? buffer : new byte[length];
        }
    }

    public void Return(byte[] buffer)
    {
        lock (_sync)
            if (buffer.Length == _length && _buffers.Count < 6) _buffers.Push(buffer);
    }
}
