using System.Runtime.InteropServices;
using System.Text;
using UniversalCam.Core.Protocol;
using UniversalCam.Core.Transport;

namespace UniversalCam.Core.Video;

/// <summary>
/// Windows Media Foundation H.264 decoder. Each Feed contains one Annex B access unit;
/// decoded frames are tightly packed NV12 so the camera path avoids a BGRA round trip.
/// Serializes native calls and defers resets until Feed so camera switching and queue
/// overflow cannot race native decode. Missing native binaries are real errors.
/// </summary>
public sealed class H264Decoder : IDisposable
{
    public event EventHandler<DecodedFrame>? FrameDecoded;
    public event EventHandler<string>? BackendChanged;
    private bool _preferHardware;
    private readonly StringBuilder _statusBuffer = new(512);
    public string BackendStatus { get; private set; } = "not initialized";

    public H264Decoder(bool preferHardware = true) => _preferHardware = preferHardware;
    private const int MfNotAccepting = unchecked((int)0xC00D36B0);
    private readonly object _sync = new();
    private IntPtr _handle;
    private int _resetPending;
    private bool _waitingForKeyframe = true;
    private bool _disposed;

    public void Feed(MediaFrame frame)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!frame.IsVideo || frame.Payload.Length == 0) return;
            if (frame.Header.IsHevc) throw new NotSupportedException("Only H.264 video is supported.");
            if (frame.Payload.Length != frame.Header.Length || frame.Payload.Length > FrameParser.MaxMediaPayloadLength)
                throw new InvalidDataException("Invalid video packet length.");
            if (Volatile.Read(ref _resetPending) != 0) _waitingForKeyframe = true;
            if (_waitingForKeyframe && !frame.Header.IsKeyframe) return;
            if (_handle == IntPtr.Zero)
                Check(Native.UCamDecoderCreateWithOptions(_preferHardware ? 1 : 0, out _handle), "initialize H.264 decoder");
            try
            {
                if (Interlocked.Exchange(ref _resetPending, 0) != 0) ResetCore();
                SubmitAndRead(frame);
            }
            catch (COMException error)
            {
                // Recreate the MFT without a device manager after a rejected
                // hardware format, device removal or GPU readback failure.
                var fallback = Native.UCamDecoderFallBackToSoftware(_handle, error.HResult);
                if (fallback == 1) { ResetCore(); throw; } // already software
                _waitingForKeyframe = true;
                _preferHardware = false;
                if (fallback < 0)
                {
                    Native.UCamDecoderDestroy(_handle);
                    _handle = IntPtr.Zero;
                    Check(fallback, "restart the software H.264 decoder");
                }
                // A delta frame cannot seed a fresh decoder. Preserve the last
                // displayed image and wait for the next complete IDR instead.
                if (frame.Header.IsKeyframe) SubmitAndRead(frame);
            }
            finally { UpdateBackendStatus(); }
        }
    }

    private void SubmitAndRead(MediaFrame frame)
    {
        var submitted = Native.UCamDecoderSubmit(_handle, frame.Payload, frame.Payload.Length, frame.Header.PtsUs,
            frame.Header.IsKeyframe ? 1 : 0);
        if (submitted == MfNotAccepting)
        {
            ReadAvailableFrames();
            submitted = Native.UCamDecoderSubmit(_handle, frame.Payload, frame.Payload.Length, frame.Header.PtsUs,
                frame.Header.IsKeyframe ? 1 : 0);
        }
        Check(submitted, "submit H.264 frame");
        _waitingForKeyframe = false;
        ReadAvailableFrames();
    }

    private void UpdateBackendStatus()
    {
        if (_handle == IntPtr.Zero) return;
        _statusBuffer.Clear();
        Check(Native.UCamDecoderGetStatus(_handle, _statusBuffer, _statusBuffer.Capacity), "query decoder backend");
        var status = _statusBuffer.ToString();
        if (BackendStatus == status) return;
        BackendStatus = status;
        BackendChanged?.Invoke(this, status);
    }

    private void ReadAvailableFrames()
    {
        for (var count = 0; count < 32; count++)
        {
            var result = Native.UCamDecoderRead(_handle, out var width, out var height,
                out var timestamp, out var pointer, out var length);
            if (result == 1) return; // S_FALSE = decoder needs more compressed input.
            Check(result, "read decoded H.264 frame");
            if (width is < 1 or > 4096 || height is < 1 or > 2304 || (width & 1) != 0 || (height & 1) != 0 ||
                length != checked(width * height * 3 / 2) || pointer == IntPtr.Zero)
                throw new InvalidDataException("The H.264 decoder returned an invalid frame.");
            var data = new byte[length];
            Marshal.Copy(pointer, data, 0, length);
            FrameDecoded?.Invoke(this, new DecodedFrame(width, height, timestamp, data));
        }
        throw new InvalidDataException("The H.264 decoder produced too many frames for one access unit.");
    }

    public void RequestReset() => Interlocked.Exchange(ref _resetPending, 1);
    public void Reset()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ResetCore();
            Interlocked.Exchange(ref _resetPending, 0);
        }
    }

    private void ResetCore()
    {
        _waitingForKeyframe = true;
        if (_handle != IntPtr.Zero) Check(Native.UCamDecoderReset(_handle), "reset H.264 decoder");
    }

    private static void Check(int result, string operation)
    {
        if (result < 0) throw new COMException($"Could not {operation} (0x{result:X8}). On Windows N, install the Media Feature Pack.", result);
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            if (_handle != IntPtr.Zero) Native.UCamDecoderDestroy(_handle);
            _handle = IntPtr.Zero;
        }
    }

    private static class Native
    {
        private const string Library = "UniversalCam.Decoder.dll";
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern int UCamDecoderCreateWithOptions(int preferHardware, out IntPtr handle);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern int UCamDecoderFallBackToSoftware(IntPtr handle, int reason);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true, CharSet = CharSet.Unicode)]
        internal static extern int UCamDecoderGetStatus(IntPtr handle, StringBuilder text, int capacity);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern int UCamDecoderSubmit(IntPtr handle, byte[] data, int length, long timestamp, int keyframe);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern int UCamDecoderRead(IntPtr handle, out int width, out int height, out long timestamp, out IntPtr pixels, out int length);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern int UCamDecoderReset(IntPtr handle);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern void UCamDecoderDestroy(IntPtr handle);
    }
}

public sealed class DecodedFrame
{
    public int Width { get; }
    public int Height { get; }
    public long PtsUs { get; }
    /// <summary>Tightly packed NV12: width*height luma bytes followed by width*height/2 chroma bytes.</summary>
    public byte[] Data { get; }
    public DecodedFrame(int width, int height, long ptsUs, byte[] data)
    { Width = width; Height = height; PtsUs = ptsUs; Data = data; }
}

