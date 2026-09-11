using VoiceChanger.Audio;
using VoiceChanger.Audio.Telemetry;
using VoiceChanger.Core.Dsp;
using Xunit;

namespace VoiceChanger.Tests;

public class TelemetryLoggerTests
{
    [Fact]
    public void TelemetryLogger_WritesFormattedSnapshot_ToRootLogFile()
    {
        string tempLogPath = Path.Combine(Path.GetTempPath(), $"telemetry_test_{Guid.NewGuid():N}.log");

        try
        {
            using (var logger = new TelemetryLogger(tempLogPath))
            {
                logger.IsEnabled = true;

                var latency = new LatencyDistribution(
                    P50Ms: 0.69,
                    P95Ms: 0.75,
                    P99Ms: 0.80,
                    MaxMs: 0.90,
                    SampleCount: 128,
                    Configuration: "Live DSP Chunks");

                var dspParams = new DspParameters
                {
                    PitchSemitones = 4.0f,
                    FormantSemitones = 2.0f,
                    NoiseGateEnabled = true,
                    NoiseGateThresholdDb = -45.0f,
                    NoiseGateAttackMs = 5.0f,
                    NoiseGateReleaseMs = 80.0f,
                    DryWetMix = 1.0f,
                    VocoderEnabled = true
                };

                logger.LogSnapshot(
                    latency,
                    fillPercentage: 8.5,
                    fillMs: 29.0,
                    driftRate: 0.1,
                    droppedSamples: 15,
                    duplicatedSamples: 0,
                    overruns: 0,
                    underruns: 0,
                    activePresetName: "Helium",
                    dspParams,
                    inputDevice: "Test Mic",
                    outputDevice: "VB-CABLE",
                    isMonitoring: true,
                    monitorDevice: "Test Headphones",
                    force: true);

                logger.IsEnabled = false;
            }

            Assert.True(File.Exists(tempLogPath));
            string content = File.ReadAllText(tempLogPath);

            Assert.Contains("DSP Latency: p50=0.69ms | p95=0.75ms | p99=0.80ms | max=0.90ms", content);
            Assert.Contains("Buffer: Fill=8.5% (29ms) | Drift=+0.1/s | Drops=15 | Dups=0 | Overruns=0 | Underruns=0", content);
            Assert.Contains("Preset='Helium'", content);
            Assert.Contains("Pitch=+4.0st", content);
            Assert.Contains("Formant=+2.0st", content);
            Assert.Contains("Gate=ON (Thresh=-45.0dB", content);
            Assert.Contains("Mix=100%", content);
            Assert.Contains("Monitor=ACTIVE ('Test Headphones')", content);
            Assert.Contains("Mic='Test Mic'", content);
            Assert.Contains("Out='VB-CABLE'", content);
        }
        finally
        {
            if (File.Exists(tempLogPath))
            {
                File.Delete(tempLogPath);
            }
        }
    }
}
