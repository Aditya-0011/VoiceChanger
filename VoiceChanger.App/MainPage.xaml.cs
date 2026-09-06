using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using VoiceChanger.Audio;
using VoiceChanger.Audio.Devices;
using VoiceChanger.Core;
using VoiceChanger.Core.Dsp;

namespace VoiceChanger.App;

/// <summary>
/// Voice transformation and diagnostics page.
/// </summary>
public sealed partial class MainPage : Page
{
    private readonly AudioEngine _engine;
    private readonly PassthroughProcessor _passthrough;
    private readonly PhaseVocoderProcessor _vocoder;
    private readonly DispatcherTimer _diagTimer;

    public MainPage()
    {
        try
        {
            _passthrough = new PassthroughProcessor();
            _vocoder = new PhaseVocoderProcessor(frameSize: 1024, analysisHop: 256, initialSemitones: 0.0f);
            _engine = new AudioEngine(_vocoder);

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
                System.IO.File.WriteAllText(@"crash.txt", $"MainPage Constructor Exception: {ex.Message}\n{ex}\n{ex.StackTrace}");
            }
            catch { }
            throw;
        }
    }

    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        WarmupDsp();
        RefreshDevices();
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs e)
    {
        _engine.Stop();
        _engine.Dispose();
    }

    /// <summary>
    /// Promotes DSP code past tier-0 JIT prior to opening audio stream (Project.md §6).
    /// </summary>
    private static void WarmupDsp()
    {
        var passthrough = new PassthroughProcessor();
        passthrough.Prepare(48000, 1024);

        var vocoder = new PhaseVocoderProcessor(frameSize: 1024, analysisHop: 256, initialSemitones: 5.0f);
        vocoder.Prepare(48000, 1024);

        Span<float> input = stackalloc float[256];
        Span<float> output = stackalloc float[256];
        input.Clear();

        for (int i = 0; i < 500; i++)
        {
            passthrough.Process(input, output);
            vocoder.Process(input, output);
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
                // Prefer VB-CABLE if present per Project.md Phase 0
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

        double fillPct = drift.FillPercentage;
        RenderFillText.Text = $"{fillPct:F1} % ({drift.FillMs:F0} ms)";
        RenderFillProgress.Value = Math.Clamp(fillPct, 0, 100);

        DriftRateText.Text = $"{drift.DriftRateSamplesPerSec:+0.0;-0.0;0.0} /s";
        DriftCorrectionsText.Text = $"{drift.DroppedSampleCount} / {drift.DuplicatedSampleCount}";
        IntegrityText.Text = $"{pipeline.OverrunFrames} / {pipeline.UnderrunFrames}";
    }

    private void ResetDiagnosticsUi()
    {
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

    private void OnVocoderToggled(object sender, RoutedEventArgs e)
    {
        if (_engine == null || _vocoder == null || _passthrough == null || VocoderToggle == null)
        {
            return;
        }

        if (VocoderToggle.IsOn)
        {
            if (PitchSlider != null)
            {
                _vocoder.PitchSemitones = (float)PitchSlider.Value;
            }
            _engine.SetProcessor(_vocoder);
        }
        else
        {
            _engine.SetProcessor(_passthrough);
        }
    }

    private void OnPitchSliderValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        float semitones = (float)e.NewValue;
        if (_vocoder != null)
        {
            _vocoder.PitchSemitones = semitones;
        }

        if (PitchValueText != null)
        {
            double ratio = Math.Pow(2.0, semitones / 12.0);
            PitchValueText.Text = $"{semitones:+0.0;-0.0;0.0} semitones ({ratio:F2}x)";
        }
    }

    private void OnPresetClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tagStr && float.TryParse(tagStr, out float semitones))
        {
            if (PitchSlider != null)
            {
                PitchSlider.Value = semitones;
            }

            if (VocoderToggle != null && !VocoderToggle.IsOn)
            {
                VocoderToggle.IsOn = true;
            }
        }
    }

    private void ShowAlert(string message, InfoBarSeverity severity)
    {
        AlertInfoBar.Message = message;
        AlertInfoBar.Severity = severity;
        AlertInfoBar.IsOpen = true;
    }
}
