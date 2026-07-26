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

        // Clickable - see snapshotGcStats.js's onTypeDrillDownClick. Every
        // row is drillable: typeDrillDown (AllocationJsonExporter.cs's
        // WriteTypeDrillDown) is a parallel array to topTypes, one entry
        // per row here, and every type in topTypes has at least one tick by
        // construction, so its typeDrillDown entry always has at least the
        // "<no stack captured>" placeholder even in the worst case.
        rows += `<tr class="typeRow" data-type-index="${index}"><td>${tdTypeName}</td><td>${tdTotalBytes}</td><td>${tdPercent}</td><td>${tdTickCount}</td><td>${tdSmallCount}</td><td>${tdLargeCount}</td><td>${tdPinnedCount}</td></tr>`;
    }

    const header = `<tr class="tableHeader"><th>Type Name</th><th>Total Bytes (mb)</th><th>% of Sampled</th><th>Tick Count</th><th>Small</th><th>Large</th><th>Pinned</th></tr>`;

    // allocationTypeTable (alongside the shared detailTable class) scopes the
    // wide/wrapping Type Name column CSS to just this table - other tables
    // sharing .detailTable (GC summary, generation breakdown) have short
    // first-column values (GC numbers) and shouldn't get that treatment.
    const tableHtml = `<div class="detailTable allocationTypeTable"><table>${header}${rows}</table></div>`;

    // Chart canvases live here (not built by client-side JS) so the tiles ->
    // charts -> table order is a single source of truth, not split across
    // GcSnapshotRenderer.ts/snapshotGcStats.js. snapshotGcStats.js just
    // injects this whole blob, then finds each canvas by id and renders
    // into it. The type-breakdown chart sits directly under the rate
    // chart - both read from allocationSummaryJson, no separate data blob.
    const chartHtml = `<div class="gcStats"><canvas id="allocationTimelineChart"></canvas></div>
        <div class="gcStats"><canvas id="allocationTypeTimelineChart"></canvas></div>`;

    // "Drill Down" (clicking a stacked-chart segment shows the resolved
    // call stacks behind that type+second - see media/drillDownStats.js)
    // is a second inner tab within the Heap Contents view, alongside this
    // "Charts" tab - a third, distinct navigational axis from the GC
    // view's own Charts/Detailed tabs (.tabButton/.tabPanel) and the
    // top-level GC/Heap Contents view switcher (.viewNavButton/.viewPanel),
    // so it gets its own class names to avoid colliding with either.
    // Its content is entirely client-rendered (which cell to show is only
    // known at click time), so - unlike every other table on this page -
    // there is no server-rendered HTML for it here, just the empty target
    // panel and a Drill Down/Back button pair the click handler reveals.
    const drillDownCells = allocationSummary["drillDown"] && allocationSummary["drillDown"]["cells"];
    const hasCellDrillDownData = drillDownCells && Object.keys(drillDownCells).length > 0;

    // typeDrillDown (whole-capture, every ranked type - see
    // AllocationJsonExporter.cs's WriteTypeDrillDown) is a second, separate
    // path into the same Drill Down tab, reached by clicking a row in
    // tableHtml below instead of a stacked-chart segment - either one being
    // present is enough to justify showing the tab/button.
    const typeDrillDown = allocationSummary["typeDrillDown"];
    var hasTypeDrillDownData = false;
    if (typeDrillDown) {
        for (var typeDrillDownIndex = 0; typeDrillDownIndex < typeDrillDown.length; ++typeDrillDownIndex) {
            if (typeDrillDown[typeDrillDownIndex] && typeDrillDown[typeDrillDownIndex].length > 0) {
                hasTypeDrillDownData = true;
                break;
            }
        }
    }

    const hasDrillDownData = hasCellDrillDownData || hasTypeDrillDownData;

    const heapContentsTabBar = `
        <div class="heapContentsTabBar">
            <button class="heapContentsTabButton active" data-heaptab="charts">Charts</button>
            ${hasDrillDownData ? `<button class="heapContentsTabButton" id="drillDownTabButton" data-heaptab="drilldown" style="display:none">Drill Down</button>
            <button class="backToChartsButton" id="backToChartsButton" style="display:none">&larr; Back to Charts (Backspace)</button>` : ``}
        </div>`;

    const chartsPanelHtml = `<div id="heapContents-tab-charts" class="heapContentsTabPanel active">${summaryTilesHtml}${chartHtml}${tableHtml}</div>`;
    const drillDownPanelHtml = hasDrillDownData ? `<div id="heapContents-tab-drilldown" class="heapContentsTabPanel"></div>` : ``;

    return `${heapContentsTabBar}${chartsPanelHtml}${drillDownPanelHtml}`;
}
