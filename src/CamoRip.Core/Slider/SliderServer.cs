using System.Net;
using System.Net.Sockets;

namespace CamoRip.Core.Slider;

public sealed class SliderServer(SliderMemory memory, int port, bool allowNetwork = false)
{
    /// <summary>Acknowledgements only feed the tablet's round-trip display, so they are sampled.</summary>
    public const int AckIntervalMilliseconds = 50;
    private const int IdleTimeoutMilliseconds = 500;
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
                // HandleClientAsync contains every per-tablet failure, so one bad session
                // can never stop the listener.
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception error)
        {
            // A timeout, socket error or broken framing ends only this tablet's session.
            // It used to escape (InvalidDataException is not an IOException) and stop the
            // listener until the receiver was toggled.
            Status?.Invoke(error is InvalidDataException ? $"Tablet disconnected: {error.Message}" : "Waiting for tablet");
        }
        finally
        {
            session.Cancel();
            client.Close();
            memory.Release();
            // Both tasks are finished or cancelled; their errors were reported above.
            try { await Task.WhenAll(input, leds); } catch { }
        }
    }

    private async Task receive(NetworkStream stream, CancellationToken cancellationToken)
    {
        byte[] length = new byte[1];
        // Slider packets arrive up to 1000 times per second; reuse their buffer. Apply
        // copies into shared memory before the next read, so reuse is safe.
        byte[]? sliderPacket = null;
        bool? ready = null;
        long nextReport = Environment.TickCount64;
        long lastAck = 0;
        // One idle timer per session, re-armed for each packet, instead of a new linked
        // token source and timer per packet.
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        while (!cancellationToken.IsCancellationRequested)
        {
            idle.CancelAfter(IdleTimeoutMilliseconds);
            await stream.ReadExactlyAsync(length, idle.Token);
            if (length[0] is < 3 or > 127) throw new InvalidDataException("Invalid controller packet length.");
            byte[] packet = length[0] == 41 ? (sliderPacket ??= new byte[42]) : new byte[length[0] + 1];
            packet[0] = length[0];
            await stream.ReadExactlyAsync(packet.AsMemory(1), idle.Token);
            bool applied;
            // A well-framed packet this build does not understand (for example from a newer
            // tablet app) is skipped; only broken framing ends the session.
            try { applied = memory.Apply(packet); }
            catch (InvalidDataException) { continue; }
            long now = Environment.TickCount64;
            if (packet.Length == 42 && now - lastAck >= AckIntervalMilliseconds)
            {
                lastAck = now;
                byte[] ack = [7, (byte)'A', (byte)'C', (byte)'K', packet[4], packet[5], packet[6], packet[7]];
                await send(stream, ack, cancellationToken);
            }
            if (ready != applied || now >= nextReport)
            {
                Status?.Invoke(applied ? "Connected" : "Waiting for game");
                ready = applied;
                nextReport = now + 2000;
            }
        }
    }

    public async Task RunUdpAsync(CancellationToken cancellationToken)
    {
        using var udp = new UdpClient(new IPEndPoint(allowNetwork ? IPAddress.Any : IPAddress.Loopback, port));
        disableConnectionReset(udp.Client);
        IPEndPoint? owner = null;
        long updated = 0, lastLed = 0, lastAck = 0;
        uint sequence = 0;
        string? status = null;
        var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                idle.CancelAfter(IdleTimeoutMilliseconds);
                UdpReceiveResult result;
                try { result = await udp.ReceiveAsync(idle.Token); }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    memory.Release();
                    if (owner != null) Status?.Invoke("Waiting for tablet");
                    // Report "Connected" again when the tablet comes back.
                    owner = null; status = null;
                    idle.Dispose();
                    idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    continue;
                }
                catch (SocketException) when (!cancellationToken.IsCancellationRequested) { continue; }
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
                try
                {
                    if (packet.Length == 42 && updated - lastAck >= AckIntervalMilliseconds)
                    {
                        lastAck = updated;
                        await udp.SendAsync(new byte[] { 7, (byte)'A', (byte)'C', (byte)'K', packet[4], packet[5], packet[6], packet[7] }, owner, cancellationToken);
                    }
                    if (updated - lastLed >= 33)
                    {
                        await udp.SendAsync(memory.LedFrame(), owner, cancellationToken);
                        lastLed = updated;
                        string nextStatus = applied ? "Connected" : "Waiting for game";
                        if (nextStatus != status) { status = nextStatus; Status?.Invoke(status); }
                    }
                }
                // A datagram that cannot be delivered must not end the receiver; the tablet
                // keeps sending and the next send is retried.
                catch (SocketException) when (!cancellationToken.IsCancellationRequested) { }
            }
        }
        finally { idle.Dispose(); memory.Release(); }
    }

    /// <summary>
    /// Windows reports an ICMP "port unreachable" for an earlier send (the tablet app closed
    /// or restarted) as WSAECONNRESET on the next receive, which used to end UDP mode.
    /// </summary>
    private static void disableConnectionReset(Socket socket)
    {
        const int SioUdpConnReset = unchecked((int)0x9800000C);
        try { socket.IOControl(SioUdpConnReset, new byte[4], null); }
        catch (Exception error) when (error is SocketException or PlatformNotSupportedException or NotSupportedException) { }
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
        deadline.CancelAfter(IdleTimeoutMilliseconds);
        await writes.WaitAsync(deadline.Token);
        try { await stream.WriteAsync(packet, deadline.Token); }
        finally { writes.Release(); }
    }
}
