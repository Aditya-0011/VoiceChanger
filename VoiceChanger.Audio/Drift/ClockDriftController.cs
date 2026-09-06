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

    private readonly struct DriftObservation(double timeSec, long fillSamples)
    {
        public readonly double TimeSec = timeSec;
        public readonly long FillSamples = fillSamples;
    }

    private const int HistoryCapacity = 64;
    private readonly DriftObservation[] _history = new DriftObservation[HistoryCapacity];
    private int _historyCount;
    private int _historyWriteIndex;
    private long _baseTimestamp;

    private long _lastCorrectionTimestamp;
    private long _lastSampleTimestamp;
    private long _fillAccumulator;
    private int _fillSampleCount;
    private bool _inLowRecovery;
    private bool _inHighRecovery;

    /// <summary>
    /// Current estimated drift velocity in samples per second (positive = buffer filling, negative = buffer draining),
    /// calculated via Ordinary Least Squares (OLS) linear regression over the recent sample history.
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
    /// Updates drift velocity instrumentation using Ordinary Least Squares linear regression.
    /// Integrates the mean fill over each observation window to filter out intra-period WASAPI batching ripple.
    /// Safe to call periodically from monitoring or worker threads.
    /// </summary>
    public void UpdateInstrumentation()
    {
        long now = Stopwatch.GetTimestamp();
        long currentFill = _ringBuffer.FillCount;
        _fillAccumulator += currentFill;
        _fillSampleCount++;

        if (_baseTimestamp == 0)
        {
            _baseTimestamp = now;
            _lastSampleTimestamp = now;
            _history[0] = new DriftObservation(0.0, currentFill);
            _historyCount = 1;
            _historyWriteIndex = 1;
            _fillAccumulator = 0;
            _fillSampleCount = 0;
            return;
        }

        long elapsedTicks = now - _lastSampleTimestamp;
        if (elapsedTicks >= Stopwatch.Frequency / 10) // Sample at >= 100 ms intervals
        {
            _lastSampleTimestamp = now;
            double t = (double)(now - _baseTimestamp) / Stopwatch.Frequency;
            long avgFill = _fillSampleCount > 0 ? (long)Math.Round((double)_fillAccumulator / _fillSampleCount) : currentFill;
            _fillAccumulator = 0;
            _fillSampleCount = 0;
            AddObservation(t, avgFill);
        }
    }

    /// <summary>
    /// Records an observation and recomputes the linear regression slope.
    /// </summary>
    internal void AddObservation(double timeSec, long fillSamples)
    {
        _history[_historyWriteIndex] = new DriftObservation(timeSec, fillSamples);
        _historyWriteIndex = (_historyWriteIndex + 1) % HistoryCapacity;
        if (_historyCount < HistoryCapacity)
        {
            _historyCount++;
        }

        ComputeLinearRegression();
    }

    private void ComputeLinearRegression()
    {
        if (_historyCount < 2)
        {
            return;
        }

        double sumT = 0.0;
        double sumY = 0.0;
        for (int i = 0; i < _historyCount; i++)
        {
            sumT += _history[i].TimeSec;
            sumY += _history[i].FillSamples;
        }

        double meanT = sumT / _historyCount;
        double meanY = sumY / _historyCount;

        double numerator = 0.0;
        double denominator = 0.0;

        for (int i = 0; i < _historyCount; i++)
        {
            double dt = _history[i].TimeSec - meanT;
            double dy = _history[i].FillSamples - meanY;
            numerator += dt * dy;
            denominator += dt * dt;
        }

        if (denominator > 1e-9)
        {
            DriftRateSamplesPerSec = numerator / denominator;
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
                _inHighRecovery = false;
                _inLowRecovery = false;
                return 0;
            }
        }

        // Tier 2 high: aggressive drop (2 samples per chunk)
        if (_aggressiveHighThresholdFrames > 0 && fill > _aggressiveHighThresholdFrames)
        {
            _inHighRecovery = true;
            _lastCorrectionTimestamp = now;
            DroppedSampleCount += 2;
            return 2;
        }

        // Check high threshold with hysteresis toward target
        if (fill > _highThresholdFrames)
        {
            _inHighRecovery = true;
        }
        else if (fill <= _targetFrames)
        {
            _inHighRecovery = false;
        }

        if (_inHighRecovery)
        {
            _lastCorrectionTimestamp = now;
            DroppedSampleCount++;
            return 1;
        }

        // Check low threshold with hysteresis toward target
        if (fill < _lowThresholdFrames)
        {
            _inLowRecovery = true;
        }
        else if (fill >= _targetFrames)
        {
            _inLowRecovery = false;
        }

        if (_inLowRecovery)
        {
            _lastCorrectionTimestamp = now;

            // Tier 2 low: if critically low (< half of low threshold), duplicate 2 samples to prevent underrun
            if (fill < _lowThresholdFrames / 2)
            {
                DuplicatedSampleCount += 2;
                return -2;
            }

            // Tier 1 low: duplicate 1 sample per chunk
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
        _baseTimestamp = 0;
        _historyCount = 0;
        _historyWriteIndex = 0;
        _fillAccumulator = 0;
        _fillSampleCount = 0;
        _inLowRecovery = false;
        _inHighRecovery = false;
        DriftRateSamplesPerSec = 0.0;
        DroppedSampleCount = 0;
        DuplicatedSampleCount = 0;
    }
}
