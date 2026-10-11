using System;

namespace UniversalCam.VirtualCamera;

/// <summary>
/// Converts BGRA32 frames to NV12 format using BT.601 full-range coefficients.
/// This matches the existing color space in H264Decoder.
/// </summary>
public static class NV12Converter
{
    /// <summary>Creates a small BGRA preview from limited-range BT.709 NV12.</summary>
    public static byte[] CreateBgraPreview(byte[] nv12, int width, int height, int previewWidth, int previewHeight)
    {
        ArgumentNullException.ThrowIfNull(nv12);
        if (width < 2 || height < 2 || (width & 1) != 0 || (height & 1) != 0 ||
            nv12.Length != checked(width * height * 3 / 2))
            throw new ArgumentException("Invalid NV12 frame dimensions or buffer length.", nameof(nv12));
        if (previewWidth < 1 || previewHeight < 1)
            throw new ArgumentOutOfRangeException(nameof(previewWidth));

        var bgra = new byte[checked(previewWidth * previewHeight * 4)];
        double scale = Math.Min((double)previewWidth / width, (double)previewHeight / height);
        int drawWidth = Math.Max(1, (int)(width * scale));
        int drawHeight = Math.Max(1, (int)(height * scale));
        int left = (previewWidth - drawWidth) / 2;
        int top = (previewHeight - drawHeight) / 2;
        int uvStart = width * height;
        for (int y = 0; y < drawHeight; ++y)
        {
            int sourceY = (int)((long)y * height / drawHeight);
            for (int x = 0; x < drawWidth; ++x)
            {
                int sourceX = (int)((long)x * width / drawWidth);
                int luma = Math.Max(0, 298 * (nv12[sourceY * width + sourceX] - 16));
                int uv = uvStart + (sourceY / 2) * width + (sourceX & ~1);
                int u = nv12[uv] - 128, v = nv12[uv + 1] - 128;
                int output = ((top + y) * previewWidth + left + x) * 4;
                bgra[output] = ClampByte((luma + 541 * u + 128) >> 8);
                bgra[output + 1] = ClampByte((luma - 55 * u - 136 * v + 128) >> 8);
                bgra[output + 2] = ClampByte((luma + 459 * v + 128) >> 8);
                bgra[output + 3] = 255;
            }
        }
        return bgra;
    }

    private static byte ClampByte(int value) => (byte)Math.Clamp(value, 0, 255);

    /// <summary>
    /// Fits an NV12 frame into the fixed camera size without changing its color
    /// format. The common 1080p rear-camera path returns the decoder buffer as-is.
    /// </summary>
    public static byte[] FitNv12(byte[] nv12, int width, int height, int outputWidth, int outputHeight)
    {
        ArgumentNullException.ThrowIfNull(nv12);
        if (width < 2 || height < 2 || (width & 1) != 0 || (height & 1) != 0 ||
            nv12.Length != checked(width * height * 3 / 2))
            throw new ArgumentException("Invalid NV12 frame dimensions or buffer length.", nameof(nv12));
        if (outputWidth < 2 || outputHeight < 2 || (outputWidth & 1) != 0 || (outputHeight & 1) != 0)
            throw new ArgumentOutOfRangeException(nameof(outputWidth), "NV12 output dimensions must be positive even numbers.");
        if (width == outputWidth && height == outputHeight) return nv12;

        int outputYBytes = checked(outputWidth * outputHeight);
        var output = new byte[checked(outputYBytes + outputYBytes / 2)];
        Array.Fill(output, (byte)16, 0, outputYBytes);
        Array.Fill(output, (byte)128, outputYBytes, outputYBytes / 2);
        double scale = Math.Min((double)outputWidth / width, (double)outputHeight / height);
        int drawWidth = Math.Max(2, (int)(width * scale) & ~1);
        int drawHeight = Math.Max(2, (int)(height * scale) & ~1);
        int left = ((outputWidth - drawWidth) / 2) & ~1;
        int top = ((outputHeight - drawHeight) / 2) & ~1;

        // Copy luma rows with nearest-neighbor scaling. Avoid all per-pixel color
        // conversion on the normal 1080p path and only scale front-camera formats.
        for (int y = 0; y < drawHeight; ++y)
        {
            int sourceY = (int)((long)y * height / drawHeight);
            int destination = (top + y) * outputWidth + left;
            int sourceRow = sourceY * width;
            for (int x = 0; x < drawWidth; ++x)
            {
                int sourceX = (int)((long)x * width / drawWidth);
                output[destination + x] = nv12[sourceRow + sourceX];
            }
        }

        int inputUvStart = width * height;
        int outputUvStart = outputYBytes;
        for (int y = 0; y < drawHeight; y += 2)
        {
            int sourceUvRow = (int)((long)(y / 2) * (height / 2) / (drawHeight / 2));
            int destinationUv = outputUvStart + ((top + y) / 2) * outputWidth + left;
            int sourceUv = inputUvStart + sourceUvRow * width;
            for (int x = 0; x < drawWidth; x += 2)
            {
                int sourceX = ((int)((long)x * width / drawWidth)) & ~1;
                output[destinationUv + x] = nv12[sourceUv + sourceX];
                output[destinationUv + x + 1] = nv12[sourceUv + sourceX + 1];
            }
        }
        return output;
    }

    /// <summary>Scales and letterboxes to an even-sized, limited-range BT.709 NV12 camera frame.</summary>
    public static byte[] ConvertBgra32ToNv12Scaled(byte[] bgra, int width, int height, int outputWidth, int outputHeight)
    {
        ArgumentNullException.ThrowIfNull(bgra);
        if (width <= 0 || height <= 0 || (long)width * height * 4 != bgra.Length)
            throw new ArgumentException("Invalid BGRA frame dimensions or buffer length.", nameof(bgra));
        if (outputWidth < 2 || outputHeight < 2 || (outputWidth & 1) != 0 || (outputHeight & 1) != 0)
            throw new ArgumentOutOfRangeException(nameof(outputWidth), "NV12 output dimensions must be positive even numbers.");
        int yBytes = checked(outputWidth * outputHeight);
        var output = new byte[checked(yBytes + yBytes / 2)];
        Array.Fill(output, (byte)16, 0, yBytes);
        Array.Fill(output, (byte)128, yBytes, yBytes / 2);
        double scale = Math.Min((double)outputWidth / width, (double)outputHeight / height);
        int drawWidth = Math.Max(2, (int)(width * scale) & ~1);
        int drawHeight = Math.Max(2, (int)(height * scale) & ~1);
        int left = ((outputWidth - drawWidth) / 2) & ~1;
        int top = ((outputHeight - drawHeight) / 2) & ~1;
        var sourceColumns = new int[drawWidth];
        var sourceRows = new int[drawHeight];
        for (int x = 0; x < drawWidth; ++x) sourceColumns[x] = (int)((long)x * width / drawWidth) * 4;
        for (int y = 0; y < drawHeight; ++y) sourceRows[y] = (int)((long)y * height / drawHeight) * width * 4;
        for (int y = 0; y < drawHeight; y += 2)
        {
            for (int x = 0; x < drawWidth; x += 2)
            {
                int sumR = 0, sumG = 0, sumB = 0;
                for (int dy = 0; dy != 2; ++dy)
                {
                    int sourceRow = sourceRows[y + dy];
                    int destination = (top + y + dy) * outputWidth + left + x;
                    for (int dx = 0; dx != 2; ++dx)
                    {
                        int source = sourceRow + sourceColumns[x + dx];
                        int b = bgra[source], g = bgra[source + 1], r = bgra[source + 2];
                        output[destination + dx] = (byte)Math.Clamp(16 + ((47 * r + 157 * g + 16 * b + 128) >> 8), 16, 235);
                        sumR += r; sumG += g; sumB += b;
                    }
                }
                int averageR = sumR >> 2, averageG = sumG >> 2, averageB = sumB >> 2;
                int uv = yBytes + ((top + y) / 2) * outputWidth + left + x;
                output[uv] = (byte)Math.Clamp(128 + ((-26 * averageR - 86 * averageG + 112 * averageB + 128) >> 8), 16, 240);
                output[uv + 1] = (byte)Math.Clamp(128 + ((112 * averageR - 102 * averageG - 10 * averageB + 128) >> 8), 16, 240);
            }
        }
        return output;
    }

    /// <summary>
    /// Converts BGRA32 frame data to NV12 format.
    /// </summary>
    /// <param name="bgra">BGRA32 frame bytes (width × height × 4)</param>
    /// <param name="width">Frame width in pixels</param>
    /// <param name="height">Frame height in pixels</param>
    /// <returns>NV12 frame bytes (width × height × 1.5)</returns>
    public static byte[] ConvertBgra32ToNv12(byte[] bgra, int width, int height)
    {
        if (bgra.Length != width * height * 4)
            throw new ArgumentException($"Invalid BGRA buffer size: expected {width * height * 4}, got {bgra.Length}");

        // NV12 = Y plane + UV plane
        // Y plane: width × height
        // UV plane: (width/2) × (height/2) × 2 (interleaved U/V)
        int ySize = width * height;
        int uvSize = ySize >> 1;  // width/2 × height/2 × 2
        byte[] nv12 = new byte[ySize + uvSize];

        unsafe
        {
            fixed (byte* pBgra = bgra, pNv12 = nv12)
            {
                // Process Y plane and sample U/V for every 2×2 block
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        int bgraIdx = (y * width + x) * 4;
                        int b = pBgra[bgraIdx + 0];
                        int g = pBgra[bgraIdx + 1];
                        int r = pBgra[bgraIdx + 2];

                        // BT.601 full-range: Y = 0.299*R + 0.587*G + 0.114*B
                        int yVal = (19595 * r + 38470 * g + 7471 * b) >> 16;
                        pNv12[y * width + x] = (byte)yVal.Clamp(0, 255);

                        // Sample U/V only for even rows and columns
                        if ((y & 1) == 0 && (x & 1) == 0)
                        {
                            // Sample 2×2 block and average
                            int b00 = pBgra[bgraIdx + 0];
                            int g00 = pBgra[bgraIdx + 1];
                            int r00 = pBgra[bgraIdx + 2];

                            int b10 = x + 1 < width ? pBgra[bgraIdx + 4 + 0] : b00;
                            int g10 = x + 1 < width ? pBgra[bgraIdx + 4 + 1] : g00;
                            int r10 = x + 1 < width ? pBgra[bgraIdx + 4 + 2] : r00;

                            int b01Idx = y + 1 < height ? bgraIdx + width * 4 : bgraIdx;
                            int b01 = pBgra[b01Idx + 0];
                            int g01 = pBgra[b01Idx + 1];
                            int r01 = pBgra[b01Idx + 2];

                            int b11 = (x + 1 < width && y + 1 < height) ? pBgra[b01Idx + 4 + 0] : b01;
                            int g11 = (x + 1 < width && y + 1 < height) ? pBgra[b01Idx + 4 + 1] : g01;
                            int r11 = (x + 1 < width && y + 1 < height) ? pBgra[b01Idx + 4 + 2] : r01;

                            int rAvg = (r00 + r10 + r01 + r11) >> 2;
                            int gAvg = (g00 + g10 + g01 + g11) >> 2;
                            int bAvg = (b00 + b10 + b01 + b11) >> 2;

                            // BT.601 full-range:
                            // U = (B - Y) / 1.772 = -0.168*R - 0.331*G + 0.5*B + 128
                            // V = (R - Y) / 1.402 = 0.5*R - 0.419*G - 0.081*B + 128
                            int uVal = ((-9714 * rAvg - 19070 * gAvg + 28784 * bAvg) >> 16) + 128;
                            int vVal = ((28784 * rAvg - 24107 * gAvg - 4677 * bAvg) >> 16) + 128;

                            int uvIdx = ySize + (y >> 1) * width + x;
                            pNv12[uvIdx + 0] = (byte)uVal.Clamp(0, 255);
                            pNv12[uvIdx + 1] = (byte)vVal.Clamp(0, 255);
                        }
                    }
                }
            }
        }

        return nv12;
    }
}

file static class IntExtensions
{
    public static int Clamp(this int value, int min, int max) =>
        value < min ? min : value > max ? max : value;
}

