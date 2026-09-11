using System.Text.Json;
using VoiceChanger.Core.Presets;
using Xunit;

namespace VoiceChanger.Tests;

public class PresetSerializationTests
{
    [Fact]
    public void Presets_SourceGeneratedJson_RoundTripsAccurately()
    {
        var presets = new List<Preset>
        {
            new()
            {
                Id = "natural",
                Name = "Natural (Passthrough)",
                Description = "Unmodified natural voice",
                PitchSemitones = 0.0f,
                FormantSemitones = 0.0f,
                NoiseGateEnabled = true,
                NoiseGateThresholdDb = -50.0f,
                DryWetMix = 1.0f,
                VocoderEnabled = true,
                Hotkey = "Ctrl+Shift+0"
            },
            new()
            {
                Id = "deep",
                Name = "Deep Voice",
                Description = "Deep resonant pitch and lowered vocal tract",
                PitchSemitones = -5.0f,
                FormantSemitones = -4.0f,
                NoiseGateEnabled = true,
                NoiseGateThresholdDb = -45.0f,
                DryWetMix = 1.0f,
                VocoderEnabled = true,
                Hotkey = "Ctrl+Shift+1"
            },
            new()
            {
                Id = "chipmunk",
                Name = "Chipmunk",
                Description = "High pitch with shrunk vocal tract formants",
                PitchSemitones = 10.0f,
                FormantSemitones = 8.0f,
                NoiseGateEnabled = true,
                NoiseGateThresholdDb = -45.0f,
                DryWetMix = 1.0f,
                VocoderEnabled = true,
                Hotkey = "Ctrl+Shift+2"
            }
        };

        // Serialize using source-generated PresetJsonContext
        string json = JsonSerializer.Serialize(presets, PresetJsonContext.Default.ListPreset);
        Assert.NotNull(json);
        Assert.Contains("natural", json);
        Assert.Contains("deep", json);
        Assert.Contains("chipmunk", json);

        // Deserialize back
        var deserialized = JsonSerializer.Deserialize(json, PresetJsonContext.Default.ListPreset);
        Assert.NotNull(deserialized);
        Assert.Equal(3, deserialized.Count);

        Assert.Equal("Deep Voice", deserialized[1].Name);
        Assert.Equal(-5.0f, deserialized[1].PitchSemitones);
        Assert.Equal(-4.0f, deserialized[1].FormantSemitones);
        Assert.Equal("Ctrl+Shift+1", deserialized[1].Hotkey);

        // Convert to runtime DspParameters
        var dspParams = deserialized[1].ToParameters();
        Assert.Equal(-5.0f, dspParams.PitchSemitones);
        Assert.Equal(-4.0f, dspParams.FormantSemitones);
    }
}
