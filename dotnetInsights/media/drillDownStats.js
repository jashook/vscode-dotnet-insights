// Script run within the webview itself - renders the "Drill Down" tab's
// resolved-stacks table against one cell of
// gcData["allocationSummary"]["drillDown"]["cells"] (see
// AllocationJsonExporter.cs's AllocationSummaryBuilder.BuildDrillDown).
// Unlike every other table on this page, this has no server-rendered HTML
// to lazily inject - which cell to show is only known once a
// stacked-chart segment is actually clicked (see allocationStats.js's
// onClick handler on the type-timeline chart), so it's built here,
// entirely client-side, from data already present in allocationSummaryJson.

// Real .NET type/method names can legitimately contain HTML-significant
// characters (compiler-generated names like "Program.<Main>$" are common -
// literally seen in this project's own real capture fixture), so anything
// from drillDown data must be escaped before going into innerHTML.
function escapeHtmlForDrillDown(value) {
    return String(value)
        .replace(/&/g, "&amp;")
        .replace(/</g, "&lt;")
        .replace(/>/g, "&gt;");
}

// cellStacks: gcData["allocationSummary"]["drillDown"]["cells"]["{typeIndex}:{bucketIndex}"]
// (undefined/empty when that exact cell has no drillDown entry - e.g. every
// tick in it landed under "Other", which isn't drillable in the first
// place, so this shouldn't normally happen for a cell the chart actually
// let the user click).
function renderDrillDownTable(cellStacks, typeName, bucketLabel) {
    const heading = `<h3 class="detailTableHeading">${escapeHtmlForDrillDown(typeName)} &mdash; ${escapeHtmlForDrillDown(bucketLabel)}</h3>`;

    if (!cellStacks || cellStacks.length === 0) {
        return `${heading}<div class="detailTable"><p>No captured stacks for this selection.</p></div>`;
    }

    const mb = 1024 * 1024;
    var rows = "";

    for (var stackIndex = 0; stackIndex < cellStacks.length; ++stackIndex) {
        var stackEntry = cellStacks[stackIndex];
        var frames = stackEntry["frames"];

        var framesHtml = "";
        for (var frameIndex = 0; frameIndex < frames.length; ++frameIndex) {
            framesHtml += `<span class="drillDownFrame">${escapeHtmlForDrillDown(frames[frameIndex])}</span>`;
        }

        var totalBytesMb = (stackEntry["totalBytes"] / mb).toFixed(2);
        rows += `<tr><td>${framesHtml}</td><td>${totalBytesMb}</td><td>${stackEntry["tickCount"]}</td></tr>`;
    }

    const header = `<tr class="tableHeader"><th>Stack (leaf first)</th><th>Total Bytes (mb)</th><th>Tick Count</th></tr>`;

    return `${heading}<div class="detailTable drillDownTable"><table>${header}${rows}</table></div>`;
}
