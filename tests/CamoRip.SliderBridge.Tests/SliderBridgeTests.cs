using System.IO.MemoryMappedFiles;
using System.Net;
using System.Net.Sockets;
using CamoRip.SliderBridge;

namespace CamoRip.SliderBridge.Tests;

public class SliderBridgeTests
{
    private static byte[] packet(uint mask)
    {
        byte[] packet = new byte[42];
        packet[0] = 41; packet[1] = (byte)'I'; packet[2] = (byte)'P'; packet[3] = (byte)'T';
        for (int sensor = 0; sensor < 32; sensor++) packet[8 + sensor] = (mask & (1u << sensor)) == 0 ? (byte)0 : (byte)128;
        return packet;
    }

    [Fact]
    public void SliderWritesAndReleasesPreserveAirLedsAndServiceButtons()
    {
        string name = @"Local\CamoRip_Slider_Test_" + Guid.NewGuid();
        using var mapping = MemoryMappedFile.CreateNew(name, 150);
        using var view = mapping.CreateViewAccessor();
        view.Write(0, (byte)63); view.Write(38, (byte)42); view.Write(134, (byte)1);
        using (var memory = new SliderMemory(name))
        {
            Assert.True(memory.Apply(packet(0x80000001)));
            Assert.Equal(128, view.ReadByte(6)); Assert.Equal(128, view.ReadByte(37));
            Assert.Equal(42, memory.LedFrame()[4]);
            Assert.Throws<InvalidDataException>(() => memory.Apply(new byte[42]));
            Assert.Equal(128, view.ReadByte(6));
        }
        Assert.Equal(0, view.ReadByte(6)); Assert.Equal(0, view.ReadByte(37));
        Assert.Equal(63, view.ReadByte(0)); Assert.Equal(42, view.ReadByte(38)); Assert.Equal(1, view.ReadByte(134));
    }

    [Fact]
    public async Task TcpReassemblesPacketsSendsLedsAndReleasesOnTimeout()
    {
        string name = @"Local\CamoRip_Slider_Test_" + Guid.NewGuid();
        using var mapping = MemoryMappedFile.CreateNew(name, 150);
        using var view = mapping.CreateViewAccessor();
        view.Write(0, (byte)17); view.Write(38, (byte)123);
        using var memory = new SliderMemory(name);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            using var tablet = new TcpClient();
            await tablet.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, deadline.Token);
            using var peer = await listener.AcceptTcpClientAsync(deadline.Token);
            Task session = new SliderServer(memory, 0).HandleClientAsync(peer, deadline.Token);
            byte[] led = new byte[100];
            await tablet.GetStream().ReadExactlyAsync(led, deadline.Token);
            Assert.Equal(99, led[0]); Assert.Equal(123, led[4]);
            foreach (byte value in packet(0x80000001)) await tablet.GetStream().WriteAsync(new byte[] { value }, deadline.Token);
            await waitFor(() => view.ReadByte(6) == 128 && view.ReadByte(37) == 128, deadline.Token);
            await session.WaitAsync(deadline.Token);
            Assert.Equal(0, view.ReadByte(6)); Assert.Equal(0, view.ReadByte(37));
            Assert.Equal(17, view.ReadByte(0));
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public async Task DisconnectReleasesHeldSliderWithoutTouchingAir()
    {
        string name = @"Local\CamoRip_Slider_Test_" + Guid.NewGuid();
        using var mapping = MemoryMappedFile.CreateNew(name, 150);
        using var view = mapping.CreateViewAccessor();
        view.Write(0, (byte)9);
        using var memory = new SliderMemory(name);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            using var tablet = new TcpClient();
            await tablet.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, deadline.Token);
            using var peer = await listener.AcceptTcpClientAsync(deadline.Token);
            Task session = new SliderServer(memory, 0).HandleClientAsync(peer, deadline.Token);
            await tablet.GetStream().WriteAsync(packet(uint.MaxValue), deadline.Token);
            await waitFor(() => view.ReadByte(6) == 128, deadline.Token);
            tablet.Close();
            await session.WaitAsync(deadline.Token);
            Assert.Equal(0, view.ReadByte(6)); Assert.Equal(9, view.ReadByte(0));
        }
        finally { listener.Stop(); }
    }

    private static async Task waitFor(Func<bool> ready, CancellationToken cancellationToken)
    {
        while (!ready()) await Task.Delay(5, cancellationToken);
    }
}
