using UniversalCam.Core.Protocol;
using UniversalCam.Core.Transport;

namespace UniversalCam.Core;

/// <summary>Local USB/Wi-Fi handshake and video streaming state for the Windows companion.</summary>
public sealed class ConnectionManager : IAsyncDisposable
{
    public event EventHandler<string>? Failure;
    public event EventHandler<CameraState>? CameraStateReceived;
    public bool SupportsCameraSettings { get; private set; }
    public bool SupportsBitrateControl { get; private set; }
    public event EventHandler<MediaFrame>? FrameReceived;
    public event EventHandler<TransportState>? StateChanged;
    public event EventHandler<AvailableCameras>? CamerasReceived;
    public event EventHandler? CameraSwitched;
    public event EventHandler<int>? LatencyUpdated;
    public event EventHandler<double>? RoundTripUpdated;
    public event EventHandler<string>? OrientationChanged;
    public event EventHandler<string>? Diagnostic;
    public event EventHandler<ConfigureAck>? ConfigurationAcknowledged;
    public event EventHandler<bool>? StreamStateChanged;

    public TransportState State { get; private set; } = TransportState.Idle;
    public TransportType ActiveTransport { get; private set; } = TransportType.None;
    public string DeviceName { get; private set; } = "iPhone";
    public string StatusMessage { get; private set; } = "Connect the iPhone over USB.";
    public bool IsStreaming { get; private set; }
    public string CaptureResolution { get; private set; } = string.Empty;
    public int CaptureFrameRate { get; private set; }

    private readonly IConnectionTransport _usb;
    public WirelessTransport? Wireless => (_usb as AutomaticTransport)?.Wireless;
    private bool _started;
    private bool _disposed;
    private volatile bool _desiredStreaming = true;
    private volatile bool _configurationPending;
    private volatile bool _startAfterConfigure;
    private bool _sessionReady;
    private int _generation;
    private int _configurationGeneration;
    private int _transportGeneration;
    private long _configurationRequestId;
    private long _pingToken, _pingSentTicks;

    public ConnectionManager() : this(new AutomaticTransport(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UniversalCam"))) { }

    public ConnectionManager(IConnectionTransport transport)
    {
        _usb = transport ?? throw new ArgumentNullException(nameof(transport));
        _usb.Diagnostic += (_, message) => Report(message);
        _usb.StateChanged += (_, state) => OnTransportStateChanged(state);
        _usb.FrameReceived += (_, frame) =>
        {
            if (ActiveTransport != TransportType.None && IsStreaming && frame.IsVideo)
                FrameReceived?.Invoke(this, frame);
        };
        _usb.ControlMessageReceived += (_, message) => OnControlMessage(message);
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started) return;
        _started = true;
        await _usb.ConnectAsync(ct);
    }

    public Task ConfigureAsync(string resolution = "1920x1080", int fps = 60,
        string codec = "h264", int bitrate = 8_000_000, CancellationToken ct = default)
    {
        if (codec != "h264") throw new ArgumentException("Only H.264 is supported.", nameof(codec));
        if (fps is < 1 or > 60) throw new ArgumentOutOfRangeException(nameof(fps), "Choose 1–60 fps.");
        if (Video.VideoMode.Parse(resolution, fps) is null) throw new ArgumentException("Choose a valid NV12 capture mode up to 4K.", nameof(resolution));
        if (bitrate is < Video.VideoBitrate.Minimum or > Video.VideoBitrate.Maximum) throw new ArgumentOutOfRangeException(nameof(bitrate));
        if (bitrate > 20_000_000 && !SupportsBitrateControl) throw new InvalidOperationException("Update the iPhone app to use bitrates above 20 Mbps.");
        if (ActiveTransport == TransportType.None) return Task.CompletedTask;
        _configurationGeneration = Volatile.Read(ref _generation);
        _configurationPending = true;
        _startAfterConfigure = _desiredStreaming;
        SetStreaming(false);
        return SendAsync(new Configure { RequestId = Interlocked.Increment(ref _configurationRequestId), Resolution = resolution, Fps = fps, Codec = codec, Bitrate = bitrate }, ct);
    }

    public Task StartStreamAsync(CancellationToken ct = default)
    {
        _desiredStreaming = true;
        return SendAsync(new StartStream(), ct);
    }
    public Task StopStreamAsync(CancellationToken ct = default)
    {
        _desiredStreaming = false;
        _startAfterConfigure = false;
        return SendAsync(new StopStream(), ct);
    }
    public Task ListCamerasAsync(CancellationToken ct = default) => SendAsync(new ListCameras(), ct);
    public Task SwitchCameraAsync(string? cameraId, CancellationToken ct = default) =>
        SendAsync(new SwitchCamera { CameraId = cameraId }, ct);
    public Task RequestCameraStateAsync(CancellationToken ct = default) => SupportsCameraSettings ? SendAsync(new GetCameraState(), ct) : Task.CompletedTask;
    public Task SetCameraSettingsAsync(CameraSettings settings, CancellationToken ct = default) => SupportsCameraSettings ? SendAsync(settings, ct) : Task.CompletedTask;
    public Task PingAsync(CancellationToken ct = default)
    {
        var token = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Interlocked.Exchange(ref _pingSentTicks, System.Diagnostics.Stopwatch.GetTimestamp());
        Interlocked.Exchange(ref _pingToken, token);
        return SendAsync(new Ping { Ts = token }, ct);
    }

    private Task SendAsync(ControlMessage message, CancellationToken ct) =>
        ActiveTransport != TransportType.None ? _usb.SendControlForConnectionAsync(message, Volatile.Read(ref _transportGeneration), ct) : Task.CompletedTask;

    private void OnTransportStateChanged(TransportState state)
    {
        if (state == TransportState.Connected)
        {
            var generation = Interlocked.Increment(ref _generation);
            _transportGeneration = _usb.ConnectionGeneration;
            _sessionReady = false;
            _configurationPending = false;
            ActiveTransport = TransportType.None;
            // Transport connection is not ready for use until Hello identifies the app.
            SetState(TransportState.Listening);
            _ = SendHandshakeAsync(generation, _transportGeneration);
            return;
        }
        if (state is TransportState.Disconnected or TransportState.Error or TransportState.Idle)
        {
            Interlocked.Increment(ref _generation);
            _sessionReady = false;
            SupportsCameraSettings = false;
            SupportsBitrateControl = false;
            _configurationPending = false;
            _startAfterConfigure = false;
            ActiveTransport = TransportType.None;
            CaptureResolution = string.Empty;
            CaptureFrameRate = 0;
            SetStreaming(false);
        }
        SetState(state);
        if (state == TransportState.Error) Failure?.Invoke(this, StatusMessage);
    }

    private async Task SendHandshakeAsync(int generation, int transportGeneration)
    {
        try
        {
            if (generation != Volatile.Read(ref _generation)) return;
            await _usb.SendControlForConnectionAsync(new Welcome(), transportGeneration);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        { if (!_disposed) Report("Camera handshake interrupted. Reconnecting to the iPhone."); }
    }

    private void OnControlMessage(ControlMessage message)
    {
        if (message is Hello hello)
        {
            if (_sessionReady) return;
            if (!hello.Capabilities.Contains("h264"))
            {
                Report("The connected app does not support H.264 video. Install the matching UniversalCam IPA.");
                SetState(TransportState.Error);
                return;
            }
            SupportsCameraSettings = hello.Capabilities.Contains("camera_controls_v1");
            SupportsBitrateControl = hello.Capabilities.Contains("bitrate_control_v1");
            DeviceName = string.IsNullOrWhiteSpace(hello.DeviceName) ? "iPhone" : hello.DeviceName;
            _sessionReady = true;
            _desiredStreaming = true;
            ActiveTransport = _usb.Kind;
            SetStreaming(false);
            Report($"{DeviceName} connected over {ActiveTransport}.");
            SetState(TransportState.Connected);
            return;
        }
        if (ActiveTransport == TransportType.None) return;
        switch (message)
        {
            case CameraState cameraState:
                CameraStateReceived?.Invoke(this, cameraState);
                break;
            case ConfigureAck ack:
                if (ack.RequestId > 0 && (!_configurationPending || ack.RequestId != Interlocked.Read(ref _configurationRequestId))) break;
                if (Video.VideoMode.Parse(ack.Resolution, ack.Fps) is null)
                { Failure?.Invoke(this, "The phone acknowledged an invalid capture mode."); break; }
                var generation = Volatile.Read(ref _generation);
                var start = _configurationPending && _configurationGeneration == generation &&
                    _startAfterConfigure && _desiredStreaming;
                _configurationPending = false;
                _startAfterConfigure = false;
                CaptureResolution = ack.Resolution;
                CaptureFrameRate = ack.Fps;
                ConfigurationAcknowledged?.Invoke(this, ack);
                Report($"Capture configured: {ack.Resolution} at {ack.Fps} fps.");
                if (start) _ = StartAfterConfigureAsync(generation, Volatile.Read(ref _transportGeneration));
                break;
            case StartStream:
                _desiredStreaming = true;
                SetStreaming(true);
                Report($"Streaming {CaptureResolution} at {CaptureFrameRate} fps over {ActiveTransport}.");
                break;
            case StopStream:
                _desiredStreaming = false;
                _startAfterConfigure = false;
                SetStreaming(false);
                Report("Streaming stopped. The connection is ready.");
                break;
            case ErrorMessage error:
                Failure?.Invoke(this, error.Message);
                _startAfterConfigure = false;
                SetStreaming(false);
                Report(string.IsNullOrWhiteSpace(error.Code) ? error.Message : $"{error.Code}: {error.Message}");
                break;
            case SwitchCameraAck:
                CameraSwitched?.Invoke(this, EventArgs.Empty);
                break;
            case AvailableCameras cameras:
                CamerasReceived?.Invoke(this, cameras);
                break;
            case Pong pong:
                if (pong.Ts != Interlocked.Read(ref _pingToken)) break;
                var sent = Interlocked.Exchange(ref _pingSentTicks, 0);
                if (sent == 0) break;
                var rtt = System.Diagnostics.Stopwatch.GetElapsedTime(sent).TotalMilliseconds;
                if (rtt is >= 0 and <= 60_000) {
                    RoundTripUpdated?.Invoke(this, rtt);
                    LatencyUpdated?.Invoke(this, (int)(rtt / 2));
                }
                break;
            case Ping ping:
                _ = ReplyPongAsync(ping.Ts);
                break;
            case Protocol.OrientationChanged orientation:
                OrientationChanged?.Invoke(this, orientation.Orientation);
                break;
        }
    }

    private async Task StartAfterConfigureAsync(int generation, int transportGeneration)
    {
        try
        {
            if (generation != Volatile.Read(ref _generation) || !_desiredStreaming) return;
            await _usb.SendControlForConnectionAsync(new StartStream(), transportGeneration);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        { if (!_disposed) Report("Camera connection interrupted before streaming could start."); }
    }

    private async Task ReplyPongAsync(long timestamp)
    {
        try { await SendAsync(new Pong { Ts = timestamp }, CancellationToken.None); }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
    }

    private void SetStreaming(bool streaming)
    {
        if (IsStreaming == streaming) return;
        IsStreaming = streaming;
        StreamStateChanged?.Invoke(this, streaming);
    }

    private void SetState(TransportState state)
    {
        if (State == state) return;
        State = state;
        StateChanged?.Invoke(this, state);
    }

    private void Report(string message)
    {
        StatusMessage = message;
        Diagnostic?.Invoke(this, message);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try { await StopStreamAsync(stopTimeout.Token); }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
        _disposed = true;
        await _usb.DisposeAsync();
    }
}

// Exactly one local transport is active at a time.
public enum TransportType { None, WiFi, USB }
