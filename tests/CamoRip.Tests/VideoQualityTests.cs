using CamoRip.Core;
using CamoRip.Core.Transport;
using CamoRip.Core.Video;

namespace CamoRip.Tests;

public class VideoQualityTests
{
    [Theory]
    [InlineData(720, 60, 6_000_000, 12_000_000)]
    [InlineData(1080, 60, 12_000_000, 24_000_000)]
    [InlineData(2160, 30, 20_000_000, 40_000_000)]
    public void PresetsPreserveDefaultsAndScaleDetail(int height, int fps, int balanced, int high)
    {
        var mode = VideoMode.Choices.Single(m => m.Height == height && m.Fps == fps);
        Assert.Equal(balanced, VideoBitrate.Resolve(mode, VideoQuality.Balanced));
        Assert.Equal(high, VideoBitrate.Resolve(mode, VideoQuality.High));
        Assert.Equal(balanced / 2, VideoBitrate.Resolve(mode, VideoQuality.Efficient));
    }
    [Theory] [InlineData(double.NaN)] [InlineData(double.PositiveInfinity)] [InlineData(0)] [InlineData(51)]
    public void CustomRejectsInvalidValues(double mbps) => Assert.Throws<ArgumentOutOfRangeException>(() => VideoBitrate.Resolve(VideoMode.Default, VideoQuality.Custom, mbps));
    [Fact] public void CustomSupportsTheFullRange()
    {
        Assert.Equal(1_000_000, VideoBitrate.Resolve(VideoMode.Default, VideoQuality.Custom, 1));
        Assert.Equal(50_000_000, VideoBitrate.Resolve(VideoMode.Default, VideoQuality.Custom, 50));
        Assert.Equal(24_500_000, VideoBitrate.Resolve(VideoMode.Default, VideoQuality.Custom, 24.5));
    }
    [Fact] public async Task LegacyPhoneIsNotSentAnUnsupportedBitrate()
    {
        await using var manager = new ConnectionManager(new TcpUsbTransport(new UsbMuxClient(1), TimeSpan.Zero));
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.ConfigureAsync(bitrate: 24_000_000));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => manager.ConfigureAsync(bitrate: 50_000_001));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => manager.ConfigureAsync(bitrate: 499_999));
    }
    [Fact] public void QualityPreferencesSurviveReloadAndValidateSavedValues()
    {
        var path = Path.Combine(Path.GetTempPath(), "CamoRip-quality-" + Guid.NewGuid());
        try {
            var settings = AppSettings.Load(path); settings.CaptureQuality = VideoQuality.Custom; settings.CustomBitrateMbps = 42.5; settings.Save();
            var loaded = AppSettings.Load(path);
            Assert.Equal(VideoQuality.Custom, loaded.CaptureQuality); Assert.Equal(42.5, loaded.CustomBitrateMbps);
            settings.CaptureQuality = (VideoQuality)99; settings.CustomBitrateMbps = 55; settings.Save();
            loaded = AppSettings.Load(path); Assert.Equal(VideoQuality.Balanced, loaded.CaptureQuality); Assert.Equal(20, loaded.CustomBitrateMbps);
        } finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
    }
    [Fact] public void ArrivalTrackerMatchesPtsBoundsMissingOutputAndResets()
    {
        var tracker = new FrameArrivalTracker(2);
        tracker.Record(10, 100); tracker.Record(20, 200); tracker.Record(30, 300);
        Assert.Equal(0, tracker.Take(10)); Assert.Equal(300, tracker.Take(30));
        Assert.Equal(0, tracker.Take(30)); Assert.Equal(200, tracker.Take(20));
        tracker.Record(40, 400); tracker.Record(40, 450); Assert.Equal(400, tracker.Take(40));
        tracker.Record(50, 500); tracker.Reset(); Assert.Equal(0, tracker.Take(50));
    }
}
