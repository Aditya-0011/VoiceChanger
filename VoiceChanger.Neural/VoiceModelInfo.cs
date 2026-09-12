namespace VoiceChanger.Neural;

/// <summary>
/// Metadata describing an imported voice model.
/// </summary>
public sealed class VoiceModelInfo
{
    /// <summary>
    /// Friendly display name for the voice model.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Absolute path to the ONNX generator model file.
    /// </summary>
    public string ModelPath { get; set; } = string.Empty;

    /// <summary>
    /// Optional absolute path to associated retrieval index file (.index).
    /// </summary>
    public string? IndexPath { get; set; }

    /// <summary>
    /// Target sample rate of the model's output waveform in Hz (typically 40,000 or 48,000 Hz).
    /// </summary>
    public int TargetSampleRate { get; set; } = 48000;

    /// <summary>
    /// Whether the model requires an F0 pitch contour input (true for standard RVC models).
    /// </summary>
    public bool RequiresF0 { get; set; } = true;

    /// <summary>
    /// Optional default pitch shift offset in semitones for this voice.
    /// </summary>
    public float DefaultPitchShiftSemitones { get; set; } = 0f;

    /// <inheritdoc/>
    public override string ToString() => $"{Name} ({TargetSampleRate} Hz)";
}
