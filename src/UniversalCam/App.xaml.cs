using System.IO;
using System.Windows;

namespace UniversalCam;

public partial class App : Application
{
    private Mutex? _instance;
    private bool _ownsInstance;
    private static StreamWriter? _log;

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
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UniversalCam", "Logs");
            Directory.CreateDirectory(folder);
            _log = new StreamWriter(Path.Combine(folder, "latest.log"), false) { AutoFlush = true };
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        base.OnStartup(e);
    }

    internal static void Log(Exception error)
    {
        lock (typeof(App))
        {
            Console.Error.WriteLine(error);
            try { _log?.WriteLine($"{DateTimeOffset.Now:O} {error}"); }
            catch (IOException) { }
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
