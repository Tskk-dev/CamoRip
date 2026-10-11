using System.Text;
using System.Net.Sockets;
using UniversalCam.Core;
using UniversalCam.Core.Protocol;
using UniversalCam.Core.Transport;

namespace UniversalCam.Tests;

public class ConnectionManagerTests
{
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
