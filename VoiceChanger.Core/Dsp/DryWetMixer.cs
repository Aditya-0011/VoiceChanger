using System.Runtime.CompilerServices;

namespace VoiceChanger.Core.Dsp;

/// <summary>
/// Delay-compensated dry/wet audio mixer.
/// Compensates for algorithmic processor delay (e.g. 1024-frame vocoder latency)
/// using a circular delay line so the dry and wet signals remain phase-aligned without comb filtering.
/// Strictly zero heap allocations in the audio processing path (Invariant #1).
/// </summary>
public sealed class DryWetMixer
{
    private float[] _delayBuffer = [];
    private int _delayMask;
    private int _delayFrames;
    private int _writeIndex;
    private float _dryWetMix = 1.0f; // 0.0 = 100% dry, 1.0 = 100% wet

    /// <summary>
    /// Gets or sets the dry/wet mix ratio (0.0 = full dry, 1.0 = full wet).
    /// </summary>
    public float Mix
    {
        get => _dryWetMix;
        set => _dryWetMix = Math.Clamp(value, 0.0f, 1.0f);
    }

    /// <summary>
    /// Algorithmic delay in frames compensated by the internal delay line.
    /// </summary>
    public int LatencyFrames => _delayFrames;

    /// <summary>
    /// Creates a new instance of <see cref="DryWetMixer"/>.
    /// </summary>
    /// <param name="initialMix">Initial dry/wet mix ratio (default 1.0 = 100% wet).</param>
    public DryWetMixer(float initialMix = 1.0f)
    {
        _dryWetMix = Math.Clamp(initialMix, 0.0f, 1.0f);
    }

    /// <summary>
    /// Pre-allocates circular delay line storage for the specified latency and block size.
    /// </summary>
    /// <param name="latencyFrames">Number of delay frames to compensate.</param>
    /// <param name="maxBlockSize">Maximum block size processed per call.</param>
    public void Prepare(int latencyFrames, int maxBlockSize)
    {
        _delayFrames = Math.Max(0, latencyFrames);
        int requiredCapacity = Math.Max(1024, _delayFrames + maxBlockSize * 2);

        // Next power of two
        int capacity = 1;
        while (capacity < requiredCapacity)
        {
            capacity <<= 1;
        }

        _delayBuffer = new float[capacity];
        _delayMask = capacity - 1;
        Reset();
    }

    /// <summary>
    /// Clears delay line history and resets pointers.
    /// </summary>
    public void Reset()
    {
        _writeIndex = 0;
        if (_delayBuffer.Length > 0)
        {
            Array.Clear(_delayBuffer);
        }
    }

    /// <summary>
    /// Mixes dry input and wet input into output with delay compensation.
    /// </summary>
    /// <param name="dryInput">Raw unmodified input audio.</param>
    /// <param name="wetInput">Processed wet audio from the vocoder/effects.</param>
    /// <param name="output">Target buffer for the combined audio.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Process(ReadOnlySpan<float> dryInput, ReadOnlySpan<float> wetInput, Span<float> output)
    {
        int length = Math.Min(dryInput.Length, Math.Min(wetInput.Length, output.Length));
        if (length == 0)
        {
            return;
        }

        float wetGain = _dryWetMix;
        float dryGain = 1.0f - wetGain;

        // Fast path 1: 100% wet (no delay line read needed, still write to delay line to keep history fresh)
        if (wetGain >= 0.999f)
        {
            if (_delayFrames > 0 && _delayBuffer.Length > 0)
            {
                for (int i = 0; i < length; i++)
                {
                    _delayBuffer[_writeIndex & _delayMask] = dryInput[i];
                    _writeIndex++;
                }
            }
            wetInput.Slice(0, length).CopyTo(output);
            return;
        }

        // Fast path 2: zero latency delay line (direct linear mix)
        if (_delayFrames == 0 || _delayBuffer.Length == 0)
        {
            for (int i = 0; i < length; i++)
            {
                output[i] = dryInput[i] * dryGain + wetInput[i] * wetGain;
            }
            return;
        }

        // Standard path: delay-compensated dry mix
        int mask = _delayMask;
        int delay = _delayFrames;
        int writeIdx = _writeIndex;

        for (int i = 0; i < length; i++)
        {
            _delayBuffer[writeIdx & mask] = dryInput[i];
            int readIdx = (writeIdx - delay) & mask;
            float delayedDry = _delayBuffer[readIdx];
            writeIdx++;

            output[i] = delayedDry * dryGain + wetInput[i] * wetGain;
        }

        _writeIndex = writeIdx;
    }
}
