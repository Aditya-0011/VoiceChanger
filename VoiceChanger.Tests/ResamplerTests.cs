using VoiceChanger.Core.Dsp;
using Xunit;

namespace VoiceChanger.Tests;

public sealed class ResamplerTests
{
    [Fact]
    public void IdentityResampling_RecoversSignal()
    {
        var resampler = new Resampler(capacity: 2048);
        float[] input = new float[256];
        for (int i = 0; i < input.Length; i++)
        {
            input[i] = MathF.Sin(i * 0.1f);
        }

        resampler.Write(input);

        float[] output = new float[250];
        int read = resampler.Read(output, ratio: 1.0);

        Assert.True(read > 240);
        // Catmull-Rom on smooth sine tone at ratio 1.0 should closely match input
        for (int i = 0; i < read; i++)
        {
            Assert.Equal(input[i], output[i], precision: 3);
        }
    }

    [Fact]
    public void RatioHalfSpeed_ProducesTwiceAsManySamplesPerInput()
    {
        var resampler = new Resampler(capacity: 2048);
        float[] input = new float[100];
        for (int i = 0; i < input.Length; i++) input[i] = i;

        resampler.Write(input);

        float[] output = new float[190];
        int read = resampler.Read(output, ratio: 0.5); // step = 0.5 (half speed playback = interpolation)

        Assert.True(read > 180);
    }

    [Fact]
    public void RatioDoubleSpeed_ProducesHalfAsManySamplesPerInput()
    {
        var resampler = new Resampler(capacity: 2048);
        float[] input = new float[100];
        for (int i = 0; i < input.Length; i++) input[i] = i;

        resampler.Write(input);

        float[] output = new float[60];
        int read = resampler.Read(output, ratio: 2.0); // step = 2.0 (double speed playback = decimation)

        Assert.True(read >= 48 && read <= 50);
    }
}
