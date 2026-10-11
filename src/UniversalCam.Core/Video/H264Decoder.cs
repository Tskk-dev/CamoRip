using System.Runtime.InteropServices;
using UniversalCam.Core.Protocol;
using UniversalCam.Core.Transport;

namespace UniversalCam.Core.Video;

/// <summary>
/// Windows Media Foundation H.264 decoder. Each Feed contains one Annex B access unit.
/// Serializes native calls and defers resets until Feed so camera switching and queue
/// overflow cannot race native decode. Missing native binaries are real errors.
/// </summary>
public sealed class H264Decoder : IDisposable
{
    public event EventHandler<DecodedFrame>? FrameDecoded;
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
            if (Interlocked.Exchange(ref _resetPending, 0) != 0) ResetCore();
            if (_waitingForKeyframe && !frame.Header.IsKeyframe) return;
            if (_handle == IntPtr.Zero) Check(Native.UCamDecoderCreate(out _handle), "initialize H.264 decoder");
            var submitted = Native.UCamDecoderSubmit(_handle, frame.Payload, frame.Payload.Length, frame.Header.PtsUs,
                frame.Header.IsKeyframe ? 1 : 0);
            if (submitted == MfNotAccepting)
            {
                ReadAvailableFrames();
                submitted = Native.UCamDecoderSubmit(_handle, frame.Payload, frame.Payload.Length, frame.Header.PtsUs,
                    frame.Header.IsKeyframe ? 1 : 0);
            }
            if (submitted < 0) { ResetCore(); Check(submitted, "submit H.264 frame"); }
            _waitingForKeyframe = false;
            try { ReadAvailableFrames(); }
            catch { ResetCore(); throw; }
        }
    }

    private void ReadAvailableFrames()
    {
        for (var count = 0; count < 32; count++)
        {
            var result = Native.UCamDecoderRead(_handle, out var width, out var height,
                out var timestamp, out var pointer, out var length);
            if (result == 1) return; // S_FALSE = decoder needs more compressed input.
            Check(result, "read decoded H.264 frame");
            if (width is < 1 or > 4096 || height is < 1 or > 2304 || length != checked(width * height * 4) || pointer == IntPtr.Zero)
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
        internal static extern int UCamDecoderCreate(out IntPtr handle);
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
    public byte[] Data { get; }
    public DecodedFrame(int width, int height, long ptsUs, byte[] data)
    { Width = width; Height = height; PtsUs = ptsUs; Data = data; }
}
