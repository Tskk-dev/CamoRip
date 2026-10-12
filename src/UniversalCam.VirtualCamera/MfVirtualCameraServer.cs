using System;
using UniversalCam.Core.Video;
using System.Runtime.InteropServices;

namespace UniversalCam.VirtualCamera;

/// <summary>Owns a Windows 11 camera instance; COM activation and MF lifetimes stay in native MTA code.</summary>
internal sealed class MfVirtualCameraServer : IDisposable
{
    private IntPtr _camera;
    public MfVirtualCameraServer(string framePath, VideoMode mode)
    {
        Marshal.ThrowExceptionForHR(CameraNative.UCamStartCameraMode(framePath, (uint)mode.Width, (uint)mode.Height, (uint)mode.Fps, out _camera));
    }
    public void Dispose()
    {
        if (_camera == IntPtr.Zero) return;
        CameraNative.UCamStopCamera(_camera);
        _camera = IntPtr.Zero;
    }
}

