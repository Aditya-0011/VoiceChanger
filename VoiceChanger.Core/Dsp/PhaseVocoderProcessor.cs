using System.Runtime.CompilerServices;

namespace VoiceChanger.Core.Dsp;

/// <summary>
/// Real-time phase vocoder audio processor with independent pitch shift and tempo preservation.
/// Strictly zero-allocation in the hot <see cref="Process"/> audio path (Invariant #1).
/// Pure managed C# with zero external dependencies (Invariant #3).
/// </summary>
public sealed class PhaseVocoderProcessor : IAudioProcessor
{
    private readonly int _frameSize;
    private readonly int _analysisHop;
    private readonly Fft _fft;
    private readonly float[] _window;
    private readonly float _colaNormalization;
    private readonly Resampler _resampler;

    // Pre-allocated DSP buffers
    private float[] _inputBuffer = [];
    private int _inputWritePos;
    private float[] _real = [];
    private float[] _imag = [];
    private float[] _prevPhase = [];
    private float[] _synthPhase = [];
    private float[] _overlapBuffer = [];
    private int _overlapMask;
    private long _overlapReadPos;
    private long _overlapWritePos;
    private float[] _intermediateChunk = [];

    private volatile float _pitchSemitones;
    private double _stretchRatio = 1.0;
    private int _sampleRate = 48000;
    private int _maxBlockSize = 256;
    private bool _isPrepared;

    /// <summary>
    /// Gets or sets the pitch shift in semitones (typically -12.0 to +12.0).
    /// Can be updated dynamically on any thread without stopping or allocating.
    /// </summary>
    public float PitchSemitones
    {
        get => _pitchSemitones;
        set
        {
            _pitchSemitones = value;
            _stretchRatio = Math.Pow(2.0, value / 12.0);
        }
    }

    /// <summary>
    /// Algorithmic latency in frames (equals the STFT frame size).
    /// </summary>
    public int LatencyFrames => _frameSize;

    /// <summary>
    /// Creates a new instance of <see cref="PhaseVocoderProcessor"/>.
    /// </summary>
    /// <param name="frameSize">FFT frame size in points (must be a power of two, default 1024 = 21.3 ms @ 48 kHz).</param>
    /// <param name="analysisHop">Analysis hop size in samples (default 256 = 4x overlap).</param>
    /// <param name="initialSemitones">Initial pitch shift in semitones (default 0.0 = passthrough).</param>
    public PhaseVocoderProcessor(int frameSize = 1024, int analysisHop = 256, float initialSemitones = 0.0f)
    {
        if (frameSize <= 0 || (frameSize & (frameSize - 1)) != 0)
        {
            throw new ArgumentException("Frame size must be a power of two.", nameof(frameSize));
        }
        if (analysisHop <= 0 || analysisHop > frameSize)
        {
            throw new ArgumentException("Analysis hop must be positive and <= frame size.", nameof(analysisHop));
        }

        _frameSize = frameSize;
        _analysisHop = analysisHop;
        _fft = new Fft(frameSize);
        _window = Window.CreateHann(frameSize, periodic: true);
        _resampler = new Resampler(capacity: 32768);

        // Compute COLA normalization factor for analysis + synthesis Hann windows
        float colaSum = Window.CalculateColaSquaredSum(_window, analysisHop);
        _colaNormalization = colaSum > 0f ? 1.0f / colaSum : 1.0f;

        PitchSemitones = initialSemitones;
    }

    /// <inheritdoc/>
    public void Prepare(int sampleRate, int maxBlockSize)
    {
        _sampleRate = sampleRate;
        _maxBlockSize = maxBlockSize;

        _inputBuffer = new float[_frameSize * 2];
        _inputWritePos = 0;

        _real = new float[_frameSize];
        _imag = new float[_frameSize];
        _prevPhase = new float[_frameSize / 2 + 1];
        _synthPhase = new float[_frameSize / 2 + 1];

        int overlapCap = 16384;
        _overlapBuffer = new float[overlapCap];
        _overlapMask = overlapCap - 1;
        _overlapReadPos = 0;
        _overlapWritePos = 0;

        _intermediateChunk = new float[_frameSize * 2];

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

        int framesToProcess = Math.Min(input.Length, output.Length);
        double ratio = _stretchRatio;

        // Fast-path passthrough when pitch shift is negligible (< 0.01 semitones)
        if (Math.Abs(_pitchSemitones) < 0.01f)
        {
            input[..framesToProcess].CopyTo(output);
            return;
        }

        int inputConsumed = 0;
        int outputProduced = 0;

        while (inputConsumed < framesToProcess)
        {
            int toCopy = Math.Min(framesToProcess - inputConsumed, _frameSize - _inputWritePos);
            input.Slice(inputConsumed, toCopy).CopyTo(_inputBuffer.AsSpan(_inputWritePos, toCopy));
            _inputWritePos += toCopy;
            inputConsumed += toCopy;

            // When a full frame is available, execute STFT -> Phase Vocoder -> ISTFT
            if (_inputWritePos >= _frameSize)
            {
                ProcessStftFrame(ratio);

                // Shift analysis FIFO by 1 hop
                Array.Copy(_inputBuffer, _analysisHop, _inputBuffer, 0, _frameSize - _analysisHop);
                _inputWritePos = _frameSize - _analysisHop;
            }

            // Resample synthesis frames into output destination
            if (outputProduced < framesToProcess)
            {
                int needed = framesToProcess - outputProduced;
                int read = _resampler.Read(output.Slice(outputProduced, needed), ratio);
                outputProduced += read;
            }
        }

        // Pad any remainder with silence if resampler ran short
        if (outputProduced < framesToProcess)
        {
            output.Slice(outputProduced, framesToProcess - outputProduced).Clear();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ProcessStftFrame(double ratio)
    {
        // 1. Window analysis frame into FFT buffers
        for (int n = 0; n < _frameSize; n++)
        {
            _real[n] = _inputBuffer[n] * _window[n];
            _imag[n] = 0f;
        }

        // 2. Forward FFT
        _fft.Forward(_real, _imag);

        // 3. Phase unwrapping, instantaneous frequency analysis, and synthesis phase advance
        int halfSize = _frameSize / 2;
        float synthesisHop = (float)(_analysisHop * ratio);
        double twoPi = 2.0 * Math.PI;
        double binFreqStep = twoPi / _frameSize;

        for (int k = 0; k <= halfSize; k++)
        {
            float r = _real[k];
            float im = _imag[k];
            float magnitude = MathF.Sqrt(r * r + im * im);
            float phase = MathF.Atan2(im, r);

            // Phase deviation from expected bin center frequency
            float expectedPhaseDiff = (float)(k * binFreqStep * _analysisHop);
            float phaseDiff = phase - _prevPhase[k];
            _prevPhase[k] = phase;

            float phaseDeviation = phaseDiff - expectedPhaseDiff;

            // Principle argument wrapping to (-pi, pi]
            float d = (float)(phaseDeviation / twoPi);
            phaseDeviation -= (float)(Math.Round(d) * twoPi);

            // True instantaneous frequency
            float instFreq = (float)(k * binFreqStep) + (phaseDeviation / _analysisHop);

            // Advance synthesis phase by stretched synthesis hop
            float newSynthPhase = _synthPhase[k] + (instFreq * synthesisHop);
            float dSynth = (float)(newSynthPhase / twoPi);
            _synthPhase[k] = newSynthPhase - (float)(Math.Round(dSynth) * twoPi);

            // Synthesize spectral bin
            _real[k] = magnitude * MathF.Cos(_synthPhase[k]);
            _imag[k] = magnitude * MathF.Sin(_synthPhase[k]);

            // Hermitian symmetry for real inverse FFT
            if (k > 0 && k < halfSize)
            {
                _real[_frameSize - k] = _real[k];
                _imag[_frameSize - k] = -_imag[k];
            }
        }

        // Nyquist bin imaginary component must be 0
        _imag[halfSize] = 0f;

        // 4. Inverse FFT
        _fft.Inverse(_real, _imag);

        // 5. Apply synthesis window with COLA normalization and overlap-add
        for (int n = 0; n < _frameSize; n++)
        {
            long writeIdx = (_overlapWritePos + n) & _overlapMask;
            _overlapBuffer[writeIdx] += _real[n] * _window[n] * _colaNormalization;
        }

        // 6. Push synthesis hop of samples to resampler
        int hopSamples = (int)Math.Round(synthesisHop);
        if (hopSamples > 0)
        {
            for (int i = 0; i < hopSamples; i++)
            {
                long readIdx = (_overlapReadPos + i) & _overlapMask;
                _intermediateChunk[i] = _overlapBuffer[readIdx];
                _overlapBuffer[readIdx] = 0f; // Clear for future overlap-adds
            }

            _overlapReadPos += hopSamples;
            _overlapWritePos += hopSamples;

            _resampler.Write(_intermediateChunk.AsSpan(0, hopSamples));
        }
    }

    /// <inheritdoc/>
    public void Reset()
    {
        _inputWritePos = 0;
        _overlapReadPos = 0;
        _overlapWritePos = 0;

        Array.Clear(_inputBuffer);
        Array.Clear(_real);
        Array.Clear(_imag);
        Array.Clear(_prevPhase);
        Array.Clear(_synthPhase);
        Array.Clear(_overlapBuffer);
        _resampler.Reset();

        // Prime the input buffer with frameSize - analysisHop samples so the first frame
        // processes after exactly one analysisHop has arrived
        _inputWritePos = _frameSize - _analysisHop;
    }
}
