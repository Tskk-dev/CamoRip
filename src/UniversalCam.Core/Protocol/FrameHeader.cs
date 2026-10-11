namespace UniversalCam.Core.Protocol;

/// <summary>
/// Binary frame header for video and audio streams.
/// Mirrors the wire format defined in QuicTransport.swift and USBTransport.swift.
///
/// Layout (14 bytes total):
///   [0]     stream_type  byte    0x01 = video, 0x02 = audio
///   [1..4]  length       uint32LE  payload byte count
///   [5..12] pts_us       int64LE   presentation timestamp in microseconds
///   [13]    flags        byte    video: bit0=keyframe, bit1=hevc; audio: channel count (1 or 2)
///   [14..N] payload      bytes   H.264 Annex B NALs or AAC-LC frame data
/// </summary>
public readonly struct FrameHeader
{
    public const int Size = 14;

    public const byte StreamTypeVideo   = 0x01;
    public const byte StreamTypeAudio   = 0x02;
    public const byte StreamTypeControl = 0x00;

    public readonly byte   StreamType;
    public readonly uint   Length;   // Payload bytes (does not include the 14-byte header itself)
    public readonly long   PtsUs;    // Presentation timestamp, microseconds
    public readonly byte   Flags;

    public bool IsKeyframe => StreamType == StreamTypeVideo && (Flags & 0x01) != 0;
    public bool IsHevc     => StreamType == StreamTypeVideo && (Flags & 0x02) != 0;
    public int  Channels   => StreamType == StreamTypeAudio ? Flags : 0;

    public FrameHeader(byte streamType, uint length, long ptsUs, byte flags)
    {
        StreamType = streamType;
        Length     = length;
        PtsUs      = ptsUs;
        Flags      = flags;
    }

    /// Parse a frame header from a 14-byte span (little-endian).
    public static bool TryParse(ReadOnlySpan<byte> data, out FrameHeader header)
    {
        if (data.Length < Size)
        {
            header = default;
            return false;
        }

        var streamType = data[0];
        var length     = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data[1..5]);
        var ptsUs      = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(data[5..13]);
        var flags      = data[13];

        header = new FrameHeader(streamType, length, ptsUs, flags);
        return true;
    }
}

/// <summary>
/// A fully received media frame (header + payload).
/// </summary>
public sealed class MediaFrame
{
    public FrameHeader Header  { get; }
    public byte[]      Payload { get; }

    public bool IsVideo => Header.StreamType == FrameHeader.StreamTypeVideo;
    public bool IsAudio => Header.StreamType == FrameHeader.StreamTypeAudio;

    public MediaFrame(FrameHeader header, byte[] payload)
    {
        Header  = header;
        Payload = payload;
    }
}
