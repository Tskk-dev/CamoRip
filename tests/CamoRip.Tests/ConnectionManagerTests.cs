using System.Text;
using System.Net.Sockets;
using CamoRip.Core;
using CamoRip.Core.Protocol;
using CamoRip.Core.Transport;

namespace CamoRip.Tests;

public class ConnectionManagerTests
{
    [Fact]
    public async Task AirCapabilityAndStateUseExistingConnectionAndRejectInvalidMasks()
    {
        using var service = new FakeAppleService();
        var received = new TaskCompletionSource<AirState>(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = Task.Run(async () => {
            await DiscoveryAsync(service);
            await service.ServeOnceAsync(async (_, stream) => {
                await FakeAppleService.ReplyAsync(stream, "<key>MessageType</key><string>Result</string><key>Number</key><integer>0</integer>");
                Assert.IsType<Welcome>(await ReadControlAsync(stream));
                await SendControlAsync(stream, new Hello { Capabilities = ["h264", "air_zones_v1", "air_preview_v1"] });
                var settings = Assert.IsType<AirConfigure>(await ReadControlAsync(stream));
                Assert.True(settings.Enabled);
                Assert.True(settings.Preview);
                Assert.True(settings.IsValid);
                await SendControlAsync(stream, new AirState { Enabled = true, Mask = 64 });
                await SendControlAsync(stream, new AirState { Enabled = true, Mask = 7 });
                await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
            });
        });
        await using var manager = new ConnectionManager(new TcpUsbTransport(new UsbMuxClient(service.Port), TimeSpan.FromSeconds(1)));
        manager.StateChanged += (_, state) => {
            if (state == TransportState.Connected) _ = manager.ConfigureAirAsync(new AirConfigure { Enabled = true, Preview = true });
        };
        manager.AirStateReceived += (_, state) => received.TrySetResult(state);
        await manager.StartAsync();
        var result = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(7, result.Mask);
        await server;
    }
    [Fact]
    public async Task UpdatedPhoneAcceptsHighBitrateAndReportsControlRoundTrip()
    {
        using var service = new FakeAppleService();
        var server = Task.Run(async () => {
            await DiscoveryAsync(service);
            await service.ServeOnceAsync(async (_, stream) => {
                await FakeAppleService.ReplyAsync(stream, "<key>MessageType</key><string>Result</string><key>Number</key><integer>0</integer>");
                Assert.IsType<Welcome>(await ReadControlAsync(stream));
                await SendControlAsync(stream, new Hello { Capabilities = ["h264", "bitrate_control_v1"] });
                var configure = Assert.IsType<Configure>(await ReadControlAsync(stream));
                Assert.Equal(40_000_000, configure.Bitrate);
                await SendControlAsync(stream, new ConfigureAck { RequestId = configure.RequestId, Resolution = configure.Resolution, Fps = configure.Fps });
                Assert.IsType<StartStream>(await ReadControlAsync(stream));
                await SendControlAsync(stream, new StartStream());
                var ping = Assert.IsType<Ping>(await ReadControlAsync(stream));
                await SendControlAsync(stream, new Pong { Ts = ping.Ts + 1 }); // unsolicited replies do not measure RTT
                await SendControlAsync(stream, new Pong { Ts = ping.Ts });
            });
        });
        await using var manager = new ConnectionManager(new TcpUsbTransport(new UsbMuxClient(service.Port), TimeSpan.FromSeconds(1)));
        var result = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);
        manager.StateChanged += (_, state) => { if (state == TransportState.Connected) _ = manager.ConfigureAsync("3840x2160", 30, bitrate: 40_000_000); };
        manager.StreamStateChanged += (_, streaming) => { if (streaming) _ = manager.PingAsync(); };
        manager.RoundTripUpdated += (_, rtt) => result.TrySetResult(rtt);
        await manager.StartAsync();
        Assert.InRange(await result.Task.WaitAsync(TimeSpan.FromSeconds(10)), 0, 5_000);
        await server;
    }
    [Fact]
    public async Task ConfigureAsync_Allows60AndRejectsHigherRates()
    {
        await using var manager = new ConnectionManager(new TcpUsbTransport(new UsbMuxClient(1), TimeSpan.Zero));
        await manager.ConfigureAsync(fps: 60);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => manager.ConfigureAsync(fps: 61));
    }

    [Fact]
    public async Task UnsolicitedConfigurationAck_DoesNotRestartAStoppedPhone()
    {
        using var service = new FakeAppleService();
        var server = Task.Run(async () =>
        {
            await DiscoveryAsync(service);
            await service.ServeOnceAsync(async (_, stream) =>
            {
                await FakeAppleService.ReplyAsync(stream, "<key>MessageType</key><string>Result</string><key>Number</key><integer>0</integer>");
                Assert.IsType<Welcome>(await ReadControlAsync(stream));
                await SendControlAsync(stream, new Hello { DeviceName = "test phone", Capabilities = ["h264"] });
                Assert.IsType<Configure>(await ReadControlAsync(stream));
                await SendControlAsync(stream, new ConfigureAck { Resolution = "1920x1080", Fps = 30 });
                Assert.IsType<StartStream>(await ReadControlAsync(stream));
                await SendControlAsync(stream, new StartStream());
                await SendControlAsync(stream, new StopStream()); // user stopped from the phone
                await SendControlAsync(stream, new ConfigureAck { Resolution = "1280x720", Fps = 30 }); // camera switch
                await AssertNoControlAsync(stream);
            });
        });
        await using var manager = new ConnectionManager(new TcpUsbTransport(new UsbMuxClient(service.Port), TimeSpan.FromSeconds(1)));
        var stopped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var configured = 0;
        manager.StateChanged += (_, state) =>
        {
            if (state == TransportState.Connected) _ = manager.ConfigureAsync();
        };
        manager.ConfigurationAcknowledged += (_, _) => Interlocked.Increment(ref configured);
        manager.StreamStateChanged += (_, streaming) => { if (!streaming) stopped.TrySetResult(true); };
        await manager.StartAsync();
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await server;
        Assert.Equal(2, configured);
        Assert.False(manager.IsStreaming);
    }

    [Fact]
    public async Task StopBeforeConfigureAck_CancelsInitialAutomaticStart()
    {
        using var service = new FakeAppleService();
        var server = Task.Run(async () =>
        {
            await DiscoveryAsync(service);
            await service.ServeOnceAsync(async (_, stream) =>
            {
                await FakeAppleService.ReplyAsync(stream, "<key>MessageType</key><string>Result</string><key>Number</key><integer>0</integer>");
                Assert.IsType<Welcome>(await ReadControlAsync(stream));
                await SendControlAsync(stream, new Hello { DeviceName = "test phone", Capabilities = ["h264"] });
                Assert.IsType<Configure>(await ReadControlAsync(stream));
                Assert.IsType<StopStream>(await ReadControlAsync(stream));
                await SendControlAsync(stream, new StopStream());
                await SendControlAsync(stream, new ConfigureAck { Resolution = "1920x1080", Fps = 30 });
                await AssertNoControlAsync(stream);
            });
        });
        await using var manager = new ConnectionManager(new TcpUsbTransport(new UsbMuxClient(service.Port), TimeSpan.FromSeconds(1)));
        manager.StateChanged += async (_, state) =>
        {
            if (state != TransportState.Connected) return;
            await manager.ConfigureAsync();
            await manager.StopStreamAsync();
        };
        await manager.StartAsync();
        await server;
        Assert.False(manager.IsStreaming);
    }

    [Fact]
    public async Task CameraControlAcknowledgementDoesNotStopVideo()
    {
        using var service = new FakeAppleService();
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var confirmed = new TaskCompletionSource<CameraState>(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = Task.Run(async () =>
        {
            await DiscoveryAsync(service);
            await service.ServeOnceAsync(async (_, stream) =>
            {
                await FakeAppleService.ReplyAsync(stream, "<key>MessageType</key><string>Result</string><key>Number</key><integer>0</integer>");
                Assert.IsType<Welcome>(await ReadControlAsync(stream));
                await SendControlAsync(stream, new Hello { DeviceName = "test phone", Capabilities = ["h264", "camera_controls_v1"] });
                var request = Assert.IsType<CameraSettings>(await ReadControlAsync(stream));
                Assert.Equal(9, request.RequestId);
                await SendControlAsync(stream, new StartStream());
                await SendControlAsync(stream, new CameraState { RequestId = request.RequestId, CameraId = "front", Error = "Torch unavailable" });
                await release.Task.WaitAsync(TimeSpan.FromSeconds(10));
            });
        });
        await using var manager = new ConnectionManager(new TcpUsbTransport(new UsbMuxClient(service.Port), TimeSpan.FromSeconds(1)));
        manager.StateChanged += (_, state) =>
        {
            if (state == TransportState.Connected)
                _ = manager.SetCameraSettingsAsync(new CameraSettings { RequestId = 9, CameraId = "front", Values = new() { ["torch_on"] = 1 } });
        };
        manager.CameraStateReceived += (_, state) => confirmed.TrySetResult(state);
        await manager.StartAsync();
        try
        {
            var state = await confirmed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(manager.SupportsCameraSettings);
            Assert.True(manager.IsStreaming);
            Assert.Equal("Torch unavailable", state.Error);
        }
        finally { release.TrySetResult(true); }
        await server;
    }

    [Fact]
    public async Task ReconfigureStreamingPhoneResumesAndIgnoresOldAcknowledgement()
    {
        using var service = new FakeAppleService();
        await using var manager = new ConnectionManager(new TcpUsbTransport(new UsbMuxClient(service.Port), TimeSpan.FromSeconds(1)));
        var resumed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var acknowledgements = new List<string>();
        manager.StateChanged += (_, state) => { if (state == TransportState.Connected) _ = manager.ConfigureAsync(); };
        manager.ConfigurationAcknowledged += (_, ack) => acknowledgements.Add(ack.Resolution);
        var starts = 0;
        manager.StreamStateChanged += (_, streaming) => {
            if (streaming && Interlocked.Increment(ref starts) == 1) _ = manager.ConfigureAsync("3840x2160", 30, bitrate: 20_000_000);
            else if (streaming) resumed.TrySetResult(true);
        };
        var server = Task.Run(async () => {
            await DiscoveryAsync(service);
            await service.ServeOnceAsync(async (_, stream) => {
                await FakeAppleService.ReplyAsync(stream, "<key>MessageType</key><string>Result</string><key>Number</key><integer>0</integer>");
                Assert.IsType<Welcome>(await ReadControlAsync(stream));
                await SendControlAsync(stream, new Hello { Capabilities = ["h264"] });
                var first = Assert.IsType<Configure>(await ReadControlAsync(stream));
                await SendControlAsync(stream, new ConfigureAck { RequestId = first.RequestId, Resolution = first.Resolution, Fps = first.Fps });
                Assert.IsType<StartStream>(await ReadControlAsync(stream));
                await SendControlAsync(stream, new StartStream());
                var second = Assert.IsType<Configure>(await ReadControlAsync(stream));
                Assert.Equal("3840x2160", second.Resolution); Assert.True(second.RequestId > first.RequestId);
                await SendControlAsync(stream, new ConfigureAck { RequestId = first.RequestId, Resolution = first.Resolution, Fps = first.Fps });
                await SendControlAsync(stream, new ConfigureAck { RequestId = second.RequestId, Resolution = second.Resolution, Fps = second.Fps });
                Assert.IsType<StartStream>(await ReadControlAsync(stream));
                await SendControlAsync(stream, new StartStream());
                await resumed.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await AssertNoControlAsync(stream);
            });
        });
        await manager.StartAsync(); await server;
        Assert.Equal(new[] { "1920x1080", "3840x2160" }, acknowledgements);
    }

    private static async Task DiscoveryAsync(FakeAppleService service)
    {
        await service.ServeOnceAsync((_, stream) => FakeAppleService.ReplyAsync(stream, """
            <key>DeviceList</key><array><dict><key>DeviceID</key><integer>7</integer>
            <key>Properties</key><dict><key>ConnectionType</key><string>USB</string>
            <key>SerialNumber</key><string>fake-phone</string></dict></dict></array>
            """));
        await service.ServeOnceAsync((_, stream) => FakeAppleService.ReplyAsync(stream,
            "<key>PairRecordData</key><data>AQ==</data>"));
    }

    private static async Task SendControlAsync(NetworkStream stream, ControlMessage message)
    {
        var json = message.ToJsonBytes();
        var packet = new byte[json.Length + 2];
        json.CopyTo(packet, 1); packet[^1] = 10;
        await stream.WriteAsync(packet);
    }

    private static async Task<ControlMessage?> ReadControlAsync(NetworkStream stream)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var first = new byte[1];
        await stream.ReadExactlyAsync(first, timeout.Token);
        Assert.Equal(0, first[0]);
        var json = new List<byte>();
        do
        {
            await stream.ReadExactlyAsync(first, timeout.Token);
            if (first[0] != 10) json.Add(first[0]);
        } while (first[0] != 10 && json.Count < 65_536);
        return ControlMessage.FromJson(json.ToArray());
    }

    private static async Task AssertNoControlAsync(NetworkStream stream)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            var data = new byte[1];
            var received = await stream.ReadAsync(data, timeout.Token);
            Assert.Fail($"Expected no new command, received {received} bytes.");
        });
    }
}
