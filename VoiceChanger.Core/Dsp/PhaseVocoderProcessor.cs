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

    private float[] _magnitude = [];
    private float[] _analysisPhase = [];
    private int[] _peakMap = [];
    private bool _isPhaseInitialized;

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

        // Calculate baseline COLA squared sum for verification
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
        int numBins = _frameSize / 2 + 1;
        _prevPhase = new float[numBins];
        _synthPhase = new float[numBins];
        _magnitude = new float[numBins];
        _analysisPhase = new float[numBins];
        _peakMap = new int[numBins];

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
        double pitchRatio = _stretchRatio; // 2^(semitones / 12)

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

            // Stage 1: Phase Vocoder Time-Stretch by factor pitchRatio.
            // Analysis hop (256 samples) synthesizes a stretched hop (256 * pitchRatio samples),
            // expanding duration by factor pitchRatio while maintaining original fundamental frequencies.
            if (_inputWritePos >= _frameSize)
            {
                ProcessStftFrame(pitchRatio);

                // Shift analysis FIFO by 1 hop
                Array.Copy(_inputBuffer, _analysisHop, _inputBuffer, 0, _frameSize - _analysisHop);
                _inputWritePos = _frameSize - _analysisHop;
            }

            // Stage 2: Resampler Decimation / Speedup by factor pitchRatio (duration factor = 1 / pitchRatio).
            // Reads from the time-stretched intermediate buffer with playback speed = pitchRatio.
            // This compresses the duration by 1 / pitchRatio (restoring original speech rate:
            // duration * pitchRatio / pitchRatio = 1.0) and shifts all frequencies up by pitchRatio.
            if (outputProduced < framesToProcess)
            {
                int needed = framesToProcess - outputProduced;
                double playbackSpeed = pitchRatio;
                int read = _resampler.Read(output.Slice(outputProduced, needed), playbackSpeed);
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

        // 3. Extract magnitude and phase
        int halfSize = _frameSize / 2;
        float synthesisHop = (float)(_analysisHop * ratio);
        double twoPi = 2.0 * Math.PI;
        double binFreqStep = twoPi / _frameSize;

        for (int k = 0; k <= halfSize; k++)
        {
            float r = _real[k];
            float im = _imag[k];
            _magnitude[k] = MathF.Sqrt(r * r + im * im);
            _analysisPhase[k] = MathF.Atan2(im, r);
        }

        if (!_isPhaseInitialized)
        {
            for (int k = 0; k <= halfSize; k++)
            {
                _synthPhase[k] = _analysisPhase[k];
                // Seed prevPhase assuming nominal center bin frequency progression
                _prevPhase[k] = _analysisPhase[k] - (float)(k * binFreqStep * _analysisHop);
            }
            _isPhaseInitialized = true;
        }

        // 4. Find spectral peaks (local maxima) and assign region of influence
        int currentPeak = 0;
        for (int k = 0; k <= halfSize; k++)
        {
            bool isPeak;
            if (k == 0)
            {
                isPeak = _magnitude[0] > _magnitude[1];
            }
            else if (k == halfSize)
            {
                isPeak = _magnitude[halfSize] > _magnitude[halfSize - 1];
            }
            else
            {
                isPeak = _magnitude[k] > _magnitude[k - 1] && _magnitude[k] >= _magnitude[k + 1];
            }

            if (isPeak)
            {
                currentPeak = k;
            }
            _peakMap[k] = currentPeak;
        }

        // Backward pass: assign bins to the closer peak in frequency
        int nextPeak = halfSize;
        for (int k = halfSize; k >= 0; k--)
        {
            bool isPeak;
            if (k == 0)
            {
                isPeak = _magnitude[0] > _magnitude[1];
            }
            else if (k == halfSize)
            {
                isPeak = _magnitude[halfSize] > _magnitude[halfSize - 1];
            }
            else
            {
                isPeak = _magnitude[k] > _magnitude[k - 1] && _magnitude[k] >= _magnitude[k + 1];
            }

            if (isPeak)
            {
                nextPeak = k;
            }

            int prevP = _peakMap[k];
            if (Math.Abs(k - nextPeak) < Math.Abs(k - prevP))
            {
                _peakMap[k] = nextPeak;
            }
        }

        // 5. Advance synthesis phase for peak bins
        for (int k = 0; k <= halfSize; k++)
        {
            if (_peakMap[k] == k)
            {
                float expectedPhaseDiff = (float)(k * binFreqStep * _analysisHop);
                float phaseDiff = _analysisPhase[k] - _prevPhase[k];
                float phaseDeviation = phaseDiff - expectedPhaseDiff;

                // Principle argument wrapping to (-pi, pi]
                float d = (float)(phaseDeviation / twoPi);
                phaseDeviation -= (float)(Math.Round(d) * twoPi);

                // True instantaneous frequency
                float instFreq = (float)(k * binFreqStep) + (phaseDeviation / _analysisHop);

                // Advance synthesis phase by stretched synthesis hop
                float newPeakPhase = _synthPhase[k] + (instFreq * synthesisHop);
                float dSynth = (float)(newPeakPhase / twoPi);
                _synthPhase[k] = newPeakPhase - (float)(Math.Round(dSynth) * twoPi);
            }
        }

        // 6. Identity Phase Locking: lock non-peak bins to their assigned peak
        for (int k = 0; k <= halfSize; k++)
        {
            int p = _peakMap[k];
            if (p != k)
            {
                float phaseOffset = _analysisPhase[k] - _analysisPhase[p];
                _synthPhase[k] = _synthPhase[p] + phaseOffset;
            }
            _prevPhase[k] = _analysisPhase[k];
        }

        // 7. Synthesize spectral bins with Hermitian symmetry
        for (int k = 0; k <= halfSize; k++)
        {
            float mag = _magnitude[k];
            _real[k] = mag * MathF.Cos(_synthPhase[k]);
            _imag[k] = mag * MathF.Sin(_synthPhase[k]);

            if (k > 0 && k < halfSize)
            {
                _real[_frameSize - k] = _real[k];
                _imag[_frameSize - k] = -_imag[k];
            }
        }
        _imag[0] = 0f;
        _imag[halfSize] = 0f;

        // 8. Inverse FFT
        _fft.Inverse(_real, _imag);

        // 9. Overlap-add with exact analytical COLA normalization
        // For a periodic Hann window of length N with hop H:
        // sum(w^2) = (3/8) * (N / H) = (0.375 * N) / H.
        // Therefore colaNorm = H / (0.375 * N) = H / 384 for N = 1024.
        int hopSamples = Math.Max(1, (int)Math.Round(synthesisHop));
        float colaNorm = hopSamples / (0.375f * _frameSize);

        for (int n = 0; n < _frameSize; n++)
        {
            long writeIdx = (_overlapWritePos + n) & _overlapMask;
            _overlapBuffer[writeIdx] += _real[n] * _window[n] * colaNorm;
        }

        // 10. Push synthesis hop of samples to resampler
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

    /// <inheritdoc/>
    public void Reset()
    {
        _inputWritePos = 0;
        _overlapReadPos = 0;
        _overlapWritePos = 0;
        _isPhaseInitialized = false;

        Array.Clear(_inputBuffer);
        Array.Clear(_real);
        Array.Clear(_imag);
        Array.Clear(_prevPhase);
        Array.Clear(_synthPhase);
        Array.Clear(_magnitude);
        Array.Clear(_analysisPhase);
        Array.Clear(_peakMap);
        Array.Clear(_overlapBuffer);
        _resampler.Reset();

        // Prime the input buffer with frameSize - analysisHop samples so the first frame
        // processes after exactly one analysisHop has arrived
        _inputWritePos = _frameSize - _analysisHop;
    }
}
