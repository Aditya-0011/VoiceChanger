using VoiceChanger.Core.Dsp;
using Xunit;

namespace VoiceChanger.Tests;

public sealed class FftTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(3)]
    [InlineData(100)]
    [InlineData(1000)]
    public void Constructor_RejectsNonPowerOfTwo(int invalidSize)
    {
        Assert.Throws<ArgumentException>(() => new Fft(invalidSize));
    }

    [Theory]
    [InlineData(64)]
    [InlineData(256)]
    [InlineData(1024)]
    [InlineData(2048)]
    public void RoundTrip_RecoversOriginalSignal(int size)
    {
        var fft = new Fft(size);

        float[] original = new float[size];
        float[] real = new float[size];
        float[] imag = new float[size];

        var rng = new Random(42);
        for (int i = 0; i < size; i++)
        {
            original[i] = (float)(rng.NextDouble() * 2.0 - 1.0);
            real[i] = original[i];
            imag[i] = 0f;
        }

        fft.Forward(real, imag);
        fft.Inverse(real, imag);

        for (int i = 0; i < size; i++)
        {
            Assert.Equal(original[i], real[i], precision: 4);
            Assert.True(MathF.Abs(imag[i]) < 1e-4f, $"Imaginary component not zero at {i}: {imag[i]}");
        }
    }

    [Fact]
    public void ImpulseResponse_YieldsFlatSpectrum()
    {
        int size = 1024;
        var fft = new Fft(size);

        float[] real = new float[size];
        float[] imag = new float[size];

        real[0] = 1.0f; // Delta impulse

        fft.Forward(real, imag);

        for (int k = 0; k < size; k++)
        {
            float magnitude = MathF.Sqrt(real[k] * real[k] + imag[k] * imag[k]);
            Assert.Equal(1.0f, magnitude, precision: 4);
        }
    }

    [Fact]
    public void SineWave_PeaksAtExpectedBin()
    {
        int size = 1024;
        int sampleRate = 48000;
        float targetFreq = 440f;

        var fft = new Fft(size);

        float[] real = new float[size];
        float[] imag = new float[size];

        for (int n = 0; n < size; n++)
        {
            real[n] = MathF.Sin(2.0f * MathF.PI * targetFreq * n / sampleRate);
        }

        fft.Forward(real, imag);

        // Expected bin index = round(targetFreq * size / sampleRate) = round(440 * 1024 / 48000) = 9
        int expectedBin = (int)MathF.Round(targetFreq * size / sampleRate);

        int maxBin = 0;
        float maxMag = 0f;

        for (int k = 0; k < size / 2; k++)
        {
            float mag = MathF.Sqrt(real[k] * real[k] + imag[k] * imag[k]);
            if (mag > maxMag)
            {
                maxMag = mag;
                maxBin = k;
            }
        }

        Assert.Equal(expectedBin, maxBin);
    }

    [Fact]
    public void ParsevalTheorem_EnergyIsConserved()
    {
        int size = 512;
        var fft = new Fft(size);

        float[] real = new float[size];
        float[] imag = new float[size];

        float timeEnergy = 0f;
        for (int n = 0; n < size; n++)
        {
            real[n] = MathF.Sin(n * 0.1f) + MathF.Cos(n * 0.3f);
            timeEnergy += real[n] * real[n];
        }

        fft.Forward(real, imag);

        float freqEnergy = 0f;
        for (int k = 0; k < size; k++)
        {
            freqEnergy += real[k] * real[k] + imag[k] * imag[k];
        }

        // Parseval's relation: sum |x[n]|^2 = (1/N) * sum |X[k]|^2
        float normalizedFreqEnergy = freqEnergy / size;
        Assert.Equal(timeEnergy, normalizedFreqEnergy, precision: 2);
    }
}
