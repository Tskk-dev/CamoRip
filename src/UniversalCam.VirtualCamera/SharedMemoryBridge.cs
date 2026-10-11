using System;
using UniversalCam.Core.Video;
using System.Runtime.InteropServices;

namespace UniversalCam.VirtualCamera;

/// <summary>Publishes the latest NV12 frame to the native source's bounded, per-user channel.</summary>
public sealed class SharedMemoryBridge : IDisposable
{
    public const int Width = 1920;
    public const int Height = 1080;
    public VideoMode Mode { get; }
    public SharedMemoryBridge(VideoMode? mode = null) { Mode = mode ?? VideoMode.Default; if (!Mode.IsValid) throw new ArgumentException("Invalid camera format."); }
    public string Path { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UniversalCam", $"stream-{Guid.NewGuid():N}.bin");
    private readonly object _sync = new();
    private IntPtr _writer;
    private bool _disposed;

    public void Open()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_writer == IntPtr.Zero)
                Marshal.ThrowExceptionForHR(CameraNative.UCamOpenWriterMode(Path, (uint)Mode.Width, (uint)Mode.Height, out _writer));
        }
    }

    public void WriteFrame(NV12Frame frame)
    {
        if (frame.Width != Mode.Width || frame.Height != Mode.Height || frame.Data.Length != Mode.Width * Mode.Height * 3 / 2)
            throw new ArgumentException("The virtual camera publishes 1920×1080 NV12 frames.", nameof(frame));
        lock (_sync)
        {
            Open();
            Marshal.ThrowExceptionForHR(CameraNative.UCamWriteFrame(_writer, frame.Data, (uint)frame.Data.Length, frame.PtsUs));
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            if (_writer != IntPtr.Zero) CameraNative.UCamCloseWriter(_writer);
            _writer = IntPtr.Zero;
            _disposed = true;
        }
    }
}

internal static class CameraNative
{
    private const string Library = "UCamMediaSource.dll";
    [DllImport(Library, CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern int UCamOpenWriter(string path, out IntPtr writer);
    [DllImport(Library, CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern int UCamOpenWriterMode(string path, uint width, uint height, out IntPtr writer);
    [DllImport(Library, CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern int UCamStartCameraMode(string path, uint width, uint height, uint fps, out IntPtr camera);
    [DllImport(Library, ExactSpelling = true)]
    internal static extern int UCamWriteFrame(IntPtr writer, byte[] frame, uint bytes, long ptsUs);
    [DllImport(Library, ExactSpelling = true)]
    internal static extern void UCamCloseWriter(IntPtr writer);
    [DllImport(Library, CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern int UCamStartCamera(string path, out IntPtr camera);
    [DllImport(Library, ExactSpelling = true)]
    internal static extern void UCamStopCamera(IntPtr camera);
    [DllImport(Library, ExactSpelling = true)]
    internal static extern int UCamRemoveCamera();
}

