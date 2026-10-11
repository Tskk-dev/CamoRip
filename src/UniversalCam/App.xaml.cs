using System.IO;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using UniversalCam.Views;

namespace UniversalCam;

public partial class App : Application
{
    private Mutex? _instance;
    private EventWaitHandle? _activate;
    private RegisteredWaitHandle? _activationWait;
    private MainWindow? _window;
    private TrayIcon? _tray;
    private bool _exiting;
    private static StreamWriter? _log;
    private static DispatcherQueue? _dispatcher;
    internal static AppSettings Settings { get; } = AppSettings.Load();
    internal StreamingService Service { get; } = new();
    internal static string LogDirectory => Path.Combine(AppSettings.DirectoryPath, "Logs");
    private static string LogPath => Path.Combine(LogDirectory, "latest.log");
    public App() { InitializeComponent(); }
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _instance = new Mutex(true, Environment.GetCommandLineArgs().Contains("--prototype") ? "Local\\CamoRip_Prototype" : "Local\\UniversalCam_USB_SingleInstance", out var owns);
        _activate = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\CamoRip_Activate");
        if (!owns) { _activate.Set(); Exit(); return; }
        SetLogging(Settings.Logging, rotate: true);
        UnhandledException += (_, e) => Log("FATAL", e.Exception.ToString());
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log("FATAL", e.ExceptionObject.ToString() ?? "Unhandled error");
        TaskScheduler.UnobservedTaskException += (_, e) => Log(e.Exception);
        _window = new MainWindow(this, Service);
        _tray = new TrayIcon(WinRT.Interop.WindowNative.GetWindowHandle(_window))
        {
            Open = ShowWindow, ToggleStream = () => Run(Service.ToggleStreamAsync),
            ToggleTorch = () => _window.ToggleTorch(), Exit = () => Run(ExitAsync)
        };
        Service.Changed += () => { _tray.Connected = Service.Connected; _tray.Streaming = Service.Streaming; if (!Service.Streaming) _tray.TorchAvailable = false; };
        Service.Connection.CameraStateReceived += (_, state) => Ui(() =>
        { _tray.Torch = state.TorchOn; _tray.TorchAvailable = state.TorchAvailable && Service.Streaming; });
        Service.Connection.StateChanged += (_, _) => Ui(() => { if (!Service.Connected) _tray.TorchAvailable = _tray.Torch = false; });
        _activationWait = ThreadPool.RegisterWaitForSingleObject(_activate, (_, _) => Ui(ShowWindow), null, Timeout.Infinite, false);
        _window.Activate();
        if (Settings.StartMinimized || Environment.GetCommandLineArgs().Contains("--minimized")) _window.HideToTray();
        if (!Environment.GetCommandLineArgs().Contains("--prototype")) Run(Service.StartAsync);
    }
    internal void ShowWindow() { _window?.ShowFromTray(); }
    internal async Task ExitAsync()
    {
        if (_exiting) return;
        _exiting = true;
        if (_window is not null) _window.PrepareExit();
        try { await Service.DisposeAsync(); } catch (Exception ex) { Log(ex); }
        _tray?.Dispose();
        _activationWait?.Unregister(null);
        _activate?.Dispose();
        Log("INFO", "CamoRip exited.");
        lock (typeof(App)) { _log?.Dispose(); _log = null; }
        _instance?.ReleaseMutex(); _instance?.Dispose();
        _window?.Close();
        Exit();
    }
    internal static void Ui(Action action) => _dispatcher?.TryEnqueue(() => action());
    internal static async void Run(Func<Task> work)
    { try { await work(); } catch (Exception ex) { Log(ex); } }
    internal static void SetLogging(bool enabled, bool rotate = false)
    {
        lock (typeof(App))
        {
            _log?.Dispose(); _log = null;
            if (!enabled) return;
            try
            {
                Directory.CreateDirectory(LogDirectory);
                if (rotate && File.Exists(LogPath)) File.Copy(LogPath, Path.Combine(LogDirectory, "previous.log"), true);
                _log = new StreamWriter(LogPath, !rotate) { AutoFlush = true };
                Log("INFO", "CamoRip diagnostic logging enabled.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
    internal static void Log(Exception error) => Log("ERROR", error.ToString());
    internal static void Log(string level, string message)
    {
        lock (typeof(App))
        {
            try { _log?.WriteLine($"{DateTimeOffset.Now:O} [{level}] {message}"); }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        }
    }
}
