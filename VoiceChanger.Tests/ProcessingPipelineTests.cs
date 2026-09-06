using VoiceChanger.Audio.Pipeline;
using VoiceChanger.Core;
using Xunit;

namespace VoiceChanger.Tests;

public sealed class ProcessingPipelineTests
{
    [Fact]
    public void Pipeline_PrimeRenderBuffer_PopulatesSilence()
    {
        using var pipeline = new ProcessingPipeline(new PassthroughProcessor(), ringCapacity: 16384, chunkSize: 256);

        Assert.Equal(0, pipeline.RenderRing.FillCount);

        pipeline.PrimeRenderBuffer(960);

        Assert.Equal(960, pipeline.RenderRing.FillCount);
        Assert.Equal(20.0, pipeline.DriftController.FillMs, precision: 1);

        float[] readBuffer = new float[960];
        int read = pipeline.RenderRing.Read(readBuffer);

        Assert.Equal(960, read);
        for (int i = 0; i < readBuffer.Length; i++)
        {
            Assert.Equal(0.0f, readBuffer[i]);
        }
    }

    [Fact]
    public void Pipeline_Start_PrimesRenderRingAndResetsCounters()
    {
        using var pipeline = new ProcessingPipeline(new PassthroughProcessor(), ringCapacity: 16384, chunkSize: 256);

        // Record some dummy glitches
        pipeline.RecordOverrun(100);
        pipeline.RecordUnderrun(200);

        // Start should reset glitches and prime 960 frames
        pipeline.Start(primeFrames: 960);

        Assert.Equal(0, pipeline.OverrunFrames);
        Assert.Equal(0, pipeline.UnderrunFrames);
        Assert.Equal(960, pipeline.RenderRing.FillCount);
        Assert.Equal(20.0, pipeline.DriftController.FillMs, precision: 1);

        // With 960 frames (20 ms), drift controller is safely within deadband (720 frames / 15 ms to 2400 frames / 50 ms)
        int correction = pipeline.DriftController.EvaluateCorrection();
        Assert.Equal(0, correction);
        Assert.Equal(0, pipeline.DriftController.DroppedSampleCount);
        Assert.Equal(0, pipeline.DriftController.DuplicatedSampleCount);

        pipeline.Stop();
    }

    [Fact]
    public void Pipeline_DriftController_TriggersTier1Drop()
    {
        using var pipeline = new ProcessingPipeline(new PassthroughProcessor(), ringCapacity: 16384, chunkSize: 256);
        // Fill 2600 frames (> 2400 high threshold)
        pipeline.PrimeRenderBuffer(2600);

        int correction = pipeline.DriftController.EvaluateCorrection();
        Assert.Equal(1, correction); // Tier 1 drop
        Assert.Equal(1, pipeline.DriftController.DroppedSampleCount);
    }

    [Fact]
    public void Pipeline_DriftController_TriggersTier2AggressiveDrop()
    {
        using var pipeline = new ProcessingPipeline(new PassthroughProcessor(), ringCapacity: 16384, chunkSize: 256);
        // Fill 3500 frames (> 3360 aggressive high threshold)
        pipeline.PrimeRenderBuffer(3500);

        int correction = pipeline.DriftController.EvaluateCorrection();
        Assert.Equal(2, correction); // Tier 2 drop 2 samples
        Assert.Equal(2, pipeline.DriftController.DroppedSampleCount);
    }

    [Fact]
    public void Pipeline_DriftController_EmergencyDiscard_ResetsFillToTarget()
    {
        using var pipeline = new ProcessingPipeline(new PassthroughProcessor(), ringCapacity: 16384, chunkSize: 256);
        // Fill 6000 frames (> 4800 emergency threshold)
        pipeline.PrimeRenderBuffer(6000);

        int correction = pipeline.DriftController.EvaluateCorrection();
        Assert.Equal(0, correction); // Emergency handled via discard
        Assert.Equal(1440, pipeline.RenderRing.FillCount); // Reset to target (30 ms)
        Assert.Equal(4560, pipeline.DriftController.DroppedSampleCount);
    }
}
