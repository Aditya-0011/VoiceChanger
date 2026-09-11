using VoiceChanger.Core.Dsp;
using Xunit;

namespace VoiceChanger.Tests;

public class ProcessorChainTests
{
    [Fact]
    public void ProcessorChain_ProcessesAudio_AndPreservesBlockLength()
    {
        var chain = new ProcessorChain(new DspParameters
        {
            PitchSemitones = 4.0f,
            FormantSemitones = 2.0f,
            NoiseGateEnabled = true,
            NoiseGateThresholdDb = -50.0f,
            DryWetMix = 0.8f,
            VocoderEnabled = true
        });

        chain.Prepare(48000, 256);

        float[] input = new float[256];
        float[] output = new float[256];
        for (int i = 0; i < 256; i++)
        {
            input[i] = 0.2f * MathF.Sin(2.0f * MathF.PI * 440.0f * i / 48000.0f);
        }

        // Process several blocks to get past priming
        for (int b = 0; b < 10; b++)
        {
            chain.Process(input, output);
        }

        // Assert output produces non-zero audio
        float maxVal = 0f;
        for (int i = 0; i < 256; i++)
        {
            maxVal = Math.Max(maxVal, MathF.Abs(output[i]));
        }

        Assert.True(maxVal > 0.01f, $"Expected audible audio output, got maxVal={maxVal}");
    }

    [Fact]
    public void ProcessorChain_AtomicParameterSwitch_UpdatesWithoutError()
    {
        var chain = new ProcessorChain();
        chain.Prepare(48000, 256);

        float[] input = new float[256];
        float[] output = new float[256];
        for (int i = 0; i < 256; i++)
        {
            input[i] = 0.1f * MathF.Sin(i);
        }

        chain.Process(input, output);

        // Dynamically swap parameters
        chain.Parameters = new DspParameters
        {
            PitchSemitones = -5.0f,
            FormantSemitones = -4.0f,
            DryWetMix = 0.5f,
            NoiseGateThresholdDb = -35.0f
        };

        chain.Process(input, output);

        Assert.Equal(-5.0f, chain.Parameters.PitchSemitones);
        Assert.Equal(-4.0f, chain.Parameters.FormantSemitones);
    }

    [Fact]
    public void ProcessorChain_ZeroAllocations_InAudioProcessCallback()
    {
        var chain = new ProcessorChain(new DspParameters
        {
            PitchSemitones = 3.0f,
            FormantSemitones = -2.0f,
            NoiseGateEnabled = true,
            DryWetMix = 0.7f,
            VocoderEnabled = true
        });

        chain.Prepare(48000, 256);

        float[] input = new float[256];
        float[] output = new float[256];
        for (int i = 0; i < 256; i++)
        {
            input[i] = 0.1f * MathF.Sin(i);
        }

        // Warmup
        for (int i = 0; i < 200; i++)
        {
            chain.Process(input, output);
        }

        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
        {
            chain.Process(input, output);
        }
        long allocatedAfter = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(0, allocatedAfter - allocatedBefore);
    }
}
