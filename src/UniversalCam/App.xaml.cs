using System.IO;
using System.Windows;

namespace UniversalCam;

public partial class App : Application
{
    private Mutex? _instance;
    private bool _ownsInstance;
    private static StreamWriter? _log;
    private static readonly string LogFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UniversalCam", "Logs", "latest.log");
    internal static string LogDirectory => Path.GetDirectoryName(LogFilePath)!;

    protected override void OnStartup(StartupEventArgs e)
    {
        _instance = new Mutex(true, "Local\\UniversalCam_USB_SingleInstance", out _ownsInstance);
        if (!_ownsInstance)
        {
            MessageBox.Show("USB iPhone Camera is already running.", "USB iPhone Camera");
            Shutdown();
            return;
        }
        try
        {
            Directory.CreateDirectory(LogDirectory);
            _log = new StreamWriter(LogFilePath, false) { AutoFlush = true };
            Log("INFO", $"Application started. OS={Environment.OSVersion}; Process={(Environment.Is64BitProcess ? "x64" : "x86")}");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        base.OnStartup(e);
    }

    internal static void Log(Exception error) => Log("ERROR", error.ToString());

    internal static void Log(string level, string message)
    {
        lock (typeof(App))
        {
            var line = $"{DateTimeOffset.Now:O} [{level}] {message}";
            Console.Error.WriteLine(line);
            try { _log?.WriteLine(line); }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _log?.Dispose();
        if (_ownsInstance) _instance?.ReleaseMutex();
        _instance?.Dispose();
        base.OnExit(e);
    }
}
