using System.Runtime.CompilerServices;

namespace VoiceChanger.Core.Dsp;

/// <summary>
/// Real-time noise gate with envelope following, hysteresis, and exponential gain smoothing.
/// Placed ahead of the phase vocoder to silence keyboard clicks and ambient noise before pitch shifting.
/// Strictly zero heap allocations in the audio processing path (Invariant #1).
/// </summary>
public sealed class NoiseGate
{
    private int _sampleRate = 48000;
    private float _thresholdDb = -45.0f;
    private float _attackMs = 5.0f;
    private float _releaseMs = 80.0f;
    private float _hysteresisDb = 4.0f;
    private bool _enabled = true;

    // Envelope and gain filter coefficients
    private float _attackCoeff;
    private float _releaseCoeff;
    private float _gainAttackCoeff;
    private float _gainReleaseCoeff;

    // Filter states
    private float _envelope;
    private float _gain = 1.0f;
    private bool _isOpen;

    /// <summary>
    /// Gets or sets whether the noise gate is enabled.
    /// </summary>
    public bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    /// <summary>
    /// Gets or sets the threshold in dB (typically -60 dB to -20 dB).
    /// </summary>
    public float ThresholdDb
    {
        get => _thresholdDb;
        set => _thresholdDb = value;
    }

    /// <summary>
    /// Gets or sets the attack time in milliseconds (typically 1 ms to 20 ms).
    /// </summary>
    public float AttackMs
    {
        get => _attackMs;
        set
        {
            _attackMs = Math.Max(0.1f, value);
            UpdateCoefficients();
        }
    }

    /// <summary>
    /// Gets or sets the release time in milliseconds (typically 20 ms to 300 ms).
    /// </summary>
    public float ReleaseMs
    {
        get => _releaseMs;
        set
        {
            _releaseMs = Math.Max(1.0f, value);
            UpdateCoefficients();
        }
    }

    /// <summary>
    /// Gets or sets the hysteresis margin in dB (difference between open and close threshold).
    /// </summary>
    public float HysteresisDb
    {
        get => _hysteresisDb;
        set => _hysteresisDb = Math.Max(0.5f, value);
    }

    /// <summary>
    /// Creates a new instance of <see cref="NoiseGate"/>.
    /// </summary>
    public NoiseGate(
        float thresholdDb = -45.0f,
        float attackMs = 5.0f,
        float releaseMs = 80.0f,
        float hysteresisDb = 4.0f,
        bool enabled = true)
    {
        _thresholdDb = thresholdDb;
        _attackMs = attackMs;
        _releaseMs = releaseMs;
        _hysteresisDb = hysteresisDb;
        _enabled = enabled;
        UpdateCoefficients();
    }

    /// <summary>
    /// Prepares filter coefficients for the specified sample rate.
    /// </summary>
    public void Prepare(int sampleRate)
    {
        _sampleRate = sampleRate > 0 ? sampleRate : 48000;
        UpdateCoefficients();
        Reset();
    }

    /// <summary>
    /// Resets envelope and gain state.
    /// </summary>
    public void Reset()
    {
        _envelope = 0.0f;
        _gain = _enabled ? 0.0f : 1.0f;
        _isOpen = false;
    }

    /// <summary>
    /// Processes an audio block in-place.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Process(Span<float> buffer)
    {
        Process(buffer, buffer);
    }

    /// <summary>
    /// Processes an input audio block into an output audio block with zero heap allocations.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Process(ReadOnlySpan<float> input, Span<float> output)
    {
        int length = Math.Min(input.Length, output.Length);
        if (!_enabled)
        {
            if (input != output)
            {
                input.Slice(0, length).CopyTo(output);
            }
            return;
        }

        float openThresholdDb = _thresholdDb;
        float closeThresholdDb = _thresholdDb - _hysteresisDb;

        float attackCoeff = _attackCoeff;
        float releaseCoeff = _releaseCoeff;
        float gainAttackCoeff = _gainAttackCoeff;
        float gainReleaseCoeff = _gainReleaseCoeff;

        float envelope = _envelope;
        float gain = _gain;
        bool isOpen = _isOpen;

        for (int i = 0; i < length; i++)
        {
            float rect = MathF.Abs(input[i]);

            // Single-pole envelope follower
            if (rect > envelope)
            {
                envelope = attackCoeff * envelope + (1.0f - attackCoeff) * rect;
            }
            else
            {
                envelope = releaseCoeff * envelope + (1.0f - releaseCoeff) * rect;
            }

            // Convert envelope to dB (clamped to -100 dB floor)
            float envDb = envelope > 0.00001f ? 20.0f * MathF.Log10(envelope) : -100.0f;

            // Schmitt trigger hysteresis
            if (isOpen)
            {
                if (envDb < closeThresholdDb)
                {
                    isOpen = false;
                }
            }
            else
            {
                if (envDb >= openThresholdDb)
                {
                    isOpen = true;
                }
            }

            // Gain smoothing toward target (1.0 = open, 0.0 = closed)
            float targetGain = isOpen ? 1.0f : 0.0f;
            if (targetGain > gain)
            {
                gain = gainAttackCoeff * gain + (1.0f - gainAttackCoeff) * targetGain;
            }
            else
            {
                gain = gainReleaseCoeff * gain + (1.0f - gainReleaseCoeff) * targetGain;
            }

            output[i] = input[i] * gain;
        }

        _envelope = envelope;
        _gain = gain;
        _isOpen = isOpen;
    }

    private void UpdateCoefficients()
    {
        float fs = _sampleRate;
        _attackCoeff = MathF.Exp(-1.0f / (Math.Max(0.0001f, _attackMs * 0.001f) * fs));
        _releaseCoeff = MathF.Exp(-1.0f / (Math.Max(0.001f, _releaseMs * 0.001f) * fs));

        // Gain smoothing response (fast attack 2 ms, natural release 15 ms)
        _gainAttackCoeff = MathF.Exp(-1.0f / (0.002f * fs));
        _gainReleaseCoeff = MathF.Exp(-1.0f / (0.015f * fs));
    }
}
