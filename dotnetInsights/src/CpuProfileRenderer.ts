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
// Approximate CPU cost of one row, as a percentage of ONE core.
//
// samples x the capture's per-sample CPU quantum = milliseconds of CPU; over
// the window's own wall-clock duration that is cores busy, and x100 makes it
// the "% of a core" people actually quote. 100% is one core saturated; a row
// can legitimately exceed 100% (it ran on several cores at once), which is why
// this is not clamped.
//
// APPROXIMATE, and labelled so, for two reasons that have nothing to do with
// arithmetic: the per-sample period is itself recovered from the capture (see
// Cpu/SamplePeriodEstimator.cs), and sampling means a row's true cost is a
// distribution, not a point. Only ever rendered when the capture is CPU-time
// sampled - on a wall-clock capture there is no quantum and the column is
// absent entirely rather than filled with a thread-time figure.
export function formatCorePercent(samples: number, samplePeriodMSec: number, windowDurationMSec: number): string {
    if (!(samples >= 0) || !(samplePeriodMSec > 0) || !(windowDurationMSec > 0)) {
        return "\u2014";
    }

    const corePercent = (samples * samplePeriodMSec * 100.0) / windowDurationMSec;

    // Sub-0.01% rows would all render as "0.00%" and read as "free"; a
    // less-than marker keeps them honestly distinct from a true zero.
    if (corePercent > 0 && corePercent < 0.01) {
        return "<0.01%";
    }

    return corePercent.toFixed(2) + "%";
}

function renderCpuCategoryTable(categories: any, cpuTime: any): string {
    if (!categories) {
        return "";
    }

    // The core-% column only exists when the capture is CPU-time sampled.
    const hasCoreColumn = !!(cpuTime && cpuTime["hasCpuTime"]);
    const samplePeriodMSec = hasCoreColumn ? Number(cpuTime["samplePeriodMSec"]) : 0;
    const windowDurationMSec = hasCoreColumn ? Number(cpuTime["totalCpuMSec"]) / Number(cpuTime["averageCoresBusy"]) : 0;

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
            // data-cpu-category is the ROW POSITION (it pairs the row with its
            // own cpuCategoryDetail<N> element); data-cpu-category-id is the
            // category's own stable id, which is what anything looking the row
            // up in cpuProfile.categories.rows must use. The two differ
            // because this list is filtered and re-sorted just above, and
            // conflating them pairs a row with another category's numbers -
            // the same trap data-cpu-category-lazy already exists to avoid.
            // data-detail-target is what the SHARED sorter pairs rows by
            // (rankedTable.js's pairedDetailRowIdFor). Without it this table's
            // detail rows do not travel with their own row through a sort:
            // every data row gets re-appended and the detail rows are left
            // stranded at the top, so expanding a category renders its tree
            // somewhere else entirely and clicking again appears to do
            // nothing. This table paired by the cpuCategoryDetail<N> id
            // convention alone and so was the one table the sorter could not
            // see.
            `<tr class="typeRow cpuCategoryRow" data-cpu-category="${index}" data-cpu-category-id="${row["id"]}" data-detail-target="cpuCategoryDetail${index}">` +
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
            // Appended at the far RIGHT deliberately: several places index
            // these rows' cells by number, and adding a column anywhere else
            // silently shifts every one of them.
            (hasCoreColumn ? `<td>${formatCorePercent(Number(row["selfSamples"]), samplePeriodMSec, windowDurationMSec)}</td>` : ``) +
            `</tr>` +
            // The caller tree is built lazily on first expand (see
            // wireCpuCategoryTable). A category's tree can be thousands of
            // nodes and there are sixteen of them, so building all of them up
            // front would cost far more than anyone opens. data-cpu-category-lazy
            // carries the category's own id, not its row position - the rows
            // are re-sorted for display, so a positional index would pair a
            // bucket with another bucket's call paths.
            `<tr id="cpuCategoryDetail${index}" class="callPathsDetail" data-cpu-category-lazy="${row["id"]}">` +
            `<td colspan="${hasCoreColumn ? 6 : 5}" class="callerTreeCell">` +
                `<div class="cpuCategoryDescription">${escapeHtmlForCpuProfile(row["description"])}</div>` +
            `</td>` +
            `</tr>`;
    }

    const columns: ReadonlyArray<[string, string]> = hasCoreColumn ? [
        ["Category", "text"],
        ["Samples", "number"],
        ["CPU %", "number"],
        ["On stack %", "number"],
        ["\u2248 Core %", "number"],
    ] : [
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
        // Shown only while the timeline is zoomed (toggled by
        // rescopeCpuCategoryTable). The category NUMBERS do rescope - the
        // export carries per-bucket self/on-stack counts per category - but
        // each row's expandable call-path tree does not, so the note names
        // that one exception rather than disclaiming the whole table.
        // Same allocationZoomStatus idiom as the methods table's own hide bar.
        // Hidden until something is actually hidden.
        `<div class="allocationZoomStatus" id="cpuCategoryHideStatus" style="display:none">` +
            `<span class="allocationZoomStatusLabel" id="cpuCategoryHideStatusLabel"></span>` +
            `<button class="resetZoomButton" id="cpuCategoryShowAllBtn">Show all</button>` +
        `</div>` +
        `<div class="lockTimelineNote" id="cpuCategoryScopeNote" style="display:none">` +
        `Scoped to the zoomed range. The call paths behind each row are <strong>not</strong> &mdash; those are folded over the whole capture and have no per-bucket form.` +
        `</div>` +
        `<div class="detailTable cpuHotMethodsTable"><table id="cpuCategoryTable" data-has-core-percent="${hasCoreColumn ? 'true' : 'false'}">${renderRankedTableHeader(columns)}${tableRows}</table></div>` +
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
    // The cores-busy overlay only exists on a capture whose sampler is driven
    // by CPU time (see nettraceParser/Cpu/SamplePeriodEstimator.cs). When it
    // is present the note says how the number was derived - including that the
    // per-sample period was MEASURED from this capture rather than assumed -
    // because "3.64 cores" is the kind of figure people quote onward, and the
    // assumptions behind it should travel with it. When it is absent the note
    // says why, and names the capture mode that would provide it: a reader
    // looking for CPU utilisation should not have to guess whether the tool
    // cannot show it or this capture cannot support it.
    const cpuTime = cpuProfile["cpuTime"];
    const hasCoresSeries = !!(cpuTime && cpuTime["hasCpuTime"]);

    const cpuTimeNoteHtml = hasSampleTimeline ? (hasCoresSeries ? `
        <div class="lockTimelineNote">
            <strong>CPU cores busy</strong> (right axis) is real CPU time: this capture was sampled on perf <code>cpu-clock</code>, which only fires on a thread that actually holds a core.
            One sample is <strong>${(cpuTime["samplePeriodMSec"] as number).toFixed(3)} ms</strong> of CPU, measured from this capture's own inter-sample gaps rather than assumed,
            so a bucket's core count is its samples &times; that period &divide; the bucket's own duration.
            Over the whole capture: <strong>${((cpuTime["totalCpuMSec"] as number) / 1000).toFixed(1)} CPU-seconds</strong>, averaging
            <strong>${(cpuTime["averageCoresBusy"] as number).toFixed(2)} cores</strong>${cpuTime["processorCount"] ? ` of ${cpuTime["processorCount"]}` : ``}.
            The samples line beside it excludes known blocking primitives and any rows you hide; the cores line deliberately does not, because on a cpu-clock capture every sample is already on-CPU time.
        </div>` : `
        <div class="lockTimelineNote">
            This chart shows sample <em>counts</em>, not CPU utilisation. The .NET runtime's sampler is wall-clock per thread &mdash; it samples every thread whether or not it holds a core &mdash;
            so no multiple of these counts is CPU time. Capture with <code>dotnet-trace collect-linux</code> (and without <code>--profile</code>/<code>--providers</code>/<code>--clrevents</code>/<code>--perf-events</code>,
            which silently disable perf CPU sampling) to get a cores-busy series here.
        </div>`) : ``;

    const timelineHtml = hasSampleTimeline ? `
        ${cpuTimeNoteHtml}
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
// "The top N methods are X% of CPU" - the question a ranked list cannot answer
// about itself, and the one that decides whether optimising the top of it can
// matter at all.
//
// DELIBERATELY JUST THE NUMBERS, with no flat/peaked verdict attached. A
// threshold looked obvious and did not survive measurement: across real
// captures the top-10 share ran 9.8% on a collect-linux capture and 95.9% on
// two v5 ones - not because those services differ, but because v5 symbolicates
// only MANAGED leaves, collapsing thousands of native and kernel frames into a
// handful of rows. The same process reads "flat" or "peaked" purely by capture
// mode, so any verdict would be a statement about the profiler rather than the
// program. The reader gets the figures and their own judgement.
// Absolute CPU time for a row, in seconds. Same information as Self % and
// Core % - all three are selfSamples times a constant - but it is the form a
// finding is written down in ("this method costs 16.6 CPU-seconds"), which the
// other two are not.
export function formatCpuSeconds(samples: number, samplePeriodMSec: number): string {
    if (!(samples >= 0) || !(samplePeriodMSec > 0)) {
        return "\u2014";
    }

    const seconds = (samples * samplePeriodMSec) / 1000.0;

    if (seconds > 0 && seconds < 0.01) {
        return "<0.01";
    }

    return seconds.toFixed(2);
}

export function renderCoverageLine(hotMethods: any, totalSampleCount: number): string {
    if (!hotMethods || hotMethods.length === 0 || !(totalSampleCount > 0)) {
        return ``;
    }

    const marks = [10, 50, 200];
    const parts: string[] = [];
    var running = 0;
    var markIndex = 0;

    for (var index = 0; index < hotMethods.length && markIndex < marks.length; ++index) {
        running += Number(hotMethods[index]["selfSamples"]);

        if (index + 1 === marks[markIndex]) {
            parts.push(`top ${marks[markIndex]} = <strong>${(running * 100.0 / totalSampleCount).toFixed(1)}%</strong>`);
            ++markIndex;
        }
    }

    if (parts.length === 0) {
        return ``;
    }

    return `<div class="lockTimelineNote" id="cpuCoverageLine">` +
        `Share of the capture's <em>total</em> CPU covered by the rows above: ${parts.join(', ')}. ` +
        `The <em>Self %</em> and <em>Coverage %</em> columns are shares of the rows shown and sum to 100%; these figures are not, ` +
        `so a low one means the cost is spread far beyond this table.` +
        `</div>`;
}

function renderHotMethodsTable(cpuProfile: any): string {
    const hotMethods = cpuProfile["hotMethods"];
    const methodNames = cpuProfile["methodNames"];
    const totalSampleCount = cpuProfile["totalSampleCount"];

    // Same gate as the category table: no CPU-time quantum, no core column.
    const methodCpuTime = cpuProfile["cpuTime"];
    const hasMethodCoreColumn = !!(methodCpuTime && methodCpuTime["hasCpuTime"]);
    const methodSamplePeriodMSec = hasMethodCoreColumn ? Number(methodCpuTime["samplePeriodMSec"]) : 0;
    // The capture's own wall-clock span, recovered from the two figures the
    // export already publishes rather than added as a third that could drift
    // out of step with them.
    const methodWindowDurationMSec = hasMethodCoreColumn
        ? Number(methodCpuTime["totalCpuMSec"]) / Number(methodCpuTime["averageCoresBusy"])
        : 0;

    // Cells per row: hide + Method + Self%/Self Samples + Total%/Total Samples
    // + Coverage%, plus Core%/CPU(s) when the capture has a CPU-time quantum.
    // Must equal the header's column count + 1; the detail row's colspan reads
    // this rather than a literal, since two of the columns are conditional.
    const methodColumnCount = hasMethodCoreColumn ? 9 : 7;

    if (!hotMethods || hotMethods.length === 0) {
        return `<div class="detailTable"><p>No ranked methods to display.</p></div>`;
    }

    // Denominator for Self %/Total %: the ranked rows' own self samples, so
    // the visible Self % column sums to 100%. Total % must share it - it is
    // inclusive, so total >= self per row, and mixing bases would print rows
    // whose Self % exceeded their Total %.
    var rankedSelfSamples = 0;
    for (var sumIndex = 0; sumIndex < hotMethods.length; ++sumIndex) {
        rankedSelfSamples += Number(hotMethods[sumIndex]["selfSamples"]);
    }

    if (rankedSelfSamples <= 0) {
        rankedSelfSamples = 1;
    }

    var rows = "";
    var cumulativeSelfPercent = 0;
    for (var index = 0; index < hotMethods.length; ++index) {
        const method = hotMethods[index];
        const rawName = methodNames[method["frame"]];
        const selfSamples = method["selfSamples"];
        const totalSamples = method["totalSamples"];
        // Share of the ROWS SHOWN, not of the whole capture - see
        // rebuildHotMethodsTable's own comment. visibleSelfSamples is every
        // ranked row's self time, since nothing is hidden at first render.
        const selfPercent = (selfSamples * 100.0) / rankedSelfSamples;
        cumulativeSelfPercent += selfPercent;
        const totalPercent = (totalSamples * 100.0) / rankedSelfSamples;

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
            // Full precision, for Coverage % to accumulate - see
            // updateCoverageColumn. The visible cell is rounded to 2dp and
            // summing 200 of those drifts the running total.
            `data-self-percent="${selfPercent}" ` +
            `data-cpu-hotmethod-index="${index}" ` +
            `data-cpu-method-expandable="true" ` +
            `data-cpu-method-target="cpuMethodDetail${index}">` +
            `<td class="rowHideColumn"><button class="rowHideBtn" type="button" title="Hide this row">&#10005;</button></td>` +
            `<td><span class="leafMethodToggle">&#9656;</span>${formatMethodNameHtml(rawName)}</td>` +
            `<td>${selfPercent.toFixed(2)}</td>` +
            `<td>${selfSamples.toLocaleString()}</td>` +
            `<td>${totalPercent.toFixed(2)}</td>` +
            `<td>${totalSamples.toLocaleString()}</td>` +
            // Far right, so the existing cells[2..5] indices this table is
            // read by elsewhere stay put. Computed from SELF samples: self is
            // the column that partitions the capture's CPU, so it is the one
            // that can be turned into a share of a core without double
            // counting a method and its callees.
            (hasMethodCoreColumn ? `<td class="corePercentCell">${formatCorePercent(selfSamples, methodSamplePeriodMSec, methodWindowDurationMSec)}</td>` : ``) +
            // Absolute CPU time for this method's own frames. Carries no
            // information Self % does not - it is selfSamples x the capture's
            // per-sample quantum, the same linear rescale Core % is - but
            // seconds is the unit a finding gets written down in.
            (hasMethodCoreColumn ? `<td class="cpuSecondsCell">${formatCpuSeconds(selfSamples, methodSamplePeriodMSec)}</td>` : ``) +
            // Running total of Self % down the table AS ORDERED. A property of
            // the view, not of the row - which is why the column is
            // unsortable and is recomputed after every sort.
            `<td class="coveragePercentCell">${cumulativeSelfPercent.toFixed(2)}</td>` +
            `</tr>` +
            // callPathsDetail row starts hidden (CSS: display:none) and is
            // shown (class "expanded") when its method row is clicked.
            // data-cpu-method-lazy stores the method index so the content is
            // built lazily by buildInlineCpuMethodCallerTree on first expand.
            `<tr id="cpuMethodDetail${index}" class="callPathsDetail" data-cpu-method-lazy="${index}">` +
            // Derived from the column list rather than hand-counted - this
            // table's column count is now conditional in two places.
            `<td colspan="${methodColumnCount}" class="callerTreeCell"></td>` +
            `</tr>`;
    }

    const baseMethodColumns: ReadonlyArray<[string, string]> = [
        ["Method", "text"],
        ["Self %", "number"],
        ["Self Samples", "number"],
        ["Total %", "number"],
        ["Total Samples", "number"],
    ];

    const columns: ReadonlyArray<[string, string]> = (hasMethodCoreColumn
        ? baseMethodColumns.concat([["\u2248 Core %", "number"], ["CPU (s)", "number"]] as ReadonlyArray<[string, string]>)
        : baseMethodColumns)
        // "Coverage %", not "Cum %": in pprof and perf "cum" means INCLUSIVE
        // time, which is this table's own Total % column sitting three cells
        // to the left. Reusing the name for cumulative coverage next to the
        // real inclusive column misread exactly as you would expect.
        .concat([["Coverage %", "none"]] as ReadonlyArray<[string, string]>);

    // renderRankedTableHeader is renderSortableTableHeader plus the hide
    // button's own bare, unsortable leading <th> - see that function for why
    // every ranked table in these webviews is built through it.
    const headerWithHideColumn = renderRankedTableHeader(columns);

    const categoryHtml = renderCpuCategoryTable(cpuProfile["categories"], cpuProfile["cpuTime"]);

    return `${categoryHtml}${renderCoverageLine(hotMethods, totalSampleCount)}<div class="detailTable cpuHotMethodsTable"><table id="cpuMethodsTable" data-has-core-percent="${hasMethodCoreColumn ? 'true' : 'false'}">${headerWithHideColumn}${rows}</table></div>`;
}
