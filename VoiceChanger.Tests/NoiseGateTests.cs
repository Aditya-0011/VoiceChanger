using VoiceChanger.Core.Dsp;
using Xunit;

namespace VoiceChanger.Tests;

public class NoiseGateTests
{
    [Fact]
    public void NoiseGate_AttenuatesSignals_BelowThreshold()
    {
        var gate = new NoiseGate(thresholdDb: -30.0f, attackMs: 2.0f, releaseMs: 10.0f);
        gate.Prepare(48000);

        // Low amplitude noise / keystroke-like transient (-50 dB = amplitude ~0.003)
        float[] input = new float[4800];
        float[] output = new float[4800];
        float lowAmplitude = 0.003f;
        for (int i = 0; i < input.Length; i++)
        {
            input[i] = lowAmplitude * MathF.Sin(2.0f * MathF.PI * 1000.0f * i / 48000.0f);
        }

        gate.Process(input, output);

        // Assert steady-state output is attenuated close to silence (< 1e-4)
        float steadyStateMax = 0f;
        for (int i = 2400; i < output.Length; i++)
        {
            steadyStateMax = Math.Max(steadyStateMax, MathF.Abs(output[i]));
        }

        Assert.True(steadyStateMax < 0.0005f, $"Expected attenuation below 0.0005, got {steadyStateMax}");
    }

    [Fact]
    public void NoiseGate_PassesSignals_AboveThreshold()
    {
        var gate = new NoiseGate(thresholdDb: -30.0f, attackMs: 2.0f, releaseMs: 20.0f);
        gate.Prepare(48000);

        // High amplitude voice signal (-10 dB = amplitude ~0.316)
        float[] input = new float[4800];
        float[] output = new float[4800];
        float voiceAmplitude = 0.316f;
        for (int i = 0; i < input.Length; i++)
        {
            input[i] = voiceAmplitude * MathF.Sin(2.0f * MathF.PI * 440.0f * i / 48000.0f);
        }

        gate.Process(input, output);

        // Assert steady-state output passes through with unity gain (> 95% amplitude)
        float steadyStateMax = 0f;
        for (int i = 2400; i < output.Length; i++)
        {
            steadyStateMax = Math.Max(steadyStateMax, MathF.Abs(output[i]));
        }

        Assert.InRange(steadyStateMax, voiceAmplitude * 0.95f, voiceAmplitude * 1.05f);
    }

    [Fact]
    public void NoiseGate_ZeroAllocations_InAudioProcessCallback()
    {
        var gate = new NoiseGate(thresholdDb: -40.0f);
        gate.Prepare(48000);

        float[] input = new float[256];
        float[] output = new float[256];
        for (int i = 0; i < input.Length; i++)
        {
            input[i] = 0.1f * MathF.Sin(i);
        }

        // Warmup JIT
        for (int i = 0; i < 100; i++)
        {
            gate.Process(input, output);
        }

        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
        {
            gate.Process(input, output);
        }
        long allocatedAfter = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(0, allocatedAfter - allocatedBefore);
    }
}
