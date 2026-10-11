
using System.Diagnostics;
using System.Threading.Channels;



using UniversalCam.Core;
using UniversalCam.Core.Protocol;
using UniversalCam.Core.Transport;
using UniversalCam.Core.Video;
using UniversalCam.VirtualCamera;

namespace UniversalCam;

internal sealed class StreamingService : IAsyncDisposable
{
    private readonly ConnectionManager _connection = new();
    private readonly H264Decoder _decoder = new(reuseFrameBuffers: true);
    private readonly CompressedVideoQueue _frames = new();
    // Only independent decoded frames may be coalesced. Slow consumers get the
    // newest image without blocking H.264 input or accumulating video latency.
    private readonly LatestFrameMailbox<(DecodedFrame Frame, long Generation)> _cameraFramesQueue;
    private readonly Channel<(DecodedFrame Frame, long Generation)> _previewFramesQueue;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly StageTiming _decodeTiming = new(), _cameraTiming = new(), _previewTiming = new();
    private VirtualCameraSession? _camera;
    private Task? _decodeTask, _cameraTask, _metricsTask;


    private bool _streaming;
    private volatile bool _closing;
    private bool _closed;
    private int _decodedWidth, _decodedHeight;
    private long _lastPreviewTicks;
    private long _decodingGeneration;
    private long _receivedFrames, _decodedFrames, _cameraFrames, _previewFrames;
    private long _cameraCoalesced, _previewCoalesced, _publishFailures;
    private long _lastUsbTicks, _lastDecodedTicks, _lastCameraTicks;
    private long _lastMetricsTicks, _lastReceivedFrames, _lastDecodedFrames, _lastCameraFrames, _lastPreviewFrames;
    private long _lastQueueDrops, _lastRecoveries, _lastCameraCoalesced, _lastPreviewCoalesced, _lastPublishFailures;
    private TimeSpan _lastCpuTime;
    private long _lastAllocatedBytes;
    private readonly int[] _lastCollections = new int[3];

    public StreamingService()
    {
        _cameraFramesQueue = new(queued => { queued.Frame.Release(); Interlocked.Increment(ref _cameraCoalesced); });
        _previewFramesQueue = LatestFrames(() => Interlocked.Increment(ref _previewCoalesced));
    }

    public async Task StartAsync()
    {
        try
        {
            _camera = new VirtualCameraSession();
            _camera.StatusChanged += (_, status) =>
            {
                App.Log("INFO", $"Virtual camera: {status}");
                Ui(() => SetStatus("CameraStatus", status));
            };
            SetStatus("CameraStatus", _camera.StatusMessage);
            App.Log("INFO", $"Virtual camera status: {_camera.StatusMessage}");
        }
        catch (Exception ex)
        {
            SetStatus("CameraStatus", $"Windows webcam could not start: {ex.Message}");
            App.Log(ex);
        }

        _decoder.BackendChanged += (_, status) =>
        {
            App.Log("INFO", $"Decoder backend: {status}");
            Ui(() => SetStatus("DecoderStatus", status.StartsWith("D3D11 hardware confirmed")
                ? "Video decoding: GPU accelerated"
                : status.StartsWith("software") ? "Video decoding: CPU fallback (see diagnostic logs)"
                : "Video decoding: starting GPU decoder"));
        };
        _decoder.FrameDecoded += (_, frame) =>
        {
            var generation = Interlocked.Read(ref _decodingGeneration);
            if (!IsCurrent(generation)) return;
            Interlocked.Increment(ref _decodedFrames);
            Interlocked.Exchange(ref _lastDecodedTicks, Stopwatch.GetTimestamp());
            Volatile.Write(ref _decodedWidth, frame.Width);
            Volatile.Write(ref _decodedHeight, frame.Height);
            _cameraFramesQueue.Publish((frame.Retain(), generation));
            if (PreviewVisible && PreviewFps > 0)
            {
                var preview = (frame.Retain(), generation);
                if (!_previewFramesQueue.Writer.TryWrite(preview)) preview.Item1.Release();
            }
        };
        _connection.Diagnostic += (_, message) => Ui(() => SetStatus("Diagnostic", message));
        _connection.Diagnostic += (_, message) => App.Log("INFO", $"Connection: {message}");
        _connection.ConfigurationAcknowledged += (_, configuration) => Ui(() =>
            SetStatus("VideoStatus", $"Capture: {configuration.Resolution} at {configuration.Fps} fps · Webcam: 1080p60"));
        _connection.ConfigurationAcknowledged += (_, configuration) => App.Log("INFO", $"Capture configured: {configuration.Resolution} at {configuration.Fps} fps.");
        _connection.StreamStateChanged += (_, streaming) =>
        {
            if (!streaming) _frames.Reset("stream stopped");
            _camera?.OnStreamStateChanged(this, streaming);
            Ui(() => SetStreaming(streaming));
        };
        _connection.CameraSwitched += (_, _) =>
        {
            _frames.Reset("camera switched");
        };
        _connection.StateChanged += OnConnectionState;
        _connection.FrameReceived += (_, frame) =>
        {
            if (!frame.IsVideo || _closing) return;
            Interlocked.Increment(ref _receivedFrames);
            Interlocked.Exchange(ref _lastUsbTicks, Stopwatch.GetTimestamp());
            _frames.Enqueue(frame);
        };
        // A dedicated MTA thread keeps decode independent of thread-pool/UI load.
        _decodeTask = Task.Factory.StartNew(DecodeFrames, CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default);
        _cameraTask = Task.Factory.StartNew(PublishCameraFrames, CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default);
        _metricsTask = Task.Run(CollectMetrics);
        App.Log("INFO", "Pipeline v5: WinUI D3D11 shader preview; pooled NV12 buffers; MMCSS dedicated decoder/publisher; D3D11 hardware preferred; independent latest-frame camera/preview; compressed queue limited to 64 frames, 16 MiB, 500 ms.");
        try { await _connection.StartAsync(); }
        catch (Exception ex) { SetStatus("Diagnostic", ex.Message); App.Log(ex); }
    }

    private void OnConnectionState(object? sender, TransportState state)
    {
        _frames.Reset($"USB state {state}");
        try { _camera?.OnConnectionStateChanged(sender, state); }
        catch (Exception ex) { App.Log(ex); }
        Ui(() =>
        {
            var connected = state == TransportState.Connected;
            Connected = connected; Changed?.Invoke();
            SetStatus("DeviceStatus", connected ? $"{_connection.DeviceName} · USB connected" : "Waiting for iPhone over USB");
            if (!connected)
            {
                SetStreaming(false);

            }
            SetStatus("Diagnostic", _connection.StatusMessage);
            if (connected) _ = ConfigurePhone();
        });
    }

    private async Task ConfigurePhone()
    {
        try { await _connection.ConfigureAsync("1920x1080", 60, "h264", 12_000_000); }
        catch (Exception ex) { Ui(() => SetStatus("Diagnostic", ex.Message)); App.Log(ex); }
    }

    private static Channel<(DecodedFrame Frame, long Generation)> LatestFrames(Action dropped) =>
        Channel.CreateBounded<(DecodedFrame, long)>(new BoundedChannelOptions(1)
        { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.DropOldest }, queued => { queued.Item1.Release(); dropped(); });

    private bool IsCurrent(long generation) => !_closing && _connection.IsStreaming && generation == _frames.Generation;

    private void DecodeFrames()
    {
        long generation = -1;
        using var scheduling = new VideoThreadScheduling();
        App.Log("INFO", $"Decoder scheduling: {scheduling.Status}");
        try
        {
            foreach (var queued in _frames.ReadAll())
            {
                if (_closing) break;
                if (!IsCurrent(queued.Generation)) continue;
                if (generation != queued.Generation)
                {
                    generation = queued.Generation;
                    _decoder.RequestReset();
                    App.Log("INFO", $"Decoder recovery: {queued.ResetReason}; generation {generation}; keyframe={queued.Frame.Header.IsKeyframe}.");
                }
                var started = Stopwatch.GetTimestamp();
                try
                {
                    Interlocked.Exchange(ref _decodingGeneration, generation);
                    _decoder.Feed(queued.Frame);
                }
                catch (Exception ex)
                {
                    _frames.Reset("decoder error");
                    Ui(() => SetStatus("Diagnostic", $"Video decoder is waiting for a new keyframe: {ex.Message}"));
                    App.Log(ex);
                }
                finally { _decodeTiming.Record(started); }
            }
        }
        catch (Exception ex) { App.Log(ex); }
    }

    private void PublishCameraFrames()
    {
        using var scheduling = new VideoThreadScheduling();
        App.Log("INFO", $"Camera publisher scheduling: {scheduling.Status}");
        try
        {
            foreach (var queued in _cameraFramesQueue.ReadAll())
            {
                if (!IsCurrent(queued.Generation)) { queued.Frame.Release(); continue; }
                var started = Stopwatch.GetTimestamp();
                try
                {
                    if (_camera?.TryPublishFrame(queued.Frame) == true)
                    {
                        Interlocked.Increment(ref _cameraFrames);
                        Interlocked.Exchange(ref _lastCameraTicks, Stopwatch.GetTimestamp());
                    }
                    else Interlocked.Increment(ref _publishFailures);
                }
                catch (Exception ex) { Interlocked.Increment(ref _publishFailures); App.Log(ex); }
                finally { _cameraTiming.Record(started); queued.Frame.Release(); }
            }
        }
        catch (Exception ex) { App.Log(ex); }
    }

    // Called on the WinUI display clock. Never block the capture/decoder or publisher.
    public void RenderLatestPreview()
    {
        if (_closing || !PreviewVisible || PreviewFps == 0) return;
        var now = Stopwatch.GetTimestamp();
        if (_lastPreviewTicks != 0 && Stopwatch.GetElapsedTime(_lastPreviewTicks, now).TotalSeconds < 1.0 / PreviewFps - 0.001) return;
        if (!_previewFramesQueue.Reader.TryRead(out var queued)) return;
        try
        {
            if (!IsCurrent(queued.Generation)) return;
            _lastPreviewTicks = now;
            var started = Stopwatch.GetTimestamp();
            PreviewFrame?.Invoke(queued.Frame);
            _previewTiming.Record(started);
            Interlocked.Increment(ref _previewFrames);
        }
        catch (Exception ex) { App.Log(ex); SetStatus("Diagnostic", "Preview: " + ex.Message); }
        finally { queued.Frame.Release(); }
    }

    private async Task CollectMetrics()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        using var process = Process.GetCurrentProcess();
        _lastMetricsTicks = Stopwatch.GetTimestamp();
        _lastCpuTime = process.TotalProcessorTime;
        _lastAllocatedBytes = GC.GetTotalAllocatedBytes();
        for (var index = 0; index < 3; index++) _lastCollections[index] = GC.CollectionCount(index);
        try
        {
            while (await timer.WaitForNextTickAsync(_shutdown.Token)) WriteMetrics(process);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (Exception ex) { App.Log(ex); }
    }

    private void WriteMetrics(Process process)
    {
        var now = Stopwatch.GetTimestamp();
        var seconds = Stopwatch.GetElapsedTime(_lastMetricsTicks, now).TotalSeconds;
        _lastMetricsTicks = now;
        var received = Interlocked.Read(ref _receivedFrames);
        var decoded = Interlocked.Read(ref _decodedFrames);
        var camera = Interlocked.Read(ref _cameraFrames);
        var preview = Interlocked.Read(ref _previewFrames);
        var queue = _frames.Snapshot();
        var receivedFps = (received - _lastReceivedFrames) / seconds;
        var decodedFps = (decoded - _lastDecodedFrames) / seconds;
        var cameraFps = (camera - _lastCameraFrames) / seconds;
        var previewFps = (preview - _lastPreviewFrames) / seconds;
        var drops = queue.Drops - _lastQueueDrops;
        var recoveries = queue.Recoveries - _lastRecoveries;
        _lastReceivedFrames = received; _lastDecodedFrames = decoded;
        _lastCameraFrames = camera; _lastPreviewFrames = preview;
        _lastQueueDrops = queue.Drops; _lastRecoveries = queue.Recoveries;
        var cameraCoalesced = Interlocked.Read(ref _cameraCoalesced);
        var previewCoalesced = Interlocked.Read(ref _previewCoalesced);
        var failures = Interlocked.Read(ref _publishFailures);
        var detail = $"coalesced camera/preview={cameraCoalesced - _lastCameraCoalesced}/{previewCoalesced - _lastPreviewCoalesced}; publish failures={failures - _lastPublishFailures}";
        _lastCameraCoalesced = cameraCoalesced; _lastPreviewCoalesced = previewCoalesced; _lastPublishFailures = failures;
        var timing = $"stage avg/max(count): decode={_decodeTiming.TakeSnapshot()} publish={_cameraTiming.TakeSnapshot()} preview={_previewTiming.TakeSnapshot()}";
        process.Refresh();
        var cpuTime = process.TotalProcessorTime;
        var cpu = (cpuTime - _lastCpuTime).TotalSeconds / seconds / Environment.ProcessorCount * 100;
        _lastCpuTime = cpuTime;
        var allocated = GC.GetTotalAllocatedBytes();
        var allocationRate = (allocated - _lastAllocatedBytes) / seconds / 1048576;
        _lastAllocatedBytes = allocated;
        var collections = new int[3];
        for (var index = 0; index < 3; index++)
        {
            var current = GC.CollectionCount(index);
            collections[index] = current - _lastCollections[index];
            _lastCollections[index] = current;
        }
        if (!_connection.IsStreaming) return;
        var message = $"Pipeline fps: USB {receivedFps:F1} · decoded {decodedFps:F1} · virtual camera {cameraFps:F1} · preview {previewFps:F1} · queue drops {drops}";
        Ui(() => SetStatus("PerformanceStatus", message));
        App.Log("METRIC", $"{message}; queue={queue.Depth}/{queue.Bytes}B age={queue.OldestAgeMs:F0}ms recoveries={recoveries} waitingIDR={queue.WaitingForKeyframe}; {timing}; {detail}; decoder backend={_decoder.BackendStatus}; decoded size={Volatile.Read(ref _decodedWidth)}x{Volatile.Read(ref _decodedHeight)}; last frame age USB/decode/camera={FrameAge(_lastUsbTicks, now)}/{FrameAge(_lastDecodedTicks, now)}/{FrameAge(_lastCameraTicks, now)}ms; CPU={cpu:F1}% workingSet={process.WorkingSet64 / 1048576}MiB; allocation={allocationRate:F1}MiB/s GC0/1/2={string.Join("/", collections)}");
    }

    private static string FrameAge(long ticks, long now) => ticks == 0 ? "none" : $"{Math.Max(0, Stopwatch.GetElapsedTime(ticks, now).TotalMilliseconds):F0}";

    private void SetStreaming(bool streaming) { _streaming = streaming; Changed?.Invoke(); }
    public async Task ToggleStreamAsync()
    {
        if (_streaming) await _connection.StopStreamAsync();
        else await _connection.StartStreamAsync();
    }
    public Task SwitchCameraAsync() => _connection.SwitchCameraAsync(null);
    public ConnectionManager Connection => _connection;
    public bool Connected { get; private set; }
    public bool Streaming => _streaming;
    public volatile int PreviewFps = 60;
    public volatile bool PreviewVisible = true;
    public Action<DecodedFrame>? PreviewFrame;
    public event Action? Changed;
    public readonly Dictionary<string, string> Status = new();
    private void SetStatus(string key, string value) { Status[key] = value; Changed?.Invoke(); }

    public async ValueTask DisposeAsync()
    {
        if (_closed) return;
        if (_closing) return;
        _closing = true;
        _shutdown.Cancel();

        _frames.Complete();
        try { await _connection.DisposeAsync(); }
        catch (Exception ex) { App.Log(ex); }
        if (_decodeTask is not null) await _decodeTask;
        _cameraFramesQueue.Complete();
        _previewFramesQueue.Writer.TryComplete();
        await Task.WhenAll(new[] { _cameraTask, _metricsTask }.OfType<Task>());
        while (_previewFramesQueue.Reader.TryRead(out var pending)) pending.Frame.Release();
        _decoder.Dispose();
        _shutdown.Dispose();
        _camera?.Dispose();
        _closed = true;

    }

    private void Ui(Action action)
    {
        App.Ui(action);
    }
}
