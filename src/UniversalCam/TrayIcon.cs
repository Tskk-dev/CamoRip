using System.Runtime.InteropServices;

namespace UniversalCam;

/// <summary>Win32 tray icon on the WinUI message loop; no extra UI framework.</summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly nint _window;
    private readonly WindowProc _callback;
    private readonly nint _previous;
    private const uint CallbackMessage = 0x8000 + 71;
    private NotifyIconData _data;
    private readonly nint _icon;
    internal Action? Open, ToggleStream, ToggleTorch, Exit;
    internal bool Connected, Streaming, Torch, TorchAvailable;
    public TrayIcon(nint window)
    {
        _window = window;
        _callback = Proc;
        _previous = SetWindowLongPtr(window, -4, Marshal.GetFunctionPointerForDelegate(_callback));
        _icon = LoadImage(0, System.IO.Path.Combine(AppContext.BaseDirectory, "camorip.ico"), 1, 32, 32, 0x10);
        _data = new() { Size = (uint)Marshal.SizeOf<NotifyIconData>(), Window = window, Id = 1, Flags = 7,
            Callback = CallbackMessage, Icon = _icon != 0 ? _icon : LoadIcon(0, (nint)32512), Tip = "CamoRip" };
        Shell_NotifyIcon(0, ref _data);
    }
    private nint Proc(nint hwnd, uint message, nint w, nint l)
    {
        if (message == CallbackMessage)
        {
            if ((uint)l == 0x203) Open?.Invoke(); // Double click.
            if ((uint)l is 0x205 or 0x7b)
            {
                var menu = CreatePopupMenu();
                try
                {
                    AppendMenu(menu, 0, 1, "Open CamoRip");
                    AppendMenu(menu, Connected ? 0u : 1u, 2, Streaming ? "Stop video" : "Start video");
                    AppendMenu(menu, TorchAvailable ? 0u : 1u, 3, Torch ? "Torch off" : "Torch on");
                    AppendMenu(menu, 0x800, 0, "");
                    AppendMenu(menu, 0, 4, "Exit");
                    GetCursorPos(out var point);
                    SetForegroundWindow(hwnd);
                    var selected = TrackPopupMenu(menu, 0x100 | 0x2, point.X, point.Y, 0, hwnd, 0);
                    switch (selected) { case 1: Open?.Invoke(); break; case 2: ToggleStream?.Invoke(); break;
                        case 3: ToggleTorch?.Invoke(); break; case 4: Exit?.Invoke(); break; }
                    PostMessage(hwnd, 0, 0, 0);
                }
                finally { DestroyMenu(menu); }
            }
            return 0;
        }
        if (message == RegisterWindowMessage("TaskbarCreated")) Shell_NotifyIcon(0, ref _data);
        return CallWindowProc(_previous, hwnd, message, w, l);
    }
    public void Dispose() { Shell_NotifyIcon(2, ref _data); SetWindowLongPtr(_window, -4, _previous); if (_icon != 0) DestroyIcon(_icon); }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size; public nint Window; public uint Id, Flags, Callback; public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Timeout;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags; public Guid Guid; public nint BalloonIcon;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    private delegate nint WindowProc(nint hwnd, uint msg, nint w, nint l);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIcon(uint message, ref NotifyIconData data);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint CallWindowProc(nint proc, nint hwnd, uint msg, nint w, nint l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint LoadImage(nint instance, string path, uint type, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint icon);
    [DllImport("user32.dll")] private static extern nint LoadIcon(nint instance, nint id);
    [DllImport("user32.dll")] private static extern nint CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(nint menu, uint flags, nuint id, string text);
    [DllImport("user32.dll")] private static extern uint TrackPopupMenu(nint menu, uint flags, int x, int y, int reserved, nint owner, nint rect);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(nint menu);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll")] private static extern bool PostMessage(nint hwnd, uint msg, nint w, nint l);
}
