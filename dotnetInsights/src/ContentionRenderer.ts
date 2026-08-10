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

    // Two tabs, mirroring the Profile view's own Flame Graph/Methods bar
    // (same heapContentsTabBar/heapContentsTabPanel classes, so the existing
    // tab CSS and switching idiom apply unchanged). The Lock Timeline tab is
    // omitted entirely - not rendered empty - when the capture carries no
    // lock identity at all, which is the case for any pre-.NET-9 runtime
    // emitting V1 ContentionStart payloads (see ClrContentionStart.Decode).
    const lockTimeline = contentionSummary["lockTimeline"];
    const hasLockTimeline = !!(lockTimeline && lockTimeline["locks"] && lockTimeline["locks"].length > 0);

    const sitesPanelInner = `${summaryTilesHtml}${timelineHtml}${sitesHideStatusHtml}${sitesTableHtml}`;

    if (!hasLockTimeline) {
        return sitesPanelInner;
    }

    const tabBarHtml = `
        <div class="heapContentsTabBar">
            <button class="heapContentsTabButton active" data-contentiontab="sites">Sites</button>
            <button class="heapContentsTabButton" data-contentiontab="locktimeline">Lock Timeline</button>
        </div>`;

    const sitesPanelHtml = `<div id="contention-tab-sites" class="heapContentsTabPanel active">${sitesPanelInner}</div>`;
    const lockTimelinePanelHtml = `<div id="contention-tab-locktimeline" class="heapContentsTabPanel">${renderLockTimelinePanel(lockTimeline)}</div>`;

    return `${tabBarHtml}${sitesPanelHtml}${lockTimelinePanelHtml}`;
}

// Lock Timeline tab: a Gantt-style track per lock (y) against capture time
// (x), each bar an observed ownership window colored by the owning thread.
//
// The canvas itself is drawn entirely by media/lockTimeline.js rather than
// Chart.js - this codebase is pinned to Chart.js 2.x (see CLAUDE.md), which
// has no floating/range bar type at all (arbitrary [start,end] bars only
// arrived in Chart.js 3), and a hand-drawn canvas also handles the ~9k
// segments a real capture produces far faster than a chart library's own
// per-element model would.
//
// The explanatory note is deliberately part of the UI, not a comment: these
// bars are inferred from contention events, which the CLR only emits when a
// lock is actually contended, so a gap means "nobody was blocked here", NOT
// "the lock was free". Without saying so the view reads as a complete
// ownership history, which it cannot be.
function renderLockTimelinePanel(lockTimeline: any): string {
    const totalDistinctLockCount = lockTimeline["totalDistinctLockCount"];

    // The lock list and the thread dropdown are both populated by
    // media/lockTimeline.js rather than server-rendered here: the lock list
    // has to rebuild whenever the Top-N selector changes, and the thread
    // list is derived from the segments themselves. Server-rendering either
    // would mean emitting one markup blob and then immediately replacing it.
    return `
        <div class="lockTimelineNote">
            Each bar is a window where a thread held a lock while another thread was blocked on it.
            Because the runtime only reports contended locks, a gap means no thread was blocked - not that the lock was free.
            This capture had ${totalDistinctLockCount.toLocaleString()} contended ${totalDistinctLockCount === 1 ? "lock" : "locks"}; locks are ranked by total wait time.
        </div>
        <div class="lockTimelineToolbar">
            <label class="lockTimelineControl">Show
                <select id="lockTopNSelect">
                    <option value="10">Top 10</option>
                    <option value="25">Top 25</option>
                    <option value="40" selected>Top 40</option>
                    <option value="100">Top 100</option>
                    <option value="250">Top 250</option>
                    <option value="all">All (${totalDistinctLockCount.toLocaleString()})</option>
                </select>
            </label>
            <label class="lockTimelineControl">Thread
                <select id="lockThreadFilterSelect">
                    <option value="all">All threads</option>
                </select>
            </label>
            <button id="lockTimelineResetZoomBtn" class="resetZoomButton" style="display:none">Reset Zoom</button>
            <span id="lockTimelineZoomLabel" class="lockTimelineZoomLabel"></span>
            <span class="lockTimelineHint">Drag to zoom · double-click to reset · click a lock name for its stacks</span>
        </div>
        <div class="lockTimelineLayout">
            <div class="lockTimelineChartArea">
                <div id="lockTimelineContainer" class="lockTimelineContainer">
                    <canvas id="lockTimelineCanvas"></canvas>
                </div>
                <div id="lockTimelineTooltip" class="lockTimelineTooltip" style="display:none"></div>
            </div>
            <div class="lockFilterPanel">
                <div class="lockFilterHeader">
                    <span id="lockFilterHeaderLabel">Locks</span>
                    <span class="lockFilterButtons">
                        <button id="lockFilterAllBtn" class="resetZoomButton">All</button>
                        <button id="lockFilterNoneBtn" class="resetZoomButton">None</button>
                    </span>
                </div>
                <div id="lockFilterList" class="lockFilterList"></div>
            </div>
        </div>
        <div id="lockStackPanel" class="lockStackPanel" style="display:none">
            <div class="lockStackHeader">
                <span id="lockStackTitle"></span>
                <button id="lockStackCloseBtn" class="resetZoomButton">Close</button>
            </div>
            <div id="lockStackBody" class="lockStackBody"></div>
        </div>`;
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
