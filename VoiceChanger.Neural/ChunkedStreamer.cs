using System.Runtime.CompilerServices;
using VoiceChanger.Core.Buffers;

namespace VoiceChanger.Neural;

/// <summary>
/// Manages chunked streaming for neural voice conversion with overlapping windows and
/// half-Hann raised-cosine crossfading to eliminate boundary clicks.
/// </summary>
public sealed class ChunkedStreamer
{
    private readonly int _windowFrames;
    private readonly int _overlapFrames;
    private readonly int _hopFrames;
    private readonly float[] _crossfadeWeights;
    private readonly float[] _previousTail;
    private readonly float[] _hopOutput;
    private readonly float[] _inputBuffer;
    private int _inputCount;
    private bool _hasPreviousTail;

    private readonly SpscRingBuffer _outputRing;
    private readonly object _inputLock = new();

    /// <summary>
    /// Total inference window size in frames (default 14,400 frames = 300 ms @ 48 kHz).
    /// </summary>
    public int WindowFrames => _windowFrames;

    /// <summary>
    /// Overlap region size in frames (default 2,400 frames = 50 ms @ 48 kHz).
    /// </summary>
    public int OverlapFrames => _overlapFrames;

    /// <summary>
    /// Hop size in frames between consecutive inference windows (WindowFrames - OverlapFrames).
    /// </summary>
    public int HopFrames => _hopFrames;

    /// <summary>
    /// Algorithmic latency in frames introduced by window accumulation and crossfade.
    /// </summary>
    public int LatencyFrames => _windowFrames;

    /// <summary>
    /// Number of output frames currently ready in the output ring buffer.
    /// </summary>
    public int AvailableOutputFrames => _outputRing.AvailableRead;

    /// <summary>
    /// Initializes a new instance of <see cref="ChunkedStreamer"/>.
    /// </summary>
    /// <param name="sampleRate">Audio sample rate in Hz (e.g. 48000).</param>
    /// <param name="windowMs">Inference window duration in milliseconds (default 300 ms).</param>
    /// <param name="overlapMs">Crossfade overlap duration in milliseconds (default 50 ms).</param>
    /// <param name="ringCapacity">Capacity of the internal output ring buffer (must be power of two, default 65536).</param>
    public ChunkedStreamer(int sampleRate = 48000, int windowMs = 300, int overlapMs = 50, int ringCapacity = 65536)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(sampleRate, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(windowMs, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(overlapMs, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(overlapMs, windowMs);

        _windowFrames = (int)((long)sampleRate * windowMs / 1000);
        _overlapFrames = (int)((long)sampleRate * overlapMs / 1000);
        _hopFrames = _windowFrames - _overlapFrames;

        _crossfadeWeights = new float[_overlapFrames];
        for (int i = 0; i < _overlapFrames; i++)
        {
            // Raised-cosine (half-Hann) ramp from 0.0 to 1.0: w(t) = 0.5 * (1 - cos(pi * t))
            double t = (double)i / _overlapFrames;
            _crossfadeWeights[i] = (float)(0.5 * (1.0 - Math.Cos(Math.PI * t)));
        }

        _previousTail = new float[_overlapFrames];
        _hopOutput = new float[_hopFrames];
        _inputBuffer = new float[_windowFrames * 4]; // Ample space to prevent stalling
        _outputRing = new SpscRingBuffer(ringCapacity);
    }

    /// <summary>
    /// Pushes new input audio frames from the audio callback into the streamer.
    /// Zero heap allocation.
    /// </summary>
    /// <param name="input">Incoming audio samples.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PushInput(ReadOnlySpan<float> input)
    {
        if (input.IsEmpty) return;

        lock (_inputLock)
        {
            int space = _inputBuffer.Length - _inputCount;
            int toCopy = Math.Min(space, input.Length);
            input[..toCopy].CopyTo(_inputBuffer.AsSpan(_inputCount, toCopy));
            _inputCount += toCopy;
        }
    }

    /// <summary>
    /// Checks if a complete window of audio is ready for inference.
    /// </summary>
    public bool IsWindowReady
    {
        get
        {
            lock (_inputLock)
            {
                return _inputCount >= _windowFrames;
            }
        }
    }

    /// <summary>
    /// Extracts the next window of audio frames for inference.
    /// Advances the input stream by <see cref="HopFrames"/>.
    /// </summary>
    /// <param name="destination">Destination span of length <see cref="WindowFrames"/>.</param>
    /// <returns>True if a window was extracted; false if insufficient audio frames.</returns>
    public bool TryExtractWindow(Span<float> destination)
    {
        if (destination.Length < _windowFrames)
        {
            throw new ArgumentException($"Destination must be at least {_windowFrames} frames.", nameof(destination));
        }

        lock (_inputLock)
        {
            if (_inputCount < _windowFrames)
            {
                return false;
            }

            _inputBuffer.AsSpan(0, _windowFrames).CopyTo(destination);

            // Shift input buffer forward by HopFrames
            int remaining = _inputCount - _hopFrames;
            if (remaining > 0)
            {
                Array.Copy(_inputBuffer, _hopFrames, _inputBuffer, 0, remaining);
            }
            _inputCount = remaining;
            return true;
        }
    }

    /// <summary>
    /// Receives a converted inference window, crossfades the overlap region with the previous chunk's tail,
    /// and pushes the resulting hop frames into the output ring buffer.
    /// Zero heap allocation.
    /// </summary>
    /// <param name="inferenceOutput">Converted audio window of length <see cref="WindowFrames"/>.</param>
    public void PushInferenceResult(ReadOnlySpan<float> inferenceOutput)
    {
        if (inferenceOutput.Length < _windowFrames)
        {
            throw new ArgumentException($"Inference output must be at least {_windowFrames} frames.", nameof(inferenceOutput));
        }

        // 1. Crossfade the head with the previous chunk's tail
        if (_hasPreviousTail)
        {
            for (int i = 0; i < _overlapFrames; i++)
            {
                float w = _crossfadeWeights[i];
                _hopOutput[i] = (1f - w) * _previousTail[i] + w * inferenceOutput[i];
            }
        }
        else
        {
            inferenceOutput[.._overlapFrames].CopyTo(_hopOutput.AsSpan(0, _overlapFrames));
            _hasPreviousTail = true;
        }

        // 2. Middle portion of the hop
        int middleCount = _hopFrames - _overlapFrames;
        if (middleCount > 0)
        {
            inferenceOutput.Slice(_overlapFrames, middleCount).CopyTo(_hopOutput.AsSpan(_overlapFrames, middleCount));
        }

        // 3. Save the tail for the next chunk's crossfade
        inferenceOutput.Slice(_hopFrames, _overlapFrames).CopyTo(_previousTail.AsSpan());

        // 4. Push hop to output ring
        _outputRing.Write(_hopOutput.AsSpan(0, _hopFrames));
    }

    /// <summary>
    /// Reads processed audio frames into <paramref name="destination"/> for the audio callback.
    /// If insufficient frames are ready, writes available frames and pads the remainder with silence.
    /// Zero heap allocation.
    /// </summary>
    /// <param name="destination">Span to receive audio samples.</param>
    /// <returns>Number of valid frames transferred from the neural tier.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int ReadOutput(Span<float> destination)
    {
        if (destination.IsEmpty) return 0;

        int read = _outputRing.Read(destination);
        if (read < destination.Length)
        {
            destination[read..].Clear();
        }
        return read;
    }

    /// <summary>
    /// Resets all internal buffers and crossfade state.
    /// </summary>
    public void Reset()
    {
        lock (_inputLock)
        {
            _inputCount = 0;
            Array.Clear(_inputBuffer);
        }
        _hasPreviousTail = false;
        Array.Clear(_previousTail);
        Array.Clear(_hopOutput);
        _outputRing.Reset();
    }
}
