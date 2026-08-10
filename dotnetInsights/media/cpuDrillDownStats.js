// Script run within the webview itself - builds inline caller-tree expansions
// for the Profile view's unified Methods table (each hot method row expands
// to show who called it, replacing the former separate Drill Down tab).
// Entry point for callers: buildInlineCpuMethodCallerTree (called from
// snapshotGcStats.js's wireProfileInnerTabs when a method row is first
// expanded) and buildLazyCpuDrillDownSubtree (called lazily for each
// interior caller node as it's expanded deeper into the tree).
//
// Deliberate near-total mirror of exceptionDrillDownStats.js's lazy-expand
// caller-tree renderer (see that file's own header comment, and
// drillDownStats.js's before it, for the full rationale) - kept as its own
// parallel file with its own names rather than sharing globals, not because
// the underlying tree algorithm differs in any way. One real shape
// difference from exceptionDrillDownStats.js: each
// hotMethodDrillDown[methodIndex] entry IS the top-level row already (the
// hot method itself, "frame" = that method's own frame id) rather than a
// hidden container whose children are the top-level rows - CpuProfileJsonExporter's
// BuildHotMethodCallerTrees only ever has one root per hot method, unlike
// exceptions' typeDrillDown which groups multiple distinct throw sites under
// one type.
function escapeHtmlForCpuDrillDown(value) {
    return String(value)
        .replace(/&/g, "&amp;")
        .replace(/</g, "&lt;")
        .replace(/>/g, "&gt;");
}

// Same convention as drillDownStats.js's formatFrameHtml - see that
// function's own comment.
function formatCpuFrameHtml(rawFrameName) {
    if (rawFrameName === "<no stack captured>" || rawFrameName.indexOf("<unresolved") === 0) {
        return `<span class="unresolvedFrame">${escapeHtmlForCpuDrillDown(rawFrameName)}</span>`;
    }

    var lastDotIndex = rawFrameName.lastIndexOf(".");
    if (lastDotIndex === -1) {
        return `<span class="methodName">${escapeHtmlForCpuDrillDown(rawFrameName)}</span>`;
    }

    var typePrefix = rawFrameName.slice(0, lastDotIndex + 1);
    var methodName = rawFrameName.slice(lastDotIndex + 1);
    return `<span class="methodTypePrefix">${escapeHtmlForCpuDrillDown(typePrefix)}</span><span class="methodName">${escapeHtmlForCpuDrillDown(methodName)}</span>`;
}

// "% of Method" - this row's share of the ROOT hot method's own total
// samples, held constant for its entire chain - same flame-graph-style
// reasoning as exceptionDrillDownStats.js's formatExceptionPercentOfSelf
// ("% of Site").
function formatCpuPercentOfMethod(rowSamples, methodTotalSamples) {
    if (!(methodTotalSamples > 0)) {
        return "";
    }

    var percentage = (rowSamples / methodTotalSamples) * 100;
    return `${percentage.toFixed(1)}%`;
}

// "% of Total" - fixed denominator (the whole capture's totalSampleCount) at
// every row and every depth, same as exceptionDrillDownStats.js's
// formatExceptionPercentOfTotal.
function formatCpuPercentOfTotal(rowSamples, grandTotalSamples) {
    if (!(grandTotalSamples > 0)) {
        return "";
    }

    var percentage = (rowSamples / grandTotalSamples) * 100;
    return `${percentage.toFixed(2)}%`;
}

// One fewer column than drillDownStats.js's CALLER_TREE_COLGROUP (no
// separate bytes/ticks column - "samples" is the one metric here, already
// its own column) - same shape as EXCEPTION_CALLER_TREE_COLGROUP.
const CPU_CALLER_TREE_COLGROUP = `<colgroup><col><col class="bytesColumn"><col class="percentColumn"><col class="percentColumn"></colgroup>`;

var cpuCallerRowIdCounter = 0;

// rowId -> { node, depth, grandTotalSamples, branchClass, methodTotalSamples } -
// same role as exceptionDrillDownStats.js's pendingExceptionLazySubtrees,
// scoped separately so a Profile drill-down click never collides with an
// Exceptions or Heap Contents drill-down click.
var pendingCpuLazySubtrees = new Map();

// cpuProfile["methodNames"] for the currently active profile session - a
// node's "frame" field is an integer index into it, same convention as
// exceptionDrillDownStats.js's currentExceptionMethodNames. Set once per
// profile view via initCpuDrillDownMethodNames.
var currentCpuMethodNames = null;

// Renders exactly one tree node as its own row - never recurses into its
// own children (see buildLazyCpuDrillDownSubtree for that, deferred until
// the row is actually expanded). Mirrors exceptionDrillDownStats.js's
// renderExceptionTreeRow, against "totalSamples" instead of "count".
function renderCpuTreeRow(rowId, roleLabelHtml, frameHtml, indentAttr, node, percentDenominatorSamples, grandTotalSamples, branchClass, methodTotalSamples) {
    var children = node["children"] || [];
    var hasChildren = children.length > 0;

    var toggleHtml = hasChildren
        ? `<span class="leafMethodToggle">&#9656;</span>`
        : `<span class="leafMethodToggle leafMethodToggleEmpty"></span>`;

    var pathCountSuffix = node["distinctStackCount"] > 1
        ? ` <span class="pathCount">(${node["distinctStackCount"].toLocaleString()} call paths)</span>`
        : ``;

    var rowHtml = `<tr class="${roleLabelHtml.rowClass} ${branchClass}"${hasChildren ? ` data-cpu-expandable="true" data-cpu-target="${rowId}"` : ``}>` +
        `<td${indentAttr}>${toggleHtml}${roleLabelHtml.html}${frameHtml}${pathCountSuffix}</td>` +
        `<td>${node["totalSamples"].toLocaleString()}</td>` +
        `<td>${formatCpuPercentOfMethod(node["totalSamples"], percentDenominatorSamples)}</td>` +
        `<td>${formatCpuPercentOfTotal(node["totalSamples"], grandTotalSamples)}</td>` +
        `</tr>`;

    if (!hasChildren) {
        return rowHtml;
    }

    pendingCpuLazySubtrees.set(rowId, { node: node, depth: 0, grandTotalSamples: grandTotalSamples, branchClass: branchClass, methodTotalSamples: methodTotalSamples });

    return rowHtml + `<tr id="${rowId}" class="callPathsDetail" data-cpu-lazy="true"><td colspan="4" class="callerTreeCell"></td></tr>`;
}

const CPU_CALLED_BY_ROLE = { rowClass: "callerRow", html: `` };

// Same values as drillDownStats.js's CALLER_INDENT_EM_PER_LEVEL/
// CALLER_INDENT_MAX_EM - see that file's own comment.
const CPU_CALLER_INDENT_EM_PER_LEVEL = 0.85;
const CPU_CALLER_INDENT_MAX_EM = 17;

// Renders one caller frame (a tree node at depth >= 1) as its own row -
// mirrors exceptionDrillDownStats.js's renderExceptionCallerRow.
function renderCpuCallerRow(node, depth, percentDenominatorSamples, grandTotalSamples, branchClass, methodTotalSamples) {
    var children = node["children"] || [];
    var rowId = children.length > 0 ? `cpuDrillDownCaller${++cpuCallerRowIdCounter}` : null;
    var uncappedIndentEm = (depth + 1) * CPU_CALLER_INDENT_EM_PER_LEVEL;
    var indentEm = uncappedIndentEm < CPU_CALLER_INDENT_MAX_EM ? uncappedIndentEm : CPU_CALLER_INDENT_MAX_EM;
    var frameHtml = formatCpuFrameHtml(currentCpuMethodNames[node["frame"]]);

    var rowHtml = renderCpuTreeRow(rowId, CPU_CALLED_BY_ROLE, frameHtml, ` style="padding-left: ${indentEm}em"`, node, percentDenominatorSamples, grandTotalSamples, branchClass, methodTotalSamples);

    if (children.length === 0) {
        return rowHtml;
    }

    pendingCpuLazySubtrees.set(rowId, { node: node, depth: depth + 1, grandTotalSamples: grandTotalSamples, branchClass: branchClass, methodTotalSamples: methodTotalSamples });

    return rowHtml;
}

// Builds exactly one level of a lazily-registered row's children - mirrors
// exceptionDrillDownStats.js's buildLazyExceptionDrillDownSubtree.
function buildLazyCpuDrillDownSubtree(rowId) {
    var pending = pendingCpuLazySubtrees.get(rowId);
    if (!pending) {
        return null;
    }
    pendingCpuLazySubtrees.delete(rowId);

    var children = pending.node["children"] || [];

    var isBranch = children.length > 1;
    var toggledClass = pending.branchClass === "drillDownAltBranch" ? "" : "drillDownAltBranch";

    var childRowsHtml = "";
    for (var childIndex = 0; childIndex < children.length; ++childIndex) {
        var childBranchClass = isBranch
            ? (childIndex % 2 === 1 ? toggledClass : pending.branchClass)
            : toggledClass;
        childRowsHtml += renderCpuCallerRow(children[childIndex], pending.depth, pending.methodTotalSamples, pending.grandTotalSamples, childBranchClass, pending.methodTotalSamples);
    }

    return `<table class="callerTreeInner">${CPU_CALLER_TREE_COLGROUP}${childRowsHtml}</table>`;
}

// Sets the method-name pool for this profile view session. Called once
// from snapshotGcStats.js's wireProfileInnerTabs when the Methods panel is
// first wired up, so every subsequent buildInlineCpuMethodCallerTree /
// buildLazyCpuDrillDownSubtree call uses the same shared pool without
// having to thread it through every call site.
function initCpuDrillDownMethodNames(methodNames) {
    currentCpuMethodNames = methodNames;
}

// Builds the inline caller tree for one hot method row's expansion -
// entry is cpuProfile["hotMethodDrillDown"][methodIndex], already loaded
// from the JSON. Returns the HTML of a callerTreeInner <table> to inject
// into the .callerTreeCell <td> of the corresponding callPathsDetail row.
//
// Mirrors renderCpuDrillDownTable's tree-building logic but skips the
// standalone heading/summary-div wrapper (the method row itself already
// identifies the method) and starts callers at depth 0 (not rendering a
// separate root row for the hot method - it IS the row that was just clicked).
function buildInlineCpuMethodCallerTree(entry, methodNames, grandTotalSamples) {
    currentCpuMethodNames = methodNames;

    if (!entry) {
        return '<p style="padding:8px;margin:0">No stack data available for this method.</p>';
    }

    var children = entry["children"] || [];
    var totalSamples = entry["totalSamples"];
    var distinctStacks = entry["distinctStackCount"];

    // Expand All/Collapse All controls, scoped to just THIS method's own
    // caller tree (not a page-wide action) - clicking one walks up to the
    // enclosing .callPathsDetail row (this method's own detail row) and
    // expands/collapses everything within it, same as
    // setAllCpuDrillDownRowsExpanded already does for the (structurally
    // identical) exceptions/heap-contents drill-down trees - see
    // snapshotGcStats.js's wireProfileInnerTabs for the click handling.
    // Omitted entirely when there's nothing to expand (children.length===0
    // below).
    var expandControlsHtml = children.length > 0
        ? `<div class="inlineCallerExpandControls">` +
            `<button class="drillDownExpandControlButton cpuMethodExpandAllBtn" type="button">Expand All</button>` +
            `<button class="drillDownExpandControlButton cpuMethodCollapseAllBtn" type="button">Collapse All</button>` +
            `</div>`
        : ``;

    // A <div> sibling BEFORE the <table>, not a row inside it - matches
    // where every other view (exceptions/heap-contents' own
    // .drillDownHeader/.drillDownExpandControls, outside their own
    // <table class="drillDownTable">) puts its own summary/controls, rather
    // than treating them as part of the tabular data.
    var summaryHtml = `<div class="inlineCallerSummary">` +
        `<span>${totalSamples.toLocaleString()} samples · ${distinctStacks.toLocaleString()} distinct call stacks</span>` +
        expandControlsHtml +
        `</div>`;

    if (children.length === 0) {
        return summaryHtml;
    }

    var isBranch = children.length > 1;
    var childRowsHtml = "";
    for (var childIndex = 0; childIndex < children.length; ++childIndex) {
        var branchClass = isBranch
            ? (childIndex % 2 === 1 ? "drillDownAltBranch" : "")
            : "drillDownAltBranch";
        childRowsHtml += renderCpuCallerRow(children[childIndex], 0, totalSamples, grandTotalSamples, branchClass, totalSamples);
    }

    return `${summaryHtml}<table class="callerTreeInner">${CPU_CALLER_TREE_COLGROUP}${childRowsHtml}</table>`;
}

