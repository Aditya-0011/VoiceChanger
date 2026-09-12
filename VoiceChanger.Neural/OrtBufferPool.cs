using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;

namespace VoiceChanger.Neural;

/// <summary>
/// A pre-allocated pinned memory buffer paired with an <see cref="OrtValue"/> tensor wrapper.
/// Guarantees zero heap allocation and zero data copies during ONNX Runtime inference.
/// </summary>
/// <typeparam name="T">Unmanaged data type (e.g. float, long, bool).</typeparam>
public sealed class PinnedTensorBuffer<T> : IDisposable where T : unmanaged
{
    private readonly T[] _data;
    private GCHandle _gcHandle;
    private readonly OrtValue _ortValue;
    private readonly long[] _shape;
    private bool _disposed;

    /// <summary>
    /// Underlying managed array.
    /// </summary>
    public T[] Array => _data;

    /// <summary>
    /// Memory view of the pinned buffer.
    /// </summary>
    public Memory<T> Memory => _data.AsMemory();

    /// <summary>
    /// Span view of the pinned buffer for zero-allocation copy operations.
    /// </summary>
    public Span<T> Span => _data.AsSpan();

    /// <summary>
    /// Reusable <see cref="OrtValue"/> native tensor wrapper.
    /// </summary>
    public OrtValue Value => _ortValue;

    /// <summary>
    /// Tensor dimension shape.
    /// </summary>
    public long[] Shape => _shape;

    /// <summary>
    /// Initializes a new pinned buffer and creates an <see cref="OrtValue"/> tensor over it.
    /// </summary>
    /// <param name="length">Number of elements in the buffer.</param>
    /// <param name="shape">Tensor dimensions.</param>
    public PinnedTensorBuffer(int length, long[] shape)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(length, 0);
        ArgumentNullException.ThrowIfNull(shape);

        _data = new T[length];
        _shape = (long[])shape.Clone();
        _gcHandle = GCHandle.Alloc(_data, GCHandleType.Pinned);

        _ortValue = OrtValue.CreateTensorValueFromMemory(
            OrtMemoryInfo.DefaultInstance,
            _data.AsMemory(),
            _shape);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _ortValue.Dispose();
        if (_gcHandle.IsAllocated)
        {
            _gcHandle.Free();
        }
    }
}

/// <summary>
/// Backward-compatible float-specific pinned buffer wrapper.
/// </summary>
public sealed class PinnedTensorBuffer : IDisposable
{
    private readonly PinnedTensorBuffer<float> _inner;

    /// <summary>
    /// Underlying managed float array.
    /// </summary>
    public float[] Array => _inner.Array;

    /// <summary>
    /// Memory view of the pinned buffer.
    /// </summary>
    public Memory<float> Memory => _inner.Memory;

    /// <summary>
    /// Span view of the pinned buffer for zero-allocation copy operations.
    /// </summary>
    public Span<float> Span => _inner.Span;

    /// <summary>
    /// Reusable <see cref="OrtValue"/> native tensor wrapper.
    /// </summary>
    public OrtValue Value => _inner.Value;

    /// <summary>
    /// Tensor dimension shape.
    /// </summary>
    public long[] Shape => _inner.Shape;

    /// <summary>
    /// Initializes a new pinned float buffer.
    /// </summary>
    public PinnedTensorBuffer(int length, long[] shape)
    {
        _inner = new PinnedTensorBuffer<float>(length, shape);
    }

    /// <inheritdoc/>
    public void Dispose() => _inner.Dispose();
}

/// <summary>
/// Pre-allocates and manages pinned tensor buffers and memory pools
/// for HuBERT/ContentVec content encoding, pitch extraction, and Generator inference.
/// </summary>
public sealed class OrtBufferPool : IDisposable
{
    private readonly int _audioWindowFrames;
    private readonly int _featureFrames;
    private readonly int _featureDim;
    private readonly int _hubert16kLen;
    private readonly int _history48kLen;

    private readonly PinnedTensorBuffer _inputAudio;
    private readonly PinnedTensorBuffer _outputAudio;
    private readonly float[] _history48k;

    private readonly PinnedTensorBuffer<float> _hubertSourceAudio;
    private readonly PinnedTensorBuffer<bool> _hubertPaddingMask;
    private readonly PinnedTensorBuffer<float> _phoneFeatures;
    private readonly PinnedTensorBuffer<long> _phoneLengths;
    private readonly PinnedTensorBuffer<long> _pitchBins;
    private readonly PinnedTensorBuffer<float> _nsff0;
    private readonly PinnedTensorBuffer<long> _speakerId;
    private readonly PinnedTensorBuffer<float> _rndNoise;

    private readonly PinnedTensorBuffer _encoderFeaturesLegacy;
    private readonly PinnedTensorBuffer _f0PitchLegacy;

    private bool _disposed;

    /// <summary>
    /// Audio window duration in frames (default 14,400 = 300 ms @ 48 kHz).
    /// </summary>
    public int AudioWindowFrames => _audioWindowFrames;

    /// <summary>
    /// Number of feature frames (default 30 for 300 ms @ 48 kHz).
    /// </summary>
    public int FeatureFrames => _featureFrames;

    /// <summary>
    /// Feature embedding dimension (768 for HuBERT/ContentVec).
    /// </summary>
    public int FeatureDim => _featureDim;

    /// <summary>
    /// Input sequence length for HuBERT @ 16 kHz (e.g. 9680 for 30 frames).
    /// </summary>
    public int Hubert16kLen => _hubert16kLen;

    /// <summary>
    /// Rolling 48 kHz context length before 3:1 decimation (e.g. 29040 samples).
    /// </summary>
    public int History48kLen => _history48kLen;

    /// <summary>
    /// Audio input tensor [1, WindowFrames].
    /// </summary>
    public PinnedTensorBuffer InputAudio => _inputAudio;

    /// <summary>
    /// Waveform output tensor [1, 1, WindowFrames] or [1, WindowFrames].
    /// </summary>
    public PinnedTensorBuffer OutputAudio => _outputAudio;

    /// <summary>
    /// Rolling 48 kHz context buffer used for 3:1 anti-aliasing decimation to 16 kHz.
    /// </summary>
    public float[] History48k => _history48k;

    /// <summary>
    /// 16 kHz source audio tensor for HuBERT encoder [1, Hubert16kLen].
    /// </summary>
    public PinnedTensorBuffer<float> HubertSourceAudio => _hubertSourceAudio;

    /// <summary>
    /// Boolean padding mask tensor for HuBERT encoder [1, Hubert16kLen] (all false).
    /// </summary>
    public PinnedTensorBuffer<bool> HubertPaddingMask => _hubertPaddingMask;

    /// <summary>
    /// Intermediate content feature tensor [1, FeatureFrames, FeatureDim] (HuBERT phonetic embeddings).
    /// </summary>
    public PinnedTensorBuffer<float> PhoneFeatures => _phoneFeatures;

    /// <summary>
    /// Sequence length tensor [1] containing the number of feature frames (e.g. 30).
    /// </summary>
    public PinnedTensorBuffer<long> PhoneLengths => _phoneLengths;

    /// <summary>
    /// Coarse pitch bin tensor [1, FeatureFrames] (values 1..255).
    /// </summary>
    public PinnedTensorBuffer<long> PitchBins => _pitchBins;

    /// <summary>
    /// Continuous fundamental frequency F0 tensor in Hz [1, FeatureFrames].
    /// </summary>
    public PinnedTensorBuffer<float> Nsff0 => _nsff0;

    /// <summary>
    /// Speaker ID tensor [1] (default 0).
    /// </summary>
    public PinnedTensorBuffer<long> SpeakerId => _speakerId;

    /// <summary>
    /// Random noise tensor [1, 192, FeatureFrames] for models that accept stochastic noise.
    /// </summary>
    public PinnedTensorBuffer<float> RndNoise => _rndNoise;

    /// <summary>
    /// Legacy feature tensor alias.
    /// </summary>
    public PinnedTensorBuffer EncoderFeatures => _encoderFeaturesLegacy;

    /// <summary>
    /// Legacy pitch tensor alias.
    /// </summary>
    public PinnedTensorBuffer F0Pitch => _f0PitchLegacy;

    /// <summary>
    /// Initializes pre-allocated pinned buffers for the RVC pipeline.
    /// </summary>
    /// <param name="audioWindowFrames">Number of audio frames per inference window (default 14,400 = 300 ms @ 48 kHz).</param>
    /// <param name="featureFrames">Optional explicit feature frame count. If null or 0, derived as audioWindowFrames / 480.</param>
    /// <param name="featureDim">Dimension of content features (default 768 for ContentVec/HuBERT).</param>
    public OrtBufferPool(int audioWindowFrames = 14400, int featureFrames = 0, int featureDim = 768)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(audioWindowFrames, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(featureDim, 0);

        _audioWindowFrames = audioWindowFrames;
        _featureFrames = featureFrames > 0 ? featureFrames : Math.Max(1, audioWindowFrames / 480);
        _featureDim = featureDim;

        _hubert16kLen = _featureFrames * 320 + 80;
        _history48kLen = _hubert16kLen * 3;
        _history48k = new float[_history48kLen];

        _inputAudio = new PinnedTensorBuffer(audioWindowFrames, [1, audioWindowFrames]);
        _outputAudio = new PinnedTensorBuffer(audioWindowFrames, [1, 1, audioWindowFrames]);

        _hubertSourceAudio = new PinnedTensorBuffer<float>(_hubert16kLen, [1, _hubert16kLen]);
        _hubertPaddingMask = new PinnedTensorBuffer<bool>(_hubert16kLen, [1, _hubert16kLen]);

        _phoneFeatures = new PinnedTensorBuffer<float>(_featureFrames * featureDim, [1, _featureFrames, featureDim]);
        _phoneLengths = new PinnedTensorBuffer<long>(1, [1]);
        _phoneLengths.Span[0] = _featureFrames;

        _pitchBins = new PinnedTensorBuffer<long>(_featureFrames, [1, _featureFrames]);
        _pitchBins.Span.Fill(1L);

        _nsff0 = new PinnedTensorBuffer<float>(_featureFrames, [1, _featureFrames]);

        _speakerId = new PinnedTensorBuffer<long>(1, [1]);
        _speakerId.Span[0] = 0L;

        _rndNoise = new PinnedTensorBuffer<float>(192 * _featureFrames, [1, 192, _featureFrames]);

        // Legacy wrappers for backward compatibility
        _encoderFeaturesLegacy = new PinnedTensorBuffer(_featureFrames * featureDim, [1, _featureFrames, featureDim]);
        _f0PitchLegacy = new PinnedTensorBuffer(_featureFrames, [1, _featureFrames]);
    }

    /// <summary>
    /// Creates a configured <see cref="OrtIoBinding"/> for the specified session with pre-bound tensors.
    /// </summary>
    public static OrtIoBinding CreateBinding(
        InferenceSession session,
        string inputTensorName,
        PinnedTensorBuffer inputBuffer,
        string outputTensorName,
        PinnedTensorBuffer outputBuffer)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(inputBuffer);
        ArgumentNullException.ThrowIfNull(outputBuffer);

        var binding = session.CreateIoBinding();
        binding.BindInput(inputTensorName, inputBuffer.Value);
        binding.BindOutput(outputTensorName, outputBuffer.Value);
        return binding;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _inputAudio.Dispose();
        _outputAudio.Dispose();
        _hubertSourceAudio.Dispose();
        _hubertPaddingMask.Dispose();
        _phoneFeatures.Dispose();
        _phoneLengths.Dispose();
        _pitchBins.Dispose();
        _nsff0.Dispose();
        _speakerId.Dispose();
        _rndNoise.Dispose();
        _encoderFeaturesLegacy.Dispose();
        _f0PitchLegacy.Dispose();
    }
}
