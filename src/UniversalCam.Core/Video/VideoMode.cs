using System.Text.Json.Serialization;

namespace UniversalCam.Core.Video;

public sealed record VideoMode(int Width, int Height, int Fps)
{
    public static VideoMode Default { get; } = new(1920, 1080, 60);
    public static IReadOnlyList<VideoMode> Choices { get; } = Array.AsReadOnly(new[] {
        new VideoMode(1280, 720, 30), new VideoMode(1280, 720, 60),
        new VideoMode(1920, 1080, 30), Default, new VideoMode(3840, 2160, 30) });
    [JsonIgnore] public bool IsValid => Width is >= 640 and <= 3840 && Height is >= 360 and <= 2160 &&
        Width % 2 == 0 && Height % 2 == 0 && Fps is >= 15 and <= 60;
    [JsonIgnore] public string Resolution => $"{Width}x{Height}";
    [JsonIgnore] public string ResolutionLabel => Height == 2160 ? "4K" : $"{Height}p";
    [JsonIgnore] public int Bitrate => Height >= 2160 ? 20_000_000 : Height >= 1080 ? (Fps > 30 ? 12_000_000 : 8_000_000) : (Fps > 30 ? 6_000_000 : 4_000_000);
    public override string ToString() => $"{ResolutionLabel} · {Fps} fps";
    public static VideoMode? Parse(string? resolution, int fps)
    {
        if (string.IsNullOrEmpty(resolution)) return null;
        var parts = resolution.Split('x');
        if (parts.Length != 2 || !int.TryParse(parts[0], out var width) || !int.TryParse(parts[1], out var height)) return null;
        var mode = new VideoMode(width, height, fps);
        return mode.IsValid ? mode : null;
    }
}
