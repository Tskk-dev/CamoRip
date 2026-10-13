using System.Net.Sockets;
using CamoRip.Core.Protocol;

namespace CamoRip.Core.Transport;

/// <summary>
/// Connects Apple's local USB multiplexing service directly to the iPhone app.
/// No forwarding process, external server, LAN socket, or network device is used.
/// </summary>
public sealed class TcpUsbTransport : IConnectionTransport
{
    public const ushort Port = 7780;
    public TransportType Kind => TransportType.USB;
    public event EventHandler<ControlMessage>? ControlMessageReceived;
    public event EventHandler<MediaFrame>? FrameReceived;
    public event EventHandler<TransportState>? StateChanged;
    public event EventHandler<string>? Diagnostic;
    public TransportState State { get; private set; } = TransportState.Idle;
    public string StatusMessage { get; private set; } = "Connect an iPhone with a USB cable.";
    public int ConnectionGeneration => Volatile.Read(ref _connectionGeneration);

    private readonly FrameParser _parser = new();
    private readonly UsbMuxClient _mux;
    private readonly TimeSpan _retryDelay;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly object _lifetimeLock = new();
    private CancellationTokenSource? _cts;
    private Task? _runTask;
    private TcpClient? _client;
    private NetworkStream? _stream;
    private bool _disposed;
    private int _connectionGeneration;

    public TcpUsbTransport() : this(new UsbMuxClient(), TimeSpan.FromSeconds(2)) { }

    public TcpUsbTransport(UsbMuxClient mux, TimeSpan retryDelay)
    {
        _mux = mux ?? throw new ArgumentNullException(nameof(mux));
        if (retryDelay < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(retryDelay));
        _retryDelay = retryDelay;
        _parser.ControlMessageParsed += (_, message) => ControlMessageReceived?.Invoke(this, message);
        // The protocol can identify old audio packets; this product forwards video only.
        _parser.FrameParsed += (_, frame) => { if (frame.IsVideo) FrameReceived?.Invoke(this, frame); };
    }

    public Task ConnectAsync(CancellationToken ct = default)
    {
        lock (_lifetimeLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_runTask is { IsCompleted: false }) return Task.CompletedTask;
            _cts?.Dispose();
            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            SetState(TransportState.Listening);
            _runTask = ConnectLoopAsync(_cts.Token);
        }
        return Task.CompletedTask;
    }

    private async Task ConnectLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var connected = false;
            try
            {
                var devices = await _mux.ListUsbDevicesAsync(ct);
                if (devices.Count == 0)
                {
                    Report("No iPhone found over USB. Connect a data cable, unlock the phone, and choose Trust This Computer in Apple Devices.");
                    SetState(TransportState.Listening);
                }
                else
                {
                    // Try each USB device. Network-paired devices never enter this list.
                    Exception? lastError = null;
                    foreach (var device in devices)
                    {
                        try
                        {
                            var pairing = await _mux.CheckPairingAsync(device, ct);
                            if (pairing == UsbPairingState.Missing)
                            {
                                lastError = new IOException("This iPhone has no pairing record. Unlock it and choose Trust This Computer in Apple Devices or iTunes.");
                                continue;
                            }
                            _client = await _mux.OpenTunnelAsync(device, Port, ct);
                            _stream = _client.GetStream();
                            Interlocked.Increment(ref _connectionGeneration);
                            connected = true;
                            break;
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                        catch (Exception ex) { lastError = ex; }
                    }
                    if (!connected) throw lastError ?? new IOException("No iPhone app is accepting a USB connection.");
                    _parser.Reset();
                    Report("USB connected. Waiting for the iPhone app.");
                    SetState(TransportState.Connected);
                    await ReceiveLoopAsync(_stream!, ct);
                    Report("USB connection interrupted. Reconnect the cable and keep CamoRip open on the iPhone.");
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (OperationCanceledException)
            {
                Report("Apple USB service timed out. Unlock the phone and open Apple Devices or iTunes.");
                SetState(TransportState.Error);
            }
            catch (SocketException)
            {
                Report("Apple USB service is unavailable. Install or repair Apple Devices or iTunes and start Apple Mobile Device Service.");
                SetState(TransportState.Error);
            }
            catch (Exception ex)
            {
                Report(ex.Message);
                SetState(TransportState.Error);
            }
            finally
            {
                _stream = null;
                _client?.Dispose();
                _client = null;
                _parser.Reset();
                if (connected && !ct.IsCancellationRequested && State != TransportState.Error)
                    SetState(TransportState.Disconnected);
            }
            try { await Task.Delay(_retryDelay, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    public async Task SendControlAsync(ControlMessage message, CancellationToken ct = default)
        => await SendControlForConnectionAsync(message, ConnectionGeneration, ct);

    public async Task SendControlForConnectionAsync(ControlMessage message, int generation, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (generation != ConnectionGeneration) return;
        var stream = _stream;
        if (stream is null) return;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts?.Token ?? CancellationToken.None);
        var json = message.ToJsonBytes();
        if (json.Length > FrameParser.MaxControlMessageLength) throw new InvalidDataException("Control message is too large.");
        var packet = new byte[json.Length + 2];
        packet[0] = FrameHeader.StreamTypeControl;
        json.CopyTo(packet, 1);
        packet[^1] = 0x0A;
        await _writeLock.WaitAsync(lifetime.Token);
        try
        {
            if (generation != ConnectionGeneration || !ReferenceEquals(_stream, stream)) return;
            await stream.WriteAsync(packet, lifetime.Token);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            Report("USB send interrupted. Reconnecting to the iPhone.");
            _client?.Dispose();
        }
        finally { _writeLock.Release(); }
    }

    private async Task ReceiveLoopAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        while (!ct.IsCancellationRequested)
        {
            var read = await stream.ReadAsync(buffer, ct);
            if (read == 0) return;
            _parser.Feed(buffer.AsSpan(0, read));
        }
    }

    private void Report(string message)
    {
        if (StatusMessage == message) return;
        StatusMessage = message;
        Diagnostic?.Invoke(this, message);
    }

    private void SetState(TransportState state)
    {
        if (State == state) return;
        State = state;
        StateChanged?.Invoke(this, state);
    }

    public async ValueTask DisposeAsync()
    {
        Task? run;
        lock (_lifetimeLock)
        {
            if (_disposed) return;
            _disposed = true;
            _cts?.Cancel();
            _client?.Dispose();
            run = _runTask;
        }
        if (run is not null)
        {
            try { await run; }
            catch (OperationCanceledException) { }
        }
        await _writeLock.WaitAsync();
        _writeLock.Release();
        _writeLock.Dispose();
        _cts?.Dispose();
        SetState(TransportState.Idle);
    }
}
