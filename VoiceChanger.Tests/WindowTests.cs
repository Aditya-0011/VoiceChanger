using VoiceChanger.Core.Dsp;
using Xunit;

namespace VoiceChanger.Tests;

public sealed class WindowTests
{
    [Fact]
    public void HannWindow_BoundsAndPeakAreAccurate()
    {
        int length = 1024;
        float[] window = Window.CreateHann(length, periodic: true);

        Assert.Equal(length, window.Length);
        Assert.Equal(0.0f, window[0], precision: 4);

        // Midpoint should be peak = 1.0
        int mid = length / 2;
        Assert.Equal(1.0f, window[mid], precision: 3);

        // All values must be in [0.0, 1.0]
        for (int i = 0; i < length; i++)
        {
            Assert.True(window[i] >= 0f && window[i] <= 1.0001f);
        }
    }

    [Theory]
    [InlineData(1024, 256)] // 4x overlap (25% hop)
    [InlineData(1024, 128)] // 8x overlap (12.5% hop)
    [InlineData(512, 128)]  // 4x overlap
    public void HannWindow_SatisfiesColaCondition(int length, int hop)
    {
        float[] window = Window.CreateHann(length, periodic: true);
        bool isCola = Window.VerifyCola(window, hop, tolerance: 0.001f); // 0.1% tolerance
        Assert.True(isCola, $"Hann window length {length} with hop {hop} should satisfy COLA.");
    }

    [Fact]
    public void NonColaHop_FailsColaVerification()
    {
        float[] window = Window.CreateHann(1024, periodic: true);
        // An arbitrary non-divisor hop like 350 violates COLA
        bool isCola = Window.VerifyCola(window, 350, tolerance: 0.001f);
        Assert.False(isCola, "Irregular hop size should fail COLA condition.");
    }
}
