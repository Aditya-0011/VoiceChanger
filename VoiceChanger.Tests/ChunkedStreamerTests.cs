using VoiceChanger.Neural;
using Xunit;

namespace VoiceChanger.Tests;

public class ChunkedStreamerTests
{
    [Fact]
    public void ChunkedStreamer_PreservesSineWaveformContinuity_AcrossChunkBoundaries()
    {
        // 48 kHz, 100 ms window, 20 ms overlap for fast unit testing
        const int sampleRate = 48000;
        const int windowMs = 100;
        const int overlapMs = 20;
        const int blockSize = 256;
        const float frequencyHz = 440f;

        var streamer = new ChunkedStreamer(sampleRate, windowMs, overlapMs);
        int windowFrames = streamer.WindowFrames; // 4,800
        int hopFrames = streamer.HopFrames;       // 3,840

        float[] inputBlock = new float[blockSize];
        float[] windowBuffer = new float[windowFrames];
        float[] outputBlock = new float[blockSize];

        double phase = 0;
        double phaseStep = 2.0 * Math.PI * frequencyHz / sampleRate;

        // Stream 1.0 second of audio (48,000 samples)
        int totalFrames = sampleRate;
        int framesSent = 0;
        int framesReceived = 0;

        var collectedOutput = new List<float>();

        while (framesSent < totalFrames)
        {
            // Generate synthetic sine chunk
            for (int i = 0; i < blockSize; i++)
            {
                inputBlock[i] = (float)Math.Sin(phase);
                phase += phaseStep;
                if (phase >= 2.0 * Math.PI) phase -= 2.0 * Math.PI;
            }

            streamer.PushInput(inputBlock);
            framesSent += blockSize;

            // Extract ready windows and feed through identity inference
            while (streamer.TryExtractWindow(windowBuffer))
            {
                streamer.PushInferenceResult(windowBuffer);
            }

            // Read available output
            int read = streamer.ReadOutput(outputBlock);
            if (read > 0)
            {
                for (int i = 0; i < read; i++)
                {
                    collectedOutput.Add(outputBlock[i]);
                }
                framesReceived += read;
            }
        }

        // Drain any remaining output
        while (streamer.AvailableOutputFrames > 0)
        {
            int read = streamer.ReadOutput(outputBlock);
            for (int i = 0; i < read; i++)
            {
                collectedOutput.Add(outputBlock[i]);
            }
            framesReceived += read;
        }

        Assert.True(collectedOutput.Count > 0, "Should have received audio output from streamer.");

        // Check steady-state RMS after latency fill (discard initial window)
        int latencyFrames = streamer.LatencyFrames;
        Assert.True(collectedOutput.Count > latencyFrames * 2, "Stream should exceed latency fill.");

        var steadyState = collectedOutput.Skip(latencyFrames).Take(collectedOutput.Count - latencyFrames - hopFrames).ToArray();

        double sumSq = 0;
        for (int i = 0; i < steadyState.Length; i++)
        {
            sumSq += steadyState[i] * steadyState[i];
        }
        double rms = Math.Sqrt(sumSq / steadyState.Length);

        // Theoretical RMS of sine is 1 / sqrt(2) = ~0.7071
        Assert.True(rms > 0.60 && rms < 0.80, $"Steady state RMS {rms:F4} should be close to 0.7071.");

        // Check that there are no NaN or infinite values
        for (int i = 0; i < steadyState.Length; i++)
        {
            Assert.False(float.IsNaN(steadyState[i]), $"NaN detected at index {i}");
            Assert.False(float.IsInfinity(steadyState[i]), $"Infinity detected at index {i}");
        }
    }

    [Fact]
    public void ChunkedStreamer_ZeroAllocations_InSteadyStateStreaming()
    {
        var streamer = new ChunkedStreamer(48000, 300, 50);
        float[] inBlock = new float[256];
        float[] outBlock = new float[256];

        // Warm up JIT
        for (int i = 0; i < 500; i++)
        {
            streamer.PushInput(inBlock);
            streamer.ReadOutput(outBlock);
        }

        // Measure allocations
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 5000; i++)
        {
            streamer.PushInput(inBlock);
            streamer.ReadOutput(outBlock);
        }
        long allocatedAfter = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(0, allocatedAfter - allocatedBefore);
    }
}
