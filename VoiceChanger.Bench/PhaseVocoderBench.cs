using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using VoiceChanger.Core.Dsp;

namespace VoiceChanger.Bench;

[MemoryDiagnoser]
public class PhaseVocoderBench
{
    private const int SampleRate = 48000;
    private const int BlockSize = 256;

    private PhaseVocoderProcessor _vocoderPlus12 = null!;
    private PhaseVocoderProcessor _vocoderMinus12 = null!;
    private PhaseVocoderProcessor _vocoderPassthrough = null!;

    private float[] _input = null!;
    private float[] _output = null!;

    [GlobalSetup]
    public void Setup()
    {
        _vocoderPlus12 = new PhaseVocoderProcessor(frameSize: 1024, analysisHop: 256, initialSemitones: 12.0f);
        _vocoderPlus12.Prepare(SampleRate, BlockSize);

        _vocoderMinus12 = new PhaseVocoderProcessor(frameSize: 1024, analysisHop: 256, initialSemitones: -12.0f);
        _vocoderMinus12.Prepare(SampleRate, BlockSize);

        _vocoderPassthrough = new PhaseVocoderProcessor(frameSize: 1024, analysisHop: 256, initialSemitones: 0.0f);
        _vocoderPassthrough.Prepare(SampleRate, BlockSize);

        _input = new float[BlockSize];
        _output = new float[BlockSize];

        for (int i = 0; i < BlockSize; i++)
        {
            _input[i] = MathF.Sin(2.0f * MathF.PI * 440.0f * i / SampleRate);
        }
    }

    [Benchmark(Baseline = true)]
    public void Passthrough_256Frames()
    {
        _vocoderPassthrough.Process(_input, _output);
    }

    [Benchmark]
    public void PitchShift_Plus12Semitones_256Frames()
    {
        _vocoderPlus12.Process(_input, _output);
    }

    [Benchmark]
    public void PitchShift_Minus12Semitones_256Frames()
    {
        _vocoderMinus12.Process(_input, _output);
    }
}
