using CamoRip.Core.Protocol;

namespace CamoRip.Core.Air;

public static class AirZoneLayout
{
    public static (double X, double Y, double Width, double Height)[] Rectangles(
        AirConfigure settings, int imageWidth, int imageHeight, double viewWidth, double viewHeight)
    {
        if (!settings.IsValid || imageWidth < 2 || imageHeight < 2 ||
            !double.IsFinite(viewWidth) || !double.IsFinite(viewHeight) || viewWidth <= 0 || viewHeight <= 0)
            return [];

        double scale = Math.Min(viewWidth / imageWidth, viewHeight / imageHeight);
        double left = (viewWidth - imageWidth * scale) / 2;
        double top = (viewHeight - imageHeight * scale) / 2;
        double side = Math.Max(2, Math.Min(imageWidth, imageHeight) * 0.04);
        double margin = side / (2 * imageHeight);
        double center = Math.Min(Math.Max(settings.Y, settings.Span / 2 + margin), 1 - settings.Span / 2 - margin);
        return Enumerable.Range(0, 6).Select(zone =>
        {
            double x = Math.Min(Math.Max(0, imageWidth * settings.X - side / 2), imageWidth - side);
            double y = Math.Min(Math.Max(0, imageHeight * (center + (zone / 5.0 - 0.5) * settings.Span) - side / 2), imageHeight - side);
            return (left + x * scale, top + y * scale, side * scale, side * scale);
        }).ToArray();
    }
}
