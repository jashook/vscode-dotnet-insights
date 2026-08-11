// Renders the "Overview" view's total-event-count tile + a ranked table of
// every distinct (provider, event type) combination actually present in the
// capture, against the gcData["eventOverview"] shape produced by
// nettraceParser/Overview/EventOverviewBuilder.cs - full transparency into
// what's in the file, not scoped to only the GC/allocation/exception subset
// this extension otherwise decodes (Rundown events, EventPipe metadata
// events, and any unrecognized CLR event all show up here too, labeled
// "EventID {n}" when this tool doesn't have a friendlier name for them - see
// EventOverviewBuilder.cs's own header comment on why that's an honest
// fallback rather than a guess).
//
// nettrace-only: gcData["eventOverview"] is absent for .gcinfo/XML input
// (see GcJsonExporter.cs) - callers should only invoke this when
// sourceFormat === "nettrace" (see GcSnapshotRenderer.ts). Unlike
// allocationSummary/exceptionSummary, eventOverview is always meaningful
// whenever it's present - every real capture has *some* events - so there's
// no "topTypes.length === 0" placeholder branch here the way those two have.
//
// timeBreakdown (gcData["timeBreakdown"], see Overview/TimeBreakdownBuilder.cs)
// is a SEPARATE top-level JSON field, not nested under eventOverview - passed
// through as its own optional parameter so a missing/absent value (older
// cached nettraceParser binary predating this feature - see the stale-cache
// trap in CLAUDE.md) degrades to simply omitting the tile rather than
// throwing. GC%/Contending Locks% render whenever hasCaptureDuration is true;
// Idle%/CPU-Bound% render only when hasCpuSampleBreakdown is ALSO true (both
// gates independent per TimeBreakdownBuilder's own contract) - all four are
// shown together as one tile only once every value in it is real, since a
// partial "2 of 4 metrics" tile would be confusing, not a graceful
// degradation.
export function renderEventOverviewTable(eventOverview: any, timeBreakdown?: any): string {
    const totalEventCount = eventOverview["totalEventCount"];
    const eventTypes = eventOverview["eventTypes"] || [];

    const hasTimeBreakdown = !!(timeBreakdown && timeBreakdown["hasCaptureDuration"] && timeBreakdown["hasCpuSampleBreakdown"]);

    const timeBreakdownTileHtml = hasTimeBreakdown ? `
            <div class="total timeBreakdownTile">
                <div>Time Breakdown</div>
                <div>Contending Locks<span>${timeBreakdown["contentionPercent"].toFixed(1)}%</span></div>
                <div>GC<span>${timeBreakdown["gcPercent"].toFixed(1)}%</span></div>
                <div>Idle (est.)<span>${timeBreakdown["idlePercent"].toFixed(1)}%</span></div>
                <div>CPU Bound (est.)<span>${timeBreakdown["cpuBoundPercent"].toFixed(1)}%</span></div>
            </div>` : "";

    const summaryTilesHtml = `
        <div class="summaryGcDiv">
            <div class="total">
                <div>Events</div>
                <div>Total<span>${totalEventCount.toLocaleString()}</span></div>
                <div>Distinct Types<span>${eventTypes.length}</span></div>
            </div>${timeBreakdownTileHtml}
        </div>`;

    var rows = "";
    for (var index = 0; index < eventTypes.length; ++index) {
        const eventType = eventTypes[index];

        const tdProviderName = eventType["providerName"];
        const tdDisplayName = eventType["displayName"];
        const tdCount = eventType["count"];
        const tdPercent = totalEventCount === 0 ? "0.00" : ((tdCount * 100.0) / totalEventCount).toFixed(2);

        rows += `<tr><td>${escapeHtml(tdProviderName)}</td><td>${escapeHtml(tdDisplayName)}</td><td>${tdCount.toLocaleString()}</td><td>${tdPercent}</td></tr>`;
    }

    const header = `<tr class="tableHeader"><th>Provider</th><th>Event Type</th><th>Count</th><th>% of Total</th></tr>`;
    const tableHtml = `<div class="detailTable allocationTypeTable"><table id="eventOverviewTable">${header}${rows}</table></div>`;

    return `${summaryTilesHtml}${tableHtml}`;
}

function escapeHtml(value: string): string {
    return String(value)
        .replace(/&/g, "&amp;")
        .replace(/</g, "&lt;")
        .replace(/>/g, "&gt;")
        .replace(/"/g, "&quot;");
}
