using VoiceChanger.Core;
using VoiceChanger.Neural;
using Xunit;

namespace VoiceChanger.Tests;

public class RvcProcessorTests
{
    [Fact]
    public void RvcProcessor_LifecycleAndWarmUp_ExecutesCleanly()
    {
        using var processor = new RvcProcessor(windowMs: 100, overlapMs: 20);
        processor.Prepare(48000, 256);

        Assert.True(processor.LatencyFrames > 0, "LatencyFrames should reflect the chunk window size.");

        // Warm up with silent inferences
        processor.WarmUp(passes: 3);

        float[] inBlock = new float[256];
        float[] outBlock = new float[256];
        inBlock.AsSpan().Fill(0.5f);

        // Process audio blocks
        for (int i = 0; i < 50; i++)
        {
            processor.Process(inBlock, outBlock);
        }

        processor.Reset();
    }

    [Fact]
    public void RvcProcessor_ZeroAllocations_InAudioProcessCallback()
    {
        using var processor = new RvcProcessor(windowMs: 100, overlapMs: 20);
        processor.Prepare(48000, 256);

        float[] inBlock = new float[256];
        float[] outBlock = new float[256];

        // Warm up JIT
        for (int i = 0; i < 500; i++)
        {
            processor.Process(inBlock, outBlock);
        }

        // Assert 0 allocations in audio callback path (Invariant #1)
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 5000; i++)
        {
            processor.Process(inBlock, outBlock);
        }
        long allocatedAfter = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(0, allocatedAfter - allocatedBefore);
    }

    [Fact]
    public void RvcProcessor_LatencyDistribution_TracksPercentiles()
    {
        using var processor = new RvcProcessor(windowMs: 50, overlapMs: 10);
        processor.Prepare(48000, 256);

        float[] inBlock = new float[256];
        float[] outBlock = new float[256];

        // Feed enough audio blocks to trigger multiple inference passes
        for (int i = 0; i < 100; i++)
        {
            processor.Process(inBlock, outBlock);
            Thread.Sleep(1); // Give inference worker time to process
        }

        var latency = processor.GetLatencyDistribution();
        if (latency != null)
        {
            Assert.True(latency.P50Ms >= 0, "p50 must be non-negative.");
            Assert.True(latency.P95Ms >= latency.P50Ms, "p95 must be >= p50.");
            Assert.True(latency.P99Ms >= latency.P95Ms, "p99 must be >= p95.");
            Assert.True(latency.MaxMs >= latency.P99Ms, "max must be >= p99.");
        }
    }

    [Fact]
    public void ExtractF0AndPitch_TracksFundamentalAccurately_WithoutOctaveDrops()
    {
        int sampleRate = 48000;
        int numFrames = 30;
        long[] pitchBins = new long[numFrames];
        float[] nsff0 = new float[numFrames];

        // 1. Synthesize a voiced tone at 200 Hz with strong harmonics at 400 Hz and 600 Hz
        float[] audio48k = new float[14400];
        for (int i = 0; i < audio48k.Length; i++)
        {
            float t = (float)i / sampleRate;
            audio48k[i] = 0.08f * MathF.Sin(2f * MathF.PI * 200f * t)
                        + 0.04f * MathF.Sin(2f * MathF.PI * 400f * t)
                        + 0.02f * MathF.Sin(2f * MathF.PI * 600f * t);
        }

        RvcProcessor.ExtractF0AndPitch(audio48k, pitchBins, nsff0, 0f);

        // Verify that pitch tracking correctly identifies the fundamental ~200 Hz across frames
        // and does NOT drop to subharmonics (e.g. 100 Hz or 66 Hz)
        int validVoicedFrames = 0;
        for (int f = 2; f < numFrames - 2; f++)
        {
            if (nsff0[f] > 0f)
            {
                validVoicedFrames++;
                Assert.InRange(nsff0[f], 190.0f, 210.0f);
                Assert.InRange(pitchBins[f], 2, 255);
            }
        }
        Assert.True(validVoicedFrames > 20, "Most frames in synthetic voiced audio should be voiced.");

        // 2. Test silence threshold: silence must produce unvoiced (f0 = 0, bin = 1)
        Array.Clear(audio48k);
        RvcProcessor.ExtractF0AndPitch(audio48k, pitchBins, nsff0, 0f);
        for (int f = 0; f < numFrames; f++)
        {
            Assert.Equal(0f, nsff0[f]);
            Assert.Equal(1L, pitchBins[f]);
        }
    }

    [Fact]
    public void ExtractF0AndPitch_PitchShiftSemitones_ShiftsFrequency()
    {
        int sampleRate = 48000;
        int numFrames = 30;
        long[] pitchBins = new long[numFrames];
        float[] nsff0 = new float[numFrames];

        // 200 Hz tone shifted up by 12 semitones (+1 octave) -> 400 Hz
        float[] audio48k = new float[14400];
        for (int i = 0; i < audio48k.Length; i++)
        {
            float t = (float)i / sampleRate;
            audio48k[i] = 0.1f * MathF.Sin(2f * MathF.PI * 200f * t);
        }

        RvcProcessor.ExtractF0AndPitch(audio48k, pitchBins, nsff0, 12f);

        for (int f = 2; f < numFrames - 2; f++)
        {
            if (nsff0[f] > 0f)
            {
                Assert.InRange(nsff0[f], 380.0f, 420.0f);
            }
        }
    }

    [Fact]
    public void RvcProcessor_InspectRealModels_IfPresent()
    {
        var catalog = GetTestVoiceCatalog();
        string? hubertPath = catalog.ContentVecModelPath;
        var voices = catalog.GetAvailableVoices();
        var activeVoice = voices.Count > 0 ? voices[0] : null;
        string? voicePath = activeVoice?.ModelPath;

        if (string.IsNullOrEmpty(hubertPath) || !File.Exists(hubertPath) ||
            string.IsNullOrEmpty(voicePath) || !File.Exists(voicePath))
        {
            return; // Skip if models not present on this machine
        }

        // 1. Run HuBERT with 9680 samples (30 frames)
        using var hubertCpuSession = new Microsoft.ML.OnnxRuntime.InferenceSession(hubertPath);
        float[] audio16k = new float[9680];
        for (int i = 0; i < audio16k.Length; i++)
        {
            float t = (float)i / 16000f;
            audio16k[i] = 0.05f * (MathF.Sin(2f * MathF.PI * 200f * t)
                                 + 0.5f * MathF.Sin(2f * MathF.PI * 400f * t)
                                 + 0.25f * MathF.Sin(2f * MathF.PI * 600f * t));
        }

        // Standardize 16 kHz audio
        float sum = 0f;
        for (int i = 0; i < audio16k.Length; i++) sum += audio16k[i];
        float mean = sum / audio16k.Length;
        float varSum = 0f;
        for (int i = 0; i < audio16k.Length; i++) { float d = audio16k[i] - mean; varSum += d * d; }
        float std = MathF.Sqrt(varSum / audio16k.Length);
        if (std > 1e-4f)
        {
            float inv = 1f / std;
            for (int i = 0; i < audio16k.Length; i++) audio16k[i] = (audio16k[i] - mean) * inv;
        }

        bool[] mask = new bool[9680];
        using var sourceVal = Microsoft.ML.OnnxRuntime.OrtValue.CreateTensorValueFromMemory(
            Microsoft.ML.OnnxRuntime.OrtMemoryInfo.DefaultInstance, audio16k.AsMemory(), [1, 9680]);
        using var maskVal = Microsoft.ML.OnnxRuntime.OrtValue.CreateTensorValueFromMemory(
            Microsoft.ML.OnnxRuntime.OrtMemoryInfo.DefaultInstance, mask.AsMemory(), [1, 9680]);

        var hubertInputs = new Dictionary<string, Microsoft.ML.OnnxRuntime.OrtValue>
        {
            { "source", sourceVal },
            { "padding_mask", maskVal }
        };

        using var hubertResults = hubertCpuSession.Run(new Microsoft.ML.OnnxRuntime.RunOptions(), hubertInputs, hubertCpuSession.OutputNames);
        var featTensor30 = hubertResults[0];
        var featSpan30 = featTensor30.GetTensorDataAsSpan<float>();

        // Extract the last 15 frames (representing current 300 ms window) and repeat 2x -> 30 frames
        float[] feat30Repeated = new float[30 * 768];
        for (int t = 0; t < 15; t++)
        {
            int srcIndex = 15 + t;
            var srcFrame = featSpan30.Slice(srcIndex * 768, 768);
            srcFrame.CopyTo(feat30Repeated.AsSpan((2 * t) * 768, 768));
            srcFrame.CopyTo(feat30Repeated.AsSpan((2 * t + 1) * 768, 768));
        }

        using var phoneVal30 = Microsoft.ML.OnnxRuntime.OrtValue.CreateTensorValueFromMemory(
            Microsoft.ML.OnnxRuntime.OrtMemoryInfo.DefaultInstance, feat30Repeated.AsMemory(), [1, 30, 768]);
        long[] phoneLen30 = [30];
        using var phoneLenVal30 = Microsoft.ML.OnnxRuntime.OrtValue.CreateTensorValueFromMemory(
            Microsoft.ML.OnnxRuntime.OrtMemoryInfo.DefaultInstance, phoneLen30.AsMemory(), [1]);

        // Synthesize realistic 200 Hz pitch contour for 30 frames
        long[] pitch30 = new long[30];
        float[] nsff0_30 = new float[30];
        float f0 = 200f;
        float f0Mel = 1127.0f * MathF.Log(1.0f + f0 / 700.0f);
        int bin = (int)MathF.Round((f0Mel - 77.472f) * 254.0f / (1063.85f - 77.472f) + 1.0f);
        Array.Fill(pitch30, (long)bin);
        Array.Fill(nsff0_30, f0);

        using var pitchVal30 = Microsoft.ML.OnnxRuntime.OrtValue.CreateTensorValueFromMemory(
            Microsoft.ML.OnnxRuntime.OrtMemoryInfo.DefaultInstance, pitch30.AsMemory(), [1, 30]);

        using var nsff0Val30 = Microsoft.ML.OnnxRuntime.OrtValue.CreateTensorValueFromMemory(
            Microsoft.ML.OnnxRuntime.OrtMemoryInfo.DefaultInstance, nsff0_30.AsMemory(), [1, 30]);

        long[] sid = [0];
        using var sidVal = Microsoft.ML.OnnxRuntime.OrtValue.CreateTensorValueFromMemory(
            Microsoft.ML.OnnxRuntime.OrtMemoryInfo.DefaultInstance, sid.AsMemory(), [1]);

        using var genDmlOptions = new Microsoft.ML.OnnxRuntime.SessionOptions();
        try { genDmlOptions.AppendExecutionProvider_DML(0); } catch { }
        using var genSession = new Microsoft.ML.OnnxRuntime.InferenceSession(voicePath, genDmlOptions);

        var genInputs = new Dictionary<string, Microsoft.ML.OnnxRuntime.OrtValue>
        {
            { "phone", phoneVal30 },
            { "phone_lengths", phoneLenVal30 },
            { "pitch", pitchVal30 },
            { "nsff0", nsff0Val30 },
            { "sid", sidVal }
        };

        using var genResults = genSession.Run(new Microsoft.ML.OnnxRuntime.RunOptions(), genInputs, genSession.OutputNames);
        var genOut = genResults[0];
        var outSpan = genOut.GetTensorDataAsSpan<float>();

        Assert.Equal(14400, outSpan.Length);
        float outEnergy = 0f;
        for (int i = 0; i < outSpan.Length; i++) outEnergy += outSpan[i] * outSpan[i];
        float outRms = MathF.Sqrt(outEnergy / outSpan.Length);
        Assert.True(outRms > 0.05f, "Generator output must have healthy audio amplitude.");
        Assert.False(float.IsNaN(outRms), "Output must not contain NaNs.");
    }

    [Fact]
    public void RvcProcessor_EndToEndWithRealModels_TransformsAudio()
    {
        var catalog = GetTestVoiceCatalog();
        string? hubertPath = catalog.ContentVecModelPath;
        var voices = catalog.GetAvailableVoices();
        var activeVoice = voices.Count > 0 ? voices[0] : null;
        string? voicePath = activeVoice?.ModelPath;

        if (string.IsNullOrEmpty(hubertPath) || !File.Exists(hubertPath) ||
            string.IsNullOrEmpty(voicePath) || !File.Exists(voicePath) ||
            activeVoice == null)
        {
            return; // Skip if models not present on this machine
        }

        var config = new ExecutionProviderConfig
        {
            ProviderType = ExecutionProviderType.Cpu
        };

        using var processor = new RvcProcessor(config: config);
        processor.ModelSet.LoadSharedSessions(hubertPath, null);
        processor.SetVoice(activeVoice);

        processor.Prepare(48000, 256);
        processor.PitchShiftSemitones = 2.0f;

        float[] inBlock = new float[256];
        float[] outBlock = new float[256];

        for (int i = 0; i < inBlock.Length; i++)
        {
            inBlock[i] = 0.3f * MathF.Sin(2f * MathF.PI * 440f * i / 48000f);
        }

        // Push sufficient blocks to trigger neural inference window (14400 frames = ~57 blocks)
        for (int b = 0; b < 70; b++)
        {
            processor.Process(inBlock, outBlock);
        }

        // Wait up to 6 seconds for background worker to complete CPU neural inference
        LatencyDistribution? latency = null;
        for (int retry = 0; retry < 60; retry++)
        {
            latency = processor.GetLatencyDistribution();
            if (latency != null) break;
            Thread.Sleep(100);
        }

        Assert.NotNull(latency);
        Assert.True(latency.P50Ms > 0, "Neural inference must have recorded runtime latency.");
    }

    [Fact]
    public void RvcProcessor_SilenceInput_ProducesZeroOutput()
    {
        using var processor = new RvcProcessor(windowMs: 50, overlapMs: 10);
        processor.Prepare(48000, 256);

        // Apply noise gate with standard -45 dB threshold
        processor.ApplyParameters(new VoiceChanger.Core.Dsp.DspParameters
        {
            NoiseGateEnabled = true,
            NoiseGateThresholdDb = -45.0f,
            PitchSemitones = 0f
        });

        float[] silentIn = new float[256];
        float[] outBlock = new float[256];

        // Feed silence for 200 blocks (plenty of windows)
        for (int i = 0; i < 200; i++)
        {
            processor.Process(silentIn, outBlock);
        }

        // Wait a moment for worker thread
        Thread.Sleep(50);

        // Process another round and verify output is dead silence (zeros)
        for (int i = 0; i < 20; i++)
        {
            processor.Process(silentIn, outBlock);
            for (int s = 0; s < outBlock.Length; s++)
            {
                Assert.Equal(0.0f, outBlock[s]);
            }
        }
    }

    [Fact]
    public void RvcProcessor_AmbientNoiseBelowGate_IsCompletelySilenced()
    {
        using var processor = new RvcProcessor(windowMs: 50, overlapMs: 10);
        processor.Prepare(48000, 256);

        // Gate threshold at -40 dB (amplitude ~0.01)
        processor.ApplyParameters(new VoiceChanger.Core.Dsp.DspParameters
        {
            NoiseGateEnabled = true,
            NoiseGateThresholdDb = -40.0f,
            NoiseGateAttackMs = 1.0f,
            NoiseGateReleaseMs = 5.0f
        });

        // Ambient noise at amplitude 0.002 (-54 dBFS)
        float[] ambientNoise = new float[256];
        for (int i = 0; i < ambientNoise.Length; i++)
        {
            ambientNoise[i] = 0.002f * MathF.Sin(i * 0.1f);
        }

        float[] outBlock = new float[256];

        // Feed ambient noise
        for (int i = 0; i < 200; i++)
        {
            processor.Process(ambientNoise, outBlock);
        }

        Thread.Sleep(50);

        // Assert all subsequent output is silenced
        for (int i = 0; i < 20; i++)
        {
            processor.Process(ambientNoise, outBlock);
            for (int s = 0; s < outBlock.Length; s++)
            {
                Assert.True(MathF.Abs(outBlock[s]) < 1e-6f, $"Sample {s} had non-zero value: {outBlock[s]}");
            }
        }
    }

    [Fact]
    public void AudioEngine_SetProcessor_PropagatesLastParameters()
    {
        using var engine = new VoiceChanger.Audio.AudioEngine();
        var customParams = new VoiceChanger.Core.Dsp.DspParameters
        {
            PitchSemitones = 4.0f,
            NoiseGateEnabled = true,
            NoiseGateThresholdDb = -35.0f
        };

        engine.ApplyParameters(customParams);

        using var rvc = new RvcProcessor(windowMs: 50, overlapMs: 10);
        rvc.Prepare(48000, 256);

        // When processor is hot-swapped into engine, it must receive customParams
        engine.SetProcessor(rvc);

        Assert.Equal(4.0f, rvc.PitchShiftSemitones);
    }

    private static VoiceCatalog GetTestVoiceCatalog()
    {
        string? customDir = null;
        try
        {
            string settingsPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "VoiceChanger",
                "settings.json");
            if (File.Exists(settingsPath))
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(settingsPath));
                if (doc.RootElement.TryGetProperty("CustomModelsDirectory", out var prop))
                {
                    customDir = prop.GetString();
                }
            }
        }
        catch
        {
            // Fallback to default
        }

        return new VoiceCatalog(customDir);
    }
}
