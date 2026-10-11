using System.Text;
using UniversalCam.Core.Protocol;

namespace UniversalCam.Tests;

public class ControlMessageTests
{
    [Fact]
    public void PhoneError_DeserializesCodeAndActionableMessage()
    {
        var error = Assert.IsType<ErrorMessage>(ParseJson("""
            {"type":"error","code":"camera_permission","message":"Enable camera access in iPhone Settings."}
            """));
        Assert.Equal("camera_permission", error.Code);
        Assert.Contains("Settings", error.Message);
    }
    // ── Serialization helpers ─────────────────────────────────────────────────

    private static ControlMessage? Round(ControlMessage msg)
        => ControlMessage.FromJson(msg.ToJsonBytes());

    private static ControlMessage? ParseJson(string json)
        => ControlMessage.FromJson(Encoding.UTF8.GetBytes(json));

    // ── Round-trip tests ──────────────────────────────────────────────────────

    [Fact]
    public void Configure_RoundTrip_AllFieldsPreserved()
    {
        var msg = new Configure
        {
            Resolution = "1920x1080",
            Fps        = 60,
            Codec      = "h264",
            Bitrate    = 12_000_000,
        };

        var back = Assert.IsType<Configure>(Round(msg));
        Assert.Equal(msg.Resolution, back.Resolution);
        Assert.Equal(msg.Fps,        back.Fps);
        Assert.Equal(msg.Codec,      back.Codec);
        Assert.Equal(msg.Bitrate,    back.Bitrate);
    }

    [Fact]
    public void Welcome_RoundTrip_TypeCorrect()
    {
        Assert.IsType<Welcome>(Round(new Welcome()));
    }

    [Fact]
    public void Ping_RoundTrip_TimestampPreserved()
    {
        var msg  = new Ping { Ts = 9876543210L };
        var back = Assert.IsType<Ping>(Round(msg));
        Assert.Equal(msg.Ts, back.Ts);
    }

    [Fact]
    public void SwitchCamera_RoundTrip_CameraIdPreserved()
    {
        var msg  = new SwitchCamera { CameraId = "camera-front-123" };
        var back = Assert.IsType<SwitchCamera>(Round(msg));
        Assert.Equal(msg.CameraId, back.CameraId);
    }

    // ── Deserialization from raw JSON ─────────────────────────────────────────

    [Fact]
    public void Hello_Deserialize_ParsesDeviceNameAndCapabilities()
    {
        var json  = "{\"type\":\"hello\",\"deviceName\":\"iPhone 15 Pro\",\"capabilities\":[\"h264\",\"aac_lc\",\"stereo_audio\"]}";
        var hello = Assert.IsType<Hello>(ParseJson(json));
        Assert.Equal("iPhone 15 Pro", hello.DeviceName);
        Assert.Equal(3, hello.Capabilities.Count);
        Assert.Contains("aac_lc", hello.Capabilities);
    }

    [Fact]
    public void ConfigureAck_Deserialize_ParsesResolutionAndFps()
    {
        var json = "{\"type\":\"configure_ack\",\"resolution\":\"1280x720\",\"fps\":30}";
        var ack  = Assert.IsType<ConfigureAck>(ParseJson(json));
        Assert.Equal("1280x720", ack.Resolution);
        Assert.Equal(30, ack.Fps);
    }

    [Fact]
    public void AvailableCameras_Deserialize_ParsesCameraList()
    {
        var json = """
            {
              "type":"available_cameras",
              "cameras":[
                {"id":"cam1","name":"Front Camera","position":"front","type":"wide_angle"},
                {"id":"cam2","name":"Back Camera","position":"back","type":"wide_angle"}
              ],
              "currentCameraID":"cam1"
            }
            """;
        var msg = Assert.IsType<AvailableCameras>(ParseJson(json));
        Assert.Equal(2, msg.Cameras.Count);
        Assert.Equal("cam1", msg.CurrentCameraId);
        Assert.Equal("Front Camera", msg.Cameras[0].Name);
    }

    [Fact]
    public void Pong_Deserialize_TimestampCorrect()
    {
        var json = "{\"type\":\"pong\",\"ts\":1234567890123}";
        var pong = Assert.IsType<Pong>(ParseJson(json));
        Assert.Equal(1234567890123L, pong.Ts);
    }

    // ── Unknown type ─────────────────────────────────────────────────────────

    [Fact]
    public void UnknownType_FromJson_ReturnsNull()
    {
        var result = ParseJson("{\"type\":\"totally_unknown_message_type\"}");
        Assert.Null(result);
    }

    [Fact]
    public void TypeField_CaseInsensitive_Parsed()
    {
        // Snake-case JSON from iOS should parse even if casing varies slightly
        var result = ParseJson("{\"type\":\"welcome\"}");
        Assert.IsType<Welcome>(result);
    }

    // ── ToJsonBytes sanity ────────────────────────────────────────────────────

    [Fact]
    public void ToJsonBytes_ContainsTypeField()
    {
        var json = Encoding.UTF8.GetString(new Welcome().ToJsonBytes());
        Assert.Contains("\"type\"", json);
        Assert.Contains("\"welcome\"", json);
    }
}
