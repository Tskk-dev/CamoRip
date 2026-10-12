using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text.Json;
using Makaretu.Dns;
using UniversalCam.Core.Protocol;

namespace UniversalCam.Core.Transport;

/// <summary>Opt-in local TLS receiver. Authentication completes before video/control parsing.</summary>
public sealed class WirelessTransport : IConnectionTransport
{
    public const int Port = 7781;
    public event EventHandler<ControlMessage>? ControlMessageReceived;
    public event EventHandler<MediaFrame>? FrameReceived;
    public event EventHandler<TransportState>? StateChanged;
    public event EventHandler<string>? Diagnostic;
    public event EventHandler? PairingCompleted;
    public TransportState State { get; private set; }
    public string StatusMessage { get; private set; } = "Wireless is off. Pair a phone to enable it.";
    public TransportType Kind => TransportType.WiFi;
    public int ConnectionGeneration => Volatile.Read(ref _generation);
    private readonly string _directory;
    private readonly int _port;
    private readonly bool _advertise;
    public int ListeningPort => (_listener?.LocalEndpoint as IPEndPoint)?.Port ?? _port;
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private WirelessIdentity? _identity;
    private TcpListener? _listener;
    private TcpClient? _client;
    private SslStream? _stream;
    private Task? _run;
    private MulticastService? _mdns;
    private ServiceDiscovery? _discovery;
    private int _generation;
    private bool _disposed;
    public bool Enabled => _listener is not null;

    public WirelessTransport(string directory, int port = Port, bool advertise = true) { _directory = directory; _port = port; _advertise = advertise; }
    public Task ConnectAsync(CancellationToken ct = default)
    {
        if (File.Exists(Path.Combine(_directory, "wireless-identity.bin"))) Enable();
        return Task.CompletedTask;
    }
    public void Enable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Enabled) return;
        _identity ??= WirelessIdentity.Load(_directory);
        var listener = new TcpListener(IPAddress.Any, _port);
        listener.Start(4); _listener = listener;
        SetState(TransportState.Listening);
        _run = RunAsync(listener, _shutdown.Token);
        try
        {
            if (!_advertise) return;
            _mdns = new MulticastService(); _discovery = new ServiceDiscovery(_mdns);
            var profile = new ServiceProfile(_identity.ServiceName, "_camorip._tcp", checked((ushort)ListeningPort));
            profile.AddProperty("id", _identity.Id);
            _discovery.Advertise(profile); _mdns.Start(); _discovery.Announce(profile);
        }
        catch { Report("Local discovery unavailable. QR address fallback remains available; check the Private-network firewall rule."); }
    }
    public string BeginPairing() { Enable(); return _identity!.CreatePairingCode(ListeningPort); }
    public void CancelPairing() => _identity?.CancelPairing();
    public void ForgetDevices() { _identity?.ForgetDevices(); DropConnection(); }
    public void DropConnection() => _client?.Dispose();

    private async Task RunAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            bool authenticated = false;
            try
            {
                using var client = await listener.AcceptTcpClientAsync(ct);
                if (client.Client.RemoteEndPoint is not IPEndPoint peer || !WirelessIdentity.IsLocalPeer(peer.Address)) continue;
                _client = client; client.NoDelay = true;
                using var tls = new SslStream(client.GetStream(), false);
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    timeout.CancelAfter(TimeSpan.FromSeconds(8));
                    await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions {
                        ServerCertificate = _identity!.Certificate, EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                        ClientCertificateRequired = false }, timeout.Token);
                    var request = JsonSerializer.Deserialize<AuthRequest>(await ReadLineAsync(tls, timeout.Token));
                    if (request is null || request.v != 1 || request.pc != _identity.Id) continue;
                    var token = _identity.Authenticate(request.token ?? "", request.pair);
                    if (token is null) { await tls.WriteAsync("{\"ok\":false}\n"u8.ToArray(), timeout.Token); continue; }
                    await tls.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(new { ok = true, token }).Concat(new byte[] { 10 }).ToArray(), timeout.Token);
                    if (request.pair) PairingCompleted?.Invoke(this, EventArgs.Empty);
                }
                _stream = tls; Interlocked.Increment(ref _generation); authenticated = true;
                Report("Paired iPhone connected over encrypted Wi-Fi."); SetState(TransportState.Connected);
                var parser = new FrameParser();
                parser.ControlMessageParsed += (_, message) => ControlMessageReceived?.Invoke(this, message);
                parser.FrameParsed += (_, frame) => { if (frame.IsVideo) FrameReceived?.Invoke(this, frame); };
                var buffer = new byte[64 * 1024];
                while (!ct.IsCancellationRequested)
                {
                    var count = await tls.ReadAsync(buffer, ct);
                    if (count == 0) break;
                    parser.Feed(buffer.AsSpan(0, count));
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) when (ex is IOException or SocketException or AuthenticationException or OperationCanceledException or ObjectDisposedException or JsonException or InvalidOperationException)
            { Report(authenticated ? "Wi-Fi interrupted. Waiting for the paired phone to reconnect." : "Wireless authentication did not complete. Rescan an unexpired QR code if needed."); }
            finally
            {
                _stream = null; _client = null;
                if (authenticated && !ct.IsCancellationRequested) SetState(TransportState.Disconnected);
            }
        }
    }
    internal static async Task<byte[]> ReadLineAsync(Stream stream, CancellationToken ct)
    {
        using var data = new MemoryStream(); var b = new byte[1];
        while (data.Length < 4096)
        {
            if (await stream.ReadAsync(b, ct) == 0) throw new IOException("Authentication interrupted.");
            if (b[0] == 10) return data.ToArray();
            data.WriteByte(b[0]);
        }
        throw new InvalidDataException("Authentication message too large.");
    }
    private sealed record AuthRequest(int v, string pc, string token, bool pair);
    public Task SendControlAsync(ControlMessage message, CancellationToken ct = default) => SendControlForConnectionAsync(message, ConnectionGeneration, ct);
    public async Task SendControlForConnectionAsync(ControlMessage message, int generation, CancellationToken ct = default)
    {
        var stream = _stream; if (stream is null || generation != ConnectionGeneration) return;
        var json = message.ToJsonBytes();
        if (json.Length > FrameParser.MaxControlMessageLength) throw new InvalidDataException("Control message too large.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _shutdown.Token);
        await _write.WaitAsync(linked.Token);
        try
        {
            if (!ReferenceEquals(stream, _stream) || generation != ConnectionGeneration) return;
            await stream.WriteAsync(new byte[] { 0 }.Concat(json).Concat(new byte[] { 10 }).ToArray(), linked.Token);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { DropConnection(); }
        finally { _write.Release(); }
    }
    private void Report(string message) { StatusMessage = message; Diagnostic?.Invoke(this, message); }
    private void SetState(TransportState state) { State = state; StateChanged?.Invoke(this, state); }
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return; _disposed = true; _shutdown.Cancel(); _listener?.Stop(); DropConnection();
        if (_run is not null) { try { await _run; } catch (OperationCanceledException) { } }
        _discovery?.Dispose(); _mdns?.Dispose(); _identity?.Dispose(); _shutdown.Dispose();
        await _write.WaitAsync(); _write.Release(); _write.Dispose();
    }
}
