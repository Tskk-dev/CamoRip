using System.ComponentModel;
using System.Diagnostics;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using UniversalCam.Core;
using UniversalCam.Core.Protocol;
using UniversalCam.Core.Transport;
using UniversalCam.Core.Video;
using UniversalCam.VirtualCamera;

namespace UniversalCam.Views;

public partial class MainWindow : Window
{
    private readonly ConnectionManager _connection = new();
    private readonly H264Decoder _decoder = new();
    private readonly CompressedVideoQueue _frames = new();
    // Only independent decoded frames may be coalesced. Slow consumers get the
    // newest image without blocking H.264 input or accumulating video latency.
    private readonly Channel<(DecodedFrame Frame, long Generation)> _cameraFramesQueue;
    private readonly Channel<(DecodedFrame Frame, long Generation)> _previewFramesQueue;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly StageTiming _decodeTiming = new(), _cameraTiming = new(), _previewTiming = new();
    private VirtualCameraSession? _camera;
    private Task? _decodeTask, _cameraTask, _previewTask, _metricsTask;
    private WriteableBitmap? _bitmap;
    private bool _streaming;
    private volatile bool _closing;
    private bool _closed;
    private int _previewPending, _decodedWidth, _decodedHeight;
    private long _decodingGeneration;
    private long _receivedFrames, _decodedFrames, _cameraFrames, _previewFrames;
    private long _cameraCoalesced, _previewCoalesced, _publishFailures;
    private long _lastUsbTicks, _lastDecodedTicks, _lastCameraTicks;
    private long _lastMetricsTicks, _lastReceivedFrames, _lastDecodedFrames, _lastCameraFrames, _lastPreviewFrames;
    private long _lastQueueDrops, _lastRecoveries, _lastCameraCoalesced, _lastPreviewCoalesced, _lastPublishFailures;
    private TimeSpan _lastCpuTime;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closing += OnClosing;
        _cameraFramesQueue = LatestFrames(() => Interlocked.Increment(ref _cameraCoalesced));
        _previewFramesQueue = LatestFrames(() => Interlocked.Increment(ref _previewCoalesced));
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _camera = new VirtualCameraSession();
            _camera.StatusChanged += (_, status) =>
            {
                App.Log("INFO", $"Virtual camera: {status}");
                Ui(() => CameraStatus.Text = status);
            };
            CameraStatus.Text = _camera.StatusMessage;
            App.Log("INFO", $"Virtual camera status: {_camera.StatusMessage}");
        }
        catch (Exception ex)
        {
            CameraStatus.Text = $"Windows webcam could not start: {ex.Message}";
            App.Log(ex);
        }

        _decoder.FrameDecoded += (_, frame) =>
        {
            var generation = Interlocked.Read(ref _decodingGeneration);
            if (!IsCurrent(generation)) return;
            Interlocked.Increment(ref _decodedFrames);
            Interlocked.Exchange(ref _lastDecodedTicks, Stopwatch.GetTimestamp());
            Volatile.Write(ref _decodedWidth, frame.Width);
            Volatile.Write(ref _decodedHeight, frame.Height);
            _cameraFramesQueue.Writer.TryWrite((frame, generation));
            _previewFramesQueue.Writer.TryWrite((frame, generation));
        };
        _connection.Diagnostic += (_, message) => Ui(() => Diagnostic.Text = message);
        _connection.Diagnostic += (_, message) => App.Log("INFO", $"Connection: {message}");
        _connection.ConfigurationAcknowledged += (_, configuration) => Ui(() =>
            VideoStatus.Text = $"Capture: {configuration.Resolution} at {configuration.Fps} fps · Webcam: 1080p60");
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
        _cameraTask = Task.Run(PublishCameraFrames);
        _previewTask = Task.Run(PreviewFrames);
        _metricsTask = Task.Run(CollectMetrics);
        App.Log("INFO", "Pipeline v2: dedicated decoder; independent latest-frame camera/preview; compressed queue limited to 64 frames, 16 MiB, 500 ms.");
        try { await _connection.StartAsync(); }
        catch (Exception ex) { Diagnostic.Text = ex.Message; App.Log(ex); }
    }

    private void OnConnectionState(object? sender, TransportState state)
    {
        _frames.Reset($"USB state {state}");
        try { _camera?.OnConnectionStateChanged(sender, state); }
        catch (Exception ex) { App.Log(ex); }
        Ui(() =>
        {
            var connected = state == TransportState.Connected;
            SwitchCamera.IsEnabled = StreamButton.IsEnabled = connected;
            DeviceStatus.Text = connected ? $"{_connection.DeviceName} · USB connected" : "Waiting for iPhone over USB";
            if (!connected)
            {
                SetStreaming(false);
                Waiting.Text = "Waiting for USB video";
            }
            Diagnostic.Text = _connection.StatusMessage;
            if (connected) _ = ConfigurePhone();
        });
    }

    private async Task ConfigurePhone()
    {
        try { await _connection.ConfigureAsync("1920x1080", 60, "h264", 12_000_000); }
        catch (Exception ex) { Ui(() => Diagnostic.Text = ex.Message); App.Log(ex); }
    }

    private static Channel<(DecodedFrame Frame, long Generation)> LatestFrames(Action dropped) =>
        Channel.CreateBounded<(DecodedFrame, long)>(new BoundedChannelOptions(1)
        { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.DropOldest }, _ => dropped());

    private bool IsCurrent(long generation) => !_closing && _connection.IsStreaming && generation == _frames.Generation;

    private void DecodeFrames()
    {
        long generation = -1;
        var reader = _frames.ReadAllAsync().GetAsyncEnumerator();
        try
        {
            while (reader.MoveNextAsync().AsTask().GetAwaiter().GetResult())
            {
                if (_closing) break;
                var queued = reader.Current;
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
                    Ui(() => Diagnostic.Text = $"Video decoder is waiting for a new keyframe: {ex.Message}");
                    App.Log(ex);
                }
                finally { _decodeTiming.Record(started); }
            }
        }
        catch (Exception ex) { App.Log(ex); }
        finally { reader.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }

    private async Task PublishCameraFrames()
    {
        try
        {
            await foreach (var queued in _cameraFramesQueue.Reader.ReadAllAsync())
            {
                if (!IsCurrent(queued.Generation)) continue;
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
                finally { _cameraTiming.Record(started); }
            }
        }
        catch (Exception ex) { App.Log(ex); }
    }

    private async Task PreviewFrames()
    {
        try
        {
            while (await _previewFramesQueue.Reader.WaitToReadAsync())
            {
                // Wait before taking the image so the conversion uses the newest
                // frame, rather than one that has aged during this preview interval.
                await Task.Delay(66, _shutdown.Token);
                if (!_previewFramesQueue.Reader.TryRead(out var queued)) continue;
                if (IsCurrent(queued.Generation)) RenderPreview(queued.Frame, queued.Generation);
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (Exception ex) { App.Log(ex); }
    }

    private async Task CollectMetrics()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        using var process = Process.GetCurrentProcess();
        _lastMetricsTicks = Stopwatch.GetTimestamp();
        _lastCpuTime = process.TotalProcessorTime;
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
        if (!_connection.IsStreaming) return;
        var message = $"Pipeline fps: USB {receivedFps:F1} · decoded {decodedFps:F1} · virtual camera {cameraFps:F1} · preview {previewFps:F1} · queue drops {drops}";
        Ui(() => PerformanceStatus.Text = message);
        App.Log("METRIC", $"{message}; queue={queue.Depth}/{queue.Bytes}B age={queue.OldestAgeMs:F0}ms recoveries={recoveries} waitingIDR={queue.WaitingForKeyframe}; {timing}; {detail}; decoded size={Volatile.Read(ref _decodedWidth)}x{Volatile.Read(ref _decodedHeight)}; last frame age USB/decode/camera={FrameAge(_lastUsbTicks, now)}/{FrameAge(_lastDecodedTicks, now)}/{FrameAge(_lastCameraTicks, now)}ms; CPU={cpu:F1}% workingSet={process.WorkingSet64 / 1048576}MiB");
    }

    private static string FrameAge(long ticks, long now) => ticks == 0 ? "none" : $"{Math.Max(0, Stopwatch.GetElapsedTime(ticks, now).TotalMilliseconds):F0}";

    private void OnOpenLogs(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = App.LogDirectory,
                UseShellExecute = true
            });
        }
        catch (Exception ex) { App.Log(ex); Diagnostic.Text = $"Could not open logs: {ex.Message}"; }
    }

    private void RenderPreview(DecodedFrame frame, long generation)
    {
        if (_closing || Interlocked.Exchange(ref _previewPending, 1) != 0) return;
        const int previewWidth = 640, previewHeight = 360;
        var started = Stopwatch.GetTimestamp();
        byte[] preview;
        try { preview = NV12Converter.CreateBgraPreview(frame.Data, frame.Width, frame.Height, previewWidth, previewHeight); }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref _previewPending, 0);
            App.Log(ex);
            return;
        }
        finally { _previewTiming.Record(started); }
        Ui(() =>
        {
            try
            {
                if (!IsCurrent(generation)) return;
                if (_bitmap is null || _bitmap.PixelWidth != previewWidth || _bitmap.PixelHeight != previewHeight)
                    _bitmap = new WriteableBitmap(previewWidth, previewHeight, 96, 96, PixelFormats.Bgra32, null);
                _bitmap.WritePixels(new Int32Rect(0, 0, previewWidth, previewHeight), preview, previewWidth * 4, 0);
                Preview.Source = _bitmap;
                Interlocked.Increment(ref _previewFrames);
                Waiting.Visibility = Visibility.Collapsed;
            }
            finally { Interlocked.Exchange(ref _previewPending, 0); }
        });
    }

    private void SetStreaming(bool streaming)
    {
        _streaming = streaming;
        StreamButton.Content = streaming ? "Stop video" : "Start video";
        if (!streaming)
        {
            Preview.Source = null;
            Waiting.Text = "Video stopped";
            Waiting.Visibility = Visibility.Visible;
        }
    }

    private async void OnToggleStream(object sender, RoutedEventArgs e)
    {
        try
        {
            var start = !_streaming;
            if (start) await _connection.StartStreamAsync();
            else await _connection.StopStreamAsync();
        }
        catch (Exception ex) { Diagnostic.Text = ex.Message; App.Log(ex); }
    }

    private async void OnSwitchCamera(object sender, RoutedEventArgs e)
    {
        try { await _connection.SwitchCameraAsync(null); }
        catch (Exception ex) { Diagnostic.Text = ex.Message; App.Log(ex); }
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closed) return;
        e.Cancel = true;
        if (_closing) return;
        _closing = true;
        _shutdown.Cancel();
        IsEnabled = false;
        _frames.Complete();
        try { await _connection.DisposeAsync(); }
        catch (Exception ex) { App.Log(ex); }
        if (_decodeTask is not null) await _decodeTask;
        _cameraFramesQueue.Writer.TryComplete();
        _previewFramesQueue.Writer.TryComplete();
        await Task.WhenAll(new[] { _cameraTask, _previewTask, _metricsTask }.OfType<Task>());
        _decoder.Dispose();
        _shutdown.Dispose();
        _camera?.Dispose();
        _closed = true;
        Close();
    }

    private void Ui(Action action)
    {
        if (Dispatcher.HasShutdownStarted) return;
        Dispatcher.InvokeAsync(() => { if (!_closed) action(); });
    }
}
