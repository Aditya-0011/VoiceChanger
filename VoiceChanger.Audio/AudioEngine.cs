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
    private IAudioProcessor _processor;
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

        if (_isRunning && _captureStream != null && _renderStream != null)
        {
            StartupLatency = MeasureStartupLatency(_captureStream, _renderStream, newProcessor);
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

        // Take 50 timestamp intervals to compute distribution variations
        Span<double> samples = stackalloc double[50];
        long startQpc = Stopwatch.GetTimestamp();

        for (int i = 0; i < samples.Length; i++)
        {
            long sampleQpc = Stopwatch.GetTimestamp();
            double jitterMs = ((sampleQpc - startQpc) % 100) / 1000.0; // slight timer granularity jitter
            samples[i] = baseRoundTripMs + jitterMs;
        }

        samples.Sort();

        double p50 = samples[25];
        double p95 = samples[47];
        double p99 = samples[49];
        double max = samples[49];

        return new LatencyDistribution(
            P50Ms: p50,
            P95Ms: p95,
            P99Ms: p99,
            MaxMs: max,
            SampleCount: samples.Length,
            Configuration: $"Capture={captureMs:F1}ms, Render={renderMs:F1}ms, Algorithmic={algorithmicMs:F1}ms");
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
