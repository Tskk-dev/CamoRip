using System.Diagnostics;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Controls.Primitives;
using CamoRip.Core.Protocol;
using CamoRip.Core.Transport;
using CamoRip.Core.Video;
using CamoRip.Core.Air;

namespace CamoRip.Views;

public sealed partial class MainWindow : Window
{
    private readonly AirInputBridge airBridge = new();
    private volatile bool airRequested;

    private async void OnAirEnabled(object sender, RoutedEventArgs e)
    {
        if (!_ready || _updating) return;
        airRequested = AirEnabled.IsOn;
        if (!airRequested) airBridge.SetEnabled(false);
        await ApplyAir();
        Refresh();
    }

    private async void OnAirCalibrate(object sender, RoutedEventArgs e) => await ApplyAir();

    private async Task ApplyAir()
    {
        try
        {
            await _service.Connection.ConfigureAirAsync(new AirConfigure {
                Enabled = airRequested, Threshold = AirThreshold.Value,
                X = AirX.Value, Y = AirY.Value, Span = AirSpan.Value });
            AirStatus.Text = airRequested ? "Calibrating: keep hands out of all six zones." : "AIR off";
        }
        catch (Exception ex)
        {
            airRequested = false;
            airBridge.SetEnabled(false);
            _updating = true;
            AirEnabled.IsOn = false;
            _updating = false;
            AirStatus.Text = ex.Message;
        }
    }
    private bool _pairingOpen;
    private async void OnWirelessPairing(object sender, RoutedEventArgs e)
    {
        var wireless = _service.Connection.Wireless;
        if (wireless is null || _pairingOpen) return;
        _pairingOpen = true;
        bool paired = false;
        ContentDialog? pairingDialog = null;
        void OnPaired(object? sender, EventArgs e) => App.Ui(() =>
        {
            paired = true;
            pairingDialog?.Hide();
        });
        wireless.PairingCompleted += OnPaired;
        try
        {
            while (true)
            {
                string code = wireless.BeginPairing();
                using var generator = new QRCoder.QRCodeGenerator();
                using var data = generator.CreateQrCode(code, QRCoder.QRCodeGenerator.ECCLevel.M);
                using var qr = new QRCoder.PngByteQRCode(data);
                byte[] png = qr.GetGraphic(8);
                using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
                using (var writer = new Windows.Storage.Streams.DataWriter(stream.GetOutputStreamAt(0)))
                { writer.WriteBytes(png); await writer.StoreAsync(); writer.DetachStream(); }
                stream.Seek(0);
                var bitmap = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
                await bitmap.SetSourceAsync(stream);
                var content = new StackPanel { Spacing = 16 };
                content.Children.Add(new TextBlock { Text = "On your iPhone, tap Connect wirelessly and scan this code. Keep both devices on the same local network.", TextWrapping = TextWrapping.Wrap });
                content.Children.Add(new Border { Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White),
                    Padding = new Thickness(12), Child = new Image { Source = bitmap, Width = 320, Height = 320 } });
                content.Children.Add(new TextBlock { Text = "This code expires in 5 minutes and works once. After pairing, reconnection is automatic. Allow CamoRip through the Windows firewall on Private networks.", TextWrapping = TextWrapping.Wrap });
                if (paired) break;
                pairingDialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = "Connect wirelessly", Content = content,
                    PrimaryButtonText = "Refresh QR", CloseButtonText = "Done" };
                var result = await pairingDialog.ShowAsync();
                pairingDialog = null;
                if (paired || result != ContentDialogResult.Primary) break;
            }
        }
        catch (Exception ex) { ConnectionNotice.Message = "Wireless pairing: " + ex.Message; ConnectionNotice.IsOpen = true; }
        finally
        {
            wireless.PairingCompleted -= OnPaired;
            wireless.CancelPairing();
            _pairingOpen = false;
        }
    }
    private async void OnForgetWireless(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = "Forget paired phones?",
            Content = "Wireless phones will need to scan a new QR code. USB connections still work.",
            PrimaryButtonText = "Forget", CloseButtonText = "Cancel" };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            try { _service.Connection.Wireless?.ForgetDevices(); }
            catch (Exception ex) { ConnectionNotice.Message = ex.Message; ConnectionNotice.IsOpen = true; }
        }
    }
    private readonly App _app;
    private readonly StreamingService _service;
    private readonly Dictionary<string, double> _pending = new();
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _commands;
    private readonly Dictionary<string, Slider> _sliders;
    private CameraState? _state;
    private bool _updatingModes;
    private readonly HashSet<Slider> _editingSliders = new();
    private bool _ready, _updating, _exiting, _hidden, _fullscreen, _sending;
    private long _requestId, _confirmedId, _lastSent, _timedOutId = -1;
    private bool _switching;
    private GpuPreview? _gpu;
    private bool _rendering;
    private long _previewRetry;
    internal MainWindow(App app, StreamingService service)
    {
        _app = app; _service = service;
        InitializeComponent();
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1180, 820));
        AppWindow.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory, "camorip.ico"));
        SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop();
        _sliders = new() { ["exposure_bias"] = ExposureBias, ["iso"] = Iso, ["shutter_ms"] = Shutter,
            ["focus"] = Focus, ["temperature"] = Temperature, ["tint"] = Tint, ["torch_level"] = TorchLevel };
        foreach (var slider in _sliders.Values)
        {
            slider.AddHandler(UIElement.PointerPressedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler((_, _) => _editingSliders.Add(slider)), true);
            slider.AddHandler(UIElement.PointerReleasedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler((_, _) => _editingSliders.Remove(slider)), true);
            slider.PointerCaptureLost += (_, _) => _editingSliders.Remove(slider);
        }
        _service.Connection.ConfigurationAcknowledged += (_, _) => App.Ui(RefreshModes);
        var settings = App.Settings;
        Quality.ItemsSource = Enum.GetValues<VideoQuality>();
        Quality.SelectedItem = settings.CaptureQuality;
        CustomBitrate.Value = settings.CustomBitrateMbps;
        CloseToTray.IsOn = settings.CloseToTray; StartMinimized.IsOn = settings.StartMinimized;
        LaunchAtSignIn.IsOn = settings.LaunchAtSignIn; Logging.IsOn = settings.Logging;
        PreviewRate.SelectedIndex = settings.PreviewFps switch { 30 => 1, 15 => 2, 0 => 3, _ => 0 };
        FillPreview.IsOn = settings.PreviewFill; MirrorPreview.IsOn = settings.PreviewMirror;
        _service.PreviewFps = settings.PreviewFps;
        MenuSelection.SelectedIndex = 0;
        Preview.CompositionScaleChanged += (_, _) => OnPreviewSize(Preview, null!);
        _service.Changed += Refresh;
        _service.PreviewFrame = frame =>
        {
            if (Stopwatch.GetTimestamp() < _previewRetry) return false;
            try
            {
                if (_gpu is null) _gpu = new GpuPreview(Preview);
                bool rendered = _gpu.Render(frame, App.Settings.PreviewFill, App.Settings.PreviewMirror);
                if (rendered) Waiting.Visibility = Visibility.Collapsed;
                return rendered;
            }
            catch
            {
                _gpu?.Dispose(); _gpu = null;
                _previewRetry = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 2;
                throw;
            }
        };
        _service.Connection.Failure += (_, message) => App.Ui(() => { ConnectionNotice.Message = message; ConnectionNotice.IsOpen = true; });
        _service.Connection.CameraStateReceived += (_, state) => App.Ui(() => ApplyState(state));
        airBridge.StatusChanged += (_, status) => App.Ui(() => AirStatus.Text = status);
        _service.Connection.AirStateReceived += (_, state) =>
        {
            airBridge.SetEnabled(airRequested && state.Enabled);
            if (airRequested && state.Enabled) airBridge.Update(state.Mask);
        };
        _service.Connection.StateChanged += (_, state) => App.Ui(() =>
        {
            _pending.Clear(); _state = null; _switching = false; _confirmedId = _requestId; Adjustments.IsEnabled = Torch.IsEnabled = TorchLevel.IsEnabled = false;
            if (state == TransportState.Connected) ConnectionNotice.IsOpen = false;
            else
            {
                airRequested = false;
                airBridge.SetEnabled(false);
                _updating = true;
                AirEnabled.IsOn = false;
                _updating = false;
                AirStatus.Text = "Disconnected; AIR zones released.";
            }
            ControlStatus.Text = state == TransportState.Connected && !_service.Connection.SupportsCameraSettings
                ? "Update the iPhone app to use camera controls." : "Waiting for phone camera capabilities";
        });
        _service.Connection.CameraSwitched += (_, _) => App.Ui(() => { _pending.Clear(); _state = null; _switching = false; _confirmedId = _requestId; Adjustments.IsEnabled = false; });
        _commands = DispatcherQueue.CreateTimer(); _commands.Interval = TimeSpan.FromMilliseconds(50);
        _commands.Tick += async (_, _) => await FlushCommands(); _commands.Start();
        AppWindow.Closing += (_, e) =>
        {
            if (_exiting) return;
            e.Cancel = true;
            if (App.Settings.CloseToTray) HideToTray(); else App.Run(_app.ExitAsync);
        };
        AppWindow.Changed += (_, _) => UpdatePreviewVisibility();
        Activated += (_, _) => UpdatePreviewVisibility();
        Root.SizeChanged += (_, _) =>
        {
            if (_fullscreen) return;
            bool narrow = Root.ActualWidth < 1000;
            Navigation.DisplayMode = narrow ? SplitViewDisplayMode.Overlay : SplitViewDisplayMode.CompactInline;
            Navigation.IsPaneOpen = !narrow;
        };
        Root.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.F11 || (_fullscreen && e.Key == Windows.System.VirtualKey.Escape))
            { OnFullscreen(Root, new RoutedEventArgs()); e.Handled = true; }
        };
        _ready = true;
        if (AppSettings.Isolated && !AppSettings.StartupCheck)
        {
            ConnectionNotice.Severity = InfoBarSeverity.Informational;
            ConnectionNotice.Message = "Prototype mode: USB is disabled. Open the installed CamoRip app to connect your iPhone.";
            ConnectionNotice.IsOpen = true;
        }
    }
    internal void CheckControls()
    {
        // Functional regression runs in an isolated process without USB or settings writes.
        var state = new CameraState { CameraId = "check", TorchAvailable = true, TorchOn = true,
            CaptureModes = VideoMode.Choices.ToList(),
            Ranges = new() { ["torch_level"] = new() { Min = 0.01, Max = 1 } },
            Values = new() { ["torch_level"] = 0.5 } };
        ApplyState(state);
        for (int i = 0; i < 100; ++i)
        {
            TorchLevel.Value = 0.01 + i / 100.0;
            ApplyState(state with { Values = new() { ["torch_level"] = i / 100.0 } });
        }
        ApplyState(state with { Ranges = new() { ["torch_level"] = new() { Min = 1, Max = 0 } } });
        if (TorchLevel.IsEnabled) throw new InvalidOperationException("Invalid torch range was accepted.");
        ApplyState(state with { CaptureModes = [new(1280, 720, 30)], TorchAvailable = false });
        if (CaptureMode.Items.Count != 1) throw new InvalidOperationException("Front camera modes were not filtered.");
        ApplyState(state);
        if (CaptureMode.Items.Count != 5) throw new InvalidOperationException("Capture mode bindings failed.");
        _state = null; RefreshModes();
        App.Log("INFO", "Control check passed: torch slider changes, acknowledgements, invalid range, and mode bindings.");
    }

    private void Refresh()
    {
        foreach (var (key, value) in _service.Status)
            if (Root.FindName(key) is TextBlock text) text.Text = value;
        StreamButton.IsEnabled = SwitchCamera.IsEnabled = _service.Connected && !_service.ChangingMode && !airRequested;
        AirEnabled.IsEnabled = _service.Connected && _service.Connection.SupportsAir && !_service.ChangingMode;
        AirCalibrate.IsEnabled = AirEnabled.IsEnabled && airRequested;
        StreamButton.Content = _service.Streaming ? "Stop video" : "Start video";
        if (_service.Streaming) ConnectionNotice.IsOpen = false;
        if (!_service.Streaming) { Preview.Visibility = Visibility.Collapsed; Waiting.Text = "Video stopped"; Waiting.Visibility = Visibility.Visible; }
        UpdateEnabled();
        RefreshModes();
        UpdatePreviewVisibility();
    }
    private void RefreshModes()
    {
        if (CaptureMode is null) return;
        var modes = (_state?.CaptureModes ?? []).Where(VideoMode.Choices.Contains).Distinct().ToArray();
        var selected = VideoMode.Parse(_service.Connection.CaptureResolution, _service.Connection.CaptureFrameRate)
            ?? VideoMode.Parse(App.Settings.CaptureResolution, App.Settings.CaptureFps);
        _updatingModes = true;
        try
        {
            var existing = CaptureMode.ItemsSource as VideoMode[];
            if (existing is null || !existing.SequenceEqual(modes)) CaptureMode.ItemsSource = modes;
            CaptureMode.SelectedItem = modes.FirstOrDefault(m => m == selected);
            CaptureMode.IsEnabled = _service.Connected && !_service.ChangingMode && !_switching && modes.Length > 0;
            RefreshQuality();
            CaptureModeHint.Text = _service.ChangingMode ? "Changing video mode…" : modes.Length == 0
                ? "Connect the updated iPhone app to choose a video mode."
                : "Changing mode restarts the webcam. Your video app may need to reopen it.";
        }
        finally { _updatingModes = false; }
    }
    private async void OnCaptureMode(object sender, SelectionChangedEventArgs args)
    {
        if (!_ready || _updatingModes || CaptureMode.SelectedItem is not VideoMode mode) return;
        if (mode == VideoMode.Parse(_service.Connection.CaptureResolution, _service.Connection.CaptureFrameRate)) return;
        _pending.Clear();
        try
        {
            await _service.SetVideoModeAsync(mode);
            App.Settings.CaptureResolution = mode.Resolution; App.Settings.CaptureFps = mode.Fps; SaveSettings();
        }
        catch (Exception ex) { Diagnostic.Text = ex.Message; App.Log(ex); }
        finally { RefreshModes(); }
    }

    private void OnQualitySelection(object sender, SelectionChangedEventArgs args) { if (_ready) RefreshQuality(); }
    private void OnCustomBitrate(NumberBox sender, NumberBoxValueChangedEventArgs args) { if (_ready) RefreshQuality(); }
    private void RefreshQuality()
    {
        var enabled = _service.Connected && _service.Connection.SupportsBitrateControl && !_service.ChangingMode && !_switching;
        Quality.IsEnabled = CustomBitrate.IsEnabled = enabled;
        CustomBitrate.Visibility = Quality.SelectedItem is VideoQuality.Custom ? Visibility.Visible : Visibility.Collapsed;
        var mode = VideoMode.Parse(_service.Connection.CaptureResolution, _service.Connection.CaptureFrameRate) ?? VideoMode.Default;
        ApplyQuality.IsEnabled = enabled && Quality.SelectedItem is VideoQuality &&
            (Quality.SelectedItem is not VideoQuality.Custom || double.IsFinite(CustomBitrate.Value) && CustomBitrate.Value is >= 1 and <= 50);
        if (!_service.Connection.SupportsBitrateControl) { QualityHint.Text = "Connect the updated iPhone app to adjust video quality."; return; }
        if (!ApplyQuality.IsEnabled) { QualityHint.Text = _service.ChangingMode ? "Applying video settings…" : "Enter a custom bitrate from 1 to 50 Mbps."; return; }
        int requested = VideoBitrate.Resolve(mode, (VideoQuality)Quality.SelectedItem, CustomBitrate.Value);
        QualityHint.Text = $"Selected: {requested / 1_000_000.0:F1} Mbps · Active target: {_service.AppliedBitrate / 1_000_000.0:F1} Mbps. Applying briefly restarts video. Higher bitrates can increase bandwidth and latency.";
    }
    private async void OnApplyQuality(object sender, RoutedEventArgs args)
    {
        if (!_ready || !ApplyQuality.IsEnabled || Quality.SelectedItem is not VideoQuality quality) return;
        var mode = VideoMode.Parse(_service.Connection.CaptureResolution, _service.Connection.CaptureFrameRate);
        if (mode is null) return;
        var custom = CustomBitrate.Value;
        try {
            await _service.SetVideoModeAsync(mode, VideoBitrate.Resolve(mode, quality, custom));
            App.Settings.CaptureQuality = quality;
            if (double.IsFinite(custom) && custom is >= 1 and <= 50) App.Settings.CustomBitrateMbps = custom;
            SaveSettings();
        } catch (Exception ex) { Diagnostic.Text = ex.Message; App.Log(ex); }
        finally { RefreshQuality(); }
    }

    private void UpdatePreviewVisibility()
    {
        _service.PreviewVisible = !_exiting && !_hidden && (AppWindow.Presenter is not OverlappedPresenter p || p.State != OverlappedPresenterState.Minimized);
        bool visible = _service.PreviewVisible && _service.PreviewFps > 0 && _service.Streaming;
        if (_rendering != visible)
        {
            _rendering = visible;
            if (visible) Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += OnRendering;
            else Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnRendering;
        }
        Preview.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!visible) { Waiting.Text = airRequested ? "AIR mode · phone sends zone inputs without video" : _service.PreviewFps == 0 ? "Preview off · webcam output continues" : "Waiting for video"; Waiting.Visibility = Visibility.Visible; }
    }
    internal void HideToTray() { _hidden = true; UpdatePreviewVisibility(); AppWindow.Hide(); }
    internal void ShowFromTray()
    {
        _hidden = false; AppWindow.Show();
        if (AppWindow.Presenter is OverlappedPresenter p) p.Restore();
        Activate(); UpdatePreviewVisibility();
    }
    private void OnRendering(object? sender, object e) => _service.RenderLatestPreview();
    internal void PrepareExit() { airRequested = false; airBridge.Dispose(); _exiting = true; _rendering = false; Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnRendering; _commands.Stop(); _service.PreviewVisible = false; _service.PreviewFrame = null; _gpu?.Dispose(); }
    private void OnPaneToggle(object sender, RoutedEventArgs e) => Navigation.IsPaneOpen = !Navigation.IsPaneOpen;
    private void OnNavigation(object sender, SelectionChangedEventArgs args)
    {
        if (CameraPage is null) return;
        var selected = (MenuSelection.SelectedItem as ListViewItem)?.Tag as string;
        CameraPage.Visibility = selected == "camera" ? Visibility.Visible : Visibility.Collapsed;
        AdjustmentsPage.Visibility = selected == "adjustments" ? Visibility.Visible : Visibility.Collapsed;
        PreviewPage.Visibility = selected == "preview" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = selected == "settings" ? Visibility.Visible : Visibility.Collapsed;
        AirPage.Visibility = selected == "air" ? Visibility.Visible : Visibility.Collapsed;
    }
    private async Task Invoke(Func<Task> action)
    { try { await action(); } catch (Exception ex) { Diagnostic.Text = ex.Message; App.Log(ex); } }
    private async void OnToggleStream(object sender, RoutedEventArgs e) => await Invoke(_service.ToggleStreamAsync);
    private async void OnSwitchCamera(object sender, RoutedEventArgs e)
    {
        _pending.Clear(); _state = null; _switching = true; _lastSent = Stopwatch.GetTimestamp(); Adjustments.IsEnabled = Torch.IsEnabled = false;
        await Invoke(_service.SwitchCameraAsync);
    }
    private void OnAdjustment(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (sender is not Slider slider || slider.Tag is not string key) return;
        slider.Header = key switch
        {
            "exposure_bias" => $"Exposure compensation · {e.NewValue:F1} EV",
            "iso" => $"ISO · {e.NewValue:F0}", "shutter_ms" => $"Exposure duration · {e.NewValue:F2} ms",
            "focus" => $"Manual focus · {e.NewValue:F2}", "temperature" => $"Temperature · {e.NewValue:F0} K",
            "tint" => $"Tint · {e.NewValue:F0}", _ => slider.Header
        };
        Queue(key, e.NewValue);
    }
    private void OnExposureMode(object sender, RoutedEventArgs e) { if (_ready && !_updating) { Queue("auto_exposure", AutoExposure.IsOn ? 1 : 0); UpdateEnabled(); } }
    private void OnFocusMode(object sender, RoutedEventArgs e) { if (_ready && !_updating) { Queue("auto_focus", AutoFocus.IsOn ? 1 : 0); UpdateEnabled(); } }
    private void OnWhiteBalanceMode(object sender, RoutedEventArgs e) { if (_ready && !_updating) { Queue("auto_white_balance", AutoWhiteBalance.IsOn ? 1 : 0); UpdateEnabled(); } }
    private void OnTorch(object sender, RoutedEventArgs e) => Queue("torch_on", Torch.IsOn ? 1 : 0);
    private void OnTorchLevel(object sender, RangeBaseValueChangedEventArgs e)
    {
        // Keep Slider.Header stable while WinUI handles thumb and tooltip events.
        if (TorchValue is not null) TorchValue.Text = $"{e.NewValue:P0}";
        Queue("torch_level", e.NewValue);
    }
    internal void ToggleTorch() { if (_state?.TorchAvailable == true && _service.Streaming) Queue("torch_on", _state.TorchOn ? 0 : 1); }
    private void OnReset(object sender, RoutedEventArgs e) { _pending.Clear(); Queue("reset", 1); }
    private void Queue(string key, double value)
    {
        if (!_ready || _updating || _state is null || !_service.Connected || !double.IsFinite(value)) return;
        _pending[key] = value; ControlStatus.Text = "Applying on iPhone…";
    }
    private async Task FlushCommands()
    {
        if (_switching && Stopwatch.GetElapsedTime(_lastSent).TotalSeconds > 5)
        { _switching = false; ControlStatus.Text = "Camera switch not confirmed. Refreshing controls…"; await Invoke(() => _service.Connection.RequestCameraStateAsync()); }
        if (_sending || !_service.Connected || _state is null) return;
        if (_pending.Count == 0)
        {
            if (_requestId > _confirmedId && _timedOutId != _requestId && Stopwatch.GetElapsedTime(_lastSent).TotalSeconds > 5)
            { _timedOutId = _requestId; ControlStatus.Text = "Phone did not confirm the change. Reconnecting may help."; }
            return;
        }
        var request = new CameraSettings { RequestId = ++_requestId, CameraId = _state.CameraId, Values = new(_pending) };
        _pending.Clear(); _sending = true; _lastSent = Stopwatch.GetTimestamp();
        App.Log("CONTROL", $"Sending camera settings #{request.RequestId}: {string.Join(", ", request.Values.Select(v => $"{v.Key}={v.Value:G6}"))}");
        try { await _service.Connection.SetCameraSettingsAsync(request); }
        catch (Exception ex) { ControlStatus.Text = ex.Message; App.Log(ex); }
        finally { _sending = false; }
    }
    private void ApplyState(CameraState state)
    {
        if (state.RequestId > 0) App.Log("CONTROL", $"Received camera settings #{state.RequestId}: torch={state.TorchOn}, level={state.Values.GetValueOrDefault("torch_level"):G6}, error={state.Error ?? "none"}");
        if (_switching || state.IsStaleComparedTo(_requestId)) return;
        if (string.IsNullOrEmpty(state.CameraId))
        { _state = null; Adjustments.IsEnabled = Torch.IsEnabled = TorchLevel.IsEnabled = false; return; }
        _state = state;
        _updating = true;
        try
        {
            foreach (var (key, slider) in _sliders)
            {
                if (_editingSliders.Contains(slider)) continue;
                if (state.Ranges.TryGetValue(key, out var range) && range.IsValid)
                {
                    // Expand before shrinking so neither setter creates an inverted range.
                    if (range.Max > slider.Maximum) slider.Maximum = range.Max;
                    if (range.Min < slider.Minimum) slider.Minimum = range.Min;
                    if (slider.Maximum != range.Max) slider.Maximum = range.Max;
                    if (slider.Minimum != range.Min) slider.Minimum = range.Min;
                }
                if (_pending.Count == 0 && (state.RequestId == 0 && (_requestId == _confirmedId || _requestId == _timedOutId) || state.RequestId >= _requestId) && state.Values.TryGetValue(key, out var value) && double.IsFinite(value))
                    slider.Value = Math.Clamp(value, slider.Minimum, slider.Maximum);
            }
            if (_pending.Count == 0 && (state.RequestId >= _requestId || _confirmedId == _requestId || _timedOutId == _requestId))
            {
                AutoExposure.IsOn = state.Values.GetValueOrDefault("auto_exposure", 1) != 0;
                AutoFocus.IsOn = state.Values.GetValueOrDefault("auto_focus", 1) != 0;
                AutoWhiteBalance.IsOn = state.Values.GetValueOrDefault("auto_white_balance", 1) != 0;
                Torch.IsOn = state.TorchOn;
            }
            if (state.RequestId >= _requestId) _confirmedId = state.RequestId;
            if (state.Error is not null) ControlStatus.Text = state.Error;
            else if (_pending.Count == 0 && _requestId == _confirmedId) ControlStatus.Text = "Confirmed by iPhone";
            Adjustments.IsEnabled = _service.Connected;
            UpdateEnabled(); RefreshModes();
        }
        finally { _updating = false; }
    }
    private void UpdateEnabled()
    {
        if (_state is null) return;
        foreach (var (key, slider) in _sliders)
            if (!_editingSliders.Contains(slider)) slider.IsEnabled = _state.Ranges.TryGetValue(key, out var range) && range.IsValid;
        AutoExposure.IsEnabled = _state.AutoExposureSupported && _state.Ranges.ContainsKey("iso");
        AutoFocus.IsEnabled = _state.AutoFocusSupported && _state.Ranges.ContainsKey("focus");
        AutoWhiteBalance.IsEnabled = _state.AutoWhiteBalanceSupported && _state.Ranges.ContainsKey("temperature");
        Iso.IsEnabled &= !AutoExposure.IsOn; Shutter.IsEnabled &= !AutoExposure.IsOn;
        ExposureBias.IsEnabled &= AutoExposure.IsOn;
        Focus.IsEnabled &= !AutoFocus.IsOn;
        Temperature.IsEnabled &= !AutoWhiteBalance.IsOn; Tint.IsEnabled &= !AutoWhiteBalance.IsOn;
        Torch.IsEnabled = _state.TorchAvailable && _service.Streaming;
        if (!_editingSliders.Contains(TorchLevel)) TorchLevel.IsEnabled = Torch.IsEnabled && _state.TorchOn && _state.Ranges.TryGetValue("torch_level", out var torchRange) && torchRange.IsValid;
    }
    private void OnPreviewOptions(object sender, object e)
    {
        if (!_ready) return;
        var settings = App.Settings;
        settings.PreviewFps = int.Parse((string)((ComboBoxItem)PreviewRate.SelectedItem).Tag);
        settings.PreviewFill = FillPreview.IsOn; settings.PreviewMirror = MirrorPreview.IsOn;
        _service.PreviewFps = settings.PreviewFps; UpdatePreviewVisibility(); SaveSettings();
    }
    private void OnPreviewSize(object sender, SizeChangedEventArgs e)
    {
        try { _gpu?.Resize(); }
        catch (Exception ex) { App.Log(ex); _gpu?.Dispose(); _gpu = null; Diagnostic.Text = "Preview resizing: " + ex.Message; }
    }
    private void OnFullscreen(object sender, RoutedEventArgs e)
    {
        _fullscreen = !_fullscreen;
        AppWindow.SetPresenter(_fullscreen ? AppWindowPresenterKind.FullScreen : AppWindowPresenterKind.Overlapped);
        Navigation.IsPaneOpen = !_fullscreen && Root.ActualWidth >= 1000;
        TopBar.Visibility = PerformanceStatus.Visibility = ConnectionDetails.Visibility = _fullscreen ? Visibility.Collapsed : Visibility.Visible;
        PreviewLayout.Padding = new Thickness(_fullscreen ? 0 : 24);
        PreviewLayout.RowSpacing = _fullscreen ? 0 : 16;
        PreviewBorder.CornerRadius = new CornerRadius(_fullscreen ? 0 : 12);
        FullscreenExit.Visibility = _fullscreen ? Visibility.Visible : Visibility.Collapsed;
        if (_fullscreen) FullscreenExit.Focus(FocusState.Programmatic);
    }
    private void OnSettings(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        var settings = App.Settings;
        settings.CloseToTray = CloseToTray.IsOn; settings.StartMinimized = StartMinimized.IsOn;
        if (settings.Logging != Logging.IsOn) { settings.Logging = Logging.IsOn; App.SetLogging(settings.Logging); }
        try { if (settings.LaunchAtSignIn != LaunchAtSignIn.IsOn) settings.SetLaunchAtSignIn(LaunchAtSignIn.IsOn); SaveSettings(); }
        catch (Exception ex) { Diagnostic.Text = ex.Message; App.Log(ex); }
    }
    private void SaveSettings() { try { App.Settings.Save(); } catch (Exception ex) { Diagnostic.Text = ex.Message; App.Log(ex); } }
    private void OnOpenLogs(object sender, RoutedEventArgs e)
    {
        try { System.IO.Directory.CreateDirectory(App.LogDirectory); Process.Start(new ProcessStartInfo(App.LogDirectory) { UseShellExecute = true }); }
        catch (Exception ex) { Diagnostic.Text = ex.Message; App.Log(ex); }
    }
    private async void OnExit(object sender, RoutedEventArgs e) => await _app.ExitAsync();
}
