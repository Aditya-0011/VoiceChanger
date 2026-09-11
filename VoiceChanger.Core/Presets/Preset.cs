using VoiceChanger.Core.Dsp;

namespace VoiceChanger.Core.Presets;

/// <summary>
/// Serializable configuration preset representing a voice transformation profile.
/// Serialized to JSON via source-generated <see cref="PresetJsonContext"/> (Invariant #3).
/// </summary>
public sealed class Preset
{
    /// <summary>
    /// Unique identifier for the preset.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Human-readable display name (e.g. "Deep Voice", "Helium").
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Short description of the acoustic transformation character.
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Pitch shift in semitones (-12.0 to +12.0).
    /// </summary>
    public float PitchSemitones { get; set; }

    /// <summary>
    /// Formant shift in semitones (-12.0 to +12.0).
    /// </summary>
    public float FormantSemitones { get; set; }

    /// <summary>
    /// Whether noise gating is active.
    /// </summary>
    public bool NoiseGateEnabled { get; set; } = true;

    /// <summary>
    /// Noise gate threshold in dB (e.g. -45.0 dB).
    /// </summary>
    public float NoiseGateThresholdDb { get; set; } = -45.0f;

    /// <summary>
    /// Noise gate attack time in milliseconds (e.g. 5.0 ms).
    /// </summary>
    public float NoiseGateAttackMs { get; set; } = 5.0f;

    /// <summary>
    /// Noise gate release time in milliseconds (e.g. 80.0 ms).
    /// </summary>
    public float NoiseGateReleaseMs { get; set; } = 80.0f;

    /// <summary>
    /// Dry/wet crossfade ratio (0.0 = full dry, 1.0 = full wet).
    /// </summary>
    public float DryWetMix { get; set; } = 1.0f;

    /// <summary>
    /// Whether vocoder DSP is active or bypassed.
    /// </summary>
    public bool VocoderEnabled { get; set; } = true;

    /// <summary>
    /// Optional global keyboard shortcut key combination (e.g. "Ctrl+Shift+1").
    /// </summary>
    public string Hotkey { get; set; } = string.Empty;

    /// <summary>
    /// Converts this preset into an immutable runtime <see cref="DspParameters"/> snapshot.
    /// </summary>
    public DspParameters ToParameters() => new()
    {
        PitchSemitones = PitchSemitones,
        FormantSemitones = FormantSemitones,
        NoiseGateEnabled = NoiseGateEnabled,
        NoiseGateThresholdDb = NoiseGateThresholdDb,
        NoiseGateAttackMs = NoiseGateAttackMs,
        NoiseGateReleaseMs = NoiseGateReleaseMs,
        DryWetMix = DryWetMix,
        VocoderEnabled = VocoderEnabled,
    };
}
