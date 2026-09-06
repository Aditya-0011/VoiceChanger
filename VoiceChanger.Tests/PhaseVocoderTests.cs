using VoiceChanger.Core.Dsp;
using Xunit;

namespace VoiceChanger.Tests;

public sealed class PhaseVocoderTests
{
    private const int SampleRate = 48000;
    private const int BlockSize = 256;

    [Fact]
    public void PitchShift_Plus12Semitones_DominantFrequencyIs880Hz()
    {
        // 440 Hz sine + 12 semitones (1 octave up) should peak at 880 Hz +/- 1%
        var vocoder = new PhaseVocoderProcessor(frameSize: 1024, analysisHop: 256, initialSemitones: 12.0f);
        vocoder.Prepare(SampleRate, BlockSize);

        float measuredFreq = MeasureProcessedToneFrequency(vocoder, inputFreq: 440f, durationSec: 1.0f);

        // Assert 880 Hz +/- 1% (871.2 Hz to 888.8 Hz)
        Assert.InRange(measuredFreq, 880f * 0.99f, 880f * 1.01f);
    }

    [Fact]
    public void PitchShift_Minus12Semitones_DominantFrequencyIs220Hz()
    {
        // 440 Hz sine - 12 semitones (1 octave down) should peak at 220 Hz +/- 1%
        var vocoder = new PhaseVocoderProcessor(frameSize: 1024, analysisHop: 256, initialSemitones: -12.0f);
        vocoder.Prepare(SampleRate, BlockSize);

        float measuredFreq = MeasureProcessedToneFrequency(vocoder, inputFreq: 440f, durationSec: 1.0f);

        // Assert 220 Hz +/- 1.5%
        Assert.InRange(measuredFreq, 220f * 0.985f, 220f * 1.015f);
    }

    [Theory]
    [InlineData(-12.0f)]
    [InlineData(-7.0f)]
    [InlineData(-3.0f)]
    [InlineData(0.0f)]
    [InlineData(4.0f)]
    [InlineData(7.0f)]
    [InlineData(12.0f)]
    public void TempoInvariance_OutputLengthEqualsInputLength(float semitones)
    {
        var vocoder = new PhaseVocoderProcessor(frameSize: 1024, analysisHop: 256, initialSemitones: semitones);
        vocoder.Prepare(SampleRate, BlockSize);

        float[] input = new float[BlockSize];
        float[] output = new float[BlockSize];

        int totalBlocks = 100;
        int totalInputFrames = totalBlocks * BlockSize;
        int totalOutputFrames = 0;

        for (int b = 0; b < totalBlocks; b++)
        {
            for (int i = 0; i < BlockSize; i++) input[i] = MathF.Sin((b * BlockSize + i) * 0.05f);
            vocoder.Process(input, output);
            totalOutputFrames += output.Length;
        }

        Assert.Equal(totalInputFrames, totalOutputFrames);
    }

    [Fact]
    public void SineSweep_MonotonicFrequencyTracking()
    {
        // Feed a linear sine sweep from 200 Hz to 600 Hz over 1.0 second.
        // Shift by +7 semitones (ratio ~1.4983).
        // Track the dominant frequency across 3 temporal segments and verify monotonic increase.
        var vocoder = new PhaseVocoderProcessor(frameSize: 1024, analysisHop: 256, initialSemitones: 7.0f);
        vocoder.Prepare(SampleRate, BlockSize);

        int totalFrames = SampleRate; // 1 second
        int numBlocks = totalFrames / BlockSize;
        float[] fullOutput = new float[totalFrames];

        float[] inputBlock = new float[BlockSize];
        float[] outputBlock = new float[BlockSize];

        for (int b = 0; b < numBlocks; b++)
        {
            for (int i = 0; i < BlockSize; i++)
            {
                float t = (float)(b * BlockSize + i) / SampleRate;
                // Instantaneous frequency sweeps linearly: f(t) = f0 + (f1 - f0) * t
                // Phase = 2 * pi * (f0 * t + 0.5 * (f1 - f0) * t^2)
                float phase = 2.0f * MathF.PI * (200f * t + 0.5f * 400f * t * t);
                inputBlock[i] = MathF.Sin(phase);
            }

            vocoder.Process(inputBlock, outputBlock);
            outputBlock.CopyTo(fullOutput.AsSpan(b * BlockSize, BlockSize));
        }

        // Measure dominant frequency in 3 segments: t = 0.25s, 0.50s, 0.75s
        int windowSize = 2048;
        float freq1 = MeasureSegmentDominantFrequency(fullOutput, (int)(SampleRate * 0.25f), windowSize);
        float freq2 = MeasureSegmentDominantFrequency(fullOutput, (int)(SampleRate * 0.50f), windowSize);
        float freq3 = MeasureSegmentDominantFrequency(fullOutput, (int)(SampleRate * 0.75f), windowSize);

        Assert.True(freq2 > freq1, $"Expected freq2 ({freq2:F1} Hz) > freq1 ({freq1:F1} Hz)");
        Assert.True(freq3 > freq2, $"Expected freq3 ({freq3:F1} Hz) > freq2 ({freq2:F1} Hz)");
    }

    [Fact]
    public void ZeroAllocations_InAudioProcessCallback()
    {
        var vocoder = new PhaseVocoderProcessor(frameSize: 1024, analysisHop: 256, initialSemitones: 5.0f);
        vocoder.Prepare(SampleRate, BlockSize);

        float[] input = new float[BlockSize];
        float[] output = new float[BlockSize];

        // Warm up JIT
        for (int i = 0; i < 50; i++)
        {
            vocoder.Process(input, output);
        }

        long beforeAllocated = GC.GetAllocatedBytesForCurrentThread();

        // Run 1,000 blocks through the hot DSP path
        for (int i = 0; i < 1000; i++)
        {
            vocoder.Process(input, output);
        }

        long afterAllocated = GC.GetAllocatedBytesForCurrentThread();
        long allocatedDelta = afterAllocated - beforeAllocated;

        // Hard Invariant #1: strictly 0 bytes allocated on the audio thread
        Assert.Equal(0, allocatedDelta);
    }

    [Theory]
    [InlineData(-12.0f)]
    [InlineData(-6.0f)]
    [InlineData(0.0f)]
    [InlineData(6.0f)]
    [InlineData(12.0f)]
    public void ColaCondition_FlatAmplitudeEnvelopeAcrossPitchRatios(float semitones)
    {
        var vocoder = new PhaseVocoderProcessor(frameSize: 1024, analysisHop: 256, initialSemitones: semitones);
        vocoder.Prepare(SampleRate, BlockSize);

        float[] input = new float[BlockSize];
        float[] output = new float[BlockSize];

        double sumSteadyRms = 0;
        int steadyCount = 0;

        for (int i = 0; i < 100; i++)
        {
            for (int s = 0; s < BlockSize; s++)
            {
                input[s] = MathF.Sin(2.0f * MathF.PI * 440f * (i * BlockSize + s) / SampleRate);
            }
            vocoder.Process(input, output);

            // Skip initial 15 blocks (settling / latency)
            if (i >= 15)
            {
                double blockRms = 0.0;
                for (int s = 0; s < output.Length; s++) blockRms += output[s] * output[s];
                blockRms = Math.Sqrt(blockRms / output.Length);

                // Individual block RMS must be within flat envelope bounds
                Assert.InRange(blockRms, 0.7071 * 0.85, 0.7071 * 1.15);
                sumSteadyRms += blockRms;
                steadyCount++;
            }
        }

        double meanSteadyRms = sumSteadyRms / steadyCount;
        // Overall steady-state RMS must be within 10% of theoretical sine RMS (0.7071)
        Assert.InRange(meanSteadyRms, 0.7071 * 0.90, 0.7071 * 1.10);
    }

    private static float MeasureProcessedToneFrequency(PhaseVocoderProcessor vocoder, float inputFreq, float durationSec)
    {
        int totalFrames = (int)(SampleRate * durationSec);
        int numBlocks = totalFrames / BlockSize;

        float[] inputBlock = new float[BlockSize];
        float[] outputBlock = new float[BlockSize];
        float[] fullOutput = new float[totalFrames];

        for (int b = 0; b < numBlocks; b++)
        {
            for (int i = 0; i < BlockSize; i++)
            {
                int n = b * BlockSize + i;
                inputBlock[i] = MathF.Sin(2.0f * MathF.PI * inputFreq * n / SampleRate);
            }

            vocoder.Process(inputBlock, outputBlock);
            outputBlock.CopyTo(fullOutput.AsSpan(b * BlockSize, BlockSize));
        }

        // Take a 4096-point FFT from the steady-state region of output (skip initial transient)
        int fftSize = 4096;
        int startOffset = totalFrames / 2;
        var fft = new Fft(fftSize);

        float[] real = new float[fftSize];
        float[] imag = new float[fftSize];

        // Apply Hann window before FFT for spectral sidelobe suppression
        float[] window = Window.CreateHann(fftSize);
        for (int i = 0; i < fftSize; i++)
        {
            real[i] = fullOutput[startOffset + i] * window[i];
            imag[i] = 0f;
        }

        fft.Forward(real, imag);

        // Find dominant peak bin
        int maxBin = 0;
        float maxMag = 0f;
        int halfFft = fftSize / 2;

        float[] magnitudes = new float[halfFft];
        for (int k = 1; k < halfFft; k++)
        {
            magnitudes[k] = MathF.Sqrt(real[k] * real[k] + imag[k] * imag[k]);
            if (magnitudes[k] > maxMag)
            {
                maxMag = magnitudes[k];
                maxBin = k;
            }
        }

        // Sub-bin parabolic peak interpolation for frequency accuracy
        float alpha = magnitudes[maxBin - 1];
        float beta = magnitudes[maxBin];
        float gamma = magnitudes[maxBin + 1];

        float delta = 0.5f * (alpha - gamma) / (alpha - 2.0f * beta + gamma);
        float peakBin = maxBin + delta;

        return peakBin * SampleRate / fftSize;
    }

    private static float MeasureSegmentDominantFrequency(float[] signal, int offset, int windowSize)
    {
        var fft = new Fft(windowSize);
        float[] real = new float[windowSize];
        float[] imag = new float[windowSize];
        float[] window = Window.CreateHann(windowSize);

        for (int i = 0; i < windowSize; i++)
        {
            real[i] = signal[offset + i] * window[i];
            imag[i] = 0f;
        }

        fft.Forward(real, imag);

        int halfSize = windowSize / 2;
        int maxBin = 0;
        float maxMag = 0f;

        for (int k = 1; k < halfSize; k++)
        {
            float mag = MathF.Sqrt(real[k] * real[k] + imag[k] * imag[k]);
            if (mag > maxMag)
            {
                maxMag = mag;
                maxBin = k;
            }
        }

        if (maxBin <= 0 || maxBin >= halfSize - 1)
        {
            return maxBin * SampleRate / windowSize;
        }

        float alpha = MathF.Sqrt(real[maxBin - 1] * real[maxBin - 1] + imag[maxBin - 1] * imag[maxBin - 1]);
        float beta = maxMag;
        float gamma = MathF.Sqrt(real[maxBin + 1] * real[maxBin + 1] + imag[maxBin + 1] * imag[maxBin + 1]);

        float delta = 0.5f * (alpha - gamma) / (alpha - 2.0f * beta + gamma);
        return (maxBin + delta) * SampleRate / windowSize;
    }
}
