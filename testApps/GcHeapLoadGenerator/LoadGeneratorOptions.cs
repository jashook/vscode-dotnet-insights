////////////////////////////////////////////////////////////////////////////////
// Module: LoadGeneratorOptions.cs
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.GcHeapLoadGenerator {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public sealed class LoadGeneratorOptions
{
    public double TargetGb = 10.0;

    // Fraction of the retained baseline held as small (non-LOH) objects vs.
    // LOH objects. 0.75 means 75% small-object cache, 25% LOH dictionary.
    public double SmallFraction = 0.75;

    public int SmallMinBytes = 32;
    public int SmallMaxBytes = 84_999;

    // LOH payloads mock a TTL cache holding serialized blobs: most are a
    // "typical" size, a minority are a long-tail "large" size.
    public int LohMinBytes = 85_000;
    public int LohTypicalMaxBytes = 300_000;
    public int LohLargeMinBytes = 500_000;
    public int LohLargeMaxBytes = 4_000_000;
    public double LohLargePayloadChance = 0.15;

    // Cache entries expire independently of insertion order, since real
    // TTL caches assign each entry its own TTL - this is what scatters
    // free holes through the LOH instead of freeing it front-to-back.
    // Kept short by default so additions and evictions churn at a high
    // rate once the baseline fills, instead of settling into a mostly
    // static heap that only rarely frees anything.
    public int LohMinTtlMs = 200;
    public int LohMaxTtlMs = 2_000;

    // Probability that any given allocation iteration produces an
    // LOH-sized object instead of a small one. Kept relatively high so
    // the LOH cache churns fast enough to be the dominant source of
    // fragmentation, matching real TTL-cache-of-large-payloads services.
    public double LohChance = 0.2;

    // Probability that a small object is retained toward the baseline
    // instead of escaping immediately (becoming garbage).
    public double RetainChance = 0.08;

    // Probability that an LOH payload is cached (subject to TTL expiry)
    // instead of being a one-off, never-cached payload that is garbage
    // immediately (e.g. a cache-miss result that turned out uncacheable).
    public double LohRetainChance = 0.7;

    public int StatusIntervalSeconds = 2;

    public long TargetBytes()
    {
        return (long)(TargetGb * 1024.0 * 1024.0 * 1024.0);
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.GcHeapLoadGenerator)
