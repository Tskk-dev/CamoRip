using System.Net;
using System.Net.Sockets;

namespace CamoRip.SliderBridge;

public sealed class SliderServer(SliderMemory memory, int port)
{
    public event Action<string>? Status;
    private readonly SemaphoreSlim writes = new(1, 1);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
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
        using var session = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var stream = client.GetStream();
        Status?.Invoke("Tablet connected; waiting for slider input.");
        Task input = receive(stream, session.Token);
        Task leds = sendLeds(stream, session.Token);
        try
        {
            Task completed = await Task.WhenAny(input, leds);
            await completed;
        }
        catch (Exception error) when (error is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
            Status?.Invoke($"Tablet disconnected: {error.Message}");
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
        byte[] packet = new byte[42];
        bool? ready = null;
        long frames = 0;
        long nextReport = Environment.TickCount64;
        while (!cancellationToken.IsCancellationRequested)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(500);
            await stream.ReadExactlyAsync(packet, deadline.Token);
            bool applied = memory.Apply(packet);
            byte[] ack = [7, (byte)'A', (byte)'C', (byte)'K', packet[4], packet[5], packet[6], packet[7]];
            await send(stream, ack, cancellationToken);
            frames++;
            if (ready != applied || Environment.TickCount64 >= nextReport)
            {
                Status?.Invoke(applied ? $"Slider active · received {frames} packets" : "Waiting for the Brokenithm game mapping. Start the game.");
                ready = applied;
                nextReport = Environment.TickCount64 + 2000;
            }
        }
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
