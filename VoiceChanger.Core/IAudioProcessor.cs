namespace VoiceChanger.Core;

/// <summary>
/// Central audio processing abstraction for real-time DSP and neural voice transformation.
/// </summary>
public interface IAudioProcessor
{
    /// <summary>
    /// Algorithmic delay in frames that this processor introduces.
    /// </summary>
    int LatencyFrames { get; }

    /// <summary>
    /// Pre-allocates all scratch buffers and initializes state for the given sample rate and maximum block size.
    /// Must not be called from the real-time audio thread.
    /// </summary>
    /// <param name="sampleRate">Sample rate in Hz (e.g., 48000).</param>
    /// <param name="maxBlockSize">Maximum block size in frames passed to <see cref="Process"/>.</param>
    void Prepare(int sampleRate, int maxBlockSize);

    /// <summary>
    /// Processes a block of audio samples. Zero heap allocations permitted.
    /// </summary>
    /// <param name="input">Input audio frame buffer.</param>
    /// <param name="output">Output audio frame buffer.</param>
    void Process(ReadOnlySpan<float> input, Span<float> output);

    /// <summary>
    /// Resets internal filter/vocoder state (e.g. clears history buffers and synthesis phase accumulators).
    /// </summary>
    void Reset();
}
