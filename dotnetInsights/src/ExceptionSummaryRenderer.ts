// Renders the "Exceptions" view's ranked-type table + summary tiles against
// the gcData["exceptionSummary"] shape produced by
// nettraceParser/Exceptions/ExceptionJsonExporter.cs - a full accounting of
// every CLR ExceptionThrown_V1 event in the capture (not sampled, unlike
// allocationSummary's AllocationTick-based sampling), ranked by throw count
// per exception type.
//
// nettrace-only: gcData["exceptionSummary"] is absent for .gcinfo/XML input
// (see GcJsonExporter.cs) - callers should only invoke this when both
// sourceFormat === "nettrace" and exceptionSummary.topTypes is non-empty
// (see GcSnapshotRenderer.ts). Structurally mirrors
// AllocationSummaryRenderer.ts's Charts/Drill Down inner-tab pattern, minus
// the type-timeline chart and the All/LOH toggle - a per-type folded
// throw-site stack tree is the whole point here, not a second view to
// switch between.
export function renderExceptionSummaryTable(exceptionSummary: any): string {
    const topTypes = exceptionSummary["topTypes"];

    if (topTypes === undefined || topTypes === null || topTypes.length === 0) {
        return `<div class="detailTable"><p>No exception events to display.</p></div>`;
    }

    const totalExceptionCount = exceptionSummary["totalExceptionCount"];
    const distinctTypeCount = exceptionSummary["distinctTypeCount"];

    const summaryTilesHtml = `
        <div class="summaryGcDiv">
            <div class="total">
                <div>Exceptions</div>
                <div>Total<span>${totalExceptionCount}</span></div>
                <div>Distinct Types<span>${distinctTypeCount}</span></div>
            </div>
        </div>`;

    var rows = "";
    for (var index = 0; index < topTypes.length; ++index) {
        const typeStats = topTypes[index];

        const tdTypeName = typeStats["TypeName"];
        const tdCount = typeStats["Count"];
        const tdPercent = Number(typeStats["PercentOfTotal"]).toFixed(2);
        const tdSampleMessage = typeStats["SampleMessage"] || "";

        // Clickable - see snapshotGcStats.js's onExceptionTypeDrillDownClick.
        // Every row is drillable: exceptionSummary["typeDrillDown"] is a
        // parallel array to topTypes, one entry per row here, and every
        // type in topTypes has at least one throw by construction, so its
        // typeDrillDown entry always has at least the "<no stack captured>"
        // placeholder even in the worst case.
        rows += `<tr class="typeRow exceptionTypeRow" data-exception-type-index="${index}"><td>${tdTypeName}</td><td>${tdCount}</td><td>${tdPercent}</td><td>${escapeHtml(tdSampleMessage)}</td></tr>`;
    }

    const header = `<tr class="tableHeader"><th>Exception Type</th><th>Count</th><th>% of Total</th><th>Sample Message</th></tr>`;

    // id lets a future zoom/filter feature find and rebuild this table's
    // rows the same way allocationTypeTable does today - not used yet
    // (exceptions have no time-bucketed chart to zoom against), kept for
    // consistency with that table's own convention.
    const tableHtml = `<div class="detailTable allocationTypeTable"><table id="exceptionTypeTable">${header}${rows}</table></div>`;

    const exceptionsTabBar = `
        <div class="heapContentsTabBar">
            <button class="heapContentsTabButton active" data-exceptiontab="types">Types</button>
            <button class="heapContentsTabButton" id="exceptionDrillDownTabButton" data-exceptiontab="drilldown" style="display:none">Drill Down</button>
            <button class="backToChartsButton" id="backToExceptionTypesButton" style="display:none">&larr; Back to Types (Backspace)</button>
        </div>`;

    const typesPanelHtml = `<div id="exceptions-tab-types" class="heapContentsTabPanel active">${summaryTilesHtml}${tableHtml}</div>`;
    const drillDownPanelHtml = `<div id="exceptions-tab-drilldown" class="heapContentsTabPanel"></div>`;

    return `${exceptionsTabBar}${typesPanelHtml}${drillDownPanelHtml}`;
}

function escapeHtml(value: string): string {
    return value
        .replace(/&/g, "&amp;")
        .replace(/</g, "&lt;")
        .replace(/>/g, "&gt;")
        .replace(/"/g, "&quot;");
}
