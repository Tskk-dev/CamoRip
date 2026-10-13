using CamoRip.Core.Protocol;

namespace CamoRip.Core.Transport;

/// <summary>One active controller. USB preempts Wi-Fi; stale events cannot retire a new link.</summary>
public sealed class AutomaticTransport : IConnectionTransport
{
    public event EventHandler<ControlMessage>? ControlMessageReceived;
    public event EventHandler<MediaFrame>? FrameReceived;
    public event EventHandler<TransportState>? StateChanged;
    public event EventHandler<string>? Diagnostic;
    public TransportState State { get; private set; }
    public string StatusMessage { get; private set; } = "Connect over USB or pair over Wi-Fi.";
    public TransportType Kind => _active?.Kind ?? TransportType.None;
    public int ConnectionGeneration => Volatile.Read(ref _generation);
    public WirelessTransport Wireless { get; }
    private readonly IConnectionTransport _usb;
    private IConnectionTransport? _active;
    private int _generation;
    private readonly object _gate = new();
    public AutomaticTransport(string directory) : this(new TcpUsbTransport(), new WirelessTransport(directory)) { }
    public AutomaticTransport(IConnectionTransport usb, WirelessTransport wireless)
    {
        _usb = usb; Wireless = wireless; Bind(usb); Bind(wireless);
    }
    private void Bind(IConnectionTransport transport)
    {
        transport.Diagnostic += (_, message) => { lock (_gate) { if (_active is not null && _active != transport) return;
            StatusMessage = message; Diagnostic?.Invoke(this, message); } };
        transport.ControlMessageReceived += (_, message) => { lock (_gate) { if (_active == transport) ControlMessageReceived?.Invoke(this, message); } };
        transport.FrameReceived += (_, frame) => { lock (_gate) { if (_active == transport) FrameReceived?.Invoke(this, frame); } };
        transport.StateChanged += (_, state) =>
        {
            lock (_gate)
            {
                if (state == TransportState.Connected)
                {
                    if (_active == _usb && transport != _usb) { Wireless.DropConnection(); return; }
                    if (_active != transport) { _active = transport; Interlocked.Increment(ref _generation); }
                    if (transport == _usb) Wireless.DropConnection();
                }
                else if (_active != transport) { if (_active is not null) return; }
                else if (state is TransportState.Disconnected or TransportState.Idle or TransportState.Error)
                { _active = null; Interlocked.Increment(ref _generation); }
                State = state; StateChanged?.Invoke(this, state);
            }
        };
    }
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        await _usb.ConnectAsync(ct);
        try { await Wireless.ConnectAsync(ct); }
        catch (Exception ex) { Diagnostic?.Invoke(this, "Wireless could not start: " + ex.Message); }
    }
    public Task SendControlAsync(ControlMessage message, CancellationToken ct = default) => SendControlForConnectionAsync(message, ConnectionGeneration, ct);
    public Task SendControlForConnectionAsync(ControlMessage message, int generation, CancellationToken ct = default)
    {
        lock (_gate) return generation == ConnectionGeneration && _active is { } active
            ? active.SendControlForConnectionAsync(message, active.ConnectionGeneration, ct) : Task.CompletedTask;
    }
    public async ValueTask DisposeAsync() { await _usb.DisposeAsync(); await Wireless.DisposeAsync(); }
}
