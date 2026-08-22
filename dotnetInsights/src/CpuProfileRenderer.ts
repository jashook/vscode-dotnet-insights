// Renders the "Profile" view's server-rendered half - summary tiles, an
// optional CPU sample timeline chart container, and the unified expandable
// Methods table (self/total sample counts plus inline caller-tree expansion,
// replacing the former separate Hot Methods + Drill Down tabs).
//
// nettrace-only: gcData["cpuProfile"] is absent for .gcinfo/XML input (see
// GcJsonExporter.cs) - callers should only invoke this when both
// sourceFormat === "nettrace" and cpuProfile.totalSampleCount > 0 (see
// GcSnapshotRenderer.ts).

import { renderRankedTableHeader } from './GcDetailTableRenderer';

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

// The coarse "where did the CPU go" breakdown, from
// nettraceParser/Cpu/CpuCategoryBuilder.cs. Rendered ABOVE the ranked method
// list on purpose: a list of 3,200 functions says what is hot, this says that
// garbage collection is 6% of the process, and the second question is the one
// somebody opening a profile usually has first.
//
// Both columns are shown because they answer different questions and neither
// alone is enough. "CPU %" is the sample's innermost frame and sums to 100%;
// "On stack %" counts a sample toward every category its stack passes through,
// so it does NOT sum to 100% - which the note under the table says out loud,
// because a column of percentages adding to 300% otherwise reads as a bug.
//
// Each row opens into the real call paths behind that bucket, not a summary of
// them - see the drill-down wiring in snapshotGcStats.js.
function renderCpuCategoryTable(categories: any): string {
    if (!categories) {
        return "";
    }

    const rows = (categories["rows"] || []).filter((row: any) => Number(row["selfSamples"]) > 0 || Number(row["onStackSamples"]) > 0);

    if (rows.length === 0) {
        return "";
    }

    rows.sort((left: any, right: any) => Number(right["selfPercent"]) - Number(left["selfPercent"]));

    var tableRows = "";

    for (var index = 0; index < rows.length; ++index) {
        const row = rows[index];

        // A bar drawn behind the name cell, so the shape of the breakdown is
        // readable without comparing numbers - the categories span two orders
        // of magnitude and the eye is much better at bars than at decimals.
        const barWidth = Math.max(0, Math.min(100, Number(row["selfPercent"])));

        tableRows +=
            `<tr class="typeRow cpuCategoryRow" data-cpu-category="${index}">` +
            `<td class="rowHideColumn"><span class="rowHideBtn" title="Hide this row">&#10005;</span></td>` +
            `<td class="cpuCategoryNameCell">` +
                `<span class="cpuCategoryBar" style="width:${barWidth.toFixed(2)}%"></span>` +
                `<span class="cpuCategoryName">&#9656; ${escapeHtmlForCpuProfile(row["name"])}</span>` +
            `</td>` +
            // Samples FIRST, then the two percentages, because the caller tree
            // each row opens into emits (samples, percent, percent) in that
            // order and both grids span the same box. With the percentage
            // first, every expanded row put a sample count under a "%" header
            // and a percentage under "Samples".
            `<td>${Number(row["selfSamples"]).toLocaleString()}</td>` +
            `<td>${Number(row["selfPercent"]).toFixed(2)}%</td>` +
            `<td>${Number(row["onStackPercent"]).toFixed(2)}%</td>` +
            `</tr>` +
            // The caller tree is built lazily on first expand (see
            // wireCpuCategoryTable). A category's tree can be thousands of
            // nodes and there are sixteen of them, so building all of them up
            // front would cost far more than anyone opens. data-cpu-category-lazy
            // carries the category's own id, not its row position - the rows
            // are re-sorted for display, so a positional index would pair a
            // bucket with another bucket's call paths.
            `<tr id="cpuCategoryDetail${index}" class="callPathsDetail" data-cpu-category-lazy="${row["id"]}">` +
            `<td colspan="5" class="callerTreeCell">` +
                `<div class="cpuCategoryDescription">${escapeHtmlForCpuProfile(row["description"])}</div>` +
            `</td>` +
            `</tr>`;
    }

    const columns: ReadonlyArray<[string, string]> = [
        ["Category", "text"],
        ["Samples", "number"],
        ["CPU %", "number"],
        ["On stack %", "number"],
    ];

    return `<div class="cpuCategorySection">` +
        renderUnresolvedModules(categories["unresolvedModules"]) +
        `<div class="threadingChartHint">Where the CPU went, by category. <b>CPU %</b> is the sample's innermost frame and sums to 100%. ` +
        `<b>On stack %</b> counts a sample toward every category anywhere in its stack, so those deliberately sum to more than 100% ` +
        `&mdash; that is what answers "how much time is spent under TLS at all". Click a row to open the call paths behind it.</div>` +
        `<div class="detailTable cpuHotMethodsTable"><table id="cpuCategoryTable">${renderRankedTableHeader(columns)}${tableRows}</table></div>` +
        `</div>`;
}

// The actionable form of the Unresolved bucket. "7.5% of this profile has no
// symbols" is a complaint; "4.2% of it is libcrypto.so.3" names the package
// whose debug symbols would fix most of it. Only shown when it is worth acting
// on - a fraction of a percent scattered across a dozen modules is noise, not
// a task.
function renderUnresolvedModules(unresolvedModules: any[]): string {
    if (!unresolvedModules || unresolvedModules.length === 0) {
        return "";
    }

    const worthReporting = unresolvedModules.filter((entry: any) => Number(entry["selfPercent"]) >= 0.25);

    if (worthReporting.length === 0) {
        return "";
    }

    var items = "";

    for (var index = 0; index < worthReporting.length; ++index) {
        items += `<li><b>${Number(worthReporting[index]["selfPercent"]).toFixed(2)}%</b> ` +
            `${escapeHtmlForCpuProfile(String(worthReporting[index]["module"]))}</li>`;
    }

    const total = unresolvedModules.reduce((sum: number, entry: any) => sum + Number(entry["selfPercent"]), 0);

    return `<div class="threadingNote cpuUnresolvedNote">` +
        `<b>${total.toFixed(2)}% of samples have no symbol</b>, concentrated in:` +
        `<ul class="cpuUnresolvedList">${items}</ul>` +
        `Symbols for .NET's own native modules come from Microsoft's symbol server automatically. Distribution libraries such as ` +
        `<b>libc</b> and <b>openssl</b> come from that distribution's debuginfod, which is added automatically when the capture ` +
        `identifies its distribution &mdash; if that server is unreachable, point <code>--symbol-path</code> at an extracted ` +
        `dbgsym tree instead. Runtime-generated stubs and the vDSO are counted separately; no symbols exist for those anywhere.` +
        `</div>`;
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
// Uses renderRankedTableHeader from GcDetailTableRenderer.ts - the same
// header shape (data-sort/sortIndicator plus the leading hide column) the
// per-GC detail table and the .gcdump ranked tables use, so
// setupDetailTableSortHandlers in media/rankedTable.js handles all of them
// without any table-specific branching.
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

    // renderRankedTableHeader is renderSortableTableHeader plus the hide
    // button's own bare, unsortable leading <th> - see that function for why
    // every ranked table in these webviews is built through it.
    const headerWithHideColumn = renderRankedTableHeader(columns);

    const categoryHtml = renderCpuCategoryTable(cpuProfile["categories"]);

    return `${categoryHtml}<div class="detailTable cpuHotMethodsTable"><table id="cpuMethodsTable">${headerWithHideColumn}${rows}</table></div>`;
}
