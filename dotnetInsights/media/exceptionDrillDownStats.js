// Script run within the webview itself - renders the Exceptions view's
// "Drill Down" tab against gcData["exceptionSummary"]["typeDrillDown"]
// (nettraceParser/Exceptions/ExceptionJsonExporter.cs's WriteCallerTreeChildren
// output), reached by clicking a row in the exceptions ranked-types table -
// see snapshotGcStats.js's onExceptionTypeDrillDownClick.
//
// Deliberate near-total mirror of drillDownStats.js's lazy-expand caller-
// tree renderer (see that file's own extensive header comment for the full
// rationale: folded-tree-not-representative-stack, flame-graph-style %,
// build-exactly-one-level-per-click) - kept as a parallel file with its own
// names rather than sharing drillDownStats.js's globals (both files are
// loaded as plain <script> tags into the same webview global scope, and
// ExceptionJsonExporter.cs's node shape uses "count" as its one metric with
// no separate bytes/ticks split AllocationJsonExporter.cs's shape has), not
// because the underlying tree algorithm differs in any way.
function escapeHtmlForExceptionDrillDown(value) {
    return String(value)
        .replace(/&/g, "&amp;")
        .replace(/</g, "&lt;")
        .replace(/>/g, "&gt;");
}

// Same convention as drillDownStats.js's formatFrameHtml - see that
// function's own comment.
function formatExceptionFrameHtml(rawFrameName) {
    if (rawFrameName === "<no stack captured>" || rawFrameName.indexOf("<unresolved") === 0) {
        return `<span class="unresolvedFrame">${escapeHtmlForExceptionDrillDown(rawFrameName)}</span>`;
    }

    var lastDotIndex = rawFrameName.lastIndexOf(".");
    if (lastDotIndex === -1) {
        return `<span class="methodName">${escapeHtmlForExceptionDrillDown(rawFrameName)}</span>`;
    }

    var typePrefix = rawFrameName.slice(0, lastDotIndex + 1);
    var methodName = rawFrameName.slice(lastDotIndex + 1);
    return `<span class="methodTypePrefix">${escapeHtmlForExceptionDrillDown(typePrefix)}</span><span class="methodName">${escapeHtmlForExceptionDrillDown(methodName)}</span>`;
}

// "% of Site" - this row's share of the THROW SITE (top-level leaf node) it
// sits under, held constant for its entire chain, same flame-graph-style
// reasoning as drillDownStats.js's formatPercentOfSelf.
function formatExceptionPercentOfSelf(rowCount, siteTotalCount) {
    if (!(siteTotalCount > 0)) {
        return "";
    }

    var percentage = (rowCount / siteTotalCount) * 100;
    return `${percentage.toFixed(1)}%`;
}

// "% of Total" - fixed denominator (the whole capture's totalExceptionCount)
// at every row and every depth, same as drillDownStats.js's
// formatPercentOfTotal.
function formatExceptionPercentOfTotal(rowCount, grandTotalCount) {
    if (!(grandTotalCount > 0)) {
        return "";
    }

    var percentage = (rowCount / grandTotalCount) * 100;
    return `${percentage.toFixed(2)}%`;
}

// One fewer column than drillDownStats.js's CALLER_TREE_COLGROUP (no
// separate ticks column - "count" is the one metric here, already its own
// column).
const EXCEPTION_CALLER_TREE_COLGROUP = `<colgroup><col><col class="bytesColumn"><col class="percentColumn"><col class="percentColumn"></colgroup>`;

var exceptionCallerRowIdCounter = 0;

// rowId -> { node, depth, grandTotalCount, branchClass, siteTotalCount } -
// same role as drillDownStats.js's pendingLazySubtrees, scoped separately
// so an exceptions drill-down click never collides with a Heap Contents
// drill-down click (even though only one panel is ever visible at a time,
// keeping these independent avoids coupling the two files' internal state).
var pendingExceptionLazySubtrees = new Map();

// exceptionSummary["methodNames"] for whichever renderExceptionDrillDownTable
// call is currently active - a node's "frame" field is an integer index
// into it, same convention as drillDownStats.js's currentMethodNames.
var currentExceptionMethodNames = null;

// Renders exactly one tree node as its own row - never recurses into its
// own children (see buildLazyExceptionDrillDownSubtree for that, deferred
// until the row is actually expanded). Mirrors drillDownStats.js's
// renderTreeRow, minus the bytes/ticks split (just "count").
function renderExceptionTreeRow(rowId, roleLabelHtml, frameHtml, indentAttr, node, percentDenominatorCount, grandTotalCount, branchClass, siteTotalCount) {
    var children = node["children"] || [];
    var hasChildren = children.length > 0;

    var toggleHtml = hasChildren
        ? `<span class="leafMethodToggle">&#9656;</span>`
        : `<span class="leafMethodToggle leafMethodToggleEmpty"></span>`;

    var pathCountSuffix = node["distinctStackCount"] > 1
        ? ` <span class="pathCount">(${node["distinctStackCount"].toLocaleString()} call paths)</span>`
        : ``;

    var rowHtml = `<tr class="${roleLabelHtml.rowClass} ${branchClass}"${hasChildren ? ` data-exception-expandable="true" data-exception-target="${rowId}"` : ``}>` +
        `<td${indentAttr}>${toggleHtml}${roleLabelHtml.html}${frameHtml}${pathCountSuffix}</td>` +
        `<td>${node["count"].toLocaleString()}</td>` +
        `<td>${formatExceptionPercentOfSelf(node["count"], percentDenominatorCount)}</td>` +
        `<td>${formatExceptionPercentOfTotal(node["count"], grandTotalCount)}</td>` +
        `</tr>`;

    if (!hasChildren) {
        return rowHtml;
    }

    pendingExceptionLazySubtrees.set(rowId, { node: node, depth: 0, grandTotalCount: grandTotalCount, branchClass: branchClass, siteTotalCount: siteTotalCount });

    return rowHtml + `<tr id="${rowId}" class="callPathsDetail" data-exception-lazy="true"><td colspan="4" class="callerTreeCell"></td></tr>`;
}

const EXCEPTION_THROW_SITE_ROLE = { rowClass: "leafMethodRow", html: `` };
const EXCEPTION_CALLED_BY_ROLE = { rowClass: "callerRow", html: `` };

// Same values as drillDownStats.js's CALLER_INDENT_EM_PER_LEVEL/
// CALLER_INDENT_MAX_EM - see that file's own comment.
const EXCEPTION_CALLER_INDENT_EM_PER_LEVEL = 0.85;
const EXCEPTION_CALLER_INDENT_MAX_EM = 17;

// Renders one caller frame (a tree node at depth >= 1) as its own row -
// mirrors drillDownStats.js's renderCallerRow.
function renderExceptionCallerRow(node, depth, percentDenominatorCount, grandTotalCount, branchClass, siteTotalCount) {
    var children = node["children"] || [];
    var rowId = children.length > 0 ? `exceptionDrillDownCaller${++exceptionCallerRowIdCounter}` : null;
    var uncappedIndentEm = (depth + 1) * EXCEPTION_CALLER_INDENT_EM_PER_LEVEL;
    var indentEm = uncappedIndentEm < EXCEPTION_CALLER_INDENT_MAX_EM ? uncappedIndentEm : EXCEPTION_CALLER_INDENT_MAX_EM;
    var frameHtml = formatExceptionFrameHtml(currentExceptionMethodNames[node["frame"]]);

    var rowHtml = renderExceptionTreeRow(rowId, EXCEPTION_CALLED_BY_ROLE, frameHtml, ` style="padding-left: ${indentEm}em"`, node, percentDenominatorCount, grandTotalCount, branchClass, siteTotalCount);

    if (children.length === 0) {
        return rowHtml;
    }

    pendingExceptionLazySubtrees.set(rowId, { node: node, depth: depth + 1, grandTotalCount: grandTotalCount, branchClass: branchClass, siteTotalCount: siteTotalCount });

    return rowHtml;
}

// Builds exactly one level of a lazily-registered row's children - mirrors
// drillDownStats.js's buildLazyDrillDownSubtree.
function buildLazyExceptionDrillDownSubtree(rowId) {
    var pending = pendingExceptionLazySubtrees.get(rowId);
    if (!pending) {
        return null;
    }
    pendingExceptionLazySubtrees.delete(rowId);

    var children = pending.node["children"] || [];

    var isBranch = children.length > 1;
    var toggledClass = pending.branchClass === "drillDownAltBranch" ? "" : "drillDownAltBranch";

    var childRowsHtml = "";
    for (var childIndex = 0; childIndex < children.length; ++childIndex) {
        var childBranchClass = isBranch
            ? (childIndex % 2 === 1 ? toggledClass : pending.branchClass)
            : toggledClass;
        childRowsHtml += renderExceptionCallerRow(children[childIndex], pending.depth, pending.siteTotalCount, pending.grandTotalCount, childBranchClass, pending.siteTotalCount);
    }

    return `<table class="callerTreeInner">${EXCEPTION_CALLER_TREE_COLGROUP}${childRowsHtml}</table>`;
}

// entry: exceptionSummary["typeDrillDown"][typeIndex] - a
// { count, distinctStackCount, totalChildCount, children } object (see this
// file's header comment). Always "whole capture" in scope - unlike
// AllocationJsonExporter.cs's typeDrillDown, there's no per-time-bucket cell
// dimension here to disambiguate, since the Exceptions view has no
// time-bucketed chart. grandTotalCount is exceptionSummary.totalExceptionCount,
// threaded down to every row as a fixed-denominator "% of Total" column
// alongside "% of Site" (see formatExceptionPercentOfSelf).
function renderExceptionDrillDownTable(entry, typeName, methodNames, grandTotalCount) {
    const typeHeading = `<h3 class="detailTableHeading drillDownTypeHeading">Type: ${escapeHtmlForExceptionDrillDown(typeName)}</h3>`;

    const leafGroups = entry && entry["children"];
    if (!leafGroups || leafGroups.length === 0) {
        return `<div class="drillDownHeader">${typeHeading}</div><div class="detailTable"><p>No captured stacks for this selection.</p></div>`;
    }

    exceptionCallerRowIdCounter = 0;
    pendingExceptionLazySubtrees.clear();
    currentExceptionMethodNames = methodNames;

    const totalCount = entry["count"];
    const distinctStackCount = entry["distinctStackCount"];
    const isTruncated = entry["totalChildCount"] > leafGroups.length;

    const siteWord = leafGroups.length === 1 ? "throw site" : "throw sites";
    const truncationNote = isTruncated
        ? ` <span class="drillDownTruncationNote">(showing top ${leafGroups.length.toLocaleString()} throw sites by count - some long-tail throw sites aren't individually listed below, but are still counted in every total/percentage.)</span>`
        : ``;
    const summaryLine = `<p class="drillDownSummary">${totalCount.toLocaleString()} exceptions across ${leafGroups.length} ${siteWord} (${distinctStackCount.toLocaleString()} distinct call stacks).${truncationNote}</p>`;

    const expandCollapseControls = `<div class="drillDownExpandControls">` +
        `<button class="drillDownExpandControlButton exceptionDrillDownExpandAllBtn" type="button">Expand All</button>` +
        `<button class="drillDownExpandControlButton exceptionDrillDownCollapseAllBtn" type="button">Collapse All</button>` +
        `</div>`;

    const heading = `<div class="drillDownHeader">${typeHeading}${summaryLine}${expandCollapseControls}</div>`;

    var rows = "";
    for (var rowIndex = 0; rowIndex < leafGroups.length; ++rowIndex) {
        var leafNode = leafGroups[rowIndex];
        var rowId = `exceptionDrillDownLeaf${rowIndex}`;
        var frameHtml = formatExceptionFrameHtml(methodNames[leafNode["frame"]]);

        // No left-indent (depth 0), never tinted at the top level - same
        // reasoning as drillDownStats.js's renderDrillDownTable.
        rows += renderExceptionTreeRow(rowId, EXCEPTION_THROW_SITE_ROLE, frameHtml, ``, leafNode, totalCount, grandTotalCount, "", leafNode["count"]);
    }

    const header = `<tr class="tableHeader"><th>Call Stack</th><th>Count</th><th>% of Site</th><th>% of Total</th></tr>`;

    return `${heading}<div class="detailTable drillDownTable"><table>${EXCEPTION_CALLER_TREE_COLGROUP}${header}${rows}</table></div>`;
}
