using System;
using UniversalCam.Core.Transport;
using UniversalCam.Core.Video;

namespace UniversalCam.VirtualCamera;

/// <summary>Publishes a fixed 1080p60 Windows 11 camera. Missing/stopped phone video shows a placeholder.</summary>
public sealed class VirtualCameraSession : IDisposable
{
    private readonly object _sync = new();
    private readonly SharedMemoryBridge _bridge = new();
    private readonly PlaceholderFrameGenerator _placeholder = new();
    private MfVirtualCameraServer? _server;
    private bool _enabled = true;
    private bool _streaming;
    private bool _disposed;
    private byte[]? _resizedFrame;
    public bool IsAvailable { get { lock (_sync) return _server != null; } }
    public string StatusMessage { get; private set; } = "Virtual camera is starting.";
    public event EventHandler<string>? StatusChanged;

    public bool IsEnabled
    {
        get { lock (_sync) return _enabled; }
        set
        {
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_enabled == value) return;
                _enabled = value;
                if (value) Initialize();
                else { _server?.Dispose(); _server = null; SetStatus("Virtual camera is disabled."); }
            }
        }
    }

    public VirtualCameraSession() { Initialize(); }

    private void Initialize()
    {
        if (_disposed || !_enabled || _server != null) return;
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            SetStatus("The virtual camera requires Windows 11 (build 22000 or later).");
            return;
        }
        try
        {
            _bridge.Open();
            WritePlaceholder();
            _server = new MfVirtualCameraServer(_bridge.Path);
            SetStatus("CamoRip is available in camera apps (1080p60).");
        }
        catch (DllNotFoundException)
        {
            SetStatus("UCamMediaSource.dll is missing. Install the Windows companion using its installer.");
        }
        catch (BadImageFormatException)
        {
            SetStatus("The virtual camera requires the x64 Windows companion and x64 source DLL.");
        }
        catch (Exception error)
        {
            SetStatus(error.HResult switch
            {
                unchecked((int)0x80040154) => "The camera source is not registered. Repair the Windows companion installation.",
                unchecked((int)0x80070005) => "Camera access was denied. Enable camera access for desktop apps in Windows Settings, then restart the companion.",
                unchecked((int)0x80070020) => "Another UniversalCam companion is already running. Close it and restart this instance.",
                _ => $"Virtual camera could not start: {error.Message} (0x{error.HResult:X8})."
            });
        }
    }

    public void OnFrameDecoded(object? sender, DecodedFrame frame) => TryPublishFrame(frame);

    /// <summary>Returns true only after the frame was written to the camera channel.</summary>
    public bool TryPublishFrame(DecodedFrame frame)
    {
        lock (_sync)
        {
            if (_disposed || !_enabled || _server == null || !_streaming) return false;
            try
            {
                var needsResize = frame.Width != SharedMemoryBridge.Width || frame.Height != SharedMemoryBridge.Height;
                if (needsResize) _resizedFrame ??= new byte[SharedMemoryBridge.Width * SharedMemoryBridge.Height * 3 / 2];
                _bridge.WriteFrame(new NV12Frame
                {
                    Width = SharedMemoryBridge.Width,
                    Height = SharedMemoryBridge.Height,
                    PtsUs = frame.PtsUs,
                    Data = NV12Converter.FitNv12(frame.Data, frame.Width, frame.Height,
                        SharedMemoryBridge.Width, SharedMemoryBridge.Height, needsResize ? _resizedFrame : null)
                });
                return true;
            }
            catch (Exception error) { SetStatus($"Video could not reach the virtual camera: {error.Message}"); return false; }
        }
    }

    public void OnConnectionStateChanged(object? sender, TransportState state)
    {
        if (state != TransportState.Connected) SetStreaming(false);
    }

    public void SetStreaming(bool streaming)
    {
        lock (_sync)
        {
            if (_disposed) return;
            _streaming = streaming;
            if (!_enabled || _server == null || streaming) return;
            try { WritePlaceholder(); }
            catch (Exception error) { SetStatus($"Could not reset the camera: {error.Message}"); }
        }
    }

    public void OnStreamStateChanged(object? sender, bool streaming) => SetStreaming(streaming);

    private void WritePlaceholder() => _bridge.WriteFrame(_placeholder.GenerateFrame());
    private void SetStatus(string message)
    {
        if (StatusMessage == message) return;
        StatusMessage = message;
        StatusChanged?.Invoke(this, message);
    }

    public static void RemoveInstalledCamera() => System.Runtime.InteropServices.Marshal.ThrowExceptionForHR(CameraNative.UCamRemoveCamera());

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _server?.Dispose(); _server = null;
            _bridge.Dispose(); _placeholder.Dispose();
        }
    }
}
