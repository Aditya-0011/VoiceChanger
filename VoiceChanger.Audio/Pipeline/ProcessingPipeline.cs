using System.Runtime.InteropServices;
using VoiceChanger.Audio.Drift;
using VoiceChanger.Audio.Interop;
using VoiceChanger.Core;
using VoiceChanger.Core.Buffers;

namespace VoiceChanger.Audio.Pipeline;

/// <summary>
/// Dedicated audio processing pipeline running on an independent high-priority worker thread
/// with MMCSS "Pro Audio" characteristics. Isolates IAudioProcessor execution from WASAPI hardware callbacks
/// via lock-free SPSC ring buffers on both input and output sides.
/// </summary>
public sealed class ProcessingPipeline : IDisposable
{
    private readonly SpscRingBuffer _captureRing;
    private readonly SpscRingBuffer _renderRing;
    private readonly ClockDriftController _driftController;
    private readonly int _chunkSize;
    private readonly float[] _inputChunk;
    private readonly float[] _outputChunk;
    private readonly AutoResetEvent _dataAvailableEvent;

    private Thread? _workerThread;
    private volatile bool _isStopping;
    private bool _disposed;
    private IAudioProcessor _processor;

    /// <summary>
    /// Ring buffer receiving raw audio from WASAPI capture stream.
    /// </summary>
    public SpscRingBuffer CaptureRing => _captureRing;

    /// <summary>
    /// Ring buffer feeding processed audio into WASAPI render stream.
    /// </summary>
    public SpscRingBuffer RenderRing => _renderRing;

    /// <summary>
    /// Clock drift controller monitoring and balancing audio clocks.
    /// </summary>
    public ClockDriftController DriftController => _driftController;

    /// <summary>
    /// Number of frames dropped due to buffer overruns.
    /// </summary>
    public long OverrunFrames { get; private set; }

    /// <summary>
    /// Number of frames zero-padded due to buffer underruns.
    /// </summary>
    public long UnderrunFrames { get; private set; }

    /// <summary>
    /// Processing chunk size in frames (default 256 frames = 5.33 ms @ 48 kHz).
    /// </summary>
    public int ChunkSize => _chunkSize;

    /// <summary>
    /// Active audio processor. Can be hot-swapped atomically.
    /// </summary>
    public IAudioProcessor Processor
    {
        get => Volatile.Read(ref _processor);
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            value.Prepare(48000, _chunkSize);
            Volatile.Write(ref _processor, value);
        }
    }

    /// <summary>
    /// Creates a new instance of <see cref="ProcessingPipeline"/>.
    /// </summary>
    /// <param name="initialProcessor">Initial audio processor implementation.</param>
    /// <param name="ringCapacity">Ring buffer capacity in frames (must be a power of two, default 16384 = 341.3 ms @ 48 kHz).</param>
    /// <param name="chunkSize">Chunk size for worker processing (default 256 frames).</param>
    public ProcessingPipeline(IAudioProcessor initialProcessor, int ringCapacity = 16384, int chunkSize = 256)
    {
        ArgumentNullException.ThrowIfNull(initialProcessor);

        _processor = initialProcessor;
        _chunkSize = chunkSize;
        _captureRing = new SpscRingBuffer(ringCapacity);
        _renderRing = new SpscRingBuffer(ringCapacity);
        _driftController = new ClockDriftController(
            _renderRing,
            minIntervalBetweenCorrectionsMs: 0,
            lowThresholdFrames: 720,             // 15 ms @ 48 kHz
            highThresholdFrames: 2400,           // 50 ms @ 48 kHz
            targetFrames: 1440,                  // 30 ms target @ 48 kHz
            aggressiveHighThresholdFrames: 3360, // 70 ms @ 48 kHz
            emergencyThresholdFrames: 4800);     // 100 ms @ 48 kHz

        _inputChunk = new float[chunkSize + 1]; // +1 for duplicate sample support
        _outputChunk = new float[chunkSize + 1];
        _dataAvailableEvent = new AutoResetEvent(false);

        _processor.Prepare(48000, chunkSize);
    }

    /// <summary>
    /// Primes the render ring buffer with a nominal cushion of silence so the render stream
    /// starts without underrunning while capture spins up.
    /// </summary>
    /// <param name="primeFrames">Number of silent frames to pre-populate (default 1440 = 30 ms @ 48 kHz).</param>
    public void PrimeRenderBuffer(int primeFrames = 1440)
    {
        if (primeFrames <= 0)
        {
            return;
        }

        int toPrime = Math.Min(primeFrames, _renderRing.AvailableWrite);
        Span<float> silence = stackalloc float[Math.Min(toPrime, 256)];
        silence.Clear();

        int remaining = toPrime;
        while (remaining > 0)
        {
            int chunk = Math.Min(remaining, silence.Length);
            _renderRing.Write(silence.Slice(0, chunk));
            remaining -= chunk;
        }
    }

    /// <summary>
    /// Signals the worker thread that new captured audio has been pushed into the capture ring.
    /// Called by the WASAPI capture thread.
    /// </summary>
    public void NotifyCaptureDataAvailable()
    {
        _dataAvailableEvent.Set();
    }

    /// <summary>
    /// Records dropped frames on buffer overrun.
    /// </summary>
    public void RecordOverrun(int droppedFrames)
    {
        Interlocked.Add(ref _overrunFramesRef, droppedFrames);
    }

    private long _overrunFramesRef;

    /// <summary>
    /// Records padded frames on buffer underrun.
    /// </summary>
    public void RecordUnderrun(int paddedFrames)
    {
        Interlocked.Add(ref _underrunFramesRef, paddedFrames);
    }

    private long _underrunFramesRef;

    /// <summary>
    /// Starts the dedicated worker thread and primes the render buffer.
    /// </summary>
    /// <param name="primeFrames">Number of silent frames to pre-populate (default 1440 = 30 ms @ 48 kHz).</param>
    public void Start(int primeFrames = 1440)
    {
        if (_workerThread != null)
        {
            return;
        }

        _isStopping = false;
        Interlocked.Exchange(ref _overrunFramesRef, 0);
        Interlocked.Exchange(ref _underrunFramesRef, 0);
        OverrunFrames = 0;
        UnderrunFrames = 0;

        _captureRing.Reset();
        _renderRing.Reset();
        _driftController.Reset();

        PrimeRenderBuffer(primeFrames);

        _workerThread = new Thread(WorkerLoop)
        {
            Name = "VoiceChanger.WorkerPipeline",
            IsBackground = true,
            Priority = ThreadPriority.Highest
        };
        _workerThread.Start();
    }

    /// <summary>
    /// Stops the worker thread and waits for exit.
    /// </summary>
    public void Stop()
    {
        _isStopping = true;
        _dataAvailableEvent.Set();

        _workerThread?.Join(1000);
        _workerThread = null;
    }

    private void WorkerLoop()
    {
        // Apply MMCSS "Pro Audio" to the worker thread per Project.md §5 Phase 1
        uint taskIndex = 0;
        nint avrtHandle = Avrt.AvSetMmThreadCharacteristicsW("Pro Audio", ref taskIndex);

        try
        {
            while (!_isStopping)
            {
                // Wait for capture data or periodic 10 ms heartbeat
                _dataAvailableEvent.WaitOne(10);

                if (_isStopping)
                {
                    break;
                }

                ProcessAvailableChunks();
            }
        }
        finally
        {
            if (avrtHandle != 0)
            {
                Avrt.AvRevertMmThreadCharacteristics(avrtHandle);
            }
        }
    }

    private void ProcessAvailableChunks()
    {
        Span<float> inputSpan = _inputChunk.AsSpan(0, _chunkSize);
        Span<float> outputSpan = _outputChunk.AsSpan(0, _chunkSize);

        while (_captureRing.AvailableRead >= _chunkSize && !_isStopping)
        {
            // Evaluate clock drift correction
            int correction = _driftController.EvaluateCorrection();

            // Read standard chunk from capture ring
            int readCount = _captureRing.Read(inputSpan);
            if (readCount < _chunkSize)
            {
                break;
            }

            // Execute active DSP processor
            IAudioProcessor proc = Volatile.Read(ref _processor);
            proc.Process(inputSpan, outputSpan);

            // Handle clock drift correction:
            // +2 = Drop 2 samples (fast correction) -> write chunkSize - 2 frames
            // +1 = Drop 1 sample (gentle correction) -> write chunkSize - 1 frames
            // -1 = Duplicate sample (buffer too empty) -> write chunkSize + 1 frames
            // 0  = Standard -> write chunkSize frames
            if (correction == 1 && _chunkSize > 1)
            {
                // Drop 1 sample: write chunkSize - 1 frames
                ReadOnlySpan<float> toWrite = _outputChunk.AsSpan(0, _chunkSize - 1);
                if (!_renderRing.Write(toWrite))
                {
                    RecordOverrun(toWrite.Length);
                }
            }
            else if (correction == 2 && _chunkSize > 2)
            {
                // Drop 2 samples: write chunkSize - 2 frames (fast slew)
                ReadOnlySpan<float> toWrite = _outputChunk.AsSpan(0, _chunkSize - 2);
                if (!_renderRing.Write(toWrite))
                {
                    RecordOverrun(toWrite.Length);
                }
            }
            else if (correction < 0)
            {
                // Duplicate 1 sample: append copy of last sample
                _outputChunk[_chunkSize] = _outputChunk[_chunkSize - 1];
                ReadOnlySpan<float> toWrite = _outputChunk.AsSpan(0, _chunkSize + 1);
                if (!_renderRing.Write(toWrite))
                {
                    RecordOverrun(toWrite.Length);
                }
            }
            else
            {
                // Normal write
                ReadOnlySpan<float> toWrite = _outputChunk.AsSpan(0, _chunkSize);
                if (!_renderRing.Write(toWrite))
                {
                    RecordOverrun(toWrite.Length);
                }
            }
        }

        // Sync atomic overrun/underrun counters
        OverrunFrames = Volatile.Read(ref _overrunFramesRef);
        UnderrunFrames = Volatile.Read(ref _underrunFramesRef);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
        _dataAvailableEvent.Dispose();
    }
}
