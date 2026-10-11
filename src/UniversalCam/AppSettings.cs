using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace UniversalCam;

internal sealed class AppSettings
{
    // Keep the established location so upgrades preserve logs and preferences.
    internal static readonly string DirectoryPath = Environment.GetCommandLineArgs().Contains("--prototype")
        ? Path.Combine(AppContext.BaseDirectory, "prototype-data")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UniversalCam");
    private static string FilePath => Path.Combine(DirectoryPath, "settings.json");
    public bool Logging { get; set; } = true;
    public bool CloseToTray { get; set; } = true;
    public bool StartMinimized { get; set; }
    public bool LaunchAtSignIn { get; set; }
    public int PreviewFps { get; set; } = 60;
    public bool PreviewMirror { get; set; }
    public bool PreviewFill { get; set; }
    public static AppSettings Load()
    {
        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new();
            if (settings.PreviewFps is not (0 or 15 or 30 or 60)) settings.PreviewFps = 60;
            return settings;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }
    public void Save()
    {
        Directory.CreateDirectory(DirectoryPath);
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
