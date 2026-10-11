using UniversalCam.Core.Protocol;

namespace UniversalCam.Core.Transport;

/// <summary>
/// Parses newline-delimited control JSON and 14-byte binary media headers.
/// One receiver owns this parser; call Reset when starting a new connection.
/// Malformed lengths terminate a connection rather than retaining unbounded data.
/// </summary>
public sealed class FrameParser
{
    public const int MaxMediaPayloadLength = 16 * 1024 * 1024;
    public const int MaxControlMessageLength = 64 * 1024;
    private const int FeedChunkSize = 64 * 1024;
    private byte[] _buffer = new byte[FeedChunkSize];
    private int _start;
    private int _end;

    public event EventHandler<ControlMessage>? ControlMessageParsed;
    public event EventHandler<MediaFrame>? FrameParsed;

    public void Feed(ReadOnlySpan<byte> data)
    {
        while (!data.IsEmpty)
        {
            var count = Math.Min(data.Length, FeedChunkSize);
            EnsureCapacity(count);
            data[..count].CopyTo(_buffer.AsSpan(_end));
            _end += count;
            data = data[count..];
            Parse();
        }
    }

    public void Reset() => _start = _end = 0;

    private void EnsureCapacity(int additional)
    {
        if (_buffer.Length - _end >= additional) return;
        var pending = _end - _start;
        _buffer.AsSpan(_start, pending).CopyTo(_buffer);
        _start = 0;
        _end = pending;
        if (_buffer.Length - _end < additional)
            Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, pending + additional));
    }

    private void Parse()
    {
        while (_start < _end)
        {
            var pending = _buffer.AsSpan(_start, _end - _start);
            var streamType = pending[0];
            if (streamType == FrameHeader.StreamTypeControl)
            {
                var newline = pending[1..].IndexOf((byte)0x0A);
                if (newline < 0)
                {
                    if (pending.Length - 1 > MaxControlMessageLength)
                        Reject("Control message exceeds the 64 KiB limit.");
                    return;
                }
                if (newline > MaxControlMessageLength)
                    Reject("Control message exceeds the 64 KiB limit.");
                var message = ControlMessage.FromJson(pending.Slice(1, newline).ToArray());
                _start += newline + 2;
                if (message is not null) ControlMessageParsed?.Invoke(this, message);
            }
            else if (streamType is FrameHeader.StreamTypeVideo or FrameHeader.StreamTypeAudio)
            {
                if (!FrameHeader.TryParse(pending, out var header)) return;
                if (header.Length > MaxMediaPayloadLength)
                    Reject("Media payload exceeds the 16 MiB limit.");
                var totalLength = FrameHeader.Size + (int)header.Length;
                if (pending.Length < totalLength) return;
                var payload = pending.Slice(FrameHeader.Size, (int)header.Length).ToArray();
                _start += totalLength;
                FrameParsed?.Invoke(this, new MediaFrame(header, payload));
            }
            else
            {
                // Preserve the original protocol's tolerance for stray unknown bytes.
                _start++;
            }
        }
        Reset();
    }

    private void Reject(string message)
    {
        Reset();
        throw new InvalidDataException(message);
    }
}
