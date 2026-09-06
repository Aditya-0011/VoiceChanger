using System.Runtime.CompilerServices;

namespace VoiceChanger.Core.Dsp;

/// <summary>
/// Zero-allocation fractional-delay resampler using 4-point Catmull-Rom cubic interpolation.
/// Maintains an internal pre-allocated circular ring buffer for seamless continuous streaming.
/// </summary>
public sealed class Resampler
{
    private readonly float[] _buffer;
    private readonly int _mask;
    private long _writePos;
    private double _readPos;

    /// <summary>
    /// Gets the internal buffer capacity.
    /// </summary>
    public int Capacity => _buffer.Length;

    /// <summary>
    /// Gets the number of available samples ready for resampling.
    /// </summary>
    public double AvailableSamples => _writePos - _readPos;

    /// <summary>
    /// Initializes a new instance of the <see cref="Resampler"/> class with the specified capacity.
    /// </summary>
    /// <param name="capacity">Capacity in samples (must be a positive power of two, default 8192).</param>
    public Resampler(int capacity = 8192)
    {
        if (capacity <= 0 || (capacity & (capacity - 1)) != 0)
        {
            throw new ArgumentException("Resampler capacity must be a positive power of two.", nameof(capacity));
        }

        _buffer = new float[capacity];
        _mask = capacity - 1;
        Reset();
    }

    /// <summary>
    /// Writes input audio samples into the resampler's buffer.
    /// </summary>
    /// <param name="input">Input samples to append.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Write(ReadOnlySpan<float> input)
    {
        for (int i = 0; i < input.Length; i++)
        {
            _buffer[_writePos & _mask] = input[i];
            _writePos++;
        }
    }

    /// <summary>
    /// Reads and resamples into the destination span at the specified playback speed ratio.
    /// </summary>
    /// <param name="output">Destination span to receive interpolated samples.</param>
    /// <param name="ratio">Playback speed ratio (step = ratio). Ratio > 1 pitches up / speeds up; ratio &lt; 1 pitches down / slows down.</param>
    /// <returns>Number of samples written into <paramref name="output"/>.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Read(Span<float> output, double ratio)
    {
        if (output.IsEmpty || ratio <= 0.0)
        {
            return 0;
        }

        double step = ratio;
        int written = 0;

        while (written < output.Length)
        {
            // We need 4 points: index-1, index, index+1, index+2
            long baseIndex = (long)Math.Floor(_readPos);
            double frac = _readPos - baseIndex;

            // Ensure we have at least 2 future samples ahead of baseIndex
            if (baseIndex + 2 >= _writePos)
            {
                break; // Not enough samples buffered yet
            }

            float y0 = _buffer[(baseIndex - 1) & _mask];
            float y1 = _buffer[baseIndex & _mask];
            float y2 = _buffer[(baseIndex + 1) & _mask];
            float y3 = _buffer[(baseIndex + 2) & _mask];

            // 4-point Catmull-Rom cubic interpolation
            float a0 = -0.5f * y0 + 1.5f * y1 - 1.5f * y2 + 0.5f * y3;
            float a1 = y0 - 2.5f * y1 + 2.0f * y2 - 0.5f * y3;
            float a2 = -0.5f * y0 + 0.5f * y2;
            float a3 = y1;

            float sample = (float)(((a0 * frac + a1) * frac + a2) * frac + a3);

            output[written++] = sample;
            _readPos += step;
        }

        return written;
    }

    /// <summary>
    /// Resets the internal read and write positions and clears the buffer.
    /// </summary>
    public void Reset()
    {
        _writePos = 2; // Offset by 2 so baseIndex - 1 is non-negative
        _readPos = 2.0;
        Array.Clear(_buffer);
    }
}
