// Renders the "Heap Contents" view's ranked-type table + summary tiles
// against the gcData["allocationSummary"] shape produced by
// AllocationJsonExporter.cs's AllocationSummaryBuilder.Build - a bounded,
// pre-aggregated ranking of GC/AllocationTick samples by TypeName. This is
// a *sampled* view of what's allocating (roughly one tick per ~100KB
// allocated, attributed to the last object allocated at tick time) - not a
// live heap object graph, which this parser doesn't capture at all.
//
// nettrace-only: gcData["allocationSummary"] is absent for .gcinfo/XML
// input (see GcJsonExporter.cs) - callers should only invoke this when both
// sourceFormat === "nettrace" and allocationSummary.topTypes is non-empty
// (see GcSnapshotRenderer.ts).
export function renderAllocationSummaryTable(allocationSummary: any): string {
    const topTypes = allocationSummary["topTypes"];

    if (topTypes === undefined || topTypes === null || topTypes.length === 0) {
        return `<div class="detailTable"><p>No allocation events to display.</p></div>`;
    }

    // Matches renderGcDetailTable's mb divisor/labeling convention
    // (GcDetailTableRenderer.ts) so byte-scale numbers agree across tables
    // on the same page.
    const mb = 1024 * 1024;

    const totalSampledBytes = parseInt(allocationSummary["totalSampledBytes"]);
    const distinctTypeCount = allocationSummary["distinctTypeCount"];
    const totalTickCount = allocationSummary["totalTickCount"];

    const summaryTilesHtml = `
        <div class="summaryGcDiv">
            <div class="total">
                <div>Sampled Allocations</div>
                <div>Total<span>${(totalSampledBytes / mb).toFixed(2)} mb</span></div>
                <div>Ticks<span>${totalTickCount}</span></div>
                <div>Distinct Types<span>${distinctTypeCount}</span></div>
            </div>
        </div>`;

    var rows = "";
    for (var index = 0; index < topTypes.length; ++index) {
        const typeStats = topTypes[index];

        const totalBytes = parseInt(typeStats["TotalBytes"]);
        const percentOfSampled = totalSampledBytes === 0 ? 0 : (totalBytes * 100.0) / totalSampledBytes;

        const tdTypeName = typeStats["TypeName"];
        const tdTotalBytes = (totalBytes / mb).toFixed(2);
        const tdPercent = percentOfSampled.toFixed(2);
        const tdTickCount = typeStats["TickCount"];
        const tdSmallCount = typeStats["SmallCount"];
        const tdLargeCount = typeStats["LargeCount"];
        const tdPinnedCount = typeStats["PinnedCount"];

        rows += `<tr><td>${tdTypeName}</td><td>${tdTotalBytes}</td><td>${tdPercent}</td><td>${tdTickCount}</td><td>${tdSmallCount}</td><td>${tdLargeCount}</td><td>${tdPinnedCount}</td></tr>`;
    }

    const header = `<tr class="tableHeader"><th>Type Name</th><th>Total Bytes (mb)</th><th>% of Sampled</th><th>Tick Count</th><th>Small</th><th>Large</th><th>Pinned</th></tr>`;

    // allocationTypeTable (alongside the shared detailTable class) scopes the
    // wide/wrapping Type Name column CSS to just this table - other tables
    // sharing .detailTable (GC summary, generation breakdown) have short
    // first-column values (GC numbers) and shouldn't get that treatment.
    const tableHtml = `<div class="detailTable allocationTypeTable"><table>${header}${rows}</table></div>`;

    // Chart canvas lives here (not built by client-side JS) so the tiles ->
    // chart -> table order is a single source of truth, not split across
    // GcSnapshotRenderer.ts/snapshotGcStats.js. snapshotGcStats.js just
    // injects this whole blob, then finds #allocationTimelineChart by id and
    // renders into it.
    const chartHtml = `<div class="gcStats"><canvas id="allocationTimelineChart"></canvas></div>`;

    return `${summaryTilesHtml}${chartHtml}${tableHtml}`;
}
