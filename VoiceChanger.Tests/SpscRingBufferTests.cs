using VoiceChanger.Core.Buffers;
using Xunit;

namespace VoiceChanger.Tests;

public sealed class SpscRingBufferTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(3)]
    [InlineData(100)]
    [InlineData(1000)]
    public void Constructor_RejectsNonPowerOfTwo(int invalidCapacity)
    {
        Assert.Throws<ArgumentException>(() => new SpscRingBuffer(invalidCapacity));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(64)]
    [InlineData(1024)]
    [InlineData(65536)]
    public void Constructor_AcceptsPowerOfTwo(int validCapacity)
    {
        var buffer = new SpscRingBuffer(validCapacity);
        Assert.Equal(validCapacity, buffer.Capacity);
        Assert.Equal(0, buffer.FillCount);
        Assert.Equal(validCapacity, buffer.AvailableWrite);
        Assert.Equal(0, buffer.AvailableRead);
    }

    [Fact]
    public void SingleThread_WriteAndRead_ExactMatch()
    {
        var ring = new SpscRingBuffer(1024);
        float[] input = new float[256];
        for (int i = 0; i < input.Length; i++) input[i] = i + 1.5f;

        bool written = ring.Write(input);
        Assert.True(written);
        Assert.Equal(256, ring.FillCount);
        Assert.Equal(256, ring.AvailableRead);
        Assert.Equal(768, ring.AvailableWrite);

        float[] output = new float[256];
        int read = ring.Read(output);
        Assert.Equal(256, read);
        Assert.Equal(input, output);
        Assert.Equal(0, ring.FillCount);
    }

    [Fact]
    public void SingleThread_WraparoundIntegrity()
    {
        var ring = new SpscRingBuffer(64);

        // Step 1: Write 48 frames, read 40 frames -> read pos = 40, write pos = 48
        float[] b1 = new float[48];
        for (int i = 0; i < b1.Length; i++) b1[i] = i;
        Assert.True(ring.Write(b1));

        float[] r1 = new float[40];
        Assert.Equal(40, ring.Read(r1));
        Assert.Equal(b1.AsSpan(0, 40).ToArray(), r1);

        // Step 2: Write 40 frames -> write pos = 88 (wraps around 64: 48 + 40 = 88 > 64)
        float[] b2 = new float[40];
        for (int i = 0; i < b2.Length; i++) b2[i] = 1000 + i;
        Assert.True(ring.Write(b2));
        Assert.Equal(48, ring.FillCount); // 8 remaining + 40 new = 48

        // Step 3: Read all 48 frames across wraparound boundary
        float[] r2 = new float[48];
        int readCount = ring.Read(r2);
        Assert.Equal(48, readCount);

        // First 8 should match end of b1 (indices 40..47)
        for (int i = 0; i < 8; i++)
        {
            Assert.Equal(40 + i, r2[i]);
        }

        // Remaining 40 should match b2 (indices 0..39)
        for (int i = 0; i < 40; i++)
        {
            Assert.Equal(1000 + i, r2[8 + i]);
        }
    }

    [Fact]
    public void SingleThread_FullBufferRejectsWrite()
    {
        var ring = new SpscRingBuffer(16);
        float[] data = new float[16];
        Assert.True(ring.Write(data));
        Assert.Equal(16, ring.FillCount);
        Assert.Equal(0, ring.AvailableWrite);

        float[] extra = new float[1];
        Assert.False(ring.Write(extra));
        Assert.Equal(16, ring.FillCount);
    }

    [Fact]
    public void SingleThread_Discard_AdvancesReadPosition()
    {
        var ring = new SpscRingBuffer(64);
        float[] data = [1.0f, 2.0f, 3.0f, 4.0f, 5.0f];
        ring.Write(data);

        int discarded = ring.Discard(2);
        Assert.Equal(2, discarded);
        Assert.Equal(3, ring.FillCount);

        float[] readBuf = new float[3];
        ring.Read(readBuf);
        Assert.Equal([3.0f, 4.0f, 5.0f], readBuf);
    }

    [Fact]
    public void SingleThread_ZeroAllocationCheck()
    {
        var ring = new SpscRingBuffer(1024);
        Span<float> writeSpan = stackalloc float[128];
        Span<float> readSpan = stackalloc float[128];

        // Warm up
        ring.Write(writeSpan);
        ring.Read(readSpan);

        long before = GC.GetAllocatedBytesForCurrentThread();

        for (int i = 0; i < 10000; i++)
        {
            ring.Write(writeSpan);
            ring.Read(readSpan);
        }

        long after = GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal(before, after); // 0 heap allocations
    }

    [Fact]
    public void ConcurrencyHammer_MillionSamples_NoLossOrCorruption()
    {
        const int totalSamples = 5_000_000;
        var ring = new SpscRingBuffer(16384);

        var producerThread = new Thread(() =>
        {
            var rng = new Random(42);
            int produced = 0;
            float[] buffer = new float[1024];

            while (produced < totalSamples)
            {
                int chunkSize = Math.Min(rng.Next(1, buffer.Length + 1), totalSamples - produced);
                for (int i = 0; i < chunkSize; i++)
                {
                    buffer[i] = produced + i;
                }

                while (!ring.Write(buffer.AsSpan(0, chunkSize)))
                {
                    Thread.Yield();
                }

                produced += chunkSize;
            }
        });

        int consumed = 0;
        bool errorDetected = false;
        string? errorMessage = null;

        var consumerThread = new Thread(() =>
        {
            var rng = new Random(1337);
            float[] buffer = new float[1024];

            while (consumed < totalSamples)
            {
                int maxRead = rng.Next(1, buffer.Length + 1);
                int read = ring.Read(buffer.AsSpan(0, maxRead));

                if (read == 0)
                {
                    Thread.Yield();
                    continue;
                }

                for (int i = 0; i < read; i++)
                {
                    int expected = consumed + i;
                    if ((int)buffer[i] != expected)
                    {
                        errorDetected = true;
                        errorMessage = $"Data corruption at sample {consumed + i}: expected {expected}, got {buffer[i]}";
                        return;
                    }
                }

                consumed += read;
            }
        });

        producerThread.Start();
        consumerThread.Start();

        bool producerFinished = producerThread.Join(15000);
        bool consumerFinished = consumerThread.Join(15000);

        Assert.True(producerFinished, "Producer thread timed out.");
        Assert.True(consumerFinished, "Consumer thread timed out.");
        Assert.False(errorDetected, errorMessage ?? "Unknown error");
        Assert.Equal(totalSamples, consumed);
        Assert.Equal(0, ring.FillCount);
    }
}
