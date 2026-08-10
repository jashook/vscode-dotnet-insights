// Renders the "Profile" view's server-rendered half - summary tiles, an
// optional CPU sample timeline chart container, and the unified expandable
// Methods table (self/total sample counts plus inline caller-tree expansion,
// replacing the former separate Hot Methods + Drill Down tabs).
//
// nettrace-only: gcData["cpuProfile"] is absent for .gcinfo/XML input (see
// GcJsonExporter.cs) - callers should only invoke this when both
// sourceFormat === "nettrace" and cpuProfile.totalSampleCount > 0 (see
// GcSnapshotRenderer.ts).

import { renderSortableTableHeader } from './GcDetailTableRenderer';

// Real .NET type/method names can legitimately contain HTML-significant
// characters (compiler-generated names like "Program.<Main>$" are common) -
// anything from cpuProfile data must be escaped before going into innerHTML.
function escapeHtmlForCpuProfile(value: string): string {
    return String(value)
        .replace(/&/g, "&amp;")
        .replace(/</g, "&lt;")
        .replace(/>/g, "&gt;");
}

// Same split-at-last-dot presentation as drillDownStats.js's
// formatFrameHtml (muted type prefix, bold method name) - kept as its own
// copy here (server-side TypeScript vs. client-side JS) rather than shared,
// matching how drillDownStats.js/exceptionDrillDownStats.js already each
// carry their own copy instead of a shared module.
function formatMethodNameHtml(rawFrameName: string): string {
    if (rawFrameName === "<no stack captured>") {
        return `<span class="unresolvedFrame">${escapeHtmlForCpuProfile(rawFrameName)}</span>`;
    }

    const lastDotIndex = rawFrameName.lastIndexOf(".");
    if (lastDotIndex === -1) {
        return `<span class="methodName">${escapeHtmlForCpuProfile(rawFrameName)}</span>`;
    }

    const typePrefix = rawFrameName.slice(0, lastDotIndex + 1);
    const methodName = rawFrameName.slice(lastDotIndex + 1);
    return `<span class="methodTypePrefix">${escapeHtmlForCpuProfile(typePrefix)}</span><span class="methodName">${escapeHtmlForCpuProfile(methodName)}</span>`;
}

export function renderCpuProfileView(cpuProfile: any): string {
    const totalSampleCount = cpuProfile["totalSampleCount"];

    if (!totalSampleCount) {
        return `<div class="detailTable"><p>No CPU samples to display.</p></div>`;
    }

    const hotMethods = cpuProfile["hotMethods"] || [];
    const hasSampleTimeline = !!(cpuProfile["sampleTimeline"]);

    // Total/Ranked Methods carry ids so a row-hide toggle
    // (rebuildHotMethodsTable in snapshotGcStats.js) can rewrite just these
    // two numbers in place instead of re-templating the whole tile block.
    const summaryTilesHtml = `
        <div class="summaryGcDiv">
            <div class="total">
                <div>CPU Samples</div>
                <div>Total<span id="cpuMethodsTotalTile">${totalSampleCount.toLocaleString()}</span></div>
                <div>Ranked Methods<span id="cpuMethodsRankedTile">${hotMethods.length.toLocaleString()}</span></div>
            </div>
        </div>`;

    // Timeline chart section - canvas starts empty, snapshotGcStats.js
    // builds the Chart.js chart client-side the first time the Methods tab
    // is shown (same lazy-build discipline the flame graph uses for its own
    // container). Only emitted when the C# exporter included sampleTimeline
    // data (requires at least one sample with a valid RelativeMSec).
    const timelineHtml = hasSampleTimeline ? `
        <div class="cpuTimelineSection">
            <div class="allocationZoomStatus" id="cpuTimelineZoomStatus" style="display:none">
                <span class="allocationZoomStatusLabel" id="cpuTimelineZoomLabel"></span>
                <button class="resetZoomButton" id="cpuTimelineResetZoomBtn">Reset Zoom (Backspace)</button>
            </div>
            <div class="cpuTimelineContainer">
                <span class="chartZoomHint" id="cpuTimelineZoomHint">Drag to zoom</span>
                <canvas id="cpuProfileTimeline"></canvas>
            </div>
        </div>` : ``;

    const hotMethodsHtml = renderHotMethodsTable(cpuProfile);

    // Master Expand All/Collapse All, governing every method row's own
    // caller tree at once - sits between the timeline chart and the ranked
    // table itself, the same position Exceptions/Heap Contents put their
    // own Expand All/Collapse All pair (outside/above their drill-down
    // table, not inside a row) - see snapshotGcStats.js's
    // expandAllCpuMethodRows. Distinct from (and in addition to) the
    // per-method Expand All/Collapse All buttons inside each row's own
    // expanded caller tree (buildInlineCpuMethodCallerTree) - those are
    // scoped to one already-open method; these expand/collapse every row.
    // Hide IO-Bound Methods - bulk applies the same per-row hide mechanism
    // (rowHideBtn/cpuMethodHider) each ranked row already has individually,
    // to every row snapshotGcStats.js's isKnownIoBoundLeafMethodName
    // recognizes as a blocking I/O syscall/API (socket, file, pipe, raw
    // Interop+Sys reads/writes) - narrower than, and largely orthogonal to,
    // the automatic timeline-only wait-method heuristic (which is mostly
    // general thread synchronization - Monitor/semaphore/Sleep/Join - and
    // only otherwise touches the chart, never this table's own rows).
    const methodsExpandControlsHtml = `<div class="drillDownExpandControls">` +
        `<button class="drillDownExpandControlButton cpuMethodsExpandAllBtn" type="button">Expand All</button>` +
        `<button class="drillDownExpandControlButton cpuMethodsCollapseAllBtn" type="button">Collapse All</button>` +
        `<button class="drillDownExpandControlButton cpuMethodsHideIoBoundBtn" type="button">Hide IO-Bound Methods</button>` +
        `</div>`;

    // Hidden until at least one row is hidden (rebuildHotMethodsTable in
    // snapshotGcStats.js) - reuses allocationZoomStatus's exact look, same
    // idiom as cpuTimelineZoomStatus above, just a different trigger.
    const methodsHideStatusHtml = `
        <div class="allocationZoomStatus" id="cpuMethodsHideStatus" style="display:none">
            <span class="allocationZoomStatusLabel" id="cpuMethodsHideStatusLabel"></span>
            <button class="resetZoomButton" id="cpuMethodsShowAllBtn">Show all</button>
        </div>`;

    // Two tabs only - Flame Graph and Methods (the former separate Drill
    // Down tab is gone; caller trees are now expanded inline within the
    // Methods table itself, matching the allocation/exception drill-down
    // pattern). No back button needed since there's no tab navigation.
    const profileTabBar = `
        <div class="heapContentsTabBar">
            <button class="heapContentsTabButton active" data-profiletab="flame">Flame Graph</button>
            <button class="heapContentsTabButton" data-profiletab="hotmethods">Methods</button>
        </div>`;

    const flamePanelHtml = `
        <div id="profile-tab-flame" class="heapContentsTabPanel active">
            <div id="flameGraphToolbar" class="flameGraphToolbar">
                <button id="flameGraphResetZoomBtn" class="resetZoomButton" style="display:none">Reset Zoom</button>
                <span id="flameGraphBreadcrumb" class="flameGraphBreadcrumb"></span>
            </div>
            <div id="flameGraphContainer" class="flameGraphContainer"></div>
            <div id="flameGraphTooltip" class="flameGraphTooltip" style="display:none"></div>
        </div>`;

    const methodsPanelHtml = `<div id="profile-tab-hotmethods" class="heapContentsTabPanel">${summaryTilesHtml}${timelineHtml}${methodsExpandControlsHtml}${methodsHideStatusHtml}${hotMethodsHtml}</div>`;

    return `${profileTabBar}${flamePanelHtml}${methodsPanelHtml}`;
}

// Unified Methods table: ranked by selfSamples descending (already sorted
// server-side - see Cpu/CpuProfileJsonExporter.cs's WriteHotMethods), with
// each row expandable inline to show the caller tree for that method.
// Clicking a row's toggle expands a callPathsDetail row immediately below it,
// which is lazily populated by buildInlineCpuMethodCallerTree (see
// cpuDrillDownStats.js) when first expanded - same lazy-expand discipline
// as the Heap Contents and Exceptions drill-down rows. The separate "Drill
// Down" tab is gone: this is both the ranked list AND the caller viewer.
//
// Uses renderSortableTableHeader from GcDetailTableRenderer.ts - the same
// header shape (data-sort/sortIndicator) the per-GC detail table already uses,
// so setupDetailTableSortHandlers in snapshotGcStats.js handles both without
// any table-specific branching.
function renderHotMethodsTable(cpuProfile: any): string {
    const hotMethods = cpuProfile["hotMethods"];
    const methodNames = cpuProfile["methodNames"];
    const totalSampleCount = cpuProfile["totalSampleCount"];

    if (!hotMethods || hotMethods.length === 0) {
        return `<div class="detailTable"><p>No ranked methods to display.</p></div>`;
    }

    var rows = "";
    for (var index = 0; index < hotMethods.length; ++index) {
        const method = hotMethods[index];
        const rawName = methodNames[method["frame"]];
        const selfSamples = method["selfSamples"];
        const totalSamples = method["totalSamples"];
        const selfPercent = (selfSamples * 100.0) / totalSampleCount;
        const totalPercent = (totalSamples * 100.0) / totalSampleCount;

        // data-cpu-method-expandable / data-cpu-method-target pair mirrors
        // the data-cpu-expandable/data-cpu-target used by caller-tree interior
        // nodes (cpuDrillDownStats.js) - a different attribute name keeps
        // the two click-delegation paths distinct in wireProfileInnerTabs.
        // rowHideBtn is its own dedicated first cell (not sharing the
        // leafMethodToggle cell) so a click on it never also fires the
        // row's own expand toggle - see snapshotGcStats.js's click
        // delegation, which checks .rowHideBtn before
        // [data-cpu-method-expandable].
        rows += `<tr class="typeRow cpuHotMethodRow" ` +
            `data-cpu-hotmethod-index="${index}" ` +
            `data-cpu-method-expandable="true" ` +
            `data-cpu-method-target="cpuMethodDetail${index}">` +
            `<td class="rowHideColumn"><button class="rowHideBtn" type="button" title="Hide this row">&#10005;</button></td>` +
            `<td><span class="leafMethodToggle">&#9656;</span>${formatMethodNameHtml(rawName)}</td>` +
            `<td>${selfPercent.toFixed(2)}</td>` +
            `<td>${selfSamples.toLocaleString()}</td>` +
            `<td>${totalPercent.toFixed(2)}</td>` +
            `<td>${totalSamples.toLocaleString()}</td>` +
            `</tr>` +
            // callPathsDetail row starts hidden (CSS: display:none) and is
            // shown (class "expanded") when its method row is clicked.
            // data-cpu-method-lazy stores the method index so the content is
            // built lazily by buildInlineCpuMethodCallerTree on first expand.
            `<tr id="cpuMethodDetail${index}" class="callPathsDetail" data-cpu-method-lazy="${index}">` +
            `<td colspan="6" class="callerTreeCell"></td>` +
            `</tr>`;
    }

    const columns: ReadonlyArray<[string, string]> = [
        ["Method", "text"],
        ["Self %", "number"],
        ["Self Samples", "number"],
        ["Total %", "number"],
        ["Total Samples", "number"],
    ];

    const header = renderSortableTableHeader(columns);

    // The hide-button column gets its own bare <th> (no data-sort, no
    // label) prepended directly onto the sortable header row rather than
    // going through renderSortableTableHeader/its columns array, since it's
    // neither sortable nor labeled - matches the plain-<th> style
    // renderSortableTableHeader itself emits, just without a data-sort
    // attribute to opt out of sortDetailTableByColumn's click handling.
    const headerWithHideColumn = header.replace('<tr class="tableHeader">', '<tr class="tableHeader"><th class="rowHideColumn"></th>');

    return `<div class="detailTable cpuHotMethodsTable"><table id="cpuMethodsTable">${headerWithHideColumn}${rows}</table></div>`;
}
