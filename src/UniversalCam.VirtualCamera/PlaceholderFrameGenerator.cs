using System;

namespace UniversalCam.VirtualCamera;

/// <summary>A cached camera icon in limited-range NV12; requires no graphics or font runtime.</summary>
public sealed class PlaceholderFrameGenerator : IDisposable
{
    private byte[]? _cached;
    private int _width, _height;
    public NV12Frame GenerateFrame(int width = 1920, int height = 1080)
    {
        if (width < 2 || height < 2 || (width & 1) != 0 || (height & 1) != 0)
            throw new ArgumentOutOfRangeException(nameof(width));
        if (_cached == null || width != _width || height != _height)
        {
            int yBytes = checked(width * height);
            _cached = new byte[checked(yBytes + yBytes / 2)];
            Array.Fill(_cached, (byte)32, 0, yBytes);
            Array.Fill(_cached, (byte)128, yBytes, yBytes / 2);
            int left = width * 2 / 5, right = width * 3 / 5;
            int top = height * 3 / 8, bottom = height * 5 / 8;
            int centerX = width / 2, centerY = height / 2, radius = Math.Max(1, height / 14);
            for (int y = top; y < bottom; ++y)
            {
                for (int x = left; x < right; ++x)
                {
                    int dx = x - centerX, dy = y - centerY;
                    _cached[y * width + x] = dx * dx + dy * dy < radius * radius ? (byte)90 : (byte)52;
                }
            }
            _width = width; _height = height;
        }
        return new NV12Frame { Width = width, Height = height, PtsUs = Environment.TickCount64 * 1000, Data = _cached };
    }
    public void Dispose() { _cached = null; }
}
