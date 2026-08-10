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
//
// allocationSummary["loh"] (if present and non-empty) mirrors
// totalSampledBytes/topTypes/typeTimeline/drillDown/typeDrillDown exactly,
// scoped to AllocationKind.Large ticks only (see AllocationJsonExporter.cs's
// WriteTypeBreakdown) - rendered as a second, LOH-only variant of the same
// tiles/chart/table, toggled client-side (see snapshotGcStats.js) rather
// than requiring a separate capture or a different chart type. The
// allocation-rate line chart (raw ticks, no per-type/kind breakdown) is
// unaffected by this toggle - it's rendered once, above it, always
// unfiltered.
// Thousands-separated, 2-decimal mb figure (e.g. "12,345.67") - a busy
// capture's sampled/type totals can run into the thousands of mb, where a
// bare toFixed(2) result is hard to scan at a glance.
function formatMb(mbValue: number): string {
    return mbValue.toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 });
}

export function renderAllocationSummaryTable(allocationSummary: any): string {
    const topTypes = allocationSummary["topTypes"];

    if (topTypes === undefined || topTypes === null || topTypes.length === 0) {
        return `<div class="detailTable"><p>No allocation events to display.</p></div>`;
    }

    const lohSummary = allocationSummary["loh"];
    const hasLohData = lohSummary && lohSummary["topTypes"] && lohSummary["topTypes"].length > 0;

    const allPanelHtml = renderTypeBreakdownPanel(allocationSummary, "all", /*isActive*/ true, /*includeToggleWrapper*/ hasLohData);

    // Chart canvas for the allocation-rate line chart lives here (not built
    // by client-side JS) so the tiles -> charts -> table order is a single
    // source of truth, not split across GcSnapshotRenderer.ts/
    // snapshotGcStats.js. It sits outside the All/LOH toggle below since it
    // has no per-type or per-kind breakdown to filter - it's the same chart
    // regardless of which view is selected.
    const rateChartHtml = `<div class="gcStats"><canvas id="allocationTimelineChart"></canvas></div>`;

    // Hidden until a zoom is actually applied - see snapshotGcStats.js's
    // updateZoomStatusUi/renderHeapContentsCharts (chartZoomHelper.js drives
    // the drag-to-zoom interaction itself; this is just the status/reset
    // affordance for people who don't know or want to use Backspace).
    const zoomStatusHtml = `
        <div id="allocationZoomStatus" class="allocationZoomStatus" style="display:none">
            <span class="allocationZoomStatusLabel"></span>
            <button id="resetZoomButton" class="resetZoomButton">Reset Zoom</button>
        </div>`;

    var toggleAndPanelsHtml: string;
    if (hasLohData) {
        const lohPanelHtml = renderTypeBreakdownPanel(lohSummary, "loh", /*isActive*/ false, /*includeToggleWrapper*/ true);
        const toggleHtml = `
            <div class="allocationViewToggle">
                <button class="allocationViewButton active" data-allocview="all">All Types</button>
                <button class="allocationViewButton" data-allocview="loh">LOH Only</button>
            </div>`;
        toggleAndPanelsHtml = `${toggleHtml}${allPanelHtml}${lohPanelHtml}`;
    } else {
        toggleAndPanelsHtml = allPanelHtml;
    }

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
    // Checked across both the "all" and "loh" scopes - either one having
    // drillable data is enough to show the tab/button, since the toggle
    // can switch to whichever scope actually has it.
    const hasDrillDownData = hasAnyDrillDownData(allocationSummary) || (hasLohData && hasAnyDrillDownData(lohSummary));

    const heapContentsTabBar = `
        <div class="heapContentsTabBar">
            <button class="heapContentsTabButton active" data-heaptab="charts">Charts</button>
            ${hasDrillDownData ? `<button class="heapContentsTabButton" id="drillDownTabButton" data-heaptab="drilldown" style="display:none">Drill Down</button>
            <button class="backToChartsButton" id="backToChartsButton" style="display:none">&larr; Back to Charts (Backspace)</button>` : ``}
        </div>`;

    const chartsPanelHtml = `<div id="heapContents-tab-charts" class="heapContentsTabPanel active">${zoomStatusHtml}${rateChartHtml}${toggleAndPanelsHtml}</div>`;
    const drillDownPanelHtml = hasDrillDownData ? `<div id="heapContents-tab-drilldown" class="heapContentsTabPanel"></div>` : ``;

    return `${heapContentsTabBar}${chartsPanelHtml}${drillDownPanelHtml}`;
}

function hasAnyDrillDownData(summary: any): boolean {
    const drillDownCells = summary["drillDown"] && summary["drillDown"]["cells"];
    if (drillDownCells && Object.keys(drillDownCells).length > 0) {
        return true;
    }

    const typeDrillDown = summary["typeDrillDown"];
    if (typeDrillDown) {
        for (var typeDrillDownIndex = 0; typeDrillDownIndex < typeDrillDown.length; ++typeDrillDownIndex) {
            var typeDrillDownEntry = typeDrillDown[typeDrillDownIndex];
            if (typeDrillDownEntry && typeDrillDownEntry["stacks"] && typeDrillDownEntry["stacks"].length > 0) {
                return true;
            }
        }
    }

    return false;
}

// Summary tiles + the stacked type-timeline chart + the ranked types table,
// for one scope ("all" or "loh") - shared by both so they're pixel-for-pixel
// the same layout, just against different data. scope is suffixed onto
// every id/data-attribute a click handler needs to disambiguate which
// scope was interacted with (see snapshotGcStats.js's onDrillDownSegmentClick/
// onTypeDrillDownClick).
function renderTypeBreakdownPanel(summary: any, scope: string, isActive: boolean, includeToggleWrapper: boolean): string {
    const topTypes = summary["topTypes"];

    // Matches renderGcDetailTable's mb divisor/labeling convention
    // (GcDetailTableRenderer.ts) so byte-scale numbers agree across tables
    // on the same page.
    const mb = 1024 * 1024;

    const totalSampledBytes = parseInt(summary["totalSampledBytes"]);
    const distinctTypeCount = summary["distinctTypeCount"];
    const totalTickCount = summary["totalTickCount"];

    // Total/Distinct Types tile ids let a row-hide toggle
    // (updateOneRankedTypesTable in snapshotGcStats.js) rewrite these two
    // numbers after hiding a type - Ticks is left unchanged (a tick count
    // isn't a "share of the total" figure the way bytes/distinct-type-count
    // are, so hiding a type shouldn't change it).
    const summaryTilesHtml = `
        <div class="summaryGcDiv">
            <div class="total">
                <div>Sampled Allocations${scope === "loh" ? " (LOH only)" : ""}</div>
                <div>Total<span id="allocationTotalTile-${scope}">${formatMb(totalSampledBytes / mb)} mb</span></div>
                <div>Ticks<span>${totalTickCount}</span></div>
                <div>Distinct Types<span id="allocationDistinctTypesTile-${scope}">${distinctTypeCount}</span></div>
            </div>
        </div>`;

    var rows = "";
    for (var index = 0; index < topTypes.length; ++index) {
        const typeStats = topTypes[index];

        const totalBytes = parseInt(typeStats["TotalBytes"]);
        const percentOfSampled = totalSampledBytes === 0 ? 0 : (totalBytes * 100.0) / totalSampledBytes;

        const tdTypeName = typeStats["TypeName"];
        const tdTotalBytes = formatMb(totalBytes / mb);
        const tdPercent = percentOfSampled.toFixed(2);
        const tdTickCount = typeStats["TickCount"];
        const tdSmallCount = typeStats["SmallCount"];
        const tdLargeCount = typeStats["LargeCount"];
        const tdPinnedCount = typeStats["PinnedCount"];

        // Clickable - see snapshotGcStats.js's onTypeDrillDownClick.
        // data-scope disambiguates which summary (allocationSummaryJson vs
        // allocationSummaryJson.loh) this row's typeDrillDown entry lives
        // in. Every row is drillable: typeDrillDown
        // (AllocationJsonExporter.cs's WriteTypeDrillDown) is a parallel
        // array to topTypes, one entry per row here, and every type in
        // topTypes has at least one tick by construction, so its
        // typeDrillDown entry always has at least the "<no stack
        // captured>" placeholder even in the worst case.
        // ticksOnlyColumn marks the four columns snapshotGcStats.js's
        // updateRankedTypesTables hides while a chart zoom is applied - the
        // export has no per-time-bucket breakdown for Tick/Small/Large/
        // Pinned counts (only Total Bytes, via typeTimeline.buckets), so
        // there's no accurate zoomed-range figure to show there; hiding
        // avoids showing stale whole-capture numbers next to a freshly
        // zoomed Bytes/% figure in the same row.
        // rowHideBtn is its own dedicated first cell rather than reusing
        // the Type Name cell - this whole row is a click-navigate target
        // (onTypeDrillDownClick, wired on .typeRow), so a hide button
        // sharing that cell would fire both a hide AND a drill-down
        // navigation on the same click. snapshotGcStats.js's delegated
        // click handler checks .rowHideBtn (with stopPropagation) before
        // its .typeRow fallthrough.
        rows += `<tr class="typeRow" data-type-index="${index}" data-scope="${scope}"><td class="rowHideColumn"><button class="rowHideBtn" type="button" title="Hide this row">&#10005;</button></td><td>${tdTypeName}</td><td>${tdTotalBytes}</td><td>${tdPercent}</td><td class="ticksOnlyColumn">${tdTickCount}</td><td class="ticksOnlyColumn">${tdSmallCount}</td><td class="ticksOnlyColumn">${tdLargeCount}</td><td class="ticksOnlyColumn">${tdPinnedCount}</td></tr>`;
    }

    const header = `<tr class="tableHeader"><th class="rowHideColumn"></th><th>Type Name</th><th>Total Bytes (mb)</th><th>% of Sampled</th><th class="ticksOnlyColumn">Tick Count</th><th class="ticksOnlyColumn">Small</th><th class="ticksOnlyColumn">Large</th><th class="ticksOnlyColumn">Pinned</th></tr>`;

    // id lets snapshotGcStats.js's updateRankedTypesTables find and rebuild
    // this table's rows on every zoom change, regardless of whether the
    // All/LOH toggle wrapper (and its id="allocView-${scope}") is present -
    // that wrapper is only rendered when LOH data exists (includeToggleWrapper
    // below), so it isn't a reliable selector for the single-scope case.
    // allocationTypeTable (alongside the shared detailTable class) scopes the
    // wide/wrapping Type Name column CSS to just this table - other tables
    // sharing .detailTable (GC summary, generation breakdown) have short
    // first-column values (GC numbers) and shouldn't get that treatment.
    const tableHtml = `<div class="detailTable allocationTypeTable"><table id="allocationTypeTable-${scope}">${header}${rows}</table></div>`;

    // Hidden until at least one row in this scope's table is hidden - same
    // allocationZoomStatus idiom as every other hide-status bar on this
    // page, scoped per "all"/"loh" the same way the table/tiles above are.
    const hideStatusHtml = `
        <div class="allocationZoomStatus" id="allocationTypeHideStatus-${scope}" style="display:none">
            <span class="allocationZoomStatusLabel" id="allocationTypeHideStatusLabel-${scope}"></span>
            <button class="resetZoomButton" data-alloc-showall-scope="${scope}">Show all</button>
        </div>`;

    // snapshotGcStats.js finds this canvas by its scoped id and renders into
    // it - both the "all" and "loh" charts are created once, up front (not
    // lazily on toggle), so switching the All Types/LOH Only buttons is a
    // pure CSS show/hide with no chart destroy/recreate involved.
    const chartHtml = `<div class="gcStats"><canvas id="allocationTypeTimelineChart-${scope}"></canvas></div>`;

    const innerHtml = `${summaryTilesHtml}${chartHtml}${hideStatusHtml}${tableHtml}`;

    if (!includeToggleWrapper) {
        return innerHtml;
    }

    return `<div id="allocView-${scope}" class="allocationViewPanel${isActive ? " active" : ""}">${innerHtml}</div>`;
}
