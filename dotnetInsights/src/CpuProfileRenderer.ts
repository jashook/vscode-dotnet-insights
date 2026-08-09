// Renders the "Profile" view's server-rendered half - summary tiles plus the
// Hot Methods ranked table (self/total sample counts, PerfView's own "By
// Name" semantics) - against the gcData["cpuProfile"] shape produced by
// Cpu/CpuProfileJsonExporter.cs. The flame graph itself is NOT rendered
// here: it's built entirely client-side by media/flameGraph.js from the raw
// flameTree/methodNames data (see GcSnapshotRenderer.ts's cpuProfileJson
// script tag) - there's no server-side HTML for it, only the empty target
// container this file emits, matching how drillDownStats.js's caller trees
// are built client-side rather than server-rendered.
//
// nettrace-only: gcData["cpuProfile"] is absent for .gcinfo/XML input (see
// GcJsonExporter.cs) - callers should only invoke this when both
// sourceFormat === "nettrace" and cpuProfile.totalSampleCount > 0 (see
// GcSnapshotRenderer.ts).

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

    const summaryTilesHtml = `
        <div class="summaryGcDiv">
            <div class="total">
                <div>CPU Samples</div>
                <div>Total<span>${totalSampleCount.toLocaleString()}</span></div>
                <div>Ranked Methods<span>${hotMethods.length.toLocaleString()}</span></div>
            </div>
        </div>`;

    const hotMethodsHtml = renderHotMethodsTable(cpuProfile);

    // Same shared heapContentsTabButton/heapContentsTabPanel styling/JS
    // (see snapshotGcStats.js's switchProfileTab) that the Heap Contents
    // and Exceptions views already reuse for their own inner Charts/Drill
    // Down and Types/Drill Down tab bars - not duplicated CSS/JS, just a
    // third, differently-scoped (#view-profile, data-profiletab) user of
    // the same generic tab-bar shape. No "back" button here - unlike those
    // two views' summary-vs-drill-down relationship, Flame Graph and Hot
    // Methods are just two independent, equally-primary ways to look at
    // the same data, not a summary/detail pair.
    const profileTabBar = `
        <div class="heapContentsTabBar">
            <button class="heapContentsTabButton active" data-profiletab="flame">Flame Graph</button>
            <button class="heapContentsTabButton" data-profiletab="hotmethods">Hot Methods</button>
        </div>`;

    // flameGraphContainer/flameGraphTooltip start empty - media/flameGraph.js
    // builds the whole flame graph client-side from cpuProfileJson the
    // first time the Profile view's "Flame Graph" tab is shown (see
    // snapshotGcStats.js's view switcher), the same lazy-build-on-first-use
    // discipline drillDownStats.js already established for the Heap
    // Contents/Exceptions drill-down trees.
    const flamePanelHtml = `
        <div id="profile-tab-flame" class="heapContentsTabPanel active">
            <div id="flameGraphToolbar" class="flameGraphToolbar">
                <button id="flameGraphResetZoomBtn" class="resetZoomButton" style="display:none">Reset Zoom</button>
                <span id="flameGraphBreadcrumb" class="flameGraphBreadcrumb"></span>
            </div>
            <div id="flameGraphContainer" class="flameGraphContainer"></div>
            <div id="flameGraphTooltip" class="flameGraphTooltip" style="display:none"></div>
        </div>`;

    const hotMethodsPanelHtml = `<div id="profile-tab-hotmethods" class="heapContentsTabPanel">${summaryTilesHtml}${hotMethodsHtml}</div>`;

    return `${profileTabBar}${flamePanelHtml}${hotMethodsPanelHtml}`;
}

// Ranked by selfSamples descending (already sorted server-side - see
// Cpu/CpuProfileJsonExporter.cs's WriteHotMethods) - Method / Self % / Self
// Samples / Total % / Total Samples, sortable via the same click-to-sort
// infrastructure the Detailed tab's per-GC table already uses
// (snapshotGcStats.js's setupDetailTableSortHandlers, wired up once this
// panel is actually shown - see that file's view switcher).
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

        rows += `<tr>` +
            `<td>${formatMethodNameHtml(rawName)}</td>` +
            `<td>${selfPercent.toFixed(2)}</td>` +
            `<td>${selfSamples.toLocaleString()}</td>` +
            `<td>${totalPercent.toFixed(2)}</td>` +
            `<td>${totalSamples.toLocaleString()}</td>` +
            `</tr>`;
    }

    // data-sort/sortIndicator shape matches GcDetailTableRenderer.ts's own
    // header cells exactly - that's what setupDetailTableSortHandlers
    // expects to find (see snapshotGcStats.js).
    const header = `<tr class="tableHeader">` +
        `<th data-sort="text"><span class="thLabel">Method</span><span class="sortIndicator"></span></th>` +
        `<th data-sort="number"><span class="thLabel">Self %</span><span class="sortIndicator"></span></th>` +
        `<th data-sort="number"><span class="thLabel">Self Samples</span><span class="sortIndicator"></span></th>` +
        `<th data-sort="number"><span class="thLabel">Total %</span><span class="sortIndicator"></span></th>` +
        `<th data-sort="number"><span class="thLabel">Total Samples</span><span class="sortIndicator"></span></th>` +
        `</tr>`;

    return `<div class="detailTable cpuHotMethodsTable"><table>${header}${rows}</table></div>`;
}
