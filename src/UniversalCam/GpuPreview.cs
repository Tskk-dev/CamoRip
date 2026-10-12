using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Controls;
using UniversalCam.Core.Video;

namespace UniversalCam;

/// <summary>D3D11 NV12 shader preview. No CPU YUV-to-BGRA conversion or per-frame bitmap allocation.</summary>
internal sealed class GpuPreview : IDisposable
{
    private readonly SwapChainPanel _panel;
    private nint _handle, _native;
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetSwapChain(nint instance, nint swap);
    private SetSwapChain? _set;
    public GpuPreview(SwapChainPanel panel)
    {
        _panel = panel;
        nint swap = 0;
        try
        {
            Marshal.ThrowExceptionForHR(UCamPreviewCreate(out _handle, out swap));
            using var reference = WinRT.MarshalInspectable<SwapChainPanel>.CreateMarshaler(panel);
            var iid = new Guid("63aad0b8-7c24-40ff-85a8-640d944cc325");
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(reference.ThisPtr, ref iid, out _native));
            var vtable = Marshal.ReadIntPtr(_native);
            _set = Marshal.GetDelegateForFunctionPointer<SetSwapChain>(Marshal.ReadIntPtr(vtable, 3 * IntPtr.Size));
            Marshal.ThrowExceptionForHR(_set(_native, swap));
            Resize();
        }
        catch { Dispose(); throw; }
        finally { if (swap != 0) Marshal.Release(swap); }
    }
    public void Resize()
    {
        if (_handle == 0) return;
        var scale = _panel.XamlRoot?.RasterizationScale ?? 1;
        Marshal.ThrowExceptionForHR(UCamPreviewResize(_handle, Math.Clamp((int)(_panel.ActualWidth * scale), 1, 16384), Math.Clamp((int)(_panel.ActualHeight * scale), 1, 16384), (float)scale));
    }
    public bool Render(DecodedFrame frame, bool fill, bool mirror)
    {
        int result = UCamPreviewRender(_handle, frame.Data, frame.Data.Length, frame.Width, frame.Height, fill ? 1 : 0, mirror ? 1 : 0);
        Marshal.ThrowExceptionForHR(result);
        return result == 0;
    }
    public void Dispose()
    {
        if (_native != 0) { _set?.Invoke(_native, 0); Marshal.Release(_native); _native = 0; }
        if (_handle != 0) { UCamPreviewDestroy(_handle); _handle = 0; }
    }
    [DllImport("UniversalCam.Decoder.dll")] private static extern int UCamPreviewCreate(out nint handle, out nint swap);
    [DllImport("UniversalCam.Decoder.dll")] private static extern int UCamPreviewResize(nint handle, int w, int h, float scale);
    [DllImport("UniversalCam.Decoder.dll")] private static extern int UCamPreviewRender(nint handle, byte[] data, int length, int w, int h, int fill, int mirror);
    [DllImport("UniversalCam.Decoder.dll")] private static extern void UCamPreviewDestroy(nint handle);
}
