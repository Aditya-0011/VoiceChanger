using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using VoiceChanger.App.Services;
using VoiceChanger.Audio;
using VoiceChanger.Audio.Devices;
using VoiceChanger.Audio.Telemetry;
using VoiceChanger.Core.Dsp;
using VoiceChanger.Core.Presets;

namespace VoiceChanger.App;

/// <summary>
/// Voice transformation, presets, and diagnostics page (Phase 3).
/// </summary>
public sealed partial class MainPage : Page
{
    private readonly ProcessorChain _chain;
    private readonly AudioEngine _engine;
    private readonly PresetManager _presetManager;
    private readonly TelemetryLogger _telemetryLogger;
    private readonly DispatcherTimer _diagTimer;
    private HotkeyService? _hotkeyService;
    private bool _isUpdatingUi;

    public MainPage()
    {
        try
        {
            _chain = new ProcessorChain(new DspParameters());
            _engine = new AudioEngine(_chain);
            _presetManager = new PresetManager();
            _telemetryLogger = new TelemetryLogger();

            _diagTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _diagTimer.Tick += OnDiagTimerTick;

            _engine.StatusChanged += OnEngineStatusChanged;
            _engine.ErrorOccurred += OnEngineErrorOccurred;

            InitializeComponent();

            Loaded += OnPageLoaded;
            Unloaded += OnPageUnloaded;
        }
        catch (Exception ex)
        {
            try
            {
                File.WriteAllText(@"crash.txt", $"MainPage Constructor Exception: {ex.Message}\n{ex}\n{ex.StackTrace}");
            }
            catch { }
            throw;
        }
    }

    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        WarmupDsp();
        RefreshDevices();
        LoadPresetsUi();
        InitializeHotkeys();

        if (LogPathText != null)
        {
            LogPathText.Text = $"Destination: {_telemetryLogger.LogFilePath}";
        }

        // Support command-line flag --telemetry or --log-telemetry or -t, or environment variable to auto-enable on launch
        if (App.IsTelemetryFlagEnabled() && TelemetryLogToggle != null)
        {
            _telemetryLogger.IsEnabled = true;
            TelemetryLogToggle.IsOn = true;
        }
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs e)
    {
        _hotkeyService?.Dispose();
        _hotkeyService = null;

        _telemetryLogger?.Dispose();

        _engine.Stop();
        _engine.Dispose();
    }

    /// <summary>
    /// Promotes DSP code past tier-0 JIT prior to opening audio stream (Project.md §6).
    /// </summary>
    private static void WarmupDsp()
    {
        var chain = new ProcessorChain(new DspParameters
        {
            PitchSemitones = 3.0f,
            FormantSemitones = 4.0f,
            NoiseGateEnabled = true,
            NoiseGateThresholdDb = -45.0f,
            DryWetMix = 0.8f
        });
        chain.Prepare(48000, 256);

        Span<float> input = stackalloc float[256];
        Span<float> output = stackalloc float[256];
        input.Clear();

        for (int i = 0; i < 500; i++)
        {
            chain.Process(input, output);
        }
    }

    private void RefreshDevices()
    {
        try
        {
            var captureDevices = AudioDeviceList.GetCaptureDevices();
            var renderDevices = AudioDeviceList.GetRenderDevices();

            InputDeviceCombo.ItemsSource = captureDevices;
            if (captureDevices.Count > 0)
            {
                int defaultIndex = 0;
                for (int i = 0; i < captureDevices.Count; i++)
                {
                    if (captureDevices[i].IsDefault)
                    {
                        defaultIndex = i;
                        break;
                    }
                }
                InputDeviceCombo.SelectedIndex = defaultIndex;
            }

            OutputDeviceCombo.ItemsSource = renderDevices;
            if (renderDevices.Count > 0)
            {
                // Prefer VB-CABLE for primary output if present per Project.md Phase 0
                int selectedIndex = 0;
                for (int i = 0; i < renderDevices.Count; i++)
                {
                    if (renderDevices[i].Name.Contains("CABLE", StringComparison.OrdinalIgnoreCase))
                    {
                        selectedIndex = i;
                        break;
                    }

                    if (renderDevices[i].IsDefault)
                    {
                        selectedIndex = i;
                    }
                }
                OutputDeviceCombo.SelectedIndex = selectedIndex;
            }

            // Headphone Monitoring devices: filter out speakers to prevent feedback loops (Phase 3 Safety Guard)
            var headphoneDevices = renderDevices.Where(d => !d.Name.Contains("Speaker", StringComparison.OrdinalIgnoreCase)).ToList();
            MonitorDeviceCombo.ItemsSource = headphoneDevices.Count > 0 ? headphoneDevices : renderDevices;
            if (MonitorDeviceCombo.Items.Count > 0)
            {
                // Pick first non-cable headphone or default
                int monitorIndex = 0;
                for (int i = 0; i < MonitorDeviceCombo.Items.Count; i++)
                {
                    if (MonitorDeviceCombo.Items[i] is AudioDeviceInfo dev &&
                        (dev.Name.Contains("Headphone", StringComparison.OrdinalIgnoreCase) ||
                         dev.Name.Contains("Headset", StringComparison.OrdinalIgnoreCase)))
                    {
                        monitorIndex = i;
                        break;
                    }
                }
                MonitorDeviceCombo.SelectedIndex = monitorIndex;
            }

            if (captureDevices.Count == 0)
            {
                ShowAlert("No active microphones detected. Please check Windows Sound Settings.", InfoBarSeverity.Warning);
            }
            else if (renderDevices.Count == 0)
            {
                ShowAlert("No active playback devices detected. Please check Windows Sound Settings.", InfoBarSeverity.Warning);
            }
            else
            {
                AlertInfoBar.IsOpen = false;
            }
        }
        catch (Exception ex)
        {
            ShowAlert($"Error enumerating devices: {ex.Message}", InfoBarSeverity.Warning);
        }
    }

    private void OnRefreshDevicesClicked(object sender, RoutedEventArgs e)
    {
        RefreshDevices();
    }

    private void LoadPresetsUi()
    {
        try
        {
            _presetManager.Load();
            PresetCombo.ItemsSource = null;
            PresetCombo.ItemsSource = _presetManager.Presets;

            if (_presetManager.Presets.Count > 0)
            {
                PresetCombo.SelectedIndex = 0;
            }
        }
        catch (Exception ex)
        {
            ShowAlert($"Error loading presets: {ex.Message}", InfoBarSeverity.Warning);
        }
    }

    private void InitializeHotkeys()
    {
        try
        {
            nint hWnd = App.MainWindowInstance != null ? WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindowInstance) : 0;
            if (hWnd != 0)
            {
                _hotkeyService?.Dispose();
                _hotkeyService = new HotkeyService(hWnd, DispatcherQueue);
                _hotkeyService.HotkeyPressed += OnHotkeyPressed;

                foreach (var preset in _presetManager.Presets)
                {
                    if (!string.IsNullOrWhiteSpace(preset.Hotkey))
                    {
                        _hotkeyService.Register(preset.Hotkey, preset.Id);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error initializing hotkeys: {ex.Message}");
        }
    }

    private void OnHotkeyPressed(string presetId)
    {
        var preset = _presetManager.Presets.FirstOrDefault(p => p.Id == presetId);
        if (preset != null)
        {
            ApplyPresetToUi(preset);
            ShowAlert($"Activated preset: '{preset.Name}' via global hotkey.", InfoBarSeverity.Informational);
        }
    }

    private void ApplyPresetToUi(Preset preset)
    {
        _isUpdatingUi = true;
        try
        {
            for (int i = 0; i < PresetCombo.Items.Count; i++)
            {
                if (PresetCombo.Items[i] is Preset p && p.Id == preset.Id)
                {
                    PresetCombo.SelectedIndex = i;
                    break;
                }
            }

            if (PitchSlider != null) PitchSlider.Value = preset.PitchSemitones;
            if (FormantSlider != null) FormantSlider.Value = preset.FormantSemitones;
            if (VocoderToggle != null) VocoderToggle.IsOn = preset.VocoderEnabled;
            if (GateToggle != null) GateToggle.IsOn = preset.NoiseGateEnabled;
            if (GateThresholdSlider != null) GateThresholdSlider.Value = preset.NoiseGateThresholdDb;
            if (GateAttackSlider != null) GateAttackSlider.Value = preset.NoiseGateAttackMs;
            if (GateReleaseSlider != null) GateReleaseSlider.Value = preset.NoiseGateReleaseMs;
            if (DryWetSlider != null) DryWetSlider.Value = preset.DryWetMix * 100.0;

            UpdateParameterTextDisplays();
            _engine.ApplyParameters(preset.ToParameters());
        }
        finally
        {
            _isUpdatingUi = false;
        }
    }

    private void OnPresetSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingUi) return;

        if (PresetCombo.SelectedItem is Preset preset)
        {
            ApplyPresetToUi(preset);
        }
    }

    private void OnSavePresetClicked(object sender, RoutedEventArgs e)
    {
        if (PresetCombo.SelectedItem is Preset preset)
        {
            preset.PitchSemitones = (float)PitchSlider.Value;
            preset.FormantSemitones = (float)FormantSlider.Value;
            preset.VocoderEnabled = VocoderToggle.IsOn;
            preset.NoiseGateEnabled = GateToggle.IsOn;
            preset.NoiseGateThresholdDb = (float)GateThresholdSlider.Value;
            preset.NoiseGateAttackMs = (float)GateAttackSlider.Value;
            preset.NoiseGateReleaseMs = (float)GateReleaseSlider.Value;
            preset.DryWetMix = (float)(DryWetSlider.Value / 100.0);

            _presetManager.AddOrUpdate(preset);
            ShowAlert($"Preset '{preset.Name}' saved successfully to %APPDATA%.", InfoBarSeverity.Success);
        }
    }

    private void OnResetDefaultsClicked(object sender, RoutedEventArgs e)
    {
        _presetManager.ResetToDefaults();
        LoadPresetsUi();
        InitializeHotkeys();
        ShowAlert("Factory default presets restored.", InfoBarSeverity.Informational);
    }

    private void OnQuickPresetTagClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tagStr)
        {
            OnHotkeyPressed(tagStr);
        }
    }

    private void OnParameterControlChanged(object? sender, object? e)
    {
        if (_isUpdatingUi) return;

        UpdateParameterTextDisplays();

        var parameters = new DspParameters
        {
            PitchSemitones = (float)(PitchSlider?.Value ?? 0.0),
            FormantSemitones = (float)(FormantSlider?.Value ?? 0.0),
            VocoderEnabled = VocoderToggle?.IsOn ?? true,
            NoiseGateEnabled = GateToggle?.IsOn ?? true,
            NoiseGateThresholdDb = (float)(GateThresholdSlider?.Value ?? -45.0),
            NoiseGateAttackMs = (float)(GateAttackSlider?.Value ?? 5.0),
            NoiseGateReleaseMs = (float)(GateReleaseSlider?.Value ?? 80.0),
            DryWetMix = (float)((DryWetSlider?.Value ?? 100.0) / 100.0)
        };

        _engine.ApplyParameters(parameters);
    }

    private void UpdateParameterTextDisplays()
    {
        if (PitchSlider != null && PitchValueText != null)
        {
            float pitch = (float)PitchSlider.Value;
            double ratio = Math.Pow(2.0, pitch / 12.0);
            PitchValueText.Text = $"{pitch:+0.0;-0.0;0.0} semitones ({ratio:F2}x)";
        }

        if (FormantSlider != null && FormantValueText != null)
        {
            float formant = (float)FormantSlider.Value;
            double ratio = Math.Pow(2.0, formant / 12.0);
            FormantValueText.Text = $"{formant:+0.0;-0.0;0.0} semitones ({ratio:F2}x)";
        }

        if (GateThresholdSlider != null && GateThresholdText != null)
        {
            GateThresholdText.Text = $"{GateThresholdSlider.Value:F0} dB";
        }

        if (GateAttackSlider != null && GateAttackText != null)
        {
            GateAttackText.Text = $"{GateAttackSlider.Value:F0} ms";
        }

        if (GateReleaseSlider != null && GateReleaseText != null)
        {
            GateReleaseText.Text = $"{GateReleaseSlider.Value:F0} ms";
        }

        if (DryWetSlider != null && DryWetText != null)
        {
            int val = (int)DryWetSlider.Value;
            DryWetText.Text = val switch
            {
                100 => "100% (Full Wet)",
                0 => "0% (Full Dry)",
                _ => $"{val}%"
            };
        }
    }

    private void OnMonitorToggled(object sender, RoutedEventArgs e)
    {
        if (MonitorToggle.IsOn)
        {
            MonitorDevicePanel.Visibility = Visibility.Visible;
            if (_engine.IsRunning)
            {
                StartMonitoringSafe();
            }
        }
        else
        {
            MonitorDevicePanel.Visibility = Visibility.Collapsed;
            _engine.StopMonitoring();
        }
    }

    private void OnMonitorDeviceSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_engine.IsRunning && MonitorToggle.IsOn)
        {
            StartMonitoringSafe();
        }
    }

    private void StartMonitoringSafe()
    {
        if (MonitorDeviceCombo.SelectedItem is AudioDeviceInfo monitorDevice)
        {
            try
            {
                _engine.StartMonitoring(monitorDevice.Id);
                AlertInfoBar.IsOpen = false;
            }
            catch (Exception ex)
            {
                MonitorToggle.IsOn = false;
                ShowAlert($"Monitoring notice: {ex.Message}", InfoBarSeverity.Warning);
            }
        }
    }

    private void OnToggleEngineClicked(object sender, RoutedEventArgs e)
    {
        if (_engine.IsRunning)
        {
            _engine.Stop();
            _diagTimer.Stop();
            ToggleEngineButton.Content = "Start Audio Engine";
            StatusText.Text = "Stopped";
            ResetDiagnosticsUi();
            return;
        }

        var selectedInput = InputDeviceCombo.SelectedItem as AudioDeviceInfo;
        var selectedOutput = OutputDeviceCombo.SelectedItem as AudioDeviceInfo;

        if (selectedInput == null || selectedOutput == null)
        {
            ShowAlert("Please select an input microphone and an output device from the dropdowns.", InfoBarSeverity.Warning);
            return;
        }

        try
        {
            _engine.Start(selectedInput.Id, selectedOutput.Id);
            _diagTimer.Start();
            ToggleEngineButton.Content = "Stop Audio Engine";
            StatusText.Text = "Running";

            // If headphone self-monitoring is checked, open secondary monitor stream
            if (MonitorToggle.IsOn)
            {
                StartMonitoringSafe();
            }

            if (_engine.StartupLatency is { } latency)
            {
                LatencyP50Text.Text = $"{latency.P50Ms:F1} ms";
                LatencyP95Text.Text = $"{latency.P95Ms:F1} ms";
                LatencyP99Text.Text = $"{latency.P99Ms:F1} ms";
                LatencyMaxText.Text = $"{latency.MaxMs:F1} ms";
                LatencyDetailsText.Text = $"{latency.Configuration} (Samples: {latency.SampleCount})";
            }
        }
        catch (Exception ex)
        {
            _diagTimer.Stop();
            ShowAlert($"Failed to start audio engine: {ex.Message}", InfoBarSeverity.Error);
            ToggleEngineButton.Content = "Start Audio Engine";
            StatusText.Text = "Error";
            ResetDiagnosticsUi();
        }
    }

    private void OnDiagTimerTick(object? sender, object e)
    {
        if (!_engine.IsRunning)
        {
            return;
        }

        var drift = _engine.DriftController;
        var pipeline = _engine.Pipeline;

        if (_engine.ProcessingLatency is { } procLatency)
        {
            LatencyP50Text.Text = $"{procLatency.P50Ms:F2} ms";
            LatencyP95Text.Text = $"{procLatency.P95Ms:F2} ms";
            LatencyP99Text.Text = $"{procLatency.P99Ms:F2} ms";
            LatencyMaxText.Text = $"{procLatency.MaxMs:F2} ms";
            LatencyDetailsText.Text = $"{procLatency.Configuration} (Over {procLatency.SampleCount} chunks)";
        }

        double fillPct = drift.FillPercentage;
        RenderFillText.Text = $"{fillPct:F1} % ({drift.FillMs:F0} ms)";
        RenderFillProgress.Value = Math.Clamp(fillPct, 0, 100);

        DriftRateText.Text = $"{drift.DriftRateSamplesPerSec:+0.0;-0.0;0.0} /s";
        DriftCorrectionsText.Text = $"{drift.DroppedSampleCount} / {drift.DuplicatedSampleCount}";
        IntegrityText.Text = $"{pipeline.OverrunFrames} / {pipeline.UnderrunFrames}";

        // Write live telemetry and parameter snapshot to root telemetry.log
        if (_telemetryLogger.IsEnabled)
        {
            _telemetryLogger.LogSnapshot(
                _engine.ProcessingLatency,
                fillPct,
                drift.FillMs,
                drift.DriftRateSamplesPerSec,
                drift.DroppedSampleCount,
                drift.DuplicatedSampleCount,
                pipeline.OverrunFrames,
                pipeline.UnderrunFrames,
                (PresetCombo?.SelectedItem as Preset)?.Name,
                _chain.Parameters,
                (InputDeviceCombo?.SelectedItem as AudioDeviceInfo)?.Name,
                (OutputDeviceCombo?.SelectedItem as AudioDeviceInfo)?.Name,
                _engine.IsMonitoring,
                (MonitorDeviceCombo?.SelectedItem as AudioDeviceInfo)?.Name);
        }
    }

    private void OnTelemetryLogToggled(object sender, RoutedEventArgs e)
    {
        if (_telemetryLogger != null && TelemetryLogToggle != null)
        {
            _telemetryLogger.IsEnabled = TelemetryLogToggle.IsOn;
            if (TelemetryLogToggle.IsOn)
            {
                ShowAlert($"Continuous telemetry logging active: writing to {_telemetryLogger.LogFilePath}", InfoBarSeverity.Informational);
            }
        }
    }

    private void OnClearLogClicked(object sender, RoutedEventArgs e)
    {
        _telemetryLogger?.ClearLog();
        ShowAlert("Telemetry log cleared.", InfoBarSeverity.Informational);
    }

    private void ResetDiagnosticsUi()
    {
        if (_engine.StartupLatency is { } startup)
        {
            LatencyP50Text.Text = $"{startup.P50Ms:F1} ms";
            LatencyP95Text.Text = $"{startup.P95Ms:F1} ms";
            LatencyP99Text.Text = $"{startup.P99Ms:F1} ms";
            LatencyMaxText.Text = $"{startup.MaxMs:F1} ms";
            LatencyDetailsText.Text = startup.Configuration;
        }
        else
        {
            LatencyP50Text.Text = "-- ms";
            LatencyP95Text.Text = "-- ms";
            LatencyP99Text.Text = "-- ms";
            LatencyMaxText.Text = "-- ms";
            LatencyDetailsText.Text = "Hardware latency not measured";
        }

        RenderFillText.Text = "-- %";
        RenderFillProgress.Value = 0;
        DriftRateText.Text = "0.0 /s";
        DriftCorrectionsText.Text = "0 / 0";
        IntegrityText.Text = "0 / 0";
    }

    private void OnEngineStatusChanged(string status)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            StatusText.Text = status;
            if (status == "Stopped")
            {
                _diagTimer.Stop();
                ToggleEngineButton.Content = "Start Audio Engine";
                ResetDiagnosticsUi();
            }
            else if (status == "Running")
            {
                _diagTimer.Start();
                ToggleEngineButton.Content = "Stop Audio Engine";
            }
        });
    }

    private void OnEngineErrorOccurred(string message)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            _diagTimer.Stop();
            ShowAlert(message, InfoBarSeverity.Error);
            ToggleEngineButton.Content = "Start Audio Engine";
            StatusText.Text = "Stopped";
            ResetDiagnosticsUi();
        });
    }

    private void ShowAlert(string message, InfoBarSeverity severity)
    {
        AlertInfoBar.Message = message;
        AlertInfoBar.Severity = severity;
        AlertInfoBar.IsOpen = true;
    }
}
