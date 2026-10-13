namespace CamoRip.Core.Video;

public enum VideoQuality { Efficient, Balanced, High, Custom }

public static class VideoBitrate
{
    public const int Minimum = 500_000;
    public const int Maximum = 50_000_000;
    public static int Resolve(VideoMode mode, VideoQuality quality, double customMbps = 20)
    {
        if (!mode.IsValid) throw new ArgumentException("Invalid video mode.", nameof(mode));
        return quality switch {
            VideoQuality.Efficient => mode.Bitrate / 2,
            VideoQuality.Balanced => mode.Bitrate,
            VideoQuality.High => mode.Bitrate * 2,
            VideoQuality.Custom when double.IsFinite(customMbps) && customMbps is >= 1 and <= 50
                => (int)Math.Round(customMbps * 1_000_000),
            _ => throw new ArgumentOutOfRangeException(nameof(quality), "Choose a quality preset or a custom bitrate from 1 to 50 Mbps.")
        };
    }
}
