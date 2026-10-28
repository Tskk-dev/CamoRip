using System.Net;
using System.Net.Sockets;

namespace CamoRip.Core.Slider;

public sealed class SliderServer(SliderMemory memory, int port, bool allowNetwork = false)
{
    public event Action<string>? Status;
    private readonly SemaphoreSlim writes = new(1, 1);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var listener = new TcpListener(allowNetwork ? IPAddress.Any : IPAddress.Loopback, port);
        listener.Start(1);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                using var client = await listener.AcceptTcpClientAsync(cancellationToken);
                await HandleClientAsync(client, cancellationToken);
            }
        }
        finally { listener.Stop(); memory.Release(); }
    }

    public async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        client.NoDelay = true;
        // ADB's local TCP peer can stall small writes while waiting for an ACK.
        try { client.Client.IOControl(unchecked((int)0x98000017), BitConverter.GetBytes(1), null); }
        catch (SocketException error) { Status?.Invoke($"Immediate TCP acknowledgements unavailable: {error.SocketErrorCode}"); }
        using var session = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var stream = client.GetStream();
        Status?.Invoke("Connected");
        Task input = receive(stream, session.Token);
        Task leds = sendLeds(stream, session.Token);
        try
        {
            Task completed = await Task.WhenAny(input, leds);
            await completed;
        }
        catch (Exception error) when (error is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
            Status?.Invoke("Waiting for tablet");
        }
        finally
        {
            session.Cancel();
            client.Close();
            memory.Release();
            try { await Task.WhenAll(input, leds); }
            catch (Exception error) when (error is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
        }
    }

    private async Task receive(NetworkStream stream, CancellationToken cancellationToken)
    {
        byte[] length = new byte[1];
        bool? ready = null;
        long frames = 0;
        long nextReport = Environment.TickCount64;
        while (!cancellationToken.IsCancellationRequested)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(500);
            await stream.ReadExactlyAsync(length, deadline.Token);
            if (length[0] is < 3 or > 127) throw new InvalidDataException("Invalid controller packet length.");
            byte[] packet = new byte[length[0] + 1];
            packet[0] = length[0];
            await stream.ReadExactlyAsync(packet.AsMemory(1), deadline.Token);
            bool applied = memory.Apply(packet);
            if (packet.Length == 42)
            {
                byte[] ack = [7, (byte)'A', (byte)'C', (byte)'K', packet[4], packet[5], packet[6], packet[7]];
                await send(stream, ack, cancellationToken);
            }
            frames++;
            if (ready != applied || Environment.TickCount64 >= nextReport)
            {
                Status?.Invoke(applied ? "Connected" : "Waiting for game");
                ready = applied;
                nextReport = Environment.TickCount64 + 2000;
            }
        }
    }

    public async Task RunUdpAsync(CancellationToken cancellationToken)
    {
        using var udp = new UdpClient(new IPEndPoint(allowNetwork ? IPAddress.Any : IPAddress.Loopback, port));
        IPEndPoint? owner = null;
        long updated = 0, lastLed = 0;
        uint sequence = 0;
        string? status = null;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                deadline.CancelAfter(500);
                UdpReceiveResult result;
                try { result = await udp.ReceiveAsync(deadline.Token); }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                { memory.Release(); owner = null; Status?.Invoke("Waiting for tablet"); continue; }
                if (owner != null && !owner.Equals(result.RemoteEndPoint) && Environment.TickCount64 - updated < 500) continue;
                byte[] packet = result.Buffer;
                if (packet.Length < 4 || packet[0] + 1 != packet.Length) continue;
                if (packet.Length == 42)
                {
                    uint next = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(4, 4));
                    if (owner?.Equals(result.RemoteEndPoint) == true && unchecked((int)(next - sequence)) <= 0) continue;
                    sequence = next;
                }
                bool applied;
                try { applied = memory.Apply(packet); } catch (InvalidDataException) { continue; }
                owner = result.RemoteEndPoint; updated = Environment.TickCount64;
                if (packet.Length == 42)
                    await udp.SendAsync(new byte[] { 7, (byte)'A', (byte)'C', (byte)'K', packet[4], packet[5], packet[6], packet[7] }, owner, cancellationToken);
                if (updated - lastLed >= 33)
                {
                    await udp.SendAsync(memory.LedFrame(), owner, cancellationToken);
                    lastLed = updated;
                    string nextStatus = applied ? "Connected" : "Waiting for game";
                    if (nextStatus != status) { status = nextStatus; Status?.Invoke(status); }
                }
            }
        }
        finally { memory.Release(); }
    }

    private async Task sendLeds(NetworkStream stream, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await send(stream, memory.LedFrame(), cancellationToken);
            await Task.Delay(33, cancellationToken);
        }
    }

    private async Task send(NetworkStream stream, byte[] packet, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(500);
        await writes.WaitAsync(deadline.Token);
        try { await stream.WriteAsync(packet, deadline.Token); }
        finally { writes.Release(); }
    }
}
