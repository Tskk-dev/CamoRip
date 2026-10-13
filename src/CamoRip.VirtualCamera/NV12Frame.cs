namespace CamoRip.VirtualCamera;

public sealed record NV12Frame
{
    public int Width { get; init; }
    public int Height { get; init; }
    public long PtsUs { get; init; }
    public byte[] Data { get; init; } = [];
}
