using UniversalCam.Core.Protocol;
using UniversalCam.Core.Transport;

namespace UniversalCam.Core;

/// <summary>USB-only handshake and video streaming state for the Windows companion.</summary>
public sealed class ConnectionManager : IAsyncDisposable
{
    public event EventHandler<CameraState>? CameraStateReceived;
    public bool SupportsCameraSettings { get; private set; }
    public event EventHandler<MediaFrame>? FrameReceived;
    public event EventHandler<TransportState>? StateChanged;
    public event EventHandler<AvailableCameras>? CamerasReceived;
    public event EventHandler? CameraSwitched;
    public event EventHandler<int>? LatencyUpdated;
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

    private readonly TcpUsbTransport _usb;
    private bool _started;
    private bool _disposed;
    private volatile bool _desiredStreaming = true;
    private volatile bool _configurationPending;
    private volatile bool _startAfterConfigure;
    private volatile bool _configured;
    private bool _sessionReady;
    private int _generation;
    private int _configurationGeneration;
    private int _transportGeneration;

    public ConnectionManager() : this(new TcpUsbTransport()) { }

    public ConnectionManager(TcpUsbTransport transport)
    {
        _usb = transport ?? throw new ArgumentNullException(nameof(transport));
        _usb.Diagnostic += (_, message) => Report(message);
        _usb.StateChanged += (_, state) => OnTransportStateChanged(state);
        _usb.FrameReceived += (_, frame) =>
        {
            if (ActiveTransport == TransportType.USB && IsStreaming && frame.IsVideo)
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
        if (bitrate is < 100_000 or > 20_000_000) throw new ArgumentOutOfRangeException(nameof(bitrate));
        if (ActiveTransport != TransportType.USB) return Task.CompletedTask;
        _configurationGeneration = Volatile.Read(ref _generation);
        _configurationPending = true;
        _startAfterConfigure = !_configured && _desiredStreaming;
        return SendAsync(new Configure { Resolution = resolution, Fps = fps, Codec = codec, Bitrate = bitrate }, ct);
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
    public Task PingAsync(CancellationToken ct = default) =>
        SendAsync(new Ping { Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }, ct);

    private Task SendAsync(ControlMessage message, CancellationToken ct) =>
        ActiveTransport == TransportType.USB ? _usb.SendControlForConnectionAsync(message, Volatile.Read(ref _transportGeneration), ct) : Task.CompletedTask;

    private void OnTransportStateChanged(TransportState state)
    {
        if (state == TransportState.Connected)
        {
            var generation = Interlocked.Increment(ref _generation);
            _transportGeneration = _usb.ConnectionGeneration;
            _sessionReady = false;
            _configured = false;
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
            _configurationPending = false;
            _startAfterConfigure = false;
            ActiveTransport = TransportType.None;
            CaptureResolution = string.Empty;
            CaptureFrameRate = 0;
            SetStreaming(false);
        }
        SetState(state);
    }

    private async Task SendHandshakeAsync(int generation, int transportGeneration)
    {
        try
        {
            if (generation != Volatile.Read(ref _generation)) return;
            await _usb.SendControlForConnectionAsync(new Welcome(), transportGeneration);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        { if (!_disposed) Report("USB handshake interrupted. Reconnecting to the iPhone."); }
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
            DeviceName = string.IsNullOrWhiteSpace(hello.DeviceName) ? "iPhone" : hello.DeviceName;
            _sessionReady = true;
            _desiredStreaming = true;
            ActiveTransport = TransportType.USB;
            SetStreaming(false);
            Report($"{DeviceName} connected over USB.");
            SetState(TransportState.Connected);
            return;
        }
        if (ActiveTransport != TransportType.USB) return;
        switch (message)
        {
            case CameraState cameraState:
                CameraStateReceived?.Invoke(this, cameraState);
                break;
            case ConfigureAck ack:
                var generation = Volatile.Read(ref _generation);
                var start = _configurationPending && _configurationGeneration == generation &&
                    _startAfterConfigure && _desiredStreaming;
                _configurationPending = false;
                _startAfterConfigure = false;
                _configured = true;
                CaptureResolution = ack.Resolution;
                CaptureFrameRate = ack.Fps;
                ConfigurationAcknowledged?.Invoke(this, ack);
                Report($"Capture configured: {ack.Resolution} at {ack.Fps} fps.");
                if (start) _ = StartAfterConfigureAsync(generation, Volatile.Read(ref _transportGeneration));
                break;
            case StartStream:
                _desiredStreaming = true;
                SetStreaming(true);
                Report($"Streaming {CaptureResolution} at {CaptureFrameRate} fps over USB.");
                break;
            case StopStream:
                _desiredStreaming = false;
                _startAfterConfigure = false;
                SetStreaming(false);
                Report("Streaming stopped. The USB connection is ready.");
                break;
            case ErrorMessage error:
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
                var rtt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - pong.Ts;
                if (rtt is >= 0 and <= 60_000) LatencyUpdated?.Invoke(this, (int)(rtt / 2));
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
        { if (!_disposed) Report("USB connection interrupted before streaming could start."); }
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

// WiFi is retained as an enum value for source compatibility; no Wi-Fi transport is created.
public enum TransportType { None, WiFi, USB }
