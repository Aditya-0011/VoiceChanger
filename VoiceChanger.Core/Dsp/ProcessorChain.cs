using System.Runtime.CompilerServices;

namespace VoiceChanger.Core.Dsp;

/// <summary>
/// Composite audio processor chaining:
/// Input -> Noise Gate -> Phase Vocoder (Pitch &amp; Formants) -> Delay-Compensated Dry/Wet Mixer -> Output.
/// Driven by atomic <see cref="DspParameters"/> snapshots (P3-0).
/// Strictly zero heap allocations in the audio callback path (Invariant #1).
/// Pure managed C# with zero external dependencies (Invariant #3).
/// </summary>
public sealed class ProcessorChain : IAudioProcessor, IParameterReceiver
{
    private readonly NoiseGate _noiseGate;
    private readonly PhaseVocoderProcessor _vocoder;
    private readonly DryWetMixer _mixer;

    private float[] _gatedBuffer = [];
    private float[] _vocodedBuffer = [];
    private int _sampleRate = 48000;
    private int _maxBlockSize = 256;
    private bool _isPrepared;

    private DspParameters _parameters;

    /// <summary>
    /// Gets or sets the active DSP parameters snapshot.
    /// Thread-safe and atomic across UI and audio worker threads.
    /// </summary>
    public DspParameters Parameters
    {
        get => Volatile.Read(ref _parameters);
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            Volatile.Write(ref _parameters, value);
        }
    }

    /// <inheritdoc/>
    public void ApplyParameters(DspParameters parameters)
    {
        Parameters = parameters;
    }

    /// <summary>
    /// Algorithmic delay in frames introduced by this processor chain (equals vocoder latency).
    /// </summary>
    public int LatencyFrames => _vocoder.LatencyFrames;

    /// <summary>
    /// Underlying noise gate instance.
    /// </summary>
    public NoiseGate NoiseGate => _noiseGate;

    /// <summary>
    /// Underlying phase vocoder instance.
    /// </summary>
    public PhaseVocoderProcessor Vocoder => _vocoder;

    /// <summary>
    /// Underlying dry/wet mixer instance.
    /// </summary>
    public DryWetMixer Mixer => _mixer;

    /// <summary>
    /// Creates a new instance of <see cref="ProcessorChain"/>.
    /// </summary>
    /// <param name="initialParameters">Initial parameter snapshot (defaults to standard natural configuration).</param>
    public ProcessorChain(DspParameters? initialParameters = null)
    {
        _parameters = initialParameters ?? new DspParameters();
        _noiseGate = new NoiseGate(
            thresholdDb: _parameters.NoiseGateThresholdDb,
            attackMs: _parameters.NoiseGateAttackMs,
            releaseMs: _parameters.NoiseGateReleaseMs,
            enabled: _parameters.NoiseGateEnabled);

        _vocoder = new PhaseVocoderProcessor(frameSize: 1024, analysisHop: 256, initialSemitones: _parameters.PitchSemitones);
        _vocoder.FormantSemitones = _parameters.FormantSemitones;

        _mixer = new DryWetMixer(_parameters.DryWetMix);
    }

    /// <inheritdoc/>
    public void Prepare(int sampleRate, int maxBlockSize)
    {
        _sampleRate = sampleRate;
        _maxBlockSize = maxBlockSize;

        _gatedBuffer = new float[maxBlockSize * 2];
        _vocodedBuffer = new float[maxBlockSize * 2];

        _noiseGate.Prepare(sampleRate);
        _vocoder.Prepare(sampleRate, maxBlockSize);
        _mixer.Prepare(_vocoder.LatencyFrames, maxBlockSize);

        Reset();
        _isPrepared = true;
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Process(ReadOnlySpan<float> input, Span<float> output)
    {
        if (!_isPrepared || input.IsEmpty)
        {
            output.Clear();
            return;
        }

        int length = Math.Min(input.Length, output.Length);
        DspParameters p = Volatile.Read(ref _parameters);

        // 1. Sync parameter snapshot into components (fast scalar assignments, 0 allocs)
        _noiseGate.Enabled = p.NoiseGateEnabled;
        _noiseGate.ThresholdDb = p.NoiseGateThresholdDb;
        _noiseGate.AttackMs = p.NoiseGateAttackMs;
        _noiseGate.ReleaseMs = p.NoiseGateReleaseMs;

        _vocoder.PitchSemitones = p.PitchSemitones;
        _vocoder.FormantSemitones = p.FormantSemitones;
        _mixer.Mix = p.DryWetMix;

        // 2. Stage 1: Noise Gate ahead of vocoder
        Span<float> gatedSpan = _gatedBuffer.AsSpan(0, length);
        _noiseGate.Process(input.Slice(0, length), gatedSpan);

        // 3. Stage 2: Phase Vocoder & Dry/Wet Mixer
        if (p.VocoderEnabled)
        {
            Span<float> vocodedSpan = _vocodedBuffer.AsSpan(0, length);
            _vocoder.Process(gatedSpan, vocodedSpan);

            // Mix gated dry signal with vocoded wet signal using latency-compensated delay line
            _mixer.Process(gatedSpan, vocodedSpan, output.Slice(0, length));
        }
        else
        {
            // Vocoder bypassed: pass gated audio directly
            gatedSpan.CopyTo(output.Slice(0, length));
        }
    }

    /// <inheritdoc/>
    public void Reset()
    {
        _noiseGate.Reset();
        _vocoder.Reset();
        _mixer.Reset();
        if (_gatedBuffer.Length > 0)
        {
            Array.Clear(_gatedBuffer);
            Array.Clear(_vocodedBuffer);
        }
    }
}
