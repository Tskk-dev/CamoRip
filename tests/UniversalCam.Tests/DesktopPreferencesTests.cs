using System.Text.Json;
using UniversalCam.Core.Protocol;

namespace UniversalCam.Tests;

public sealed class DesktopPreferencesTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "CamoRip-check-" + Guid.NewGuid().ToString("N"));
    [Fact]
    public void DisabledLoggingDoesNotCreateFiles()
    {
        using var log = new DiagnosticFileLog(_directory);
        log.SetEnabled(false); log.Write("INFO", "must not write");
        Assert.False(Directory.Exists(_directory));
    }
    [Fact]
    public void DisableStopsConcurrentWritesAndPreservesExistingContent()
    {
        using var log = new DiagnosticFileLog(_directory);
        log.SetEnabled(true);
        log.Write("INFO", "existing run");
        log.SetEnabled(false);
        var file = Path.Combine(_directory, "latest.log");
        var before = File.ReadAllBytes(file);
        Parallel.For(0, 1000, i => log.Write("METRIC", $"disabled write {i}"));
        Assert.Equal(before, File.ReadAllBytes(file));
        log.SetEnabled(true);
        log.Write("INFO", "resumed");
        log.SetEnabled(false);
        Assert.Contains("existing run", File.ReadAllText(file));
        Assert.Contains("resumed", File.ReadAllText(file));
        Assert.DoesNotContain("disabled write", File.ReadAllText(file));
    }
    [Fact]
    public void NewRunPreservesPreviousLog()
    {
        using var log = new DiagnosticFileLog(_directory);
        log.SetEnabled(true); log.Write("INFO", "preceding run"); log.SetEnabled(false);
        var before = File.ReadAllBytes(Path.Combine(_directory, "latest.log"));
        log.SetEnabled(true, rotate: true); log.SetEnabled(false);
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(_directory, "previous.log")));
        Assert.DoesNotContain("preceding run", File.ReadAllText(Path.Combine(_directory, "latest.log")));
    }
    [Fact]
    public void LoggingAndTrayPreviewPreferencesSurviveReload()
    {
        var settings = AppSettings.Load(_directory);
        settings.Logging = false; settings.CloseToTray = true; settings.StartMinimized = true;
        settings.PreviewFps = 0; settings.PreviewFill = true; settings.PreviewMirror = true;
        settings.Save();
        var restored = AppSettings.Load(_directory);
        Assert.False(restored.Logging); Assert.True(restored.CloseToTray); Assert.True(restored.StartMinimized);
        Assert.Equal(0, restored.PreviewFps); Assert.True(restored.PreviewFill); Assert.True(restored.PreviewMirror);
        Assert.False(File.Exists(Path.Combine(_directory, "settings.json.tmp")));
    }
    [Theory]
    [InlineData("{broken")]
    [InlineData("{\"PreviewFps\":1000}")]
    public void InvalidSettingsRecoverSafeDefaults(string json)
    {
        Directory.CreateDirectory(_directory); File.WriteAllText(Path.Combine(_directory, "settings.json"), json);
        var settings = AppSettings.Load(_directory);
        Assert.Equal(60, settings.PreviewFps); Assert.True(settings.Logging);
        settings.Logging = false; settings.Save();
        Assert.False(AppSettings.Load(_directory).Logging);
    }
    [Theory]
    [InlineData(1, 2, true)]
    [InlineData(2, 2, false)]
    [InlineData(0, 2, false)]
    public void OlderAcknowledgementsCannotOverwriteLatestControls(long reply, long latest, bool stale)
        => Assert.Equal(stale, new CameraState { RequestId = reply }.IsStaleComparedTo(latest));
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
