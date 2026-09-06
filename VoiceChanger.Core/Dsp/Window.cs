using System.Runtime.CompilerServices;

namespace VoiceChanger.Core.Dsp;

/// <summary>
/// Window generation and Constant OverLap-Add (COLA) verification for STFT processing.
/// </summary>
public static class Window
{
    /// <summary>
    /// Generates a periodic (DFT-even) or symmetric Hann window of the specified length.
    /// Standard STFT utilizes periodic Hann: w[n] = 0.5 * (1 - cos(2*pi*n / length)).
    /// </summary>
    /// <param name="length">Length of window in samples.</param>
    /// <param name="periodic">True for periodic (DFT-even, recommended for STFT), false for symmetric.</param>
    /// <returns>Pre-calculated float array containing the window coefficients.</returns>
    public static float[] CreateHann(int length, bool periodic = true)
    {
        if (length <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length), "Window length must be positive.");
        }

        float[] w = new float[length];
        double denom = periodic ? length : length - 1;

        for (int n = 0; n < length; n++)
        {
            w[n] = (float)(0.5 * (1.0 - Math.Cos((2.0 * Math.PI * n) / denom)));
        }

        return w;
    }

    /// <summary>
    /// Multiplies the input signal element-wise by the window into the output destination.
    /// </summary>
    /// <param name="window">Pre-calculated window coefficients.</param>
    /// <param name="input">Input signal.</param>
    /// <param name="output">Output destination.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Apply(ReadOnlySpan<float> window, ReadOnlySpan<float> input, Span<float> output)
    {
        int len = Math.Min(window.Length, Math.Min(input.Length, output.Length));
        for (int i = 0; i < len; i++)
        {
            output[i] = input[i] * window[i];
        }
    }

    /// <summary>
    /// Computes the Constant OverLap-Add (COLA) normalization sum for the window and hop size.
    /// When both analysis and synthesis Hann windows are applied, the overlap-add sum is sum(w[n - k*hop]^2).
    /// </summary>
    /// <param name="window">Window coefficients.</param>
    /// <param name="hopSize">Hop size in samples.</param>
    /// <returns>The average sum of squared overlapping windows.</returns>
    public static float CalculateColaSquaredSum(ReadOnlySpan<float> window, int hopSize)
    {
        if (hopSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(hopSize), "Hop size must be positive.");
        }

        int winLen = window.Length;
        // Test in the middle of a multi-frame overlap sequence
        int testIndex = winLen;
        float sum = 0f;

        for (int offset = 0; offset <= winLen * 2; offset += hopSize)
        {
            int n = testIndex - offset;
            if (n >= 0 && n < winLen)
            {
                float w = window[n];
                sum += w * w;
            }
        }

        return sum;
    }

    /// <summary>
    /// Verifies whether the window and hop size combination satisfies the Constant OverLap-Add (COLA) condition,
    /// ensuring flat unity reconstruction without amplitude modulation or ripple.
    /// </summary>
    /// <param name="window">Window coefficients.</param>
    /// <param name="hopSize">Hop size in samples.</param>
    /// <param name="tolerance">Maximum permissible ripple ratio (default 0.005 = 0.5%).</param>
    /// <returns>True if the overlap-add sum is constant within tolerance; otherwise false.</returns>
    public static bool VerifyCola(ReadOnlySpan<float> window, int hopSize, float tolerance = 0.005f)
    {
        if (hopSize <= 0 || window.Length <= 0)
        {
            return false;
        }

        int winLen = window.Length;
        int numTestPoints = hopSize;
        float minSum = float.MaxValue;
        float maxSum = float.MinValue;

        // Test over a full hop period in the steady-state overlap region
        for (int p = 0; p < numTestPoints; p++)
        {
            int centerN = winLen + p;
            float sum = 0f;

            for (int k = -winLen; k <= winLen * 2; k += hopSize)
            {
                int idx = centerN - k;
                if (idx >= 0 && idx < winLen)
                {
                    float w = window[idx];
                    sum += w * w;
                }
            }

            if (sum < minSum) minSum = sum;
            if (sum > maxSum) maxSum = sum;
        }

        if (minSum <= 0f)
        {
            return false;
        }

        float ripple = (maxSum - minSum) / minSum;
        return ripple <= tolerance;
    }
}
