using System.Buffers.Binary;

namespace CamoRip.Core.Protocol;

// Keep the 14-byte layout compatible with installed phone apps.
public readonly record struct FrameHeader(byte StreamType, uint Length, long PtsUs, byte Flags)
{
    public const int Size = 14;
    public const byte StreamTypeControl = 0;
    public const byte StreamTypeVideo = 1;
    public const byte StreamTypeAudio = 2;

    public bool IsKeyframe => StreamType == StreamTypeVideo && (Flags & 1) != 0;
    public bool IsHevc => StreamType == StreamTypeVideo && (Flags & 2) != 0;
    public int Channels => StreamType == StreamTypeAudio ? Flags : 0;

    public static bool TryParse(ReadOnlySpan<byte> data, out FrameHeader header)
    {
        header = default;
        if (data.Length < Size)
            return false;

        header = new FrameHeader(data[0], BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(1, 4)),
            BinaryPrimitives.ReadInt64LittleEndian(data.Slice(5, 8)), data[13]);
        return true;
    }
}

public sealed record MediaFrame(FrameHeader Header, byte[] Payload)
{
    public bool IsVideo => Header.StreamType == FrameHeader.StreamTypeVideo;
    public bool IsAudio => Header.StreamType == FrameHeader.StreamTypeAudio;
}
