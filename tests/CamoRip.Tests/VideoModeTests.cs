using CamoRip.Core.Video;
using CamoRip.Core.Protocol;

namespace CamoRip.Tests;
public class VideoModeTests
{
    [Fact] public void CatalogContainsOnlyRequestedModes()
    {
        Assert.Equal(5, VideoMode.Choices.Count);
        Assert.Contains(new VideoMode(3840, 2160, 30), VideoMode.Choices);
        Assert.DoesNotContain(new VideoMode(3840, 2160, 60), VideoMode.Choices);
        Assert.All(VideoMode.Choices, mode => { Assert.True(mode.IsValid); Assert.Equal(mode, VideoMode.Parse(mode.Resolution, mode.Fps)); });
    }
    [Theory]
    [InlineData("3841x2160", 30)] [InlineData("1920x1081", 30)] [InlineData("x", 60)]
    [InlineData("1920x1080", 0)] [InlineData("1920x1080", 120)]
    public void MalformedModeIsRejected(string resolution, int fps) => Assert.Null(VideoMode.Parse(resolution, fps));
    [Fact] public void PhoneCapabilitiesRoundTrip()
    {
        var state = new CameraState { CaptureModes = VideoMode.Choices.ToList() };
        Assert.Equal(state.CaptureModes, Assert.IsType<CameraState>(ControlMessage.FromJson(state.ToJsonBytes())).CaptureModes);
    }
    [Theory] [InlineData(0.01, 1, true)] [InlineData(1, 0.01, false)]
    [InlineData(double.NaN, 1, false)] [InlineData(0, double.PositiveInfinity, false)]
    public void InvalidSliderRangesAreRejected(double min, double max, bool valid)
        => Assert.Equal(valid, new ControlRange { Min = min, Max = max }.IsValid);
    [Theory] [InlineData(75, 60)] [InlineData(120, 60)] [InlineData(60, 30)]
    public void PreviewCadenceDoesNotRoundDownToHalfRefresh(int refresh, int target)
    {
        var cadence = new PreviewCadence(60_000); int count = 0;
        for (int i = 0; i < refresh * 2; ++i) {
            long now = (long)i * 60_000 / refresh;
            if (!cadence.IsDue(now, target)) continue;
            cadence.Presented(now); ++count;
        }
        Assert.InRange(count, target * 2 - 1, target * 2 + 1);
        Assert.False(cadence.IsDue(120_000, 0));
        Assert.True(cadence.IsDue(120_000, 15));
    }
    [Fact] public void CapturePreferenceSurvivesReload()
    {
        var path = Path.Combine(Path.GetTempPath(), "CamoRip-mode-" + Guid.NewGuid());
        try {
            var settings = AppSettings.Load(path); settings.CaptureResolution = "3840x2160"; settings.CaptureFps = 30; settings.Save();
            var loaded = AppSettings.Load(path); Assert.Equal("3840x2160", loaded.CaptureResolution); Assert.Equal(30, loaded.CaptureFps);
            settings.CaptureFps = 60; settings.Save(); Assert.Equal(60, AppSettings.Load(path).CaptureFps); Assert.Equal("1920x1080", AppSettings.Load(path).CaptureResolution);
        } finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
    }
}
