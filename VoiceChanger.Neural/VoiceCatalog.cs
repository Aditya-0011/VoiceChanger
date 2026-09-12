namespace VoiceChanger.Neural;

/// <summary>
/// Discovers and organizes user-imported voice models and shared model assets.
/// </summary>
public sealed class VoiceCatalog
{
    private static readonly HashSet<string> SharedModelBaseNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "contentvec",
        "contentvec_base",
        "hubert",
        "hubert_base",
        "rmvpe",
        "rmvpe_gpu",
        "fcpe"
    };

    /// <summary>
    /// Default root directory where user voice models and base models are stored.
    /// Defaults to %APPDATA%\VoiceChanger\models\.
    /// </summary>
    public static string DefaultModelsDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "VoiceChanger",
        "models");

    /// <summary>
    /// Root directory where user voice models and base models are stored.
    /// Defaults to %APPDATA%\VoiceChanger\models\, but can be customized.
    /// </summary>
    public string ModelsDirectory { get; private set; }

    /// <summary>
    /// Path to shared ContentVec / HuBERT encoder model if present.
    /// </summary>
    public string? ContentVecModelPath =>
        FindSharedModel("contentvec.onnx") ??
        FindSharedModel("hubert.onnx") ??
        FindSharedModel("hubert_base.onnx") ??
        FindSharedModel("contentvec_base.onnx");

    /// <summary>
    /// Path to shared RMVPE F0 pitch extractor model if present.
    /// </summary>
    public string? RmvpeModelPath =>
        FindSharedModel("rmvpe.onnx") ??
        FindSharedModel("fcpe.onnx") ??
        FindSharedModel("rmvpe_gpu.onnx");

    /// <summary>
    /// Initializes a new instance of <see cref="VoiceCatalog"/>.
    /// </summary>
    /// <param name="customDirectory">Optional custom directory path. If null, checks VOICECHANGER_MODELS_DIR or uses %APPDATA%\VoiceChanger\models\.</param>
    public VoiceCatalog(string? customDirectory = null)
    {
        ModelsDirectory = !string.IsNullOrWhiteSpace(customDirectory)
            ? Path.GetFullPath(customDirectory)
            : ResolveInitialDirectory();

        EnsureDirectoryExists();
    }

    private static string ResolveInitialDirectory()
    {
        string? envDir = Environment.GetEnvironmentVariable("VOICECHANGER_MODELS_DIR");
        if (!string.IsNullOrWhiteSpace(envDir))
        {
            return Path.GetFullPath(envDir);
        }

        return DefaultModelsDirectory;
    }

    /// <summary>
    /// Updates the models directory location and ensures it exists.
    /// Pass null or empty string to reset to the default %APPDATA% location.
    /// </summary>
    /// <param name="newDirectory">The new models directory, or null/empty to reset to default.</param>
    public void SetModelsDirectory(string? newDirectory)
    {
        ModelsDirectory = !string.IsNullOrWhiteSpace(newDirectory)
            ? Path.GetFullPath(newDirectory)
            : DefaultModelsDirectory;

        EnsureDirectoryExists();
    }

    /// <summary>
    /// Ensures the models folder structure exists on disk.
    /// </summary>
    public void EnsureDirectoryExists()
    {
        try
        {
            if (!Directory.Exists(ModelsDirectory))
            {
                Directory.CreateDirectory(ModelsDirectory);
            }
        }
        catch
        {
            // Directory access failure: suppress to avoid crashing if path is temporarily unavailable
        }
    }

    /// <summary>
    /// Scans the models directory and returns all discovered voice generator models.
    /// Supports nested subdirectories (e.g. root/models/SpeakerA/model.onnx, root/SpeakerA/model.onnx).
    /// </summary>
    /// <returns>A list of discovered <see cref="VoiceModelInfo"/> items.</returns>
    public IReadOnlyList<VoiceModelInfo> GetAvailableVoices()
    {
        var result = new List<VoiceModelInfo>();
        var seenModelPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!Directory.Exists(ModelsDirectory))
        {
            return result;
        }

        try
        {
            // Search all .onnx files in the models directory tree
            string[] onnxFiles = Directory.GetFiles(ModelsDirectory, "*.onnx", SearchOption.AllDirectories);

            foreach (string onnxFile in onnxFiles)
            {
                string baseName = Path.GetFileNameWithoutExtension(onnxFile);
                if (SharedModelBaseNames.Contains(baseName))
                {
                    continue;
                }

                string? dirPath = Path.GetDirectoryName(onnxFile);
                if (string.IsNullOrEmpty(dirPath))
                {
                    continue;
                }

                string dirName = Path.GetFileName(dirPath);

                // Ignore models inside shared "base" assets folder
                if (string.Equals(dirName, "base", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!seenModelPaths.Add(onnxFile))
                {
                    continue;
                }

                // Determine clean display name
                string cleanBase = baseName.Replace('-', ' ').Replace('_', ' ');
                string displayName;

                if (string.Equals(dirPath, ModelsDirectory, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(dirName, "models", StringComparison.OrdinalIgnoreCase))
                {
                    displayName = baseName;
                }
                else if (string.Equals(baseName, "model", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(baseName, "generator", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(cleanBase, dirName, StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(baseName, dirName, StringComparison.OrdinalIgnoreCase))
                {
                    displayName = dirName;
                }
                else
                {
                    displayName = $"{dirName} - {baseName}";
                }

                // Match .index file
                string? indexPath = null;
                string directIndex = Path.ChangeExtension(onnxFile, ".index");
                if (File.Exists(directIndex))
                {
                    indexPath = directIndex;
                }
                else
                {
                    string[] dirIndexes = Directory.GetFiles(dirPath, "*.index");
                    if (dirIndexes.Length > 0)
                    {
                        indexPath = dirIndexes[0];
                    }
                }

                result.Add(new VoiceModelInfo
                {
                    Name = displayName,
                    ModelPath = onnxFile,
                    IndexPath = indexPath,
                    TargetSampleRate = 48000,
                    RequiresF0 = true
                });
            }
        }
        catch
        {
            // Directory access failure: return any collected models without throwing
        }

        return result;
    }

    private string? FindSharedModel(string fileName)
    {
        // 1. Direct candidates in active ModelsDirectory and default %APPDATA%
        string[] candidates =
        [
            Path.Combine(ModelsDirectory, fileName),
            Path.Combine(ModelsDirectory, "base", fileName),
            Path.Combine(ModelsDirectory, "models", "base", fileName),
            Path.Combine(ModelsDirectory, "models", fileName),
            Path.Combine(DefaultModelsDirectory, fileName),
            Path.Combine(DefaultModelsDirectory, "base", fileName)
        ];

        foreach (string candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        // 2. Recursive search in ModelsDirectory if nested in custom subfolder
        try
        {
            if (Directory.Exists(ModelsDirectory))
            {
                string[] matches = Directory.GetFiles(ModelsDirectory, fileName, SearchOption.AllDirectories);
                if (matches.Length > 0)
                {
                    return matches[0];
                }
            }
        }
        catch
        {
            // Suppress search errors
        }

        return null;
    }
}
