namespace VoiceChanger.Core;

/// <summary>
/// Passthrough audio processor that forwards input audio unmodified to output.
/// Serves as the Phase 0 baseline and zero-allocation reference implementation.
/// </summary>
public sealed class PassthroughProcessor : IAudioProcessor
{
    /// <inheritdoc/>
    public int LatencyFrames => 0;

    /// <inheritdoc/>
    public void Prepare(int sampleRate, int maxBlockSize)
    {
        // Passthrough requires no pre-allocated DSP history or state buffers.
    }

    /// <inheritdoc/>
    public void Process(ReadOnlySpan<float> input, Span<float> output)
    {
        int count = Math.Min(input.Length, output.Length);
        input[..count].CopyTo(output[..count]);

        if (output.Length > count)
        {
            output[count..].Clear();
        }
    }

    /// <inheritdoc/>
    public void Reset()
    {
        // No state to clear.
    }
}
