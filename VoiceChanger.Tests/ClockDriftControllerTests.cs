using VoiceChanger.Audio.Drift;
using VoiceChanger.Core.Buffers;
using Xunit;

namespace VoiceChanger.Tests;

public sealed class ClockDriftControllerTests
{
    [Fact]
    public void NominalFill_ProducesZeroCorrection()
    {
        var ring = new SpscRingBuffer(1024);
        // Fill 500 frames (approx 50% = between 25% and 75%)
        ring.Write(new float[500]);

        var controller = new ClockDriftController(ring, highThresholdRatio: 0.75, lowThresholdRatio: 0.25, minIntervalBetweenCorrectionsMs: 10);

        int correction = controller.EvaluateCorrection();
        Assert.Equal(0, correction);
        Assert.Equal(0, controller.DroppedSampleCount);
        Assert.Equal(0, controller.DuplicatedSampleCount);
        Assert.True(controller.FillPercentage > 45 && controller.FillPercentage < 55);
    }

    [Fact]
    public void HighFill_TriggersDropCorrection()
    {
        var ring = new SpscRingBuffer(1024);
        // Fill 800 frames (78.1% > 75%)
        ring.Write(new float[800]);

        var controller = new ClockDriftController(ring, highThresholdRatio: 0.75, lowThresholdRatio: 0.25, minIntervalBetweenCorrectionsMs: 1);

        int correction = controller.EvaluateCorrection();
        Assert.Equal(1, correction); // Drop sample
        Assert.Equal(1, controller.DroppedSampleCount);
        Assert.Equal(0, controller.DuplicatedSampleCount);
    }

    [Fact]
    public void LowFill_TriggersDuplicateCorrection()
    {
        var ring = new SpscRingBuffer(1024);
        // Fill 200 frames (19.5% < 25%)
        ring.Write(new float[200]);

        var controller = new ClockDriftController(ring, highThresholdRatio: 0.75, lowThresholdRatio: 0.25, minIntervalBetweenCorrectionsMs: 1);

        int correction = controller.EvaluateCorrection();
        Assert.Equal(-1, correction); // Duplicate sample
        Assert.Equal(0, controller.DroppedSampleCount);
        Assert.Equal(1, controller.DuplicatedSampleCount);
    }

    [Fact]
    public void RateLimiter_PreventsRapidConsecutiveCorrections()
    {
        var ring = new SpscRingBuffer(1024);
        ring.Write(new float[800]); // > 75%

        // 500 ms minimum interval between corrections
        var controller = new ClockDriftController(ring, highThresholdRatio: 0.75, lowThresholdRatio: 0.25, minIntervalBetweenCorrectionsMs: 500);

        int first = controller.EvaluateCorrection();
        Assert.Equal(1, first);

        // Immediate second call should be rate-limited
        int second = controller.EvaluateCorrection();
        Assert.Equal(0, second);
        Assert.Equal(1, controller.DroppedSampleCount);
    }

    [Fact]
    public void LinearRegression_CalculatesExactDriftSlope()
    {
        var ring = new SpscRingBuffer(1024);
        var controller = new ClockDriftController(ring);

        // Feed synthetic linear slope of +10 samples/sec
        controller.AddObservation(0.0, 500);
        controller.AddObservation(0.5, 505);
        controller.AddObservation(1.0, 510);
        controller.AddObservation(1.5, 515);
        controller.AddObservation(2.0, 520);

        Assert.Equal(10.0, controller.DriftRateSamplesPerSec, precision: 2);

        // Test reset and negative slope of -5.0 samples/sec
        controller.Reset();
        controller.AddObservation(0.0, 500);
        controller.AddObservation(1.0, 495);
        controller.AddObservation(2.0, 490);

        Assert.Equal(-5.0, controller.DriftRateSamplesPerSec, precision: 2);
    }
}
