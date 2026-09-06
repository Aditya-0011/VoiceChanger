using BenchmarkDotNet.Running;

namespace VoiceChanger.Bench;

public static class Program
{
    public static void Main(string[] args)
    {
        BenchmarkRunner.Run<PhaseVocoderBench>(args: args);
    }
}
