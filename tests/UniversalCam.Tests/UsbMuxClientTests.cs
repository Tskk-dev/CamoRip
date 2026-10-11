using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;
using UniversalCam.Core.Transport;

namespace UniversalCam.Tests;

public class UsbMuxClientTests
{
    [Fact]
    public async Task ListDevices_ExcludesNetworkDevices()
    {
        using var service = new FakeAppleService();
        var server = service.ServeOnceAsync(async (request, stream) =>
        {
            Assert.Equal("ListDevices", FakeAppleService.Value(request, "MessageType"));
            await FakeAppleService.ReplyAsync(stream, """
                <key>DeviceList</key><array>
                <dict><key>DeviceID</key><integer>1</integer><key>Properties</key><dict>
                  <key>ConnectionType</key><string>Network</string><key>SerialNumber</key><string>wifi</string>
                </dict></dict>
                <dict><key>DeviceID</key><integer>2</integer><key>Properties</key><dict>
                  <key>ConnectionType</key><string>USB</string><key>SerialNumber</key><string>usb</string>
                </dict></dict></array>
                """);
        });
        var devices = await new UsbMuxClient(service.Port).ListUsbDevicesAsync();
        Assert.Equal(new UsbMuxDevice(2, "usb"), Assert.Single(devices));
        await server;
    }

    [Fact]
    public async Task Connect_UsesNetworkOrderPort_AndPreservesRawTunnelBytes()
    {
        using var service = new FakeAppleService();
        byte[] raw = [0, 123, 34, 120, 34, 58, 49, 125, 10];
        var server = service.ServeOnceAsync(async (request, stream) =>
        {
            Assert.Equal("Connect", FakeAppleService.Value(request, "MessageType"));
            Assert.Equal("42", FakeAppleService.Value(request, "DeviceID"));
            Assert.Equal("25630", FakeAppleService.Value(request, "PortNumber")); // htons(7780=0x1E64)
            await FakeAppleService.ReplyAsync(stream, "<key>MessageType</key><string>Result</string><key>Number</key><integer>0</integer>", fragment: true);
            await stream.WriteAsync(raw);
        });
        using var tunnel = await new UsbMuxClient(service.Port).OpenTunnelAsync(new UsbMuxDevice(42, "phone"), 7780);
        var bytes = new byte[raw.Length];
        await tunnel.GetStream().ReadExactlyAsync(bytes);
        Assert.Equal(raw, bytes);
        await server;
    }

    [Fact]
    public async Task Connect_Refused_ReturnsActionableAppError()
    {
        using var service = new FakeAppleService();
        var server = service.ServeOnceAsync((_, stream) => FakeAppleService.ReplyAsync(stream,
            "<key>MessageType</key><string>Result</string><key>Number</key><integer>3</integer>"));
        var error = await Assert.ThrowsAsync<UsbMuxException>(() => new UsbMuxClient(service.Port)
            .OpenTunnelAsync(new UsbMuxDevice(1, "phone"), 7780));
        Assert.Equal(3, error.ResultCode);
        Assert.Contains("open UniversalCam", error.Message);
        await server;
    }

    [Fact]
    public async Task PairRecordMissing_ReportsUnpairedWithoutReadingCredentials()
    {
        using var service = new FakeAppleService();
        var server = service.ServeOnceAsync(async (request, stream) =>
        {
            Assert.Equal("ReadPairRecord", FakeAppleService.Value(request, "MessageType"));
            Assert.Equal("test-device", FakeAppleService.Value(request, "PairRecordID"));
            await FakeAppleService.ReplyAsync(stream, "<key>MessageType</key><string>Result</string><key>Number</key><integer>2</integer>");
        });
        Assert.Equal(UsbPairingState.Missing, await new UsbMuxClient(service.Port)
            .CheckPairingAsync(new UsbMuxDevice(2, "test-device")));
        await server;
    }

    [Fact]
    public async Task OversizedReply_IsRejectedBeforeAllocatingPayload()
    {
        using var service = new FakeAppleService();
        var server = service.ServeOnceAsync(async (_, stream) =>
        {
            var header = new byte[16];
            BinaryPrimitives.WriteUInt32LittleEndian(header, uint.MaxValue);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), 1);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), 8);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), 1);
            await stream.WriteAsync(header);
        });
        await Assert.ThrowsAsync<InvalidDataException>(() => new UsbMuxClient(service.Port).ListUsbDevicesAsync());
        await server;
    }

    [Fact]
    public async Task Transport_ReconnectsAfterTunnelCloses_AndDropsAudio()
    {
        using var service = new FakeAppleService();
        var server = Task.Run(async () =>
        {
            for (var session = 0; session < 2; session++)
            {
                await service.ServeOnceAsync((_, stream) => FakeAppleService.ReplyAsync(stream, """
                    <key>DeviceList</key><array><dict><key>DeviceID</key><integer>7</integer>
                    <key>Properties</key><dict><key>ConnectionType</key><string>USB</string>
                    <key>SerialNumber</key><string>fake-phone</string></dict></dict></array>
                    """));
                await service.ServeOnceAsync((_, stream) => FakeAppleService.ReplyAsync(stream,
                    "<key>PairRecordData</key><data>AQ==</data>"));
                await service.ServeOnceAsync(async (_, stream) =>
                {
                    await FakeAppleService.ReplyAsync(stream, "<key>MessageType</key><string>Result</string><key>Number</key><integer>0</integer>");
                    // An old audio packet is recognized but never delivered to this video-only app.
                    var audio = new byte[15]; audio[0] = 2; audio[1] = 1;
                    var video = new byte[15]; video[0] = 1; video[1] = 1;
                    await stream.WriteAsync(audio.Concat(video).ToArray());
                });
            }
        });
        var received = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var frames = 0;
        await using var transport = new TcpUsbTransport(new UsbMuxClient(service.Port), TimeSpan.FromMilliseconds(5));
        transport.FrameReceived += (_, frame) =>
        {
            Assert.True(frame.IsVideo);
            if (Interlocked.Increment(ref frames) == 2) received.TrySetResult(true);
        };
        await transport.ConnectAsync();
        await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2, frames);
        await server;
    }
}

internal sealed class FakeAppleService : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public FakeAppleService() => _listener.Start();

    public async Task ServeOnceAsync(Func<XElement, NetworkStream, Task> reply)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var client = await _listener.AcceptTcpClientAsync(timeout.Token);
        var stream = client.GetStream();
        var header = new byte[16];
        await stream.ReadExactlyAsync(header, timeout.Token);
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4)));
        Assert.Equal(8u, BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8)));
        var length = BinaryPrimitives.ReadUInt32LittleEndian(header) - 16;
        Assert.InRange(length, 1u, 64 * 1024u);
        var data = new byte[length];
        await stream.ReadExactlyAsync(data, timeout.Token);
        var document = XDocument.Parse(Encoding.UTF8.GetString(data));
        await reply(document.Root!.Element("dict")!, stream);
    }

    public static string? Value(XElement dictionary, string key) => dictionary.Elements("key")
        .FirstOrDefault(element => element.Value == key)?.ElementsAfterSelf().FirstOrDefault()?.Value;

    public static async Task ReplyAsync(NetworkStream stream, string dictionaryContents, bool fragment = false)
    {
        var body = Encoding.UTF8.GetBytes($"<?xml version=\"1.0\"?><plist version=\"1.0\"><dict>{dictionaryContents}</dict></plist>");
        var packet = new byte[16 + body.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(packet, (uint)packet.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8), 8);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(12), 1);
        body.CopyTo(packet, 16);
        if (!fragment) { await stream.WriteAsync(packet); return; }
        for (var offset = 0; offset < packet.Length; offset += 3)
            await stream.WriteAsync(packet.AsMemory(offset, Math.Min(3, packet.Length - offset)));
    }

    public void Dispose() => _listener.Stop();
}
