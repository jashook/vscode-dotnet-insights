// Adaptively downsamples gcData["allocationSummary"]["ticks"] (raw
// per-AllocationTick samples from AllocationJsonExporter.cs) before it gets
// embedded into the webview's JSON payload. nettraceParser deliberately
// emits every raw tick with no cap - see AllocationJsonExporter.cs's own
// header comment ("full per-GC fidelity ... matching how GcJsonExporter.cs
// already ships full per-GC fidelity") - so a heavily-allocating capture
// can produce millions of entries. GcSnapshotRenderer.ts then
// JSON.stringify's the whole thing into the webview's HTML and the webview
// JSON.parse's it back, and allocationStats.js's own per-GC-boundary
// summation (buildAllocationBeforeGcSegments -> sumTicksInRange) scans the
// full ticks array once per GC - with millions of ticks and thousands of
// GCs that's tens of billions of iterations, which is what actually hangs
// the page (not chart rendering - the main rate chart already buckets
// client-side at a 1-second width regardless).
//
// This only touches captures large enough to cause that: anything at or
// under MaxTickCount passes through completely unchanged, preserving full
// fidelity for the typical/small-capture case nettraceParser was designed
// around. Only oversized captures get bucketed, and only just enough to
// land under the cap - bucket width is derived from the capture's own
// duration divided by MaxTickCount, not a fixed constant, so a short
// bursty capture stays far finer than a long one.
//
// Bucket width is intentionally NOT fixed to allocationStats.js's own
// 1-second DEFAULT_BUCKET_WIDTH_MSEC: at MaxTickCount buckets over a
// multi-minute capture the derived width lands well under a second, which
// keeps "allocated before this specific GC" attribution meaningfully
// precise even when several gen0 GCs land within the same second - a fixed
// 1-second bucket would blur that attribution for any busy app.

const MaxTickCount = 100_000;

export function adaptivelyBucketTicks(ticks: any[]): any[] {
    if (ticks === null || ticks === undefined || ticks.length <= MaxTickCount) {
        return ticks;
    }

    // Ticks are already sorted ascending by RelativeMSec (WriteTicks sorts
    // defensively before writing), so the first/last entries bound the
    // capture's tick-covered duration.
    const firstRelativeMSec = ticks[0]["RelativeMSec"];
    const lastRelativeMSec = ticks[ticks.length - 1]["RelativeMSec"];
    const totalDurationMSec = lastRelativeMSec - firstRelativeMSec;

    // Guard against a degenerate all-ticks-at-one-timestamp capture, where
    // a zero-width bucket would make every tick collide via division by zero.
    const bucketWidthMSec = totalDurationMSec > 0 ? totalDurationMSec / MaxTickCount : 1;

    const amountByBucketIndex = new Map<number, number>();

    for (let tickIndex = 0; tickIndex < ticks.length; ++tickIndex) {
        const tick = ticks[tickIndex];

        // The very last tick's RelativeMSec equals totalDurationMSec
        // exactly, which divides out to bucketIndex === MaxTickCount (one
        // past the last valid index, 0..MaxTickCount-1) - clamp it into the
        // final bucket instead of letting it create an extra one.
        const rawBucketIndex = Math.floor((tick["RelativeMSec"] - firstRelativeMSec) / bucketWidthMSec);
        const bucketIndex = Math.min(rawBucketIndex, MaxTickCount - 1);

        const existingAmount = amountByBucketIndex.get(bucketIndex);
        amountByBucketIndex.set(bucketIndex, (existingAmount === undefined ? 0 : existingAmount) + tick["AllocationAmount"]);
    }

    // Only non-empty buckets are emitted (matching the raw ticks list's own
    // convention of having no entry for quiet periods), sorted back into
    // RelativeMSec order since Map iteration order isn't guaranteed to be
    // numeric-ascending once indexes are inserted out of order.
    const sortedBucketIndexes = Array.from(amountByBucketIndex.keys());
    sortedBucketIndexes.sort((left, right) => left - right);

    const bucketedTicks: any[] = [];
    for (let sortedIndex = 0; sortedIndex < sortedBucketIndexes.length; ++sortedIndex) {
        const bucketIndex = sortedBucketIndexes[sortedIndex];
        bucketedTicks.push({
            RelativeMSec: firstRelativeMSec + (bucketIndex * bucketWidthMSec),
            AllocationAmount: amountByBucketIndex.get(bucketIndex)
        });
    }

    return bucketedTicks;
}
