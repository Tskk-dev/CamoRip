using System.IO.MemoryMappedFiles;
using CamoRip.Core.Air;
using CamoRip.Core.Protocol;

namespace CamoRip.Tests;

public class AirInputTests
{
    [Fact]
    public void CalibrationOverlayMatchesPhoneZonesAndLetterboxesVideo()
    {
        var settings = new AirConfigure();
        var rectangles = AirZoneLayout.Rectangles(settings, 1280, 720, 1000, 1000);
        Assert.Equal(6, rectangles.Length);
        Assert.Equal(488.75, rectangles[0].X, 6);
        Assert.Equal(305.9375, rectangles[0].Y, 6);
        Assert.Equal(22.5, rectangles[0].Width, 6);
        Assert.Equal(22.5, rectangles[0].Height, 6);
        Assert.True(rectangles[5].Y > rectangles[0].Y);
        Assert.Empty(AirZoneLayout.Rectangles(settings, 1280, 720, 0, 1000));
        foreach (var rectangle in AirZoneLayout.Rectangles(settings with { X = 1, Y = 1 }, 640, 480, 800, 600))
        {
            Assert.InRange(rectangle.X, 0, 800 - rectangle.Width);
            Assert.InRange(rectangle.Y, 0, 600 - rectangle.Height + 0.000001);
        }
    }
    [Fact]
    public void SixZonesMapToBrokenithmBeamOrder()
    {
        int[] positions = [4, 5, 2, 3, 0, 1];
        for (int zone = 0; zone < 6; zone++)
        {
            var bytes = AirInputBridge.BeamBytes(1 << zone);
            Assert.Equal(1, bytes.Sum(value => value));
            Assert.Equal(1, bytes[positions[zone]]);
        }
        Assert.All(AirInputBridge.BeamBytes(0), value => Assert.Equal(0, value));
        Assert.All(AirInputBridge.BeamBytes(63), value => Assert.Equal(1, value));
        Assert.Throws<ArgumentOutOfRangeException>(() => AirInputBridge.BeamBytes(64));
    }

    [Theory]
    [InlineData(0, 63)]
    [InlineData(499, 63)]
    [InlineData(500, 0)]
    [InlineData(-1, 0)]
    public void StaleInputReleasesAllZones(long age, int expected)
        => Assert.Equal(expected, AirInputBridge.SafeMask(63, age));

    [Fact]
    public void AirMessagesRoundTripAndValidateSettings()
    {
        var settings = new AirConfigure { Enabled = true, X = 0.25, Threshold = 20 };
        var decoded = Assert.IsType<AirConfigure>(ControlMessage.FromJson(settings.ToJsonBytes()));
        Assert.Equal(settings, decoded);
        Assert.True(decoded.IsValid);
        Assert.False((decoded with { X = double.NaN }).IsValid);
        Assert.False((decoded with { Span = 2 }).IsValid);
        var state = new AirState { Mask = 42, Enabled = true };
        Assert.Equal(state, ControlMessage.FromJson(state.ToJsonBytes()));
    }

    [Fact]
    public async Task SharedBufferWritesPreserveSliderAndReleaseOnTimeoutAndDisable()
    {
        // Use a test-only mapping to avoid interfering with a running controller.
        string name = @"Local\CamoRip_Air_Test_" + Guid.NewGuid();
        using var mapping = MemoryMappedFile.CreateNew(name, 0x88);
        using var view = mapping.CreateViewAccessor();
        view.Write(6, (byte)123);
        using var bridge = new AirInputBridge(name);
        bridge.SetEnabled(true);
        bridge.Update(63);
        for (int index = 0; index < 6; index++) Assert.Equal(1, view.ReadByte(index));
        Assert.Equal(123, view.ReadByte(6));
        await Task.Delay(800);
        for (int index = 0; index < 6; index++) Assert.Equal(0, view.ReadByte(index));
        bridge.Update(63);
        bridge.SetEnabled(false);
        for (int index = 0; index < 6; index++) Assert.Equal(0, view.ReadByte(index));
        Assert.Equal(123, view.ReadByte(6));
    }
}
