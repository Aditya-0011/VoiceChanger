using System.Diagnostics;
using VoiceChanger.Core.Buffers;

namespace VoiceChanger.Audio.Drift;

/// <summary>
/// Monitors buffer fill levels between independent audio clock domains (e.g. microphone input vs output device)
/// and applies micro-corrections (single sample drop or duplication) to prevent monotonic buffer drift and glitches.
/// </summary>
public sealed class ClockDriftController
{
    private readonly SpscRingBuffer _ringBuffer;
    private readonly int _highThresholdFrames;
    private readonly int _lowThresholdFrames;
    private readonly int _targetFrames;
    private readonly int _aggressiveHighThresholdFrames;
    private readonly int _emergencyThresholdFrames;
    private readonly long _minIntervalBetweenCorrectionsTicks;

    private long _lastCorrectionTimestamp;
    private long _lastSampleTimestamp;
    private long _lastFillCount;

    /// <summary>
    /// Current estimated drift velocity in samples per second (positive = buffer filling, negative = buffer draining).
    /// </summary>
    public double DriftRateSamplesPerSec { get; private set; }

    /// <summary>
    /// Total number of samples dropped due to buffer filling.
    /// </summary>
    public long DroppedSampleCount { get; private set; }

    /// <summary>
    /// Total number of samples duplicated due to buffer draining.
    /// </summary>
    public long DuplicatedSampleCount { get; private set; }

    /// <summary>
    /// Current buffer fill level in milliseconds at 48 kHz.
    /// </summary>
    public double FillMs => (_ringBuffer.FillCount * 1000.0) / 48000.0;

    /// <summary>
    /// Current buffer fill level as a percentage (0.0 to 100.0%).
    /// </summary>
    public double FillPercentage
    {
        get
        {
            long fill = _ringBuffer.FillCount;
            int cap = _ringBuffer.Capacity;
            return cap > 0 ? (fill * 100.0) / cap : 0.0;
        }
    }

    /// <summary>
    /// Creates a new instance of <see cref="ClockDriftController"/>.
    /// </summary>
    /// <param name="ringBuffer">The SPSC ring buffer to monitor and balance.</param>
    /// <param name="highThresholdRatio">Upper threshold ratio (default 0.50 = 50%).</param>
    /// <param name="lowThresholdRatio">Lower threshold ratio (default 0.12 = 12%).</param>
    /// <param name="minIntervalBetweenCorrectionsMs">Minimum interval in milliseconds between single-sample corrections (default 0 ms = per-chunk).</param>
    /// <param name="lowThresholdFrames">Optional explicit lower threshold in frames (takes precedence over ratio).</param>
    /// <param name="highThresholdFrames">Optional explicit upper threshold in frames (takes precedence over ratio).</param>
    /// <param name="targetFrames">Optional target fill frames for emergency discard recovery.</param>
    /// <param name="aggressiveHighThresholdFrames">Optional threshold to drop 2 samples per chunk.</param>
    /// <param name="emergencyThresholdFrames">Optional emergency threshold to discard accumulated excess immediately.</param>
    public ClockDriftController(
        SpscRingBuffer ringBuffer,
        double highThresholdRatio = 0.50,
        double lowThresholdRatio = 0.12,
        int minIntervalBetweenCorrectionsMs = 0,
        int? lowThresholdFrames = null,
        int? highThresholdFrames = null,
        int? targetFrames = null,
        int? aggressiveHighThresholdFrames = null,
        int? emergencyThresholdFrames = null)
    {
        ArgumentNullException.ThrowIfNull(ringBuffer);

        _ringBuffer = ringBuffer;
        _highThresholdFrames = highThresholdFrames ?? Math.Max(1, (int)(ringBuffer.Capacity * highThresholdRatio));
        _lowThresholdFrames = lowThresholdFrames ?? Math.Max(1, (int)(ringBuffer.Capacity * lowThresholdRatio));
        _targetFrames = targetFrames ?? (_lowThresholdFrames + _highThresholdFrames) / 2;
        _aggressiveHighThresholdFrames = aggressiveHighThresholdFrames ?? (int)(_highThresholdFrames * 1.4);
        _emergencyThresholdFrames = emergencyThresholdFrames ?? (int)(_highThresholdFrames * 2.0);

        if (_lowThresholdFrames >= _highThresholdFrames || _lowThresholdFrames <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(lowThresholdFrames), "Low threshold must be positive and strictly less than high threshold.");
        }

        _minIntervalBetweenCorrectionsTicks = (Stopwatch.Frequency * minIntervalBetweenCorrectionsMs) / 1000;

        Reset();
    }

    /// <summary>
    /// Updates drift velocity instrumentation. Safe to call periodically from monitoring or worker threads.
    /// </summary>
    public void UpdateInstrumentation()
    {
        long now = Stopwatch.GetTimestamp();
        long currentFill = _ringBuffer.FillCount;

        if (_lastSampleTimestamp == 0)
        {
            _lastSampleTimestamp = now;
            _lastFillCount = currentFill;
            return;
        }

        long elapsedTicks = now - _lastSampleTimestamp;
        if (elapsedTicks >= Stopwatch.Frequency / 2) // Update every >= 500 ms
        {
            double elapsedSeconds = (double)elapsedTicks / Stopwatch.Frequency;
            long deltaSamples = currentFill - _lastFillCount;

            // Exponential moving average for smooth drift rate reporting
            double instantRate = deltaSamples / elapsedSeconds;
            DriftRateSamplesPerSec = (DriftRateSamplesPerSec * 0.8) + (instantRate * 0.2);

            _lastSampleTimestamp = now;
            _lastFillCount = currentFill;
        }
    }

    /// <summary>
    /// Evaluates current fill level and determines whether a clock drift correction is needed.
    /// Must be invoked on the worker thread prior to pushing/popping chunk buffers.
    /// </summary>
    /// <returns>
    /// <c>2</c> if 2 samples should be dropped (fast correction),
    /// <c>1</c> if 1 sample should be dropped (gentle correction),
    /// <c>-1</c> if a sample should be duplicated (buffer too empty),
    /// or <c>0</c> if no correction is needed.
    /// </returns>
    public int EvaluateCorrection()
    {
        UpdateInstrumentation();

        long now = Stopwatch.GetTimestamp();
        if (_minIntervalBetweenCorrectionsTicks > 0 &&
            now - _lastCorrectionTimestamp < _minIntervalBetweenCorrectionsTicks)
        {
            return 0; // Rate limited
        }

        long fill = _ringBuffer.FillCount;

        // Emergency check: if fill has grown beyond the emergency threshold,
        // discard excess down to target immediately to prevent unconstrained latency and overruns.
        if (_emergencyThresholdFrames > 0 && fill > _emergencyThresholdFrames)
        {
            int toDiscard = (int)(fill - _targetFrames);
            if (toDiscard > 0)
            {
                int discarded = _ringBuffer.Discard(toDiscard);
                DroppedSampleCount += discarded;
                _lastCorrectionTimestamp = now;
                return 0;
            }
        }

        // Tier 2 high: aggressive drop (2 samples per chunk)
        if (_aggressiveHighThresholdFrames > 0 && fill > _aggressiveHighThresholdFrames)
        {
            _lastCorrectionTimestamp = now;
            DroppedSampleCount += 2;
            return 2;
        }

        // Tier 1 high: standard drop (1 sample per chunk)
        if (fill > _highThresholdFrames)
        {
            _lastCorrectionTimestamp = now;
            DroppedSampleCount++;
            return 1;
        }

        // Low threshold: duplicate (1 sample per chunk)
        if (fill < _lowThresholdFrames && fill > 0)
        {
            _lastCorrectionTimestamp = now;
            DuplicatedSampleCount++;
            return -1;
        }

        return 0;
    }

    /// <summary>
    /// Resets all metrics and timestamps.
    /// </summary>
    public void Reset()
    {
        _lastCorrectionTimestamp = 0;
        _lastSampleTimestamp = 0;
        _lastFillCount = 0;
        DriftRateSamplesPerSec = 0.0;
        DroppedSampleCount = 0;
        DuplicatedSampleCount = 0;
    }
}
