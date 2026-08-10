// Renders the "Contention" view's summary tiles, optional timeline chart, and
// ranked lock-contention sites table against the gcData["contentionSummary"]
// shape produced by nettraceParser/Contention/ContentionJsonExporter.cs.
//
// nettrace-only: gcData["contentionSummary"] is absent for .gcinfo/XML input.
// Callers should only invoke this when both sourceFormat === "nettrace" and
// contentionSummary.totalContentionCount > 0 (see GcSnapshotRenderer.ts).
//
// Each ranked site row expands inline to show its caller tree (same
// lazy-expand pattern as the CPU Methods table), populated lazily by
// buildInlineContentionSiteCallerTree in contentionDrillDownStats.js when a
// row is first expanded.

import { renderSortableTableHeader } from './GcDetailTableRenderer';

function escapeHtmlForContention(value: string): string {
    return String(value)
        .replace(/&/g, "&amp;")
        .replace(/</g, "&lt;")
        .replace(/>/g, "&gt;");
}

function formatSiteNameHtml(rawName: string): string {
    if (rawName === "<no stack captured>" || rawName.startsWith("<unresolved")) {
        return `<span class="unresolvedFrame">${escapeHtmlForContention(rawName)}</span>`;
    }

    const lastDotIndex = rawName.lastIndexOf(".");
    if (lastDotIndex === -1) {
        return `<span class="methodName">${escapeHtmlForContention(rawName)}</span>`;
    }

    const typePrefix = rawName.slice(0, lastDotIndex + 1);
    const methodName = rawName.slice(lastDotIndex + 1);
    return `<span class="methodTypePrefix">${escapeHtmlForContention(typePrefix)}</span><span class="methodName">${escapeHtmlForContention(methodName)}</span>`;
}

export function renderContentionView(contentionSummary: any): string {
    const totalCount = contentionSummary["totalContentionCount"];
    const totalWaitMSec = contentionSummary["totalContentionWaitMSec"];

    if (!totalCount) {
        return `<div class="detailTable"><p>No contention events to display.</p></div>`;
    }

    const topSites = contentionSummary["topSites"] || [];
    const hasTimeline = !!(contentionSummary["timeline"]);

    const avgWaitMSec = totalCount > 0 ? (totalWaitMSec / totalCount) : 0;

    // Ids on the three derived tiles let rebuildContentionSitesTable
    // (snapshotGcStats.js) rewrite them in place after a row is hidden -
    // Total Events itself never changes (hiding doesn't remove an event
    // from the capture), only Total/Avg Wait, which are recomputed against
    // the remaining visible sites.
    const summaryTilesHtml = `
        <div class="summaryGcDiv">
            <div class="total">
                <div>Lock Contention</div>
                <div>Total Events<span>${totalCount.toLocaleString()}</span></div>
                <div>Total Wait (ms)<span id="contentionTotalWaitTile">${totalWaitMSec.toFixed(1)}</span></div>
                <div>Avg Wait (ms)<span id="contentionAvgWaitTile">${avgWaitMSec.toFixed(3)}</span></div>
            </div>
        </div>`;

    const timelineHtml = hasTimeline ? `
        <div class="cpuTimelineSection">
            <div class="allocationZoomStatus" id="contentionTimelineZoomStatus" style="display:none">
                <span class="allocationZoomStatusLabel" id="contentionTimelineZoomLabel"></span>
                <button class="resetZoomButton" id="contentionTimelineResetZoomBtn">Reset Zoom (Backspace)</button>
            </div>
            <div class="cpuTimelineContainer"><canvas id="contentionTimeline"></canvas></div>
        </div>` : ``;

    // Hidden until at least one row is hidden (rebuildContentionSitesTable
    // in snapshotGcStats.js) - same allocationZoomStatus idiom as every
    // other hide-status bar on this page.
    const sitesHideStatusHtml = `
        <div class="allocationZoomStatus" id="contentionSitesHideStatus" style="display:none">
            <span class="allocationZoomStatusLabel" id="contentionSitesHideStatusLabel"></span>
            <button class="resetZoomButton" id="contentionSitesShowAllBtn">Show all</button>
        </div>`;

    const sitesTableHtml = renderTopSitesTable(contentionSummary);

    return `${summaryTilesHtml}${timelineHtml}${sitesHideStatusHtml}${sitesTableHtml}`;
}

// Ranked sites table: each row shows the contention site (leaf frame),
// contention count, total wait, average wait, and % of total wait. Each row
// is expandable inline to show the full caller tree, lazily populated by
// buildInlineContentionSiteCallerTree (contentionDrillDownStats.js).
function renderTopSitesTable(contentionSummary: any): string {
    const topSites = contentionSummary["topSites"];

    if (!topSites || topSites.length === 0) {
        return `<div class="detailTable"><p>No ranked sites to display.</p></div>`;
    }

    var rows = "";
    for (var index = 0; index < topSites.length; ++index) {
        const site = topSites[index];
        const siteName = site["SiteName"] || "";
        const contentionCount = site["ContentionCount"];
        const totalWaitMSec = site["TotalWaitMSec"];
        const averageWaitMSec = site["AverageWaitMSec"];
        const percentOfTotal = site["PercentOfTotalWait"];

        rows += `<tr class="typeRow contentionSiteRow" ` +
            `data-contention-site-index="${index}" ` +
            `data-contention-expandable="true" ` +
            `data-contention-target="contentionSiteDetail${index}">` +
            `<td class="rowHideColumn"><button class="rowHideBtn" type="button" title="Hide this row">&#10005;</button></td>` +
            `<td><span class="leafMethodToggle">&#9656;</span>${formatSiteNameHtml(siteName)}</td>` +
            `<td>${contentionCount.toLocaleString()}</td>` +
            `<td>${totalWaitMSec.toFixed(3)}</td>` +
            `<td>${averageWaitMSec.toFixed(3)}</td>` +
            `<td>${percentOfTotal.toFixed(2)}</td>` +
            `</tr>` +
            `<tr id="contentionSiteDetail${index}" class="callPathsDetail" data-contention-lazy="${index}">` +
            `<td colspan="6" class="callerTreeCell"></td>` +
            `</tr>`;
    }

    const columns: ReadonlyArray<[string, string]> = [
        ["Lock Acquisition Site", "text"],
        ["Count", "number"],
        ["Total Wait (ms)", "number"],
        ["Avg Wait (ms)", "number"],
        ["% of Wait", "number"],
    ];

    const header = renderSortableTableHeader(columns);
    const headerWithHideColumn = header.replace('<tr class="tableHeader">', '<tr class="tableHeader"><th class="rowHideColumn"></th>');

    return `<div class="detailTable cpuHotMethodsTable"><table id="contentionSitesTable">${headerWithHideColumn}${rows}</table></div>`;
}
