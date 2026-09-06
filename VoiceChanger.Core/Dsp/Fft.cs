using System.Numerics;
using System.Runtime.CompilerServices;

namespace VoiceChanger.Core.Dsp;

/// <summary>
/// High-performance, zero-allocation radix-2 Decimation-In-Time (DIT) Fast Fourier Transform.
/// Pre-computes bit-reversal indices and twiddle factor tables for forward and inverse transforms.
/// </summary>
public sealed class Fft
{
    private readonly int _size;
    private readonly int[] _bitReversal;
    private readonly float[] _cosTable;
    private readonly float[] _sinTable;

    /// <summary>
    /// Gets the FFT size (number of points). Always a power of two.
    /// </summary>
    public int Size => _size;

    /// <summary>
    /// Initializes a new instance of the <see cref="Fft"/> class for the specified size.
    /// </summary>
    /// <param name="size">FFT size in points (must be a positive power of two).</param>
    /// <exception cref="ArgumentException">Thrown when size is not a power of two.</exception>
    public Fft(int size)
    {
        if (size <= 0 || (size & (size - 1)) != 0)
        {
            throw new ArgumentException("FFT size must be a positive power of two.", nameof(size));
        }

        _size = size;

        // Pre-compute bit-reversal lookup table
        _bitReversal = new int[size];
        int bits = BitOperations.Log2((uint)size);
        for (int i = 0; i < size; i++)
        {
            _bitReversal[i] = ReverseBits(i, bits);
        }

        // Pre-compute twiddle factors: W_size^k = cos(2*pi*k/size) - j*sin(2*pi*k/size)
        int halfSize = size / 2;
        _cosTable = new float[halfSize];
        _sinTable = new float[halfSize];
        double angleStep = 2.0 * Math.PI / size;

        for (int k = 0; k < halfSize; k++)
        {
            double angle = k * angleStep;
            _cosTable[k] = (float)Math.Cos(angle);
            _sinTable[k] = (float)Math.Sin(angle);
        }
    }

    /// <summary>
    /// Computes the in-place forward FFT of the complex input signal.
    /// </summary>
    /// <param name="real">Real component span of length <see cref="Size"/>.</param>
    /// <param name="imag">Imaginary component span of length <see cref="Size"/>.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Forward(Span<float> real, Span<float> imag)
    {
        ValidateSpans(real, imag);
        Transform(real, imag, isInverse: false);
    }

    /// <summary>
    /// Computes the in-place inverse FFT (IFFT) of the complex frequency-domain signal,
    /// scaled by 1/Size so that IFFT(FFT(x)) == x.
    /// </summary>
    /// <param name="real">Real component span of length <see cref="Size"/>.</param>
    /// <param name="imag">Imaginary component span of length <see cref="Size"/>.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Inverse(Span<float> real, Span<float> imag)
    {
        ValidateSpans(real, imag);
        Transform(real, imag, isInverse: true);

        // Normalize by 1/N
        float scale = 1.0f / _size;
        for (int i = 0; i < _size; i++)
        {
            real[i] *= scale;
            imag[i] *= scale;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Transform(Span<float> real, Span<float> imag, bool isInverse)
    {
        // 1. Bit-reversal permutation
        for (int i = 0; i < _size; i++)
        {
            int j = _bitReversal[i];
            if (i < j)
            {
                (real[i], real[j]) = (real[j], real[i]);
                (imag[i], imag[j]) = (imag[j], imag[i]);
            }
        }

        // 2. Cooley-Tukey butterfly stages
        for (int len = 2; len <= _size; len <<= 1)
        {
            int half = len >> 1;
            int step = _size / len;

            for (int i = 0; i < _size; i += len)
            {
                int k = 0;
                for (int j = 0; j < half; j++)
                {
                    float cos = _cosTable[k];
                    float sin = _sinTable[k];
                    k += step;

                    int u = i + j;
                    int v = u + half;

                    float vr = real[v];
                    float vi = imag[v];

                    // Complex multiplication:
                    // Forward: (vr + j*vi) * (cos - j*sin) = (vr*cos + vi*sin) + j*(vi*cos - vr*sin)
                    // Inverse: (vr + j*vi) * (cos + j*sin) = (vr*cos - vi*sin) + j*(vi*cos + vr*sin)
                    float tr, ti;
                    if (isInverse)
                    {
                        tr = vr * cos - vi * sin;
                        ti = vi * cos + vr * sin;
                    }
                    else
                    {
                        tr = vr * cos + vi * sin;
                        ti = vi * cos - vr * sin;
                    }

                    float ur = real[u];
                    float ui = imag[u];

                    real[u] = ur + tr;
                    imag[u] = ui + ti;
                    real[v] = ur - tr;
                    imag[v] = ui - ti;
                }
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ValidateSpans(ReadOnlySpan<float> real, ReadOnlySpan<float> imag)
    {
        if (real.Length < _size || imag.Length < _size)
        {
            throw new ArgumentException($"Spans must be at least of length {_size}.", nameof(real));
        }
    }

    private static int ReverseBits(int value, int bits)
    {
        int result = 0;
        for (int i = 0; i < bits; i++)
        {
            result = (result << 1) | (value & 1);
            value >>= 1;
        }
        return result;
    }
}
