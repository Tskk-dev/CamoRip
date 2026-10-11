using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace UniversalCam;

internal sealed class AppSettings
{
    // Keep the established location so upgrades preserve logs and preferences.
    internal static readonly bool StartupCheck = Environment.GetCommandLineArgs().Contains("--startup-check");
    internal static readonly bool Isolated = StartupCheck || Environment.GetCommandLineArgs().Contains("--prototype");
    internal static readonly string DirectoryPath = Isolated
        ? Path.Combine(AppContext.BaseDirectory, StartupCheck ? "startup-check-data" : "prototype-data")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UniversalCam");
    private string _storageDirectory = DirectoryPath;
    private string FilePath => Path.Combine(_storageDirectory, "settings.json");
    public bool Logging { get; set; } = true;
    public bool CloseToTray { get; set; } = true;
    public bool StartMinimized { get; set; }
    public bool LaunchAtSignIn { get; set; }
    public int PreviewFps { get; set; } = 60;
    public bool PreviewMirror { get; set; }
    public bool PreviewFill { get; set; }
    public string CaptureResolution { get; set; } = "1920x1080";
    public int CaptureFps { get; set; } = 60;
    public static AppSettings Load(string? directory = null)
    {
        directory ??= DirectoryPath;
        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path.Combine(directory, "settings.json"))) ?? new();
            settings._storageDirectory = directory;
            if (settings.PreviewFps is not (0 or 15 or 30 or 60)) settings.PreviewFps = 60;
            if (!Core.Video.VideoMode.Choices.Contains(Core.Video.VideoMode.Parse(settings.CaptureResolution ?? "", settings.CaptureFps)))
            { settings.CaptureResolution = "1920x1080"; settings.CaptureFps = 60; }
            return settings;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return new() { _storageDirectory = directory }; }
    }
    public void Save()
    {
        Directory.CreateDirectory(_storageDirectory);
        var temporary = FilePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this));
        File.Move(temporary, FilePath, true);
    }
    public void SetLaunchAtSignIn(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (enabled) key.SetValue("CamoRip", $"\"{Environment.ProcessPath}\" --minimized");
        else key.DeleteValue("CamoRip", false);
        LaunchAtSignIn = enabled;
    }
}
