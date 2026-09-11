using System.Text.Json;
using VoiceChanger.Core.Presets;

namespace VoiceChanger.App.Services;

/// <summary>
/// Manages loading, saving, and querying voice transformation presets from %APPDATA%\VoiceChanger\presets.json.
/// Uses source-generated JSON serialization (Invariant #3).
/// </summary>
public sealed class PresetManager
{
    private readonly string _filePath;
    private readonly List<Preset> _presets = [];

    /// <summary>
    /// Current list of available presets.
    /// </summary>
    public IReadOnlyList<Preset> Presets => _presets;

    /// <summary>
    /// Creates a new instance of <see cref="PresetManager"/>.
    /// </summary>
    /// <param name="customPath">Optional custom path for testing.</param>
    public PresetManager(string? customPath = null)
    {
        if (!string.IsNullOrWhiteSpace(customPath))
        {
            _filePath = customPath;
        }
        else
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string dir = Path.Combine(appData, "VoiceChanger");
            Directory.CreateDirectory(dir);
            _filePath = Path.Combine(dir, "presets.json");
        }
    }

    /// <summary>
    /// Loads presets from disk, or seeds default factory presets if the file does not exist.
    /// </summary>
    public void Load()
    {
        _presets.Clear();

        if (File.Exists(_filePath))
        {
            try
            {
                string json = File.ReadAllText(_filePath);
                var list = JsonSerializer.Deserialize(json, PresetJsonContext.Default.ListPreset);
                if (list != null && list.Count > 0)
                {
                    _presets.AddRange(list);
                    return;
                }
            }
            catch
            {
                // Fallback to defaults on corrupted file
            }
        }

        ResetToDefaults();
    }

    /// <summary>
    /// Saves current presets to disk.
    /// </summary>
    public void Save()
    {
        try
        {
            string? dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string json = JsonSerializer.Serialize(_presets, PresetJsonContext.Default.ListPreset);
            File.WriteAllText(_filePath, json);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to save presets: {ex.Message}");
        }
    }

    /// <summary>
    /// Adds or updates a preset in the collection.
    /// </summary>
    public void AddOrUpdate(Preset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);

        int index = _presets.FindIndex(p => p.Id == preset.Id);
        if (index >= 0)
        {
            _presets[index] = preset;
        }
        else
        {
            _presets.Add(preset);
        }

        Save();
    }

    /// <summary>
    /// Deletes a preset by ID.
    /// </summary>
    public bool Delete(string presetId)
    {
        int count = _presets.RemoveAll(p => p.Id == presetId);
        if (count > 0)
        {
            Save();
            return true;
        }

        return false;
    }

    /// <summary>
    /// Resets the preset collection to factory defaults.
    /// </summary>
    public void ResetToDefaults()
    {
        _presets.Clear();
        _presets.AddRange(GetFactoryDefaults());
        Save();
    }

    /// <summary>
    /// Returns the standard set of built-in factory presets.
    /// </summary>
    public static List<Preset> GetFactoryDefaults() =>
    [
        new Preset
        {
            Id = "natural",
            Name = "Natural (Passthrough)",
            Description = "Unmodified voice with clean noise gating",
            PitchSemitones = 0.0f,
            FormantSemitones = 0.0f,
            NoiseGateEnabled = true,
            NoiseGateThresholdDb = -45.0f,
            NoiseGateAttackMs = 5.0f,
            NoiseGateReleaseMs = 80.0f,
            DryWetMix = 1.0f,
            VocoderEnabled = true,
            Hotkey = "Ctrl+Shift+0"
        },
        new Preset
        {
            Id = "deep",
            Name = "Deep Voice",
            Description = "Deep resonant pitch and enlarged vocal tract",
            PitchSemitones = -5.0f,
            FormantSemitones = -4.0f,
            NoiseGateEnabled = true,
            NoiseGateThresholdDb = -45.0f,
            NoiseGateAttackMs = 5.0f,
            NoiseGateReleaseMs = 80.0f,
            DryWetMix = 1.0f,
            VocoderEnabled = true,
            Hotkey = "Ctrl+Shift+1"
        },
        new Preset
        {
            Id = "chipmunk",
            Name = "Chipmunk",
            Description = "High pitch with shrunk vocal tract formants",
            PitchSemitones = 10.0f,
            FormantSemitones = 8.0f,
            NoiseGateEnabled = true,
            NoiseGateThresholdDb = -45.0f,
            NoiseGateAttackMs = 4.0f,
            NoiseGateReleaseMs = 60.0f,
            DryWetMix = 1.0f,
            VocoderEnabled = true,
            Hotkey = "Ctrl+Shift+2"
        },
        new Preset
        {
            Id = "helium",
            Name = "Helium Gas",
            Description = "Elevated formants with subtle pitch lift",
            PitchSemitones = 4.0f,
            FormantSemitones = 9.0f,
            NoiseGateEnabled = true,
            NoiseGateThresholdDb = -45.0f,
            NoiseGateAttackMs = 4.0f,
            NoiseGateReleaseMs = 70.0f,
            DryWetMix = 1.0f,
            VocoderEnabled = true,
            Hotkey = "Ctrl+Shift+3"
        },
        new Preset
        {
            Id = "radio",
            Name = "Radio Walkie-Talkie",
            Description = "Band-limited resonant speech character",
            PitchSemitones = 0.0f,
            FormantSemitones = 3.5f,
            NoiseGateEnabled = true,
            NoiseGateThresholdDb = -38.0f,
            NoiseGateAttackMs = 3.0f,
            NoiseGateReleaseMs = 50.0f,
            DryWetMix = 0.85f,
            VocoderEnabled = true,
            Hotkey = "Ctrl+Shift+4"
        },
        new Preset
        {
            Id = "monster",
            Name = "Monster Beast",
            Description = "Sub-octave guttural rumble",
            PitchSemitones = -9.0f,
            FormantSemitones = -7.0f,
            NoiseGateEnabled = true,
            NoiseGateThresholdDb = -42.0f,
            NoiseGateAttackMs = 5.0f,
            NoiseGateReleaseMs = 90.0f,
            DryWetMix = 1.0f,
            VocoderEnabled = true,
            Hotkey = "Ctrl+Shift+5"
        }
    ];
}
