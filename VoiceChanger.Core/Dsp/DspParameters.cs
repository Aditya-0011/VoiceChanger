namespace VoiceChanger.Core.Dsp;

/// <summary>
/// Immutable snapshot of audio processing parameters (P3-0).
/// Published atomically from the UI/control thread and read once per block by the audio worker thread,
/// ensuring zero heap allocations in the audio processing path (Invariant #1).
/// </summary>
public sealed record DspParameters
{
    /// <summary>
    /// Pitch shift in semitones (-12.0 to +12.0). Default is 0.0 (unmodified pitch).
    /// </summary>
    public float PitchSemitones { get; init; } = 0.0f;

    /// <summary>
    /// Formant shift in semitones (-12.0 to +12.0). Default is 0.0 (unmodified formants).
    /// </summary>
    public float FormantSemitones { get; init; } = 0.0f;

    /// <summary>
    /// Whether the noise gate ahead of the vocoder is active.
    /// </summary>
    public bool NoiseGateEnabled { get; init; } = true;

    /// <summary>
    /// Noise gate threshold in decibels (e.g. -45.0 dB). Audio below this level is attenuated.
    /// </summary>
    public float NoiseGateThresholdDb { get; init; } = -45.0f;

    /// <summary>
    /// Noise gate attack time in milliseconds (e.g. 5.0 ms).
    /// </summary>
    public float NoiseGateAttackMs { get; init; } = 5.0f;

    /// <summary>
    /// Noise gate release time in milliseconds (e.g. 80.0 ms).
    /// </summary>
    public float NoiseGateReleaseMs { get; init; } = 80.0f;

    /// <summary>
    /// Dry/wet crossfade ratio (0.0 = 100% dry, 1.0 = 100% wet).
    /// </summary>
    public float DryWetMix { get; init; } = 1.0f;

    /// <summary>
    /// Whether the phase vocoder DSP tier is enabled. If false, passes audio through.
    /// </summary>
    public bool VocoderEnabled { get; init; } = true;
}
