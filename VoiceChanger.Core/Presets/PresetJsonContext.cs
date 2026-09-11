using System.Text.Json.Serialization;

namespace VoiceChanger.Core.Presets;

/// <summary>
/// Source-generated JsonSerializerContext for voice transformation presets.
/// Eliminates reflection-based serialization, improves cold-start performance,
/// and maintains 100% dependency-free BCL compliance in VoiceChanger.Core (Invariant #3).
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(Preset))]
[JsonSerializable(typeof(List<Preset>))]
[JsonSerializable(typeof(Preset[]))]
public partial class PresetJsonContext : JsonSerializerContext
{
}
