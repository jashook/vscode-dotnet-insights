// Renders the "Exceptions" view's summary tiles, optional timeline chart, and
// ranked-type table against the gcData["exceptionSummary"] shape produced by
// nettraceParser/Exceptions/ExceptionJsonExporter.cs - a full accounting of
// every CLR ExceptionThrown_V1 event in the capture (not sampled, unlike
// allocationSummary's AllocationTick-based sampling), ranked by throw count
// per exception type.
//
// nettrace-only: gcData["exceptionSummary"] is absent for .gcinfo/XML input
// (see GcJsonExporter.cs) - callers should only invoke this when both
// sourceFormat === "nettrace" and exceptionSummary.topTypes is non-empty
// (see GcSnapshotRenderer.ts).
//
// No tab bar/Drill Down tab anymore - this used to be a "Types" tab whose
// rows navigated to a separate "Drill Down" tab (mirroring
// AllocationSummaryRenderer.ts's Charts/Drill Down pattern). Unified into one
// table instead, matching ContentionRenderer.ts's own shape exactly: each
// ranked row expands inline to show its caller tree, lazily populated by
// buildInlineExceptionTypeCallerTree (exceptionDrillDownStats.js) when first
// expanded - there's no second alternate view here to justify a tab bar, the
// same reasoning Contention's own view already follows.

import { renderRankedTableHeader } from './GcDetailTableRenderer';

function escapeHtml(value: string): string {
    return String(value)
        .replace(/&/g, "&amp;")
        .replace(/</g, "&lt;")
        .replace(/>/g, "&gt;")
        .replace(/"/g, "&quot;");
}

export function renderExceptionSummaryTable(exceptionSummary: any): string {
    const topTypes = exceptionSummary["topTypes"];

    if (topTypes === undefined || topTypes === null || topTypes.length === 0) {
        return `<div class="detailTable"><p>No exception events to display.</p></div>`;
    }

    const totalExceptionCount = exceptionSummary["totalExceptionCount"];
    const distinctTypeCount = exceptionSummary["distinctTypeCount"];
    const hasTimeline = !!(exceptionSummary["timeline"]);

    // Total/Distinct Types tile ids let rebuildExceptionTypesTable
    // (snapshotGcStats.js) rewrite them after a row is hidden.
    const summaryTilesHtml = `
        <div class="summaryGcDiv">
            <div class="total">
                <div>Exceptions</div>
                <div>Total<span id="exceptionsTotalTile">${totalExceptionCount}</span></div>
                <div>Distinct Types<span id="exceptionsDistinctTypesTile">${distinctTypeCount}</span></div>
            </div>
        </div>`;

    // Same .cpuTimelineSection/.cpuTimelineContainer shape CPU Methods/
    // Contention already use verbatim (both classes are fully generic, no
    // new CSS needed) - gated on exceptionSummary.timeline being non-null,
    // same hasTimeline convention Contention's own view uses.
    const timelineHtml = hasTimeline ? `
        <div class="cpuTimelineSection">
            <div class="allocationZoomStatus" id="exceptionTimelineZoomStatus" style="display:none">
                <span class="allocationZoomStatusLabel" id="exceptionTimelineZoomLabel"></span>
                <button class="resetZoomButton" id="exceptionTimelineResetZoomBtn">Reset Zoom (Backspace)</button>
            </div>
            <div class="cpuTimelineContainer">
                <span class="chartZoomHint" id="exceptionTimelineZoomHint">Drag to zoom</span>
                <canvas id="exceptionTimeline"></canvas>
            </div>
        </div>` : ``;

    // Hidden until at least one row is hidden - same allocationZoomStatus
    // idiom as every other hide-status bar on this page.
    const hideStatusHtml = `
        <div class="allocationZoomStatus" id="exceptionTypesHideStatus" style="display:none">
            <span class="allocationZoomStatusLabel" id="exceptionTypesHideStatusLabel"></span>
            <button class="resetZoomButton" id="exceptionTypesShowAllBtn">Show all</button>
        </div>`;

    const tableHtml = renderExceptionTypesTable(exceptionSummary);

    return `${summaryTilesHtml}${timelineHtml}${hideStatusHtml}${tableHtml}`;
}

// Ranked types table: each row shows the exception type, throw count, %
// of total, and a sample message. Each row is expandable inline to show the
// full caller tree, lazily populated by buildInlineExceptionTypeCallerTree
// (exceptionDrillDownStats.js) - same shape as CpuProfileRenderer.ts's
// renderHotMethodsTable/ContentionRenderer.ts's renderTopSitesTable.
function renderExceptionTypesTable(exceptionSummary: any): string {
    const topTypes = exceptionSummary["topTypes"];

    if (!topTypes || topTypes.length === 0) {
        return `<div class="detailTable"><p>No ranked types to display.</p></div>`;
    }

    var rows = "";
    for (var index = 0; index < topTypes.length; ++index) {
        const typeStats = topTypes[index];

        const tdTypeName = typeStats["TypeName"];
        const tdCount = typeStats["Count"];
        const tdPercent = Number(typeStats["PercentOfTotal"]).toFixed(2);
        const tdSampleMessage = typeStats["SampleMessage"] || "";

        // Every row is drillable: exceptionSummary["typeDrillDown"] is a
        // parallel array to topTypes, one entry per row here, and every type
        // in topTypes has at least one throw by construction, so its
        // typeDrillDown entry always has at least the "<no stack captured>"
        // placeholder even in the worst case.
        rows += `<tr class="typeRow exceptionTypeRow" ` +
            `data-exception-type-index="${index}" ` +
            `data-exception-expandable="true" ` +
            `data-exception-target="exceptionTypeDetail${index}">` +
            `<td class="rowHideColumn"><button class="rowHideBtn" type="button" title="Hide this row">&#10005;</button></td>` +
            `<td><span class="leafMethodToggle">&#9656;</span>${escapeHtml(tdTypeName)}</td>` +
            `<td>${tdCount}</td>` +
            `<td>${tdPercent}</td>` +
            // Wrapped and clamped to two lines by CSS rather than truncated
            // here: the title keeps the whole message one hover away, which a
            // server-side substring would throw away outright.
            `<td class="exceptionSampleMessage" title="${escapeHtml(tdSampleMessage)}">` +
            `<span class="exceptionSampleMessageText">${escapeHtml(tdSampleMessage)}</span></td>` +
            `</tr>` +
            `<tr id="exceptionTypeDetail${index}" class="callPathsDetail" data-exception-type-lazy="${index}">` +
            `<td colspan="5" class="callerTreeCell"></td>` +
            `</tr>`;
    }

    const columns: ReadonlyArray<[string, string]> = [
        ["Exception Type", "text"],
        ["Count", "number"],
        ["% of Total", "number"],
        ["Sample Message", "text"],
    ];

    // renderRankedTableHeader is renderSortableTableHeader plus the hide
    // button's own bare, unsortable leading <th> - see that function.
    const headerWithHideColumn = renderRankedTableHeader(columns);

    return `<div class="detailTable cpuHotMethodsTable"><table id="exceptionTypeTable">${headerWithHideColumn}${rows}</table></div>`;
}
