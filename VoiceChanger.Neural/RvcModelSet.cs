using Microsoft.ML.OnnxRuntime;

namespace VoiceChanger.Neural;

/// <summary>
/// Manages shared speaker-independent ONNX sessions (ContentVec content encoder, RMVPE F0 extractor)
/// and per-voice Generator sessions. Supports atomic hot-swapping of voice models without audio dropout.
/// </summary>
public sealed class RvcModelSet : IDisposable
{
    private readonly ExecutionProviderConfig _config;
    private InferenceSession? _encoderSession;
    private InferenceSession? _f0Session;
    private InferenceSession? _generatorSession;
    private VoiceModelInfo? _activeVoice;
    private bool _disposed;
    private readonly object _swapLock = new();

    /// <summary>
    /// Shared speaker-independent ContentVec / HuBERT content encoder session.
    /// </summary>
    public InferenceSession? EncoderSession => Volatile.Read(ref _encoderSession);

    /// <summary>
    /// Shared speaker-independent RMVPE pitch extractor session.
    /// </summary>
    public InferenceSession? F0Session => Volatile.Read(ref _f0Session);

    /// <summary>
    /// Active per-voice Generator / Synthesizer session.
    /// </summary>
    public InferenceSession? GeneratorSession => Volatile.Read(ref _generatorSession);

    /// <summary>
    /// Currently loaded voice model metadata.
    /// </summary>
    public VoiceModelInfo? ActiveVoice => Volatile.Read(ref _activeVoice);

    /// <summary>
    /// Whether at least a generator session or shared sessions are loaded.
    /// </summary>
    public bool IsLoaded => _generatorSession != null || _encoderSession != null;

    /// <summary>
    /// Initializes a new instance of <see cref="RvcModelSet"/>.
    /// </summary>
    /// <param name="config">Execution provider and session options configuration.</param>
    public RvcModelSet(ExecutionProviderConfig? config = null)
    {
        _config = config ?? new ExecutionProviderConfig();
    }

    /// <summary>
    /// Loads the shared speaker-independent sessions (ContentVec and RMVPE).
    /// </summary>
    /// <param name="encoderPath">Path to ContentVec or HuBERT ONNX model.</param>
    /// <param name="f0Path">Path to RMVPE or FCPE ONNX model.</param>
    public void LoadSharedSessions(string? encoderPath, string? f0Path)
    {
        lock (_swapLock)
        {
            if (!string.IsNullOrWhiteSpace(encoderPath) && File.Exists(encoderPath))
            {
                _encoderSession?.Dispose();
                _encoderSession = CreateSession(encoderPath);
            }

            if (!string.IsNullOrWhiteSpace(f0Path) && File.Exists(f0Path))
            {
                _f0Session?.Dispose();
                _f0Session = CreateSession(f0Path);
            }
        }
    }

    /// <summary>
    /// Hot-swaps the active voice generator session without stopping audio.
    /// The new session is initialized before atomically replacing the active reference.
    /// </summary>
    /// <param name="voice">Target voice model metadata.</param>
    public void LoadVoice(VoiceModelInfo voice)
    {
        ArgumentNullException.ThrowIfNull(voice);

        if (!File.Exists(voice.ModelPath))
        {
            throw new FileNotFoundException($"Model file not found: {voice.ModelPath}", voice.ModelPath);
        }

        // Initialize new session before taking lock
        var newSession = CreateSession(voice.ModelPath);

        InferenceSession? oldSession;
        lock (_swapLock)
        {
            oldSession = _generatorSession;
            Volatile.Write(ref _generatorSession, newSession);
            Volatile.Write(ref _activeVoice, voice);
        }

        // Safely dispose old session outside lock
        oldSession?.Dispose();
    }

    private InferenceSession CreateSession(string modelPath)
    {
        try
        {
            using var options = _config.CreateSessionOptions();
            return new InferenceSession(modelPath, options);
        }
        catch when (_config.ProviderType == ExecutionProviderType.DirectML)
        {
            // DirectML can fail on specific ops or memory constraints; fallback cleanly to CPU
            using var cpuOptions = new SessionOptions
            {
                GraphOptimizationLevel = _config.OptimizationLevel
            };
            return new InferenceSession(modelPath, cpuOptions);
        }
    }

    /// <summary>
    /// Unloads all sessions.
    /// </summary>
    public void Unload()
    {
        lock (_swapLock)
        {
            _generatorSession?.Dispose();
            _generatorSession = null;

            _encoderSession?.Dispose();
            _encoderSession = null;

            _f0Session?.Dispose();
            _f0Session = null;

            _activeVoice = null;
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Unload();
    }
}
