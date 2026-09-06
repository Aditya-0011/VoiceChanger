using VoiceChanger.Core;
using Xunit;

namespace VoiceChanger.Tests;

public sealed class PassthroughProcessorTests
{
    [Fact]
    public void Passthrough_LatencyFrames_IsZero()
    {
        var processor = new PassthroughProcessor();
        Assert.Equal(0, processor.LatencyFrames);
    }

    [Fact]
    public void Passthrough_SineWave_CopiesFidelity()
    {
        var processor = new PassthroughProcessor();
        processor.Prepare(sampleRate: 48000, maxBlockSize: 1024);

        float[] input = new float[1024];
        float[] output = new float[1024];

        // Generate 440 Hz sine wave
        for (int i = 0; i < input.Length; i++)
        {
            input[i] = MathF.Sin(2.0f * MathF.PI * 440.0f * i / 48000.0f);
        }

        processor.Process(input, output);

        for (int i = 0; i < input.Length; i++)
        {
            Assert.Equal(input[i], output[i], precision: 6);
        }
    }

    [Fact]
    public void Passthrough_Impulse_CopiesFidelity()
    {
        var processor = new PassthroughProcessor();
        processor.Prepare(sampleRate: 48000, maxBlockSize: 256);

        float[] input = new float[256];
        float[] output = new float[256];
        input[0] = 1.0f;

        processor.Process(input, output);

        Assert.Equal(1.0f, output[0]);
        for (int i = 1; i < output.Length; i++)
        {
            Assert.Equal(0.0f, output[i]);
        }
    }

    [Fact]
    public void Passthrough_WhiteNoise_CopiesFidelity()
    {
        var processor = new PassthroughProcessor();
        processor.Prepare(sampleRate: 48000, maxBlockSize: 512);

        float[] input = new float[512];
        float[] output = new float[512];
        var rng = new Random(42);

        for (int i = 0; i < input.Length; i++)
        {
            input[i] = (float)(rng.NextDouble() * 2.0 - 1.0);
        }

        processor.Process(input, output);

        Assert.Equal(input, output);
    }

    [Fact]
    public void Passthrough_Silence_ProducesSilence()
    {
        var processor = new PassthroughProcessor();
        processor.Prepare(sampleRate: 48000, maxBlockSize: 512);

        float[] input = new float[512];
        float[] output = new float[512];
        Array.Fill(output, 0.5f); // poison destination

        processor.Process(input, output);

        for (int i = 0; i < output.Length; i++)
        {
            Assert.Equal(0.0f, output[i]);
        }
    }

    [Fact]
    public void Passthrough_Process_HasZeroAllocations()
    {
        var processor = new PassthroughProcessor();
        processor.Prepare(sampleRate: 48000, maxBlockSize: 512);

        float[] input = new float[512];
        float[] output = new float[512];

        // Warm up JIT tier-1
        for (int i = 0; i < 1000; i++)
        {
            processor.Process(input, output);
        }

        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();

        for (int i = 0; i < 10000; i++)
        {
            processor.Process(input, output);
        }

        long allocatedAfter = GC.GetAllocatedBytesForCurrentThread();
        long delta = allocatedAfter - allocatedBefore;

        Assert.Equal(0, delta);
    }
}
