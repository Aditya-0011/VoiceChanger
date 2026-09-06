namespace VoiceChanger.Audio.Streams;

/// <summary>
/// Interim pre-allocated SPSC FIFO buffer connecting the capture callback and render callback.
/// Adheres strictly to Invariant #1 (zero heap allocation in audio path) and Invariant #2 (no locks).
/// </summary>
public sealed class PassthroughBridge
{
    private readonly float[] _buffer;
    private readonly int _mask;
    private long _writePos;
    private long _readPos;
    private long _underrunFrames;
    private long _overrunFrames;

    /// <summary>
    /// Total buffer capacity in samples/frames.
    /// </summary>
    public int Capacity => _buffer.Length;

    /// <summary>
    /// Cumulative underrun frame count.
    /// </summary>
    public long UnderrunFrames => Volatile.Read(ref _underrunFrames);

    /// <summary>
    /// Cumulative overrun frame count.
    /// </summary>
    public long OverrunFrames => Volatile.Read(ref _overrunFrames);

    /// <summary>
    /// Current estimated number of frames buffered.
    /// </summary>
    public int BufferedFrames
    {
        get
        {
            long w = Volatile.Read(ref _writePos);
            long r = Volatile.Read(ref _readPos);
            long diff = w - r;
            return (int)Math.Clamp(diff, 0, _buffer.Length);
        }
    }

    /// <summary>
    /// Initializes a new instance of <see cref="PassthroughBridge"/>.
    /// </summary>
    /// <param name="capacity">Capacity in frames, rounded up to power of two. Default 32768 (~682 ms at 48 kHz).</param>
    public PassthroughBridge(int capacity = 32768)
    {
        int p2 = 1;
        while (p2 < capacity)
        {
            p2 <<= 1;
        }

        _buffer = new float[p2];
        _mask = p2 - 1;
        _writePos = 0;
        _readPos = 0;
    }

    /// <summary>
    /// Writes audio frames from the capture callback into the buffer.
    /// Zero heap allocation.
    /// </summary>
    public void Write(ReadOnlySpan<float> input)
    {
        if (input.IsEmpty)
        {
            return;
        }

        long writePos = Volatile.Read(ref _writePos);
        long readPos = Volatile.Read(ref _readPos);

        int availableSpace = _buffer.Length - (int)(writePos - readPos);
        if (availableSpace < input.Length)
        {
            // Overrun: record dropped frames and advance read pointer to preserve freshest audio
            int drop = input.Length - availableSpace;
            Interlocked.Add(ref _overrunFrames, drop);
            Volatile.Write(ref _readPos, readPos + drop);
        }

        int writeIndex = (int)(writePos & _mask);
        int firstChunk = Math.Min(input.Length, _buffer.Length - writeIndex);
        input[..firstChunk].CopyTo(_buffer.AsSpan(writeIndex, firstChunk));

        int secondChunk = input.Length - firstChunk;
        if (secondChunk > 0)
        {
            input.Slice(firstChunk, secondChunk).CopyTo(_buffer.AsSpan(0, secondChunk));
        }

        Volatile.Write(ref _writePos, writePos + input.Length);
    }

    /// <summary>
    /// Reads audio frames into the render buffer. Zero heap allocation.
    /// Fills with silence if insufficient frames are available.
    /// </summary>
    public int Read(Span<float> output)
    {
        if (output.IsEmpty)
        {
            return 0;
        }

        long writePos = Volatile.Read(ref _writePos);
        long readPos = Volatile.Read(ref _readPos);

        int availableFrames = (int)(writePos - readPos);
        if (availableFrames <= 0)
        {
            // Underrun: zero-fill output
            output.Clear();
            Interlocked.Add(ref _underrunFrames, output.Length);
            return 0;
        }

        int framesToRead = Math.Min(output.Length, availableFrames);
        int readIndex = (int)(readPos & _mask);
        int firstChunk = Math.Min(framesToRead, _buffer.Length - readIndex);
        _buffer.AsSpan(readIndex, firstChunk).CopyTo(output[..firstChunk]);

        int secondChunk = framesToRead - firstChunk;
        if (secondChunk > 0)
        {
            _buffer.AsSpan(0, secondChunk).CopyTo(output.Slice(firstChunk, secondChunk));
        }

        // Zero-pad any remaining frames if requested more than available
        if (output.Length > framesToRead)
        {
            output[framesToRead..].Clear();
            Interlocked.Add(ref _underrunFrames, output.Length - framesToRead);
        }

        Volatile.Write(ref _readPos, readPos + framesToRead);
        return framesToRead;
    }

    /// <summary>
    /// Resets buffer read and write pointers. Call only when audio stream is stopped.
    /// </summary>
    public void Reset()
    {
        Volatile.Write(ref _writePos, 0);
        Volatile.Write(ref _readPos, 0);
        Volatile.Write(ref _underrunFrames, 0);
        Volatile.Write(ref _overrunFrames, 0);
        Array.Clear(_buffer);
    }
}
