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
    private DispatcherQueueTimer? _startupCheckTimer;
    private static readonly DiagnosticFileLog LogFile = new(LogDirectory);
    private static DispatcherQueue? _dispatcher;
    internal static AppSettings Settings { get; } = AppSettings.Load();
    internal StreamingService Service { get; } = new();
    internal static string LogDirectory => Path.Combine(AppSettings.DirectoryPath, "Logs");
    public App() { InitializeComponent(); }
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        var instanceName = AppSettings.StartupCheck ? "Local\\CamoRip_StartupCheck_" + Environment.ProcessId : AppSettings.Isolated ? "Local\\CamoRip_Prototype" : "Local\\UniversalCam_USB_SingleInstance";
        _instance = new Mutex(true, instanceName, out var owns);
        _activate = new EventWaitHandle(false, EventResetMode.AutoReset, AppSettings.Isolated ? instanceName + "_Activate" : "Local\\CamoRip_Activate");
        if (!owns) { _activate.Set(); Exit(); return; }
        SetLogging(Settings.Logging, rotate: true);
        Log("INFO", $"CamoRip {typeof(App).Assembly.GetName().Version}; mode={(AppSettings.StartupCheck ? "startup check" : AppSettings.Isolated ? "prototype (USB disabled)" : "USB camera")}; executable={Environment.ProcessPath}");
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
        if (AppSettings.StartupCheck)
        {
            _window.CheckControls();
            Log("INFO", "Startup check passed: WinUI resources loaded.");
            _startupCheckTimer = _dispatcher.CreateTimer();
            _startupCheckTimer.Interval = TimeSpan.FromSeconds(1);
            _startupCheckTimer.Tick += (_, _) => { _startupCheckTimer.Stop(); Run(ExitAsync); };
            _startupCheckTimer.Start();
        }
        else if (!AppSettings.Isolated) Run(Service.StartAsync);
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
        LogFile.Dispose();
        _instance?.ReleaseMutex(); _instance?.Dispose();
        _window?.Close();
        Exit();
    }
    internal static void Ui(Action action) => _dispatcher?.TryEnqueue(() => { try { action(); } catch (Exception ex) { Log(ex); } });
    internal static async void Run(Func<Task> work)
    { try { await work(); } catch (Exception ex) { Log(ex); } }
    internal static void SetLogging(bool enabled, bool rotate = false) => LogFile.SetEnabled(enabled, rotate);
    internal static void Log(Exception error) => Log("ERROR", error.ToString());
    internal static void Log(string level, string message) => LogFile.Write(level, message);
}
