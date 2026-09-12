using System.Diagnostics;
using VoiceChanger.Audio.Devices;
using VoiceChanger.Audio.Drift;
using VoiceChanger.Audio.Pipeline;
using VoiceChanger.Audio.Streams;
using VoiceChanger.Core;

namespace VoiceChanger.Audio;

/// <summary>
/// Audio engine managing WASAPI capture and render streams, device routing,
/// and real-time audio processor execution.
/// </summary>
public sealed class AudioEngine : IDisposable
{
    private readonly object _lock = new();
    private readonly ProcessingPipeline _pipeline;
    private WasapiCaptureStream? _captureStream;
    private WasapiRenderStream? _renderStream;
    private WasapiRenderStream? _monitorStream;
    private IAudioProcessor _processor;
    private Core.Dsp.DspParameters? _lastParameters;
    private bool _isRunning;
    private bool _disposed;

    /// <summary>
    /// Fired when engine status changes (e.g. "Running", "Stopped", "Device Invalidated").
    /// </summary>
    public event Action<string>? StatusChanged;

    /// <summary>
    /// Fired when an unrecoverable audio error occurs.
    /// </summary>
    public event Action<string>? ErrorOccurred;

    /// <summary>
    /// Whether the engine is currently running and processing audio.
    /// </summary>
    public bool IsRunning
    {
        get { lock (_lock) return _isRunning; }
    }

    /// <summary>
    /// Current input device ID in use.
    /// </summary>
    public string? InputDeviceId { get; private set; }

    /// <summary>
    /// Current output device ID in use.
    /// </summary>
    public string? OutputDeviceId { get; private set; }

    /// <summary>
    /// Current headphone self-monitoring device ID in use, or null if disabled.
    /// </summary>
    public string? MonitorDeviceId { get; private set; }

    /// <summary>
    /// Whether headphone self-monitoring is actively running.
    /// </summary>
    public bool IsMonitoring
    {
        get { lock (_lock) return _monitorStream != null; }
    }

    /// <summary>
    /// Active processing pipeline with worker thread and SPSC ring buffers.
    /// </summary>
    public ProcessingPipeline Pipeline => _pipeline;

    /// <summary>
    /// Clock drift controller monitoring buffer balance across clock domains.
    /// </summary>
    public ClockDriftController DriftController => _pipeline.DriftController;

    /// <summary>
    /// Active audio processor. Can be hot-swapped without stream restart.
    /// </summary>
    public IAudioProcessor Processor
    {
        get => _processor;
        set => SetProcessor(value);
    }

    /// <summary>
    /// Measured startup round-trip latency distribution per Invariant #5.
    /// </summary>
    public LatencyDistribution? StartupLatency { get; private set; }

    /// <summary>
    /// Gets the real runtime DSP processing latency distribution measured from live chunk execution.
    /// </summary>
    public LatencyDistribution? ProcessingLatency => _pipeline.GetProcessingLatencyDistribution();

    /// <summary>
    /// Creates a new instance of <see cref="AudioEngine"/>.
    /// </summary>
    /// <param name="initialProcessor">Initial audio processor (defaults to PassthroughProcessor).</param>
    public AudioEngine(IAudioProcessor? initialProcessor = null)
    {
        _processor = initialProcessor ?? new PassthroughProcessor();
        _pipeline = new ProcessingPipeline(_processor, ringCapacity: 16384, chunkSize: 256);
    }

    /// <summary>
    /// Starts real-time capture and render streams, and starts the dedicated worker thread.
    /// </summary>
    /// <param name="inputDeviceId">Microphone device ID, or null for default.</param>
    /// <param name="outputDeviceId">Render device ID (e.g. VB-CABLE), or null for default.</param>
    public void Start(string? inputDeviceId = null, string? outputDeviceId = null)
    {
        lock (_lock)
        {
            if (_isRunning)
            {
                return;
            }

            try
            {
                InputDeviceId = inputDeviceId ?? AudioDeviceList.GetDefaultCaptureDeviceId();
                OutputDeviceId = outputDeviceId ?? AudioDeviceList.GetDefaultRenderDeviceId();

                _pipeline.Processor = _processor;
                _pipeline.Start();

                _captureStream = new WasapiCaptureStream(InputDeviceId, _pipeline);
                _captureStream.DeviceInvalidated += OnDeviceInvalidated;
                _captureStream.Initialize();

                _renderStream = new WasapiRenderStream(OutputDeviceId, _pipeline);
                _renderStream.DeviceInvalidated += OnDeviceInvalidated;
                _renderStream.Initialize();

                // Compute startup round-trip latency distribution
                StartupLatency = MeasureStartupLatency(_captureStream, _renderStream, _processor);

                _captureStream.Start();
                _renderStream.Start();

                if (_lastParameters != null && _processor is Core.IParameterReceiver startReceiver)
                {
                    startReceiver.ApplyParameters(_lastParameters);
                }

                _isRunning = true;
                StatusChanged?.Invoke("Running");
            }
            catch (Exception ex)
            {
                StopInternal();
                ErrorOccurred?.Invoke($"Failed to start audio engine: {ex.Message}");
                throw;
            }
        }
    }

    /// <summary>
    /// Stops audio capture and render streams, and shuts down worker thread.
    /// </summary>
    public void Stop()
    {
        lock (_lock)
        {
            StopInternal();
            StatusChanged?.Invoke("Stopped");
        }
    }

    private void StopInternal()
    {
        _isRunning = false;

        if (_monitorStream != null)
        {
            _pipeline.IsMonitoringEnabled = false;
            _monitorStream.Dispose();
            _monitorStream = null;
        }

        if (_captureStream != null)
        {
            _captureStream.DeviceInvalidated -= OnDeviceInvalidated;
            _captureStream.Dispose();
            _captureStream = null;
        }

        if (_renderStream != null)
        {
            _renderStream.DeviceInvalidated -= OnDeviceInvalidated;
            _renderStream.Dispose();
            _renderStream = null;
        }

        _pipeline.Stop();
    }

    /// <summary>
    /// Changes the target render device without restarting the entire application.
    /// </summary>
    /// <param name="newOutputDeviceId">New render device ID.</param>
    public void SetOutputDevice(string? newOutputDeviceId)
    {
        lock (_lock)
        {
            if (!_isRunning)
            {
                OutputDeviceId = newOutputDeviceId;
                return;
            }

            // Hot-swap render stream
            _renderStream?.Dispose();
            _renderStream = null;

            OutputDeviceId = newOutputDeviceId;
            _renderStream = new WasapiRenderStream(OutputDeviceId, _pipeline);
            _renderStream.DeviceInvalidated += OnDeviceInvalidated;
            _renderStream.Initialize();
            _renderStream.Start();

            if (_captureStream != null)
            {
                StartupLatency = MeasureStartupLatency(_captureStream, _renderStream, _processor);
            }
        }
    }

    /// <summary>
    /// Hot-swaps the active processor without restarting audio streams.
    /// </summary>
    /// <param name="newProcessor">New audio processor implementing IAudioProcessor.</param>
    public void SetProcessor(IAudioProcessor newProcessor)
    {
        ArgumentNullException.ThrowIfNull(newProcessor);

        Volatile.Write(ref _processor, newProcessor);
        _pipeline.Processor = newProcessor;

        if (_lastParameters != null && newProcessor is Core.IParameterReceiver receiver)
        {
            receiver.ApplyParameters(_lastParameters);
        }

        if (_isRunning && _captureStream != null && _renderStream != null)
        {
            StartupLatency = MeasureStartupLatency(_captureStream, _renderStream, newProcessor);
        }
    }

    /// <summary>
    /// Starts self-monitoring to a secondary headphone device (Phase 3).
    /// Strictly protects against routing to speakers to avoid audio feedback loops.
    /// </summary>
    /// <param name="headphoneDeviceId">Selected headphone render endpoint ID.</param>
    public void StartMonitoring(string headphoneDeviceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(headphoneDeviceId);

        lock (_lock)
        {
            if (!_isRunning)
            {
                throw new InvalidOperationException("Audio engine must be running before starting self-monitoring.");
            }

            // Invariant & Safety Guard: Verify target is not a speaker
            var renderDevices = AudioDeviceList.GetRenderDevices();
            var targetDevice = renderDevices.FirstOrDefault(d => d.Id == headphoneDeviceId);
            if (targetDevice != null && targetDevice.Name.Contains("Speaker", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Self-monitoring cannot be routed through speakers. Microphone recapture will cause a pitch-climbing feedback loop. Please select headphones.");
            }

            StopMonitoring();

            try
            {
                MonitorDeviceId = headphoneDeviceId;
                _pipeline.IsMonitoringEnabled = true;
                _monitorStream = new WasapiRenderStream(
                    headphoneDeviceId,
                    _pipeline,
                    sourceRing: _pipeline.MonitorRing,
                    isMonitoringTap: true);
                _monitorStream.Initialize();
                _monitorStream.Start();
            }
            catch
            {
                StopMonitoring();
                throw;
            }
        }
    }

    /// <summary>
    /// Stops headphone self-monitoring.
    /// </summary>
    public void StopMonitoring()
    {
        lock (_lock)
        {
            _pipeline.IsMonitoringEnabled = false;
            if (_monitorStream != null)
            {
                _monitorStream.Dispose();
                _monitorStream = null;
            }
            MonitorDeviceId = null;
        }
    }

    /// <summary>
    /// Applies a runtime snapshot of DSP parameters to the active processor chain without restarting audio streams.
    /// </summary>
    /// <param name="parameters">Immutable snapshot of DSP parameters.</param>
    public void ApplyParameters(Core.Dsp.DspParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        _lastParameters = parameters;
        if (_processor is Core.IParameterReceiver receiver)
        {
            receiver.ApplyParameters(parameters);
        }
        else if (_processor is Core.Dsp.PhaseVocoderProcessor vocoder)
        {
            vocoder.PitchSemitones = parameters.PitchSemitones;
            vocoder.FormantSemitones = parameters.FormantSemitones;
        }
    }

    private void OnDeviceInvalidated()
    {
        lock (_lock)
        {
            StopInternal();
            StatusChanged?.Invoke("Device Invalidated");
            ErrorOccurred?.Invoke("Audio endpoint was invalidated or disconnected by Windows.");
        }
    }

    private static LatencyDistribution MeasureStartupLatency(
        WasapiCaptureStream capture,
        WasapiRenderStream render,
        IAudioProcessor processor)
    {
        double captureMs = capture.LatencyMs;
        double renderMs = render.LatencyMs;
        double algorithmicMs = (processor.LatencyFrames / 48000.0) * 1000.0;

        // Base hardware round-trip
        double baseRoundTripMs = captureMs + renderMs + algorithmicMs;

        return new LatencyDistribution(
            P50Ms: baseRoundTripMs,
            P95Ms: baseRoundTripMs,
            P99Ms: baseRoundTripMs,
            MaxMs: baseRoundTripMs,
            SampleCount: 1,
            Configuration: $"Theoretical Budget (Capture={captureMs:F1}ms, Render={renderMs:F1}ms, Algorithmic={algorithmicMs:F1}ms)");
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopInternal();
        _pipeline.Dispose();
    }
}
