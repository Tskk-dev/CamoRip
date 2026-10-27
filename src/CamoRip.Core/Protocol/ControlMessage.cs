using System.Text.Json;
using System.Text.Json.Serialization;

namespace CamoRip.Core.Protocol;

/// <summary>
/// All control messages exchanged between the Windows PC and the iPhone.
/// Mirrors the ControlMessage enum in ios/CamoRipPhone/Transport/ConnectionManager.swift.
///
/// Wire format: JSON object terminated by newline (0x0A), prefixed with stream type byte 0x00.
/// </summary>
public abstract record ControlMessage
{
    [JsonPropertyName("type")]
    public abstract string Type { get; }


    private static readonly JsonSerializerOptions _options = new()
    {
        PropertyNamingPolicy        = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };

    public byte[] ToJsonBytes()
    {
        return JsonSerializer.SerializeToUtf8Bytes(this, GetType(), _options);
    }

    public static ControlMessage? FromJson(byte[] json)
    {
        using var doc = JsonDocument.Parse(json);
        var type = doc.RootElement.GetProperty("type").GetString();
        return type switch
        {
            "air_configure" => doc.Deserialize<AirConfigure>(_options),
            "air_state" => doc.Deserialize<AirState>(_options),
            "camera_state"      => doc.Deserialize<CameraState>(_options),
            "camera_settings"   => doc.Deserialize<CameraSettings>(_options),
            "get_camera_state"  => new GetCameraState(),
            "welcome"           => doc.Deserialize<Welcome>(_options),
            "hello"             => doc.Deserialize<Hello>(_options),
            "configure"         => doc.Deserialize<Configure>(_options),
            "configure_ack"     => doc.Deserialize<ConfigureAck>(_options),
            "start_stream"      => new StartStream(),
            "stop_stream"       => new StopStream(),
            "switch_camera"     => doc.Deserialize<SwitchCamera>(_options),
            "switch_camera_ack" => doc.Deserialize<SwitchCameraAck>(_options),
            "list_cameras"      => new ListCameras(),
            "available_cameras" => doc.Deserialize<AvailableCameras>(_options),
            "ping"                => doc.Deserialize<Ping>(_options),
            "pong"                => doc.Deserialize<Pong>(_options),
            "orientation_changed" => doc.Deserialize<OrientationChanged>(_options),
            "error"               => doc.Deserialize<ErrorMessage>(_options),
            _                     => null,
        };
    }
}


/// Sent by PC immediately after the USB tunnel is established.
public sealed record Welcome : ControlMessage
{
    public override string Type => "welcome";
}

/// PC requests specific encoding settings.
public sealed record Configure : ControlMessage
{
    public override string Type => "configure";
    public long RequestId { get; init; }

    [JsonPropertyName("resolution")]  public string Resolution { get; init; } = "1920x1080";
    [JsonPropertyName("fps")]         public int    Fps        { get; init; } = 60;
    [JsonPropertyName("codec")]       public string Codec      { get; init; } = "h264";
    [JsonPropertyName("bitrate")]     public int    Bitrate    { get; init; } = 8_000_000;
}

/// PC requests iPhone to switch to a specific camera, or toggle front/back if CameraId is null.
public sealed record SwitchCamera : ControlMessage
{
    public override string Type => "switch_camera";

    [JsonPropertyName("cameraID")] public string? CameraId { get; init; }
}

/// PC requests the list of available cameras.
public sealed record ListCameras : ControlMessage
{
    public override string Type => "list_cameras";
}

public sealed record Ping : ControlMessage
{
    public override string Type => "ping";

    [JsonPropertyName("ts")] public long Ts { get; init; }
}


/// iPhone introduces itself after receiving Welcome.
public sealed record Hello : ControlMessage
{
    public override string Type => "hello";

    [JsonPropertyName("deviceName")]   public string       DeviceName   { get; init; } = string.Empty;
    [JsonPropertyName("capabilities")] public List<string> Capabilities { get; init; } = new();
}

/// iPhone confirms Configure was applied.
public sealed record ConfigureAck : ControlMessage
{
    public override string Type => "configure_ack";
    public long RequestId { get; init; }

    [JsonPropertyName("resolution")] public string Resolution { get; init; } = string.Empty;
    [JsonPropertyName("fps")]        public int    Fps        { get; init; }
}

/// iPhone signals it is beginning to send media frames.
public sealed record StartStream : ControlMessage
{
    public override string Type => "start_stream";
}

/// Either side requests stream to stop.
public sealed record StopStream : ControlMessage
{
    public override string Type => "stop_stream";
}

/// iPhone confirms a camera switch.
public sealed record SwitchCameraAck : ControlMessage
{
    public override string Type => "switch_camera_ack";

    [JsonPropertyName("cameraID")]   public string CameraId   { get; init; } = string.Empty;
    [JsonPropertyName("cameraName")] public string CameraName { get; init; } = string.Empty;
}

/// iPhone reports its available cameras.
public sealed record AvailableCameras : ControlMessage
{
    public override string Type => "available_cameras";

    [JsonPropertyName("cameras")]         public List<CameraInfo> Cameras         { get; init; } = new();
    [JsonPropertyName("currentCameraID")] public string           CurrentCameraId { get; init; } = string.Empty;
}

public sealed record Pong : ControlMessage
{
    public override string Type => "pong";

    [JsonPropertyName("ts")] public long Ts { get; init; }
}

/// iPhone reports device orientation changed. "portrait" | "landscape"
public sealed record OrientationChanged : ControlMessage
{
    public override string Type => "orientation_changed";

    [JsonPropertyName("orientation")] public string Orientation { get; init; } = "landscape";
}

/// iPhone reports a capture, encoding, permission, or configuration error.
public sealed record ErrorMessage : ControlMessage
{
    public override string Type => "error";
    [JsonPropertyName("code")] public string Code { get; init; } = string.Empty;
    [JsonPropertyName("message")] public string Message { get; init; } = string.Empty;
}


/// Describes a camera on the iPhone. Mirrors CameraInfo in CameraSession.swift.
public sealed record CameraInfo
{
    [JsonPropertyName("id")]          public string Id         { get; init; } = string.Empty;
    [JsonPropertyName("name")]        public string Name       { get; init; } = string.Empty;
    [JsonPropertyName("position")]    public string Position   { get; init; } = string.Empty;
    [JsonPropertyName("type")]        public string Type       { get; init; } = string.Empty;
    [JsonPropertyName("zoom_factor")] public double ZoomFactor { get; init; } = 1.0;
}

public sealed record GetCameraState : ControlMessage { public override string Type => "get_camera_state"; }
public sealed record AirConfigure : ControlMessage
{
    public override string Type => "air_configure";
    public bool Enabled { get; init; }
    public bool Preview { get; init; }
    public bool Recalibrate { get; init; }
    public double Threshold { get; init; } = 12;
    public double X { get; init; } = 0.5;
    public double Y { get; init; } = 0.5;
    public double Span { get; init; } = 0.65;
    public double Width { get; init; }
    [JsonIgnore] public bool IsValid => double.IsFinite(Threshold) && Threshold is >= 1 and <= 100 &&
        double.IsFinite(X) && X is >= 0 and <= 1 && double.IsFinite(Y) && Y is >= 0 and <= 1 &&
        double.IsFinite(Span) && Span is >= 0.1 and <= 0.9 && double.IsFinite(Width) && Width is >= 0 and <= 1;
}

public sealed record AirState : ControlMessage
{
    public override string Type => "air_state";
    public int Mask { get; init; }
    public bool Enabled { get; init; }
}
public sealed record CameraSettings : ControlMessage
{
    public override string Type => "camera_settings";
    public long RequestId { get; init; }
    public string CameraId { get; init; } = "";
    public Dictionary<string, double> Values { get; init; } = new();
}
public sealed record ControlRange
{
    [JsonIgnore] public bool IsValid => double.IsFinite(Min) && double.IsFinite(Max) && Max >= Min;
    public double Min { get; init; }
    public double Max { get; init; }
}
public sealed record CameraState : ControlMessage
{
    public List<Video.VideoMode> CaptureModes { get; init; } = new();
    public bool IsStaleComparedTo(long latestRequest) => RequestId > 0 && RequestId < latestRequest;
    public override string Type => "camera_state";
    public long RequestId { get; init; }
    public string CameraId { get; init; } = "";
    public string? Error { get; init; }
    public bool TorchAvailable { get; init; }
    public bool TorchOn { get; init; }
    public bool AutoExposureSupported { get; init; }
    public bool AutoFocusSupported { get; init; }
    public bool AutoWhiteBalanceSupported { get; init; }
    public Dictionary<string, ControlRange> Ranges { get; init; } = new();
    public Dictionary<string, double> Values { get; init; } = new();
}
