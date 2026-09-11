using VoiceChanger.Core.Dsp;
using Xunit;

namespace VoiceChanger.Tests;

public class DryWetMixerTests
{
    [Fact]
    public void DryWetMixer_FullWet_OutputsWetSignal()
    {
        var mixer = new DryWetMixer(initialMix: 1.0f);
        mixer.Prepare(latencyFrames: 256, maxBlockSize: 256);

        float[] dry = new float[256];
        float[] wet = new float[256];
        float[] output = new float[256];

        for (int i = 0; i < 256; i++)
        {
            dry[i] = 1.0f;
            wet[i] = 2.0f;
        }

        mixer.Process(dry, wet, output);

        for (int i = 0; i < 256; i++)
        {
            Assert.Equal(2.0f, output[i], precision: 5);
        }
    }

    [Fact]
    public void DryWetMixer_FullDry_OutputsDelayedDrySignal()
    {
        int latency = 128;
        var mixer = new DryWetMixer(initialMix: 0.0f);
        mixer.Prepare(latencyFrames: latency, maxBlockSize: 256);

        float[] dry = new float[512];
        float[] wet = new float[512];
        float[] output = new float[512];

        // Impulse at index 50
        dry[50] = 1.0f;

        mixer.Process(dry, wet, output);

        // Assert delayed impulse appears at index 50 + latency = 178
        Assert.Equal(1.0f, output[50 + latency], precision: 5);
        Assert.Equal(0.0f, output[50], precision: 5);
    }

    [Fact]
    public void DryWetMixer_HalfMix_CombinesLinearly()
    {
        var mixer = new DryWetMixer(initialMix: 0.5f);
        mixer.Prepare(latencyFrames: 0, maxBlockSize: 256);

        float[] dry = new float[256];
        float[] wet = new float[256];
        float[] output = new float[256];

        for (int i = 0; i < 256; i++)
        {
            dry[i] = 1.0f;
            wet[i] = 3.0f;
        }

        mixer.Process(dry, wet, output);

        for (int i = 0; i < 256; i++)
        {
            // 1.0 * 0.5 + 3.0 * 0.5 = 2.0
            Assert.Equal(2.0f, output[i], precision: 5);
        }
    }

    [Fact]
    public void DryWetMixer_ZeroAllocations_InAudioProcessCallback()
    {
        var mixer = new DryWetMixer(initialMix: 0.5f);
        mixer.Prepare(latencyFrames: 1024, maxBlockSize: 256);

        float[] dry = new float[256];
        float[] wet = new float[256];
        float[] output = new float[256];

        for (int i = 0; i < 100; i++)
        {
            mixer.Process(dry, wet, output);
        }

        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
        {
            mixer.Process(dry, wet, output);
        }
        long allocatedAfter = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(0, allocatedAfter - allocatedBefore);
    }
}
