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
export function renderEventOverviewTable(eventOverview: any): string {
    const totalEventCount = eventOverview["totalEventCount"];
    const eventTypes = eventOverview["eventTypes"] || [];

    const summaryTilesHtml = `
        <div class="summaryGcDiv">
            <div class="total">
                <div>Events</div>
                <div>Total<span>${totalEventCount.toLocaleString()}</span></div>
                <div>Distinct Types<span>${eventTypes.length}</span></div>
            </div>
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
