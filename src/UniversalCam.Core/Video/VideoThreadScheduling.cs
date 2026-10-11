using System.Runtime.InteropServices;

namespace UniversalCam.Core.Video;

/// <summary>Register and revert on the same dedicated native thread. Failure is nonfatal.</summary>
public sealed class VideoThreadScheduling : IDisposable
{
    private readonly IntPtr _handle;
    public string Status { get; }

    public VideoThreadScheduling()
    {
        uint index = 0;
        _handle = AvSetMmThreadCharacteristicsW("Capture", ref index);
        Status = _handle != IntPtr.Zero ? "MMCSS Capture active" : $"MMCSS unavailable (Win32 {Marshal.GetLastWin32Error()})";
    }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero) AvRevertMmThreadCharacteristics(_handle);
    }

    [DllImport("avrt.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr AvSetMmThreadCharacteristicsW(string taskName, ref uint taskIndex);
    [DllImport("avrt.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AvRevertMmThreadCharacteristics(IntPtr handle);
}
