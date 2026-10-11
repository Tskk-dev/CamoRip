using System.Text;
using System.Text.Json;
using UniversalCam.Core.Protocol;

namespace UniversalCam.Tests;
public class CameraControlTests
{
    [Fact]
    public void CameraRequestUsesPhoneWireNamesAndPreservesValues()
    {
        var request = new CameraSettings { RequestId = 42, CameraId = "rear", Values = new() { ["exposure_bias"] = -0.7, ["auto_focus"] = 0, ["focus"] = 0.25 } };
        using var json = JsonDocument.Parse(request.ToJsonBytes());
        Assert.Equal(42, json.RootElement.GetProperty("request_id").GetInt64());
        Assert.Equal("rear", json.RootElement.GetProperty("camera_id").GetString());
        var back = Assert.IsType<CameraSettings>(ControlMessage.FromJson(request.ToJsonBytes()));
        Assert.Equal(request.Values, back.Values);
    }
    [Fact]
    public void PhoneCameraStateDeserializesCapabilitiesRangesAndActualValues()
    {
        var state = Assert.IsType<CameraState>(ControlMessage.FromJson(Encoding.UTF8.GetBytes("""
        {"type":"camera_state","request_id":42,"camera_id":"rear","torch_available":true,"torch_on":false,
        "auto_exposure_supported":true,"auto_focus_supported":true,"auto_white_balance_supported":false,
        "ranges":{"iso":{"min":25,"max":1600},"focus":{"min":0,"max":1}},"values":{"iso":100,"focus":0.25,"auto_focus":0}}
        """)));
        Assert.True(state.TorchAvailable); Assert.False(state.TorchOn);
        Assert.True(state.AutoExposureSupported); Assert.False(state.AutoWhiteBalanceSupported);
        Assert.Equal(1600, state.Ranges["iso"].Max); Assert.Equal(0.25, state.Values["focus"]);
        Assert.Equal(42, state.RequestId);
    }
    [Fact]
    public void ControlErrorsAreNotStreamStopMessages()
    {
        var state = new CameraState { RequestId = 7, Error = "Torch unavailable", CameraId = "front" };
        var back = Assert.IsType<CameraState>(ControlMessage.FromJson(state.ToJsonBytes()));
        Assert.Equal(state.Error, back.Error); Assert.Empty(back.Ranges);
        Assert.IsNotType<ErrorMessage>(back);
    }
    [Fact]
    public void CapabilitiesRequestRoundTrips() => Assert.IsType<GetCameraState>(ControlMessage.FromJson(new GetCameraState().ToJsonBytes()));
}
