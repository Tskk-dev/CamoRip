using System;

namespace UniversalCam.VirtualCamera;

/// <summary>
/// Represents a video frame in NV12 pixel format (Y plane + interleaved UV plane).
/// Suitable for hardware video encoding/streaming.
/// </summary>
public sealed record NV12Frame
{
    /// <summary>
    /// Video frame width in pixels.
    /// </summary>
    public int Width { get; init; }

    /// <summary>
    /// Video frame height in pixels.
    /// </summary>
    public int Height { get; init; }

    /// <summary>
    /// Presentation timestamp in microseconds (matching protocol).
    /// </summary>
    public long PtsUs { get; init; }

    /// <summary>
    /// NV12 frame data:
    /// [Y plane: width × height bytes]
    /// [UV plane: (width/2) × (height/2) × 2 bytes interleaved]
    /// Total: width × height × 1.5 bytes
    /// </summary>
    public byte[] Data { get; init; } = Array.Empty<byte>();
}
