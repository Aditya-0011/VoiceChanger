using Microsoft.ML.OnnxRuntime;

namespace VoiceChanger.Neural;

/// <summary>
/// Execution provider backends supported by the neural tier.
/// </summary>
public enum ExecutionProviderType
{
    /// <summary>
    /// DirectML hardware acceleration via DirectX 12 (default on Windows 11).
    /// Compatible with NVIDIA, AMD, and Intel Arc GPUs.
    /// </summary>
    DirectML,

    /// <summary>
    /// Standard CPU execution provider (fallback or testing).
    /// </summary>
    Cpu
}

/// <summary>
/// Configuration for ONNX Runtime session creation and execution provider registration.
/// </summary>
public sealed class ExecutionProviderConfig
{
    /// <summary>
    /// Desired execution provider type (defaults to DirectML).
    /// </summary>
    public ExecutionProviderType ProviderType { get; set; } = ExecutionProviderType.DirectML;

    /// <summary>
    /// GPU device ID for DirectML (defaults to 0 for primary GPU).
    /// </summary>
    public int DeviceId { get; set; } = 0;

    /// <summary>
    /// Graph optimization level (defaults to ORT_ENABLE_ALL).
    /// </summary>
    public GraphOptimizationLevel OptimizationLevel { get; set; } = GraphOptimizationLevel.ORT_ENABLE_ALL;

    /// <summary>
    /// Intra-operator thread count (0 allows ONNX Runtime to choose based on CPU core topology).
    /// </summary>
    public int IntraOpNumThreads { get; set; } = 0;

    /// <summary>
    /// Inter-operator thread count (0 allows ONNX Runtime to choose).
    /// </summary>
    public int InterOpNumThreads { get; set; } = 0;

    /// <summary>
    /// Creates configured SessionOptions. If DirectML fails to register, falls back to CPU cleanly.
    /// </summary>
    /// <returns>A new <see cref="SessionOptions"/> instance ready for session initialization.</returns>
    public SessionOptions CreateSessionOptions()
    {
        var options = new SessionOptions
        {
            GraphOptimizationLevel = OptimizationLevel
        };

        if (IntraOpNumThreads > 0)
        {
            options.IntraOpNumThreads = IntraOpNumThreads;
        }

        if (InterOpNumThreads > 0)
        {
            options.InterOpNumThreads = InterOpNumThreads;
        }

        if (ProviderType == ExecutionProviderType.DirectML)
        {
            try
            {
                options.AppendExecutionProvider_DML(DeviceId);
            }
            catch
            {
                // Fallback gracefully to CPU if DirectML fails to initialize on this adapter
                ProviderType = ExecutionProviderType.Cpu;
            }
        }

        return options;
    }
}
