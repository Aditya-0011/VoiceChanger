using System.Text;
using VoiceChanger.Core.Dsp;

namespace VoiceChanger.Audio.Telemetry;

/// <summary>
/// Service that writes real-time telemetry, DSP latency distributions, and parameter snapshots
/// continuously into a log file located at the root of the project (telemetry.log).
/// </summary>
public sealed class TelemetryLogger : IDisposable
{
    private readonly string _logFilePath;
    private readonly object _lock = new();
    private StreamWriter? _writer;
    private bool _isEnabled;
    private DateTime _lastLogTime = DateTime.MinValue;
    private readonly TimeSpan _throttleInterval = TimeSpan.FromSeconds(1.0);

    /// <summary>
    /// Absolute path to the destination telemetry log file.
    /// </summary>
    public string LogFilePath => _logFilePath;

    /// <summary>
    /// Gets or sets whether continuous telemetry logging is active.
    /// </summary>
    public bool IsEnabled
    {
        get { lock (_lock) return _isEnabled; }
        set
        {
            lock (_lock)
            {
                if (_isEnabled == value) return;
                _isEnabled = value;

                if (_isEnabled)
                {
                    EnsureWriterOpen();
                    _writer?.WriteLine($"================================================================================");
                    _writer?.WriteLine($"=== Telemetry Logging Started: {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} ===");
                    _writer?.WriteLine($"=== Destination: {_logFilePath} ===");
                    _writer?.WriteLine($"================================================================================");
                    _writer?.Flush();
                }
                else
                {
                    _writer?.WriteLine($"=== Telemetry Logging Stopped: {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} ===");
                    _writer?.Flush();
                    CloseWriter();
                }
            }
        }
    }

    /// <summary>
    /// Creates a new instance of <see cref="TelemetryLogger"/>.
    /// </summary>
    /// <param name="customPath">Optional custom path for testing.</param>
    public TelemetryLogger(string? customPath = null)
    {
        _logFilePath = customPath ?? Path.Combine(FindProjectRoot(), "telemetry.log");
    }

    /// <summary>
    /// Gets or sets whether snapshots are also echoed to standard console output.
    /// Default is true.
    /// </summary>
    public bool EchoToConsole { get; set; } = true;

    /// <summary>
    /// Appends a structured telemetry and parameter snapshot to the log file.
    /// Throttled to a maximum rate of once per second to prevent disk thrashing.
    /// </summary>
    public void LogSnapshot(
        LatencyDistribution? procLatency,
        double fillPercentage,
        double fillMs,
        double driftRate,
        long droppedSamples,
        long duplicatedSamples,
        long overruns,
        long underruns,
        string? activePresetName,
        DspParameters dspParams,
        string? inputDevice,
        string? outputDevice,
        bool isMonitoring,
        string? monitorDevice,
        bool force = false)
    {
        lock (_lock)
        {
            if (!_isEnabled)
            {
                return;
            }

            DateTime now = DateTime.UtcNow;
            if (!force && (now - _lastLogTime) < _throttleInterval)
            {
                return;
            }
            _lastLogTime = now;

            EnsureWriterOpen();
            if (_writer == null)
            {
                return;
            }

            string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            string latencyStr = procLatency != null
                ? $"p50={procLatency.P50Ms:F2}ms | p95={procLatency.P95Ms:F2}ms | p99={procLatency.P99Ms:F2}ms | max={procLatency.MaxMs:F2}ms (chunks={procLatency.SampleCount})"
                : "p50=-- | p95=-- | p99=-- | max=--";

            double pitchRatio = Math.Pow(2.0, dspParams.PitchSemitones / 12.0);
            double formantRatio = Math.Pow(2.0, dspParams.FormantSemitones / 12.0);
            string gateStr = dspParams.NoiseGateEnabled
                ? $"ON (Thresh={dspParams.NoiseGateThresholdDb:F1}dB, Att={dspParams.NoiseGateAttackMs:F0}ms, Rel={dspParams.NoiseGateReleaseMs:F0}ms)"
                : "BYPASSED";
            string vocoderStr = dspParams.VocoderEnabled ? "ON" : "BYPASSED";
            string monStr = isMonitoring ? $"ACTIVE ('{monitorDevice ?? "Headphones"}')" : "OFF";

            _writer.WriteLine($"[{timestamp}] DSP Latency: {latencyStr}");
            _writer.WriteLine($"  Ring Buffer: Fill={fillPercentage:F1}% ({fillMs:F0}ms) | Drift={driftRate:+0.0;-0.0;0.0}/s | Drops={droppedSamples} | Dups={duplicatedSamples} | Overruns={overruns} | Underruns={underruns}");
            _writer.WriteLine($"  DSP Params:  Preset='{activePresetName ?? "Custom"}' | Pitch={dspParams.PitchSemitones:+0.0;-0.0;0.0}st ({pitchRatio:F2}x) | Formant={dspParams.FormantSemitones:+0.0;-0.0;0.0}st ({formantRatio:F2}x) | Vocoder={vocoderStr} | Gate={gateStr} | Mix={(int)(dspParams.DryWetMix * 100)}% | Monitor={monStr}");
            _writer.WriteLine($"  Endpoints:   Mic='{inputDevice ?? "Default"}' | Out='{outputDevice ?? "Default"}'");
            _writer.WriteLine();
            _writer.Flush();

            if (EchoToConsole)
            {
                try
                {
                    Console.WriteLine($"[{timestamp}] DSP Latency: {latencyStr}");
                    Console.WriteLine($"  Ring Buffer: Fill={fillPercentage:F1}% ({fillMs:F0}ms) | Drift={driftRate:+0.0;-0.0;0.0}/s | Drops={droppedSamples} | Dups={duplicatedSamples} | Overruns={overruns} | Underruns={underruns}");
                    Console.WriteLine($"  DSP Params:  Preset='{activePresetName ?? "Custom"}' | Pitch={dspParams.PitchSemitones:+0.0;-0.0;0.0}st ({pitchRatio:F2}x) | Formant={dspParams.FormantSemitones:+0.0;-0.0;0.0}st ({formantRatio:F2}x) | Vocoder={vocoderStr} | Gate={gateStr} | Mix={(int)(dspParams.DryWetMix * 100)}% | Monitor={monStr}");
                    Console.WriteLine($"  Endpoints:   Mic='{inputDevice ?? "Default"}' | Out='{outputDevice ?? "Default"}'");
                    Console.WriteLine();
                }
                catch
                {
                    // Suppress if console stream not writable
                }
            }
        }
    }

    /// <summary>
    /// Clears the contents of the telemetry log file.
    /// </summary>
    public void ClearLog()
    {
        lock (_lock)
        {
            CloseWriter();
            try
            {
                if (File.Exists(_logFilePath))
                {
                    File.Delete(_logFilePath);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to clear telemetry log: {ex.Message}");
            }

            if (_isEnabled)
            {
                EnsureWriterOpen();
            }
        }
    }

    private void EnsureWriterOpen()
    {
        if (_writer == null)
        {
            try
            {
                var stream = new FileStream(_logFilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                _writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to open telemetry log: {ex.Message}");
            }
        }
    }

    private void CloseWriter()
    {
        if (_writer != null)
        {
            try
            {
                _writer.Flush();
                _writer.Dispose();
            }
            catch { }
            _writer = null;
        }
    }

    /// <summary>
    /// Locates the root of the project containing VoiceChanger.sln.
    /// </summary>
    public static string FindProjectRoot()
    {
        string current = Environment.CurrentDirectory;
        if (File.Exists(Path.Combine(current, "VoiceChanger.sln")))
        {
            return current;
        }

        var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "VoiceChanger.sln")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }

        return current;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        lock (_lock)
        {
            CloseWriter();
        }
    }
}
