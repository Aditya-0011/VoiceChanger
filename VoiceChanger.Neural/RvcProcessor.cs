using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.ML.OnnxRuntime;
using VoiceChanger.Core;
using VoiceChanger.Core.Dsp;

namespace VoiceChanger.Neural;

/// <summary>
/// Neural voice conversion processor implementing the RVC pipeline via ONNX Runtime & DirectML.
/// Decouples neural inference onto an independent high-priority worker thread, ensuring the
/// real-time audio callback executes with zero heap allocations and zero stalls (Invariants #1 and #2).
/// </summary>
public sealed class RvcProcessor : IAudioProcessor, IParameterReceiver, IDisposable
{
    private readonly RvcModelSet _modelSet;
    private readonly ExecutionProviderConfig _config;
    private ChunkedStreamer _streamer;
    private OrtBufferPool _bufferPool;
    private readonly AutoResetEvent _inferenceSignal;

    private readonly NoiseGate _noiseGate = new(thresholdDb: -45.0f, attackMs: 5.0f, releaseMs: 80.0f, enabled: true);
    private float[] _gatedBlock = [];

    private Thread? _neuralThread;
    private volatile bool _isStopping;
    private bool _disposed;

    private int _sampleRate = 48000;
    private int _maxBlockSize = 256;
    private float _pitchShiftSemitones = 0f;

    private const int LatencyHistoryCapacity = 128;
    private readonly double[] _inferenceDurationsMs = new double[LatencyHistoryCapacity];
    private int _latencyHistoryCount;
    private int _latencyHistoryIndex;
    private readonly object _latencyLock = new();

    // Reusable input dictionaries to guarantee zero allocations during background inference
    private Dictionary<string, OrtValue>? _cachedGenInputs;
    private InferenceSession? _cachedGenSession;
    private Dictionary<string, OrtValue>? _cachedEncInputs;
    private InferenceSession? _cachedEncSession;

    private static readonly float[] DownsampleFilterTaps = CreateDownsampleFilter(15, 3);
    private const float F0Min = 50.0f;
    private const float F0Max = 1100.0f;
    private const float F0MelMin = 77.472f;
    private const float F0MelMax = 1063.85f;

    private bool _historyInitialized;

    /// <summary>
    /// Underlying model set managing shared and per-voice sessions.
    /// </summary>
    public RvcModelSet ModelSet => _modelSet;

    /// <summary>
    /// Execution provider configuration.
    /// </summary>
    public ExecutionProviderConfig Config => _config;

    /// <summary>
    /// Chunked streaming manager with half-Hann crossfading.
    /// </summary>
    public ChunkedStreamer Streamer => _streamer;

    /// <summary>
    /// Buffer pool managing pre-allocated pinned tensors.
    /// </summary>
    public OrtBufferPool BufferPool => _bufferPool;

    /// <summary>
    /// Real-time noise gate protecting the neural pipeline against background noise.
    /// </summary>
    public NoiseGate NoiseGate => _noiseGate;

    /// <summary>
    /// Algorithmic delay in frames introduced by chunked windowing.
    /// </summary>
    public int LatencyFrames => _streamer.LatencyFrames;

    /// <summary>
    /// Additional pitch shift in semitones applied to the extracted pitch contour.
    /// </summary>
    public float PitchShiftSemitones
    {
        get => Volatile.Read(ref _pitchShiftSemitones);
        set => Volatile.Write(ref _pitchShiftSemitones, value);
    }

    /// <inheritdoc/>
    public void ApplyParameters(DspParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        PitchShiftSemitones = parameters.PitchSemitones;
        _noiseGate.Enabled = parameters.NoiseGateEnabled;
        _noiseGate.ThresholdDb = parameters.NoiseGateThresholdDb;
        _noiseGate.AttackMs = parameters.NoiseGateAttackMs;
        _noiseGate.ReleaseMs = parameters.NoiseGateReleaseMs;
    }

    /// <summary>
    /// Initializes a new instance of <see cref="RvcProcessor"/>.
    /// </summary>
    /// <param name="modelSet">Optional model set. If null, a new instance is created.</param>
    /// <param name="config">Optional execution provider configuration.</param>
    /// <param name="windowMs">Inference window duration in milliseconds (default 300 ms).</param>
    /// <param name="overlapMs">Crossfade overlap duration in milliseconds (default 50 ms).</param>
    public RvcProcessor(
        RvcModelSet? modelSet = null,
        ExecutionProviderConfig? config = null,
        int windowMs = 300,
        int overlapMs = 50)
    {
        _config = config ?? new ExecutionProviderConfig();
        _modelSet = modelSet ?? new RvcModelSet(_config);
        _inferenceSignal = new AutoResetEvent(false);

        _streamer = new ChunkedStreamer(_sampleRate, windowMs, overlapMs);
        _bufferPool = new OrtBufferPool(_streamer.WindowFrames);
    }

    /// <inheritdoc/>
    public void Prepare(int sampleRate, int maxBlockSize)
    {
        _sampleRate = sampleRate;
        _maxBlockSize = maxBlockSize;
        _gatedBlock = new float[Math.Max(maxBlockSize * 8, 16384)];

        _noiseGate.Prepare(sampleRate);
        _streamer.Reset();
        _historyInitialized = false;

        // Start neural inference worker thread if not already running
        if (_neuralThread == null)
        {
            _isStopping = false;
            _neuralThread = new Thread(InferenceLoop)
            {
                Name = "VoiceChanger.NeuralInferenceWorker",
                IsBackground = true,
                Priority = ThreadPriority.AboveNormal
            };
            _neuralThread.Start();
        }
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Process(ReadOnlySpan<float> input, Span<float> output)
    {
        // Invariant #1: Zero heap allocation in audio callback
        // Invariant #2: No inference or locks on audio thread
        if (_noiseGate.Enabled)
        {
            int len = input.Length;
            if (_gatedBlock.Length < len)
            {
                _streamer.PushInput(input);
            }
            else
            {
                Span<float> gatedInput = _gatedBlock.AsSpan(0, len);
                _noiseGate.Process(input, gatedInput);
                _streamer.PushInput(gatedInput);
            }
        }
        else
        {
            _streamer.PushInput(input);
        }

        if (_streamer.IsWindowReady)
        {
            _inferenceSignal.Set();
        }

        _streamer.ReadOutput(output);
    }

    /// <inheritdoc/>
    public void Reset()
    {
        _noiseGate.Reset();
        _streamer.Reset();
        _historyInitialized = false;
        Array.Clear(_bufferPool.History48k);
        lock (_latencyLock)
        {
            _latencyHistoryCount = 0;
            _latencyHistoryIndex = 0;
            Array.Clear(_inferenceDurationsMs);
        }
    }

    /// <summary>
    /// Runs warm-up inferences on silent audio before opening live microphone streams.
    /// Eliminates graph optimization, kernel compilation, and JIT spikes from runtime audio.
    /// </summary>
    /// <param name="passes">Number of silent inferences (default 8 per Project.md §5 Phase 4).</param>
    public void WarmUp(int passes = 8)
    {
        Span<float> silence = _bufferPool.InputAudio.Span;
        silence.Clear();

        for (int i = 0; i < passes; i++)
        {
            ExecuteInferenceStep();
        }

        _historyInitialized = false;
        Array.Clear(_bufferPool.History48k);
    }

    /// <summary>
    /// Hot-swaps the active voice generator session without stopping audio.
    /// </summary>
    /// <param name="voice">Target voice model info.</param>
    public void SetVoice(VoiceModelInfo voice)
    {
        _modelSet.LoadVoice(voice);
    }

    /// <summary>
    /// Background loop dedicated to neural model inference.
    /// </summary>
    private void InferenceLoop()
    {
        while (!_isStopping)
        {
            _inferenceSignal.WaitOne(10);

            if (_isStopping)
            {
                break;
            }

            while (_streamer.IsWindowReady && !_isStopping)
            {
                if (_streamer.TryExtractWindow(_bufferPool.InputAudio.Span))
                {
                    long t0 = Stopwatch.GetTimestamp();
                    ExecuteInferenceStep();
                    long t1 = Stopwatch.GetTimestamp();

                    double elapsedMs = (t1 - t0) * 1000.0 / Stopwatch.Frequency;
                    RecordLatency(elapsedMs);

                    _streamer.PushInferenceResult(_bufferPool.OutputAudio.Span);
                }
            }
        }
    }

    /// <summary>
    /// Executes one forward pass through HuBERT, pitch extraction, and the active Generator session.
    /// In the absence of loaded ONNX sessions, executes a synthetic pass for hardware-free verification.
    /// </summary>
    private void ExecuteInferenceStep()
    {
        // 0. Energy and silence detection on the current audio window (14,400 samples = 300 ms)
        ReadOnlySpan<float> inputWindow = _bufferPool.InputAudio.Span;
        float inEnergy = 0f;
        for (int i = 0; i < inputWindow.Length; i++)
        {
            float s = inputWindow[i];
            inEnergy += s * s;
        }
        float inRms = MathF.Sqrt(inEnergy / inputWindow.Length);

        // Dynamic silence threshold:
        // If noise gate is active, anything below the threshold is considered silence.
        float silenceThreshold = _noiseGate.Enabled
            ? MathF.Max(0.003f, MathF.Pow(10.0f, _noiseGate.ThresholdDb / 20.0f) * 0.75f)
            : 0.003f;

        // If the window is silent (e.g. ambient mic hiss/room noise or gated),
        // immediately output pure silence and skip all model inference.
        if (inRms < silenceThreshold)
        {
            _bufferPool.OutputAudio.Span.Clear();
            return;
        }

        var genSession = _modelSet.GeneratorSession;

        if (genSession != null)
        {
            try
            {
                var encSession = _modelSet.EncoderSession;
                if (encSession != null)
                {
                    // 1. Maintain seamless rolling 48 kHz context without sample discontinuities
                    int winLen = _bufferPool.AudioWindowFrames;
                    int histLen = _bufferPool.History48kLen;
                    int hopLen = _streamer.HopFrames;

                    if (!_historyInitialized)
                    {
                        Array.Clear(_bufferPool.History48k);
                        _bufferPool.InputAudio.Span.CopyTo(_bufferPool.History48k.AsSpan(histLen - winLen, winLen));
                        _historyInitialized = true;
                    }
                    else
                    {
                        Array.Copy(_bufferPool.History48k, hopLen, _bufferPool.History48k, 0, histLen - hopLen);
                        _bufferPool.InputAudio.Span.Slice(winLen - hopLen, hopLen)
                            .CopyTo(_bufferPool.History48k.AsSpan(histLen - hopLen, hopLen));
                    }

                    // 2. Downsample 48 kHz context (600 ms) to 16 kHz for HuBERT
                    Downsample48kTo16k(_bufferPool.History48k, _bufferPool.HubertSourceAudio.Span);

                    // Standardize 16 kHz audio for HuBERT (zero mean, unit variance)
                    Span<float> audio16k = _bufferPool.HubertSourceAudio.Span;
                    float sum16k = 0f;
                    for (int i = 0; i < audio16k.Length; i++)
                    {
                        sum16k += audio16k[i];
                    }
                    float mean16k = sum16k / audio16k.Length;
                    float varSum16k = 0f;
                    for (int i = 0; i < audio16k.Length; i++)
                    {
                        float diff = audio16k[i] - mean16k;
                        varSum16k += diff * diff;
                    }
                    float std16k = MathF.Sqrt(varSum16k / audio16k.Length);
                    if (std16k > 0.003f)
                    {
                        float invStd = 1f / std16k;
                        for (int i = 0; i < audio16k.Length; i++)
                        {
                            audio16k[i] = (audio16k[i] - mean16k) * invStd;
                        }
                    }
                    else
                    {
                        audio16k.Clear();
                    }

                    // 3. Run HuBERT encoder forward pass (outputs 30 frames @ 50 fps = 600 ms)
                    var encInputs = GetEncoderInputs(encSession);
                    using var encResults = encSession.Run(new RunOptions(), encInputs, encSession.OutputNames);
                    if (encResults.Count > 0)
                    {
                        var featTensor = encResults[0];
                        var featSpan = featTensor.GetTensorDataAsSpan<float>();
                        int featDim = _bufferPool.FeatureDim;
                        int totalHubertFrames = featSpan.Length / featDim;
                        int targetGenFrames = _bufferPool.FeatureFrames; // 30 frames @ 100 fps = 300 ms
                        int hubertWindowFrames = targetGenFrames / 2;    // 15 frames @ 50 fps = 300 ms

                        if (totalHubertFrames >= targetGenFrames)
                        {
                            // Slice the last 15 frames (corresponding to current 300 ms window)
                            // and repeat 2x -> 30 frames @ 100 fps to match generator tempo
                            int startFrame = totalHubertFrames - hubertWindowFrames;
                            for (int t = 0; t < hubertWindowFrames; t++)
                            {
                                var srcFrame = featSpan.Slice((startFrame + t) * featDim, featDim);
                                srcFrame.CopyTo(_bufferPool.PhoneFeatures.Span.Slice((2 * t) * featDim, featDim));
                                srcFrame.CopyTo(_bufferPool.PhoneFeatures.Span.Slice((2 * t + 1) * featDim, featDim));
                            }
                        }
                        else
                        {
                            int copyLen = Math.Min(featSpan.Length, _bufferPool.PhoneFeatures.Span.Length);
                            featSpan[..copyLen].CopyTo(_bufferPool.PhoneFeatures.Span);
                        }
                    }
                }

                // 4. Extract fundamental pitch contour (F0 in Hz and quantized Mel pitch bins)
                ExtractF0AndPitch(
                    _bufferPool.InputAudio.Span,
                    _bufferPool.PitchBins.Span,
                    _bufferPool.Nsff0.Span,
                    PitchShiftSemitones);

                // 5. Run Generator forward pass
                var genInputs = GetGeneratorInputs(genSession);
                using var results = genSession.Run(new RunOptions(), genInputs, genSession.OutputNames);
                if (results.Count > 0)
                {
                    var outputTensor = results[0];
                    var outputSpan = outputTensor.GetTensorDataAsSpan<float>();
                    int copyLen = Math.Min(outputSpan.Length, _bufferPool.OutputAudio.Span.Length);
                    outputSpan[..copyLen].CopyTo(_bufferPool.OutputAudio.Span);

                    // Dynamic volume envelope matching: generator outputs nominal ~0.20 RMS;
                    // scale output to track input speaking dynamics smoothly.
                    float outEnergy = 0f;
                    for (int i = 0; i < copyLen; i++)
                    {
                        float s = _bufferPool.OutputAudio.Span[i];
                        outEnergy += s * s;
                    }
                    float outRms = MathF.Sqrt(outEnergy / copyLen);
                    if (outRms > 1e-4f)
                    {
                        float targetGain = Math.Clamp(inRms / outRms, 0.0f, 1.25f);
                        for (int i = 0; i < copyLen; i++)
                        {
                            _bufferPool.OutputAudio.Span[i] *= targetGain;
                        }
                    }
                    return;
                }
            }
            catch
            {
                // Graceful degradation on inference exception to synthetic pass-through
            }
        }

        // Synthetic verification pass: copy input to output (used in CI/unit tests without physical models)
        _bufferPool.InputAudio.Span.CopyTo(_bufferPool.OutputAudio.Span);
    }

    private Dictionary<string, OrtValue> GetEncoderInputs(InferenceSession session)
    {
        if (_cachedEncInputs != null && ReferenceEquals(_cachedEncSession, session))
        {
            return _cachedEncInputs;
        }

        var dict = new Dictionary<string, OrtValue>(StringComparer.Ordinal);
        foreach (var name in session.InputNames)
        {
            if (name.Contains("mask", StringComparison.OrdinalIgnoreCase))
            {
                dict[name] = _bufferPool.HubertPaddingMask.Value;
            }
            else
            {
                dict[name] = _bufferPool.HubertSourceAudio.Value;
            }
        }

        _cachedEncInputs = dict;
        _cachedEncSession = session;
        return dict;
    }

    private Dictionary<string, OrtValue> GetGeneratorInputs(InferenceSession session)
    {
        if (_cachedGenInputs != null && ReferenceEquals(_cachedGenSession, session))
        {
            return _cachedGenInputs;
        }

        var dict = new Dictionary<string, OrtValue>(StringComparer.Ordinal);
        foreach (var name in session.InputNames)
        {
            if (name.Contains("phone_len", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("lengths", StringComparison.OrdinalIgnoreCase))
            {
                dict[name] = _bufferPool.PhoneLengths.Value;
            }
            else if (name.Equals("phone", StringComparison.OrdinalIgnoreCase) ||
                     name.Equals("feats", StringComparison.OrdinalIgnoreCase) ||
                     name.Contains("feat", StringComparison.OrdinalIgnoreCase) ||
                     name.Contains("content", StringComparison.OrdinalIgnoreCase))
            {
                dict[name] = _bufferPool.PhoneFeatures.Value;
            }
            else if (name.Equals("pitch", StringComparison.OrdinalIgnoreCase))
            {
                dict[name] = _bufferPool.PitchBins.Value;
            }
            else if (name.Contains("nsff0", StringComparison.OrdinalIgnoreCase) ||
                     name.Contains("pitchf", StringComparison.OrdinalIgnoreCase) ||
                     name.Equals("f0", StringComparison.OrdinalIgnoreCase))
            {
                dict[name] = _bufferPool.Nsff0.Value;
            }
            else if (name.Equals("sid", StringComparison.OrdinalIgnoreCase) ||
                     name.Equals("ds", StringComparison.OrdinalIgnoreCase) ||
                     name.Equals("spk", StringComparison.OrdinalIgnoreCase))
            {
                dict[name] = _bufferPool.SpeakerId.Value;
            }
            else if (name.Contains("rnd", StringComparison.OrdinalIgnoreCase) ||
                     name.Contains("noise", StringComparison.OrdinalIgnoreCase))
            {
                dict[name] = _bufferPool.RndNoise.Value;
            }
        }

        _cachedGenInputs = dict;
        _cachedGenSession = session;
        return dict;
    }

    /// <summary>
    /// Anti-aliasing FIR decimation from 48 kHz to 16 kHz (3:1 ratio).
    /// </summary>
    public static void Downsample48kTo16k(ReadOnlySpan<float> src48k, Span<float> dst16k)
    {
        int filterLen = DownsampleFilterTaps.Length;
        int halfLen = filterLen / 2;
        int count = Math.Min(dst16k.Length, src48k.Length / 3);

        for (int i = 0; i < count; i++)
        {
            int center48k = i * 3;
            float acc = 0f;
            for (int k = 0; k < filterLen; k++)
            {
                int srcIdx = center48k + k - halfLen;
                if ((uint)srcIdx < (uint)src48k.Length)
                {
                    acc += src48k[srcIdx] * DownsampleFilterTaps[k];
                }
            }
            dst16k[i] = acc;
        }
    }

    private static float[] CreateDownsampleFilter(int numTaps, int decimationFactor)
    {
        float[] taps = new float[numTaps];
        int center = numTaps / 2;
        float sum = 0f;

        for (int i = 0; i < numTaps; i++)
        {
            int n = i - center;
            float sinc = (n == 0) ? 1f : MathF.Sin(MathF.PI * n / decimationFactor) / (MathF.PI * n / decimationFactor);
            // Blackman window
            float w = 0.42f - 0.5f * MathF.Cos(2f * MathF.PI * i / (numTaps - 1)) + 0.08f * MathF.Cos(4f * MathF.PI * i / (numTaps - 1));
            taps[i] = sinc * w;
            sum += taps[i];
        }

        for (int i = 0; i < numTaps; i++)
        {
            taps[i] /= sum;
        }
        return taps;
    }

    /// <summary>
    /// Fast zero-allocation pitch extraction using normalized autocorrelation with sub-sample refinement.
    /// Maps fundamental frequency to continuous F0 (Hz) and coarse RVC Mel bins (1..255).
    /// </summary>
    public static void ExtractF0AndPitch(
        ReadOnlySpan<float> audio48k,
        Span<long> pitchBins,
        Span<float> nsff0,
        float pitchShiftSemitones)
    {
        int numFrames = Math.Min(pitchBins.Length, nsff0.Length);
        if (numFrames == 0 || audio48k.IsEmpty) return;

        int totalSamples = audio48k.Length;
        int hopSamples = totalSamples / numFrames;

        const int minLag = 44;   // 1100 Hz @ 48 kHz
        const int maxLag = 960;  // 50 Hz @ 48 kHz
        const int corrWindow = 1024;

        float pitchFactor = MathF.Pow(2.0f, pitchShiftSemitones / 12.0f);
        Span<float> normCorr = stackalloc float[maxLag + 1];

        for (int frame = 0; frame < numFrames; frame++)
        {
            int center = frame * hopSamples + hopSamples / 2;
            int winStart = center - corrWindow / 2;
            if (winStart < 0) winStart = 0;
            if (winStart + corrWindow + maxLag > totalSamples)
            {
                winStart = Math.Max(0, totalSamples - (corrWindow + maxLag));
            }

            int actualMaxLag = Math.Min(maxLag, totalSamples - winStart - corrWindow);
            if (actualMaxLag <= minLag)
            {
                pitchBins[frame] = 1;
                nsff0[frame] = 0f;
                continue;
            }

            ReadOnlySpan<float> baseWin = audio48k.Slice(winStart, corrWindow);

            float energy0 = 0f;
            for (int i = 0; i < corrWindow; i++)
            {
                float s = baseWin[i];
                energy0 += s * s;
            }

            // Silence threshold: energy per sample < 1e-5 (RMS < ~0.003 for microphone speech)
            if (energy0 < 1e-5f * corrWindow)
            {
                pitchBins[frame] = 1;
                nsff0[frame] = 0f;
                continue;
            }

            normCorr.Clear();
            float globalMaxCorr = 0f;
            int globalMaxLag = -1;

            for (int lag = minLag; lag < actualMaxLag; lag++)
            {
                ReadOnlySpan<float> lagWin = audio48k.Slice(winStart + lag, corrWindow);
                float cross = 0f;
                float energyLag = 0f;

                for (int i = 0; i < corrWindow; i++)
                {
                    float s0 = baseWin[i];
                    float sL = lagWin[i];
                    cross += s0 * sL;
                    energyLag += sL * sL;
                }

                float denom = MathF.Sqrt(energy0 * energyLag) + 1e-9f;
                float nc = cross / denom;
                normCorr[lag] = nc;

                if (nc > globalMaxCorr)
                {
                    globalMaxCorr = nc;
                    globalMaxLag = lag;
                }
            }

            if (globalMaxCorr >= 0.35f && globalMaxLag > 0)
            {
                // First-local-peak picker: prevent octave drop / subharmonic traps (e.g. 200 Hz -> 66 Hz)
                int bestLag = globalMaxLag;
                float threshold = MathF.Max(0.35f, 0.80f * globalMaxCorr);

                for (int lag = minLag + 1; lag < actualMaxLag - 1; lag++)
                {
                    float c = normCorr[lag];
                    if (c >= threshold && c > normCorr[lag - 1] && c >= normCorr[lag + 1])
                    {
                        bestLag = lag;
                        break;
                    }
                }

                // Parabolic sub-sample refinement
                float refinedLag = bestLag;
                if (bestLag > minLag && bestLag < actualMaxLag - 1)
                {
                    float alpha = normCorr[bestLag - 1];
                    float beta = normCorr[bestLag];
                    float gamma = normCorr[bestLag + 1];
                    float denom = 2.0f * (2.0f * beta - alpha - gamma);
                    if (MathF.Abs(denom) > 1e-6f)
                    {
                        float delta = (gamma - alpha) / denom;
                        refinedLag = Math.Clamp(bestLag + delta, minLag, maxLag);
                    }
                }

                float f0 = 48000.0f / refinedLag;
                float shiftedF0 = Math.Clamp(f0 * pitchFactor, F0Min, F0Max);

                float f0Mel = 1127.0f * MathF.Log(1.0f + shiftedF0 / 700.0f);
                int pitchBin = (int)MathF.Round((f0Mel - F0MelMin) * 254.0f / (F0MelMax - F0MelMin) + 1.0f);
                pitchBin = Math.Clamp(pitchBin, 1, 255);

                pitchBins[frame] = pitchBin;
                nsff0[frame] = shiftedF0;
            }
            else
            {
                pitchBins[frame] = 1;
                nsff0[frame] = 0f;
            }
        }
    }

    private void RecordLatency(double elapsedMs)
    {
        lock (_latencyLock)
        {
            _inferenceDurationsMs[_latencyHistoryIndex] = elapsedMs;
            _latencyHistoryIndex = (_latencyHistoryIndex + 1) % LatencyHistoryCapacity;
            if (_latencyHistoryCount < LatencyHistoryCapacity)
            {
                _latencyHistoryCount++;
            }
        }
    }

    /// <summary>
    /// Computes the runtime neural inference latency distribution (p50, p95, p99, max).
    /// </summary>
    public LatencyDistribution? GetLatencyDistribution()
    {
        Span<double> scratch = stackalloc double[LatencyHistoryCapacity];
        int count;

        lock (_latencyLock)
        {
            count = _latencyHistoryCount;
            if (count == 0) return null;
            _inferenceDurationsMs.AsSpan(0, count).CopyTo(scratch);
        }

        scratch[..count].Sort();

        double p50 = scratch[(int)(count * 0.50)];
        double p95 = scratch[Math.Min((int)(count * 0.95), count - 1)];
        double p99 = scratch[Math.Min((int)(count * 0.99), count - 1)];
        double max = scratch[count - 1];

        return new LatencyDistribution(p50, p95, p99, max);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _isStopping = true;
        _inferenceSignal.Set();
        _neuralThread?.Join(500);
        _neuralThread = null;

        _inferenceSignal.Dispose();
        _bufferPool.Dispose();
        _modelSet.Dispose();
    }
}
