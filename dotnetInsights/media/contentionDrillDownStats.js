// Script run within the webview itself - builds inline caller-tree expansions
// for the Contention view's ranked lock-contention sites table (each site row
// expands to show who called the lock-acquisition site). Entry point:
// buildInlineContentionSiteCallerTree (called from snapshotGcStats.js's
// wireContentionTab when a site row is first expanded) and
// buildLazyContentionDrillDownSubtree (called lazily for each interior caller
// node as it's expanded deeper into the tree).
//
// Deliberate near-total mirror of cpuDrillDownStats.js's inline caller-tree
// renderer - kept as its own parallel file with its own names rather than
// sharing globals. Primary metric is totalWaitMSec (double) rather than
// totalSamples (int): "% of Site" uses the lock-site's own total wait as the
// denominator, "% of Total" uses the capture's total wait time.
function escapeHtmlForContentionDrillDown(value) {
    return String(value)
        .replace(/&/g, "&amp;")
        .replace(/</g, "&lt;")
        .replace(/>/g, "&gt;");
}

function formatContentionFrameHtml(rawFrameName) {
    if (rawFrameName === "<no stack captured>" || rawFrameName.indexOf("<unresolved") === 0) {
        return `<span class="unresolvedFrame">${escapeHtmlForContentionDrillDown(rawFrameName)}</span>`;
    }

    var lastDotIndex = rawFrameName.lastIndexOf(".");
    if (lastDotIndex === -1) {
        return `<span class="methodName">${escapeHtmlForContentionDrillDown(rawFrameName)}</span>`;
    }

    var typePrefix = rawFrameName.slice(0, lastDotIndex + 1);
    var methodName = rawFrameName.slice(lastDotIndex + 1);
    return `<span class="methodTypePrefix">${escapeHtmlForContentionDrillDown(typePrefix)}</span><span class="methodName">${escapeHtmlForContentionDrillDown(methodName)}</span>`;
}

// "% of Site" - this row's share of the root contention site's own total
// wait time, held constant for its entire chain.
function formatContentionPercentOfSite(rowWaitMSec, siteTotalWaitMSec) {
    if (!(siteTotalWaitMSec > 0)) {
        return "";
    }

    var percentage = (rowWaitMSec / siteTotalWaitMSec) * 100;
    return `${percentage.toFixed(1)}%`;
}

// "% of Total" - fixed denominator (the whole capture's totalContentionWaitMSec)
// at every row and every depth.
function formatContentionPercentOfTotal(rowWaitMSec, grandTotalWaitMSec) {
    if (!(grandTotalWaitMSec > 0)) {
        return "";
    }

    var percentage = (rowWaitMSec / grandTotalWaitMSec) * 100;
    return `${percentage.toFixed(2)}%`;
}

const CONTENTION_CALLER_TREE_COLGROUP = `<colgroup><col><col class="bytesColumn"><col class="percentColumn"><col class="percentColumn"></colgroup>`;

var contentionCallerRowIdCounter = 0;

// rowId -> { node, depth, grandTotalWaitMSec, branchClass, siteTotalWaitMSec }
var pendingContentionLazySubtrees = new Map();

// contentionSummary["methodNames"] for the currently active session.
var currentContentionMethodNames = null;

function renderContentionTreeRow(rowId, roleLabelHtml, frameHtml, indentAttr, node, percentDenominatorWaitMSec, grandTotalWaitMSec, branchClass, siteTotalWaitMSec) {
    var children = node["children"] || [];
    var hasChildren = children.length > 0;

    var toggleHtml = hasChildren
        ? `<span class="leafMethodToggle">&#9656;</span>`
        : `<span class="leafMethodToggle leafMethodToggleEmpty"></span>`;

    var pathCountSuffix = node["distinctStackCount"] > 1
        ? ` <span class="pathCount">(${node["distinctStackCount"].toLocaleString()} call paths)</span>`
        : ``;

    var totalWaitMSec = node["totalWaitMSec"];

    var rowHtml = `<tr class="${roleLabelHtml.rowClass} ${branchClass}"${hasChildren ? ` data-contention-expandable="true" data-contention-target="${rowId}"` : ``}>` +
        `<td${indentAttr}>${toggleHtml}${roleLabelHtml.html}${frameHtml}${pathCountSuffix}</td>` +
        `<td>${totalWaitMSec.toFixed(3)}</td>` +
        `<td>${formatContentionPercentOfSite(totalWaitMSec, percentDenominatorWaitMSec)}</td>` +
        `<td>${formatContentionPercentOfTotal(totalWaitMSec, grandTotalWaitMSec)}</td>` +
        `</tr>`;

    if (!hasChildren) {
        return rowHtml;
    }

    pendingContentionLazySubtrees.set(rowId, { node: node, depth: 0, grandTotalWaitMSec: grandTotalWaitMSec, branchClass: branchClass, siteTotalWaitMSec: siteTotalWaitMSec });

    return rowHtml + `<tr id="${rowId}" class="callPathsDetail" data-contention-lazy-inner="true"><td colspan="4" class="callerTreeCell"></td></tr>`;
}

const CONTENTION_CALLED_BY_ROLE = { rowClass: "callerRow", html: `` };

const CONTENTION_CALLER_INDENT_EM_PER_LEVEL = 0.85;
const CONTENTION_CALLER_INDENT_MAX_EM = 17;

function renderContentionCallerRow(node, depth, percentDenominatorWaitMSec, grandTotalWaitMSec, branchClass, siteTotalWaitMSec) {
    var children = node["children"] || [];
    var rowId = children.length > 0 ? `contentionDrillDownCaller${++contentionCallerRowIdCounter}` : null;
    var uncappedIndentEm = (depth + 1) * CONTENTION_CALLER_INDENT_EM_PER_LEVEL;
    var indentEm = uncappedIndentEm < CONTENTION_CALLER_INDENT_MAX_EM ? uncappedIndentEm : CONTENTION_CALLER_INDENT_MAX_EM;
    var frameHtml = formatContentionFrameHtml(currentContentionMethodNames[node["frame"]]);

    var rowHtml = renderContentionTreeRow(rowId, CONTENTION_CALLED_BY_ROLE, frameHtml, ` style="padding-left: ${indentEm}em"`, node, percentDenominatorWaitMSec, grandTotalWaitMSec, branchClass, siteTotalWaitMSec);

    if (children.length === 0) {
        return rowHtml;
    }

    pendingContentionLazySubtrees.set(rowId, { node: node, depth: depth + 1, grandTotalWaitMSec: grandTotalWaitMSec, branchClass: branchClass, siteTotalWaitMSec: siteTotalWaitMSec });

    return rowHtml;
}

// Builds exactly one level of a lazily-registered row's children.
function buildLazyContentionDrillDownSubtree(rowId) {
    var pending = pendingContentionLazySubtrees.get(rowId);
    if (!pending) {
        return null;
    }
    pendingContentionLazySubtrees.delete(rowId);

    var children = pending.node["children"] || [];

    var isBranch = children.length > 1;
    var toggledClass = pending.branchClass === "drillDownAltBranch" ? "" : "drillDownAltBranch";

    var childRowsHtml = "";
    for (var childIndex = 0; childIndex < children.length; ++childIndex) {
        var childBranchClass = isBranch
            ? (childIndex % 2 === 1 ? toggledClass : pending.branchClass)
            : toggledClass;
        childRowsHtml += renderContentionCallerRow(children[childIndex], pending.depth, pending.siteTotalWaitMSec, pending.grandTotalWaitMSec, childBranchClass, pending.siteTotalWaitMSec);
    }

    return `<table class="callerTreeInner">${CONTENTION_CALLER_TREE_COLGROUP}${childRowsHtml}</table>`;
}

// Sets the method-name pool for this contention view session.
function initContentionDrillDownMethodNames(methodNames) {
    currentContentionMethodNames = methodNames;
}

// Builds the inline caller tree for one contention site row's expansion -
// entry is contentionSummary["siteDrillDown"][siteIndex]. Returns the HTML
// of a callerTreeInner <table> to inject into the .callerTreeCell <td>.
function buildInlineContentionSiteCallerTree(entry, methodNames, grandTotalWaitMSec) {
    currentContentionMethodNames = methodNames;

    if (!entry) {
        return '<p style="padding:8px;margin:0">No stack data available for this site.</p>';
    }

    var children = entry["children"] || [];
    var totalWaitMSec = entry["totalWaitMSec"];
    var contentionCount = entry["contentionCount"];
    var distinctStacks = entry["distinctStackCount"];

    var summaryRow = `<tr class="inlineCallerSummaryRow"><td colspan="4" class="inlineCallerSummary">` +
        `${totalWaitMSec.toFixed(3)} ms total wait · ${contentionCount.toLocaleString()} contentions · ${distinctStacks.toLocaleString()} distinct call stacks` +
        `</td></tr>`;

    if (children.length === 0) {
        return `<table class="callerTreeInner">${CONTENTION_CALLER_TREE_COLGROUP}${summaryRow}</table>`;
    }

    var isBranch = children.length > 1;
    var childRowsHtml = "";
    for (var childIndex = 0; childIndex < children.length; ++childIndex) {
        var branchClass = isBranch
            ? (childIndex % 2 === 1 ? "drillDownAltBranch" : "")
            : "drillDownAltBranch";
        childRowsHtml += renderContentionCallerRow(children[childIndex], 0, totalWaitMSec, grandTotalWaitMSec, branchClass, totalWaitMSec);
    }

    return `<table class="callerTreeInner">${CONTENTION_CALLER_TREE_COLGROUP}${summaryRow}${childRowsHtml}</table>`;
}
