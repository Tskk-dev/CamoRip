namespace UniversalCam.Core.Video;

/// <summary>Preserves cadence on displays whose refresh rate is not a multiple of the requested rate.</summary>
public sealed class PreviewCadence(long frequency)
{
    private double _next;
    private int _fps;
    public bool IsDue(long now, int fps)
    {
        if (fps <= 0) { _fps = 0; _next = 0; return false; }
        if (_fps != fps) { _fps = fps; _next = now; }
        return now >= _next;
    }
    public void Presented(long now)
    {
        if (_fps <= 0) return;
        double interval = (double)frequency / _fps;
        _next = _next == 0 || now - _next > interval ? now + interval : _next + interval;
    }
}
