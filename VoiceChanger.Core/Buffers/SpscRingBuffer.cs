using System.Runtime.CompilerServices;

namespace VoiceChanger.Core.Buffers;

/// <summary>
/// Lock-free Single-Producer Single-Consumer (SPSC) ring buffer for audio streaming.
/// Memory layout is pre-allocated with a power-of-two capacity for zero-allocation
/// masked slicing over <see cref="Span{T}"/>.
/// </summary>
public sealed class SpscRingBuffer
{
    private readonly float[] _buffer;
    private readonly int _mask;
    private long _writePos;
    private long _readPos;

    /// <summary>
    /// Total buffer capacity in audio frames.
    /// </summary>
    public int Capacity => _buffer.Length;

    /// <summary>
    /// Current number of unread audio frames in the buffer.
    /// </summary>
    public long FillCount
    {
        get
        {
            long w = Volatile.Read(ref _writePos);
            long r = Volatile.Read(ref _readPos);
            long diff = w - r;
            if (diff < 0) return 0;
            if (diff > Capacity) return Capacity;
            return diff;
        }
    }

    /// <summary>
    /// Number of frames immediately available for reading.
    /// </summary>
    public int AvailableRead => (int)FillCount;

    /// <summary>
    /// Number of frames that can currently be written without overrunning.
    /// </summary>
    public int AvailableWrite
    {
        get
        {
            long w = Volatile.Read(ref _writePos);
            long r = Volatile.Read(ref _readPos);
            long diff = w - r;
            int avail = Capacity - (int)diff;
            return avail < 0 ? 0 : avail;
        }
    }

    /// <summary>
    /// Initializes a new instance of <see cref="SpscRingBuffer"/>.
    /// </summary>
    /// <param name="capacity">Capacity in frames. Must be a positive power of two.</param>
    /// <exception cref="ArgumentException">Thrown when capacity is not a power of two.</exception>
    public SpscRingBuffer(int capacity)
    {
        if (capacity <= 0 || (capacity & (capacity - 1)) != 0)
        {
            throw new ArgumentException("Ring buffer capacity must be a positive power of two.", nameof(capacity));
        }

        _buffer = new float[capacity];
        _mask = capacity - 1;
    }

    /// <summary>
    /// Writes audio frames from <paramref name="src"/> into the ring buffer.
    /// Wraparound is handled with up to two contiguous span slices without per-sample branching.
    /// </summary>
    /// <param name="src">Audio frames to write.</param>
    /// <returns><c>true</c> if all frames were written; <c>false</c> if buffer did not have sufficient capacity.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Write(ReadOnlySpan<float> src)
    {
        if (src.IsEmpty)
        {
            return true;
        }

        long w = _writePos;
        long r = Volatile.Read(ref _readPos);

        if (Capacity - (int)(w - r) < src.Length)
        {
            return false;
        }

        int writeIndex = (int)(w & _mask);
        int firstChunk = Math.Min(src.Length, Capacity - writeIndex);
        int secondChunk = src.Length - firstChunk;

        src[..firstChunk].CopyTo(_buffer.AsSpan(writeIndex, firstChunk));

        if (secondChunk > 0)
        {
            src.Slice(firstChunk, secondChunk).CopyTo(_buffer.AsSpan(0, secondChunk));
        }

        Volatile.Write(ref _writePos, w + src.Length);
        return true;
    }

    /// <summary>
    /// Reads up to <c>dst.Length</c> audio frames into <paramref name="dst"/>.
    /// Wraparound is handled with up to two contiguous span slices without per-sample branching.
    /// </summary>
    /// <param name="dst">Destination span to receive audio frames.</param>
    /// <returns>Number of frames actually read and transferred into <paramref name="dst"/>.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Read(Span<float> dst)
    {
        if (dst.IsEmpty)
        {
            return 0;
        }

        long r = _readPos;
        long w = Volatile.Read(ref _writePos);

        int available = (int)(w - r);
        if (available <= 0)
        {
            return 0;
        }

        int toRead = Math.Min(dst.Length, available);
        int readIndex = (int)(r & _mask);
        int firstChunk = Math.Min(toRead, Capacity - readIndex);
        int secondChunk = toRead - firstChunk;

        _buffer.AsSpan(readIndex, firstChunk).CopyTo(dst[..firstChunk]);

        if (secondChunk > 0)
        {
            _buffer.AsSpan(0, secondChunk).CopyTo(dst.Slice(firstChunk, secondChunk));
        }

        Volatile.Write(ref _readPos, r + toRead);
        return toRead;
    }

    /// <summary>
    /// Drops up to <paramref name="count"/> frames from the buffer without copying them.
    /// Used by clock drift correction.
    /// </summary>
    /// <param name="count">Maximum number of frames to discard.</param>
    /// <returns>Number of frames actually discarded.</returns>
    public int Discard(int count)
    {
        if (count <= 0) return 0;

        long r = _readPos;
        long w = Volatile.Read(ref _writePos);

        int available = (int)(w - r);
        if (available <= 0) return 0;

        int toDiscard = Math.Min(count, available);
        Volatile.Write(ref _readPos, r + toDiscard);
        return toDiscard;
    }

    /// <summary>
    /// Resets the buffer to empty state. Not thread-safe with concurrent Read/Write;
    /// invoke only during pipeline startup, shutdown, or stopped states.
    /// </summary>
    public void Reset()
    {
        Volatile.Write(ref _writePos, 0);
        Volatile.Write(ref _readPos, 0);
        Array.Clear(_buffer);
    }
}
