using System.ComponentModel;
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
    private readonly Channel<(MediaFrame Frame, long Generation)> _frames = Channel.CreateBounded<(MediaFrame, long)>(new BoundedChannelOptions(8)
    {
        SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait
    });
    private VirtualCameraSession? _camera;
    private Task? _decodeTask;
    private WriteableBitmap? _bitmap;
    private bool _streaming;
    private bool _closing;
    private bool _closed;
    private bool _awaitKeyframe = true;
    private int _previewPending;
    private long _generation;
    private long _decodingGeneration;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _camera = new VirtualCameraSession();
            _camera.StatusChanged += (_, status) => Ui(() => CameraStatus.Text = status);
            CameraStatus.Text = _camera.StatusMessage;
        }
        catch (Exception ex)
        {
            CameraStatus.Text = $"Windows webcam could not start: {ex.Message}";
            App.Log(ex);
        }

        _decoder.FrameDecoded += (_, frame) =>
        {
            if (_closing || !_connection.IsStreaming || Interlocked.Read(ref _decodingGeneration) != Interlocked.Read(ref _generation)) return;
            try { _camera?.OnFrameDecoded(this, frame); }
            catch (Exception ex) { Ui(() => CameraStatus.Text = $"Windows webcam: {ex.Message}"); App.Log(ex); }
            RenderPreview(frame);
        };
        _connection.Diagnostic += (_, message) => Ui(() => Diagnostic.Text = message);
        _connection.ConfigurationAcknowledged += (_, configuration) => Ui(() =>
            VideoStatus.Text = $"Capture: {configuration.Resolution} at {configuration.Fps} fps · Webcam: 1080p30");
        _connection.StreamStateChanged += (_, streaming) =>
        {
            if (!streaming) Interlocked.Increment(ref _generation);
            _camera?.OnStreamStateChanged(this, streaming);
            Ui(() => SetStreaming(streaming));
        };
        _connection.CameraSwitched += (_, _) =>
        {
            Interlocked.Increment(ref _generation);
            _decoder.RequestReset();
        };
        _connection.StateChanged += OnConnectionState;
        _connection.FrameReceived += (_, frame) =>
        {
            if (!frame.IsVideo || _closing) return;
            if (!_frames.Writer.TryWrite((frame, Interlocked.Read(ref _generation))))
                Interlocked.Increment(ref _generation);
        };
        _decodeTask = Task.Run(DecodeFrames);
        try { await _connection.StartAsync(); }
        catch (Exception ex) { Diagnostic.Text = ex.Message; App.Log(ex); }
    }

    private void OnConnectionState(object? sender, TransportState state)
    {
        Interlocked.Increment(ref _generation);
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
        try { await _connection.ConfigureAsync("1920x1080", 30, "h264", 8_000_000); }
        catch (Exception ex) { Ui(() => Diagnostic.Text = ex.Message); App.Log(ex); }
    }

    private async Task DecodeFrames()
    {
        long generation = -1;
        await foreach (var queued in _frames.Reader.ReadAllAsync())
        {
            if (_closing) break;
            var current = Interlocked.Read(ref _generation);
            if (queued.Generation != current) continue;
            var frame = queued.Frame;
            if (generation != current)
            {
                generation = current;
                _awaitKeyframe = true;
                _decoder.RequestReset();
            }
            if (_awaitKeyframe && !frame.Header.IsKeyframe) continue;
            _awaitKeyframe = false;
            try
            {
                Interlocked.Exchange(ref _decodingGeneration, generation);
                _decoder.Feed(frame);
            }
            catch (Exception ex)
            {
                _awaitKeyframe = true;
                _decoder.RequestReset();
                Ui(() => Diagnostic.Text = $"Video decoder is waiting for a new keyframe: {ex.Message}");
                App.Log(ex);
            }
        }
    }

    private void RenderPreview(DecodedFrame frame)
    {
        if (_closing || Interlocked.Exchange(ref _previewPending, 1) != 0) return;
        var generation = Interlocked.Read(ref _generation);
        Ui(() =>
        {
            try
            {
                if (_closing || !_connection.IsStreaming || generation != Interlocked.Read(ref _generation)) return;
                if (_bitmap is null || _bitmap.PixelWidth != frame.Width || _bitmap.PixelHeight != frame.Height)
                    _bitmap = new WriteableBitmap(frame.Width, frame.Height, 96, 96, PixelFormats.Bgra32, null);
                _bitmap.WritePixels(new Int32Rect(0, 0, frame.Width, frame.Height), frame.Data, frame.Width * 4, 0);
                Preview.Source = _bitmap;
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
        IsEnabled = false;
        _frames.Writer.TryComplete();
        try { await _connection.DisposeAsync(); }
        catch (Exception ex) { App.Log(ex); }
        if (_decodeTask is not null) await _decodeTask;
        _decoder.Dispose();
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
