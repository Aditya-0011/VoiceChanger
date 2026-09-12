namespace VoiceChanger.Core;

/// <summary>
/// Immutable record capturing an audio latency distribution per Invariant #5.
/// Never reports a bare mean alone.
/// </summary>
public sealed record LatencyDistribution(
    double P50Ms,
    double P95Ms,
    double P99Ms,
    double MaxMs,
    int SampleCount = 0,
    string Configuration = "")
{
    /// <inheritdoc/>
    public override string ToString() =>
        $"p50={P50Ms:F2}ms, p95={P95Ms:F2}ms, p99={P99Ms:F2}ms, max={MaxMs:F2}ms (Config: {Configuration}, Samples: {SampleCount})";
}
