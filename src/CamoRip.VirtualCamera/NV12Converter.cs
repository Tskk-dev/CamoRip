using System;

namespace CamoRip.VirtualCamera;

/// <summary>
/// Fits NV12 frames and converts limited-range BT.709 preview pixels.
/// </summary>
public static class NV12Converter
{
    /// <summary>Creates a small BGRA preview from limited-range BT.709 NV12.</summary>
    public static byte[] CreateBgraPreview(byte[] nv12, int width, int height, int previewWidth, int previewHeight, byte[]? destination = null)
    {
        ArgumentNullException.ThrowIfNull(nv12);
        if (width < 2 || height < 2 || (width & 1) != 0 || (height & 1) != 0 ||
            nv12.Length != checked(width * height * 3 / 2))
            throw new ArgumentException("Invalid NV12 frame dimensions or buffer length.", nameof(nv12));
        if (previewWidth < 1 || previewHeight < 1)
            throw new ArgumentOutOfRangeException(nameof(previewWidth));

        var length = checked(previewWidth * previewHeight * 4);
        if (destination is not null && destination.Length != length)
            throw new ArgumentException("Invalid preview destination length.", nameof(destination));
        var bgra = destination ?? new byte[length];
        Array.Clear(bgra); // Clear letterboxing left by a previous aspect ratio.
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
    public static byte[] FitNv12(byte[] nv12, int width, int height, int outputWidth, int outputHeight, byte[]? destinationBuffer = null)
    {
        ArgumentNullException.ThrowIfNull(nv12);
        if (width < 2 || height < 2 || (width & 1) != 0 || (height & 1) != 0 ||
            nv12.Length != checked(width * height * 3 / 2))
            throw new ArgumentException("Invalid NV12 frame dimensions or buffer length.", nameof(nv12));
        if (outputWidth < 2 || outputHeight < 2 || (outputWidth & 1) != 0 || (outputHeight & 1) != 0)
            throw new ArgumentOutOfRangeException(nameof(outputWidth), "NV12 output dimensions must be positive even numbers.");
        if (width == outputWidth && height == outputHeight) return nv12;

        int outputYBytes = checked(outputWidth * outputHeight);
        var length = checked(outputYBytes + outputYBytes / 2);
        if (destinationBuffer is not null && (destinationBuffer.Length != length || ReferenceEquals(destinationBuffer, nv12)))
            throw new ArgumentException("NV12 resize requires a separate destinationBuffer of the exact output length.", nameof(destinationBuffer));
        var output = destinationBuffer ?? new byte[length];
        Array.Fill(output, (byte)16, 0, outputYBytes);
        Array.Fill(output, (byte)128, outputYBytes, outputYBytes / 2);
        double scale = Math.Min((double)outputWidth / width, (double)outputHeight / height);
        int drawWidth = Math.Max(2, (int)(width * scale) & ~1);
        int drawHeight = Math.Max(2, (int)(height * scale) & ~1);
        int left = ((outputWidth - drawWidth) / 2) & ~1;
        int top = ((outputHeight - drawHeight) / 2) & ~1;

        var sourceColumns = new int[drawWidth];
        for (int x = 0; x < drawWidth; ++x)
            sourceColumns[x] = (int)((long)x * width / drawWidth);
        // Copy luma rows with nearest-neighbor scaling. Avoid all per-pixel color
        // conversion on the normal 1080p path and only scale front-camera formats.
        int previousSourceRow = -1;
        for (int y = 0; y < drawHeight; ++y)
        {
            int sourceY = (int)((long)y * height / drawHeight);
            int destination = (top + y) * outputWidth + left;
            int sourceRow = sourceY * width;
            if (sourceRow == previousSourceRow)
            {
                Array.Copy(output, destination - outputWidth, output, destination, drawWidth);
                continue;
            }
            previousSourceRow = sourceRow;
            for (int x = 0; x < drawWidth; ++x)
            {
                output[destination + x] = nv12[sourceRow + sourceColumns[x]];
            }
        }

        int inputUvStart = width * height;
        int outputUvStart = outputYBytes;
        previousSourceRow = -1;
        for (int y = 0; y < drawHeight; y += 2)
        {
            int sourceUvRow = (int)((long)(y / 2) * (height / 2) / (drawHeight / 2));
            int destinationUv = outputUvStart + ((top + y) / 2) * outputWidth + left;
            int sourceUv = inputUvStart + sourceUvRow * width;
            if (sourceUv == previousSourceRow)
            {
                Array.Copy(output, destinationUv - outputWidth, output, destinationUv, drawWidth);
                continue;
            }
            previousSourceRow = sourceUv;
            for (int x = 0; x < drawWidth; x += 2)
            {
                int sourceX = sourceColumns[x] & ~1;
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
}
