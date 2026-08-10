// Script run within the webview itself - builds inline caller-tree expansions
// for the Exceptions view's unified ranked-types table (each exception type
// row expands to show its throw sites and their own callers, replacing the
// former separate "Drill Down" tab). Entry point for callers:
// buildInlineExceptionTypeCallerTree (called from snapshotGcStats.js's
// exceptions panel wiring when a type row is first expanded) and
// buildLazyExceptionDrillDownSubtree (called lazily for each interior caller
// node as it's expanded deeper into the tree).
//
// Deliberate near-total mirror of cpuDrillDownStats.js's lazy-expand caller-
// tree renderer (see that file's own header comment, and drillDownStats.js's
// before it, for the full rationale) - kept as its own parallel file with its
// own names rather than sharing globals, not because the underlying tree
// algorithm differs in any way. One real shape difference from
// cpuDrillDownStats.js: exceptionSummary["typeDrillDown"][typeIndex] is a
// container whose "children" are multiple distinct THROW SITES for that
// type (each its own top-level row, EXCEPTION_THROW_SITE_ROLE), and each
// throw site's OWN children are its callers (EXCEPTION_CALLED_BY_ROLE) -
// unlike CPU's hotMethodDrillDown[methodIndex], which only ever has one root
// (the hot method itself, already shown as the outer table row) with no
// separate "throw site" level in between.
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
// reasoning as cpuDrillDownStats.js's formatCpuPercentOfMethod.
function formatExceptionPercentOfSelf(rowCount, siteTotalCount) {
    if (!(siteTotalCount > 0)) {
        return "";
    }

    var percentage = (rowCount / siteTotalCount) * 100;
    return `${percentage.toFixed(1)}%`;
}

// "% of Total" - fixed denominator (the whole capture's totalExceptionCount)
// at every row and every depth, same as cpuDrillDownStats.js's
// formatCpuPercentOfTotal.
function formatExceptionPercentOfTotal(rowCount, grandTotalCount) {
    if (!(grandTotalCount > 0)) {
        return "";
    }

    var percentage = (rowCount / grandTotalCount) * 100;
    return `${percentage.toFixed(2)}%`;
}

// One fewer column than drillDownStats.js's CALLER_TREE_COLGROUP (no
// separate ticks column - "count" is the one metric here, already its own
// column) - same shape as CPU_CALLER_TREE_COLGROUP.
//
// Leading spacer <col> (width matches .rowHideColumn's own 1.6em in
// snapshot.css) plus a matching empty leading <td> on every row below
// (renderExceptionTreeRow) - this tree is now inlined directly into the
// ranked Exceptions table's own row (previously a standalone, un-hide-
// columned table on a separate Drill Down tab), and that outer table has
// its own leading rowHideColumn ✕ column - see CPU_CALLER_TREE_COLGROUP's
// own comment for the alignment bug this avoids.
const EXCEPTION_CALLER_TREE_COLGROUP = `<colgroup><col style="width: 1.6em"><col><col class="bytesColumn"><col class="percentColumn"><col class="percentColumn"></colgroup>`;

var exceptionCallerRowIdCounter = 0;

// rowId -> { node, depth, grandTotalCount, branchClass, siteTotalCount } -
// same role as cpuDrillDownStats.js's pendingCpuLazySubtrees, scoped
// separately so an Exceptions drill-down click never collides with a
// Profile or Heap Contents drill-down click.
var pendingExceptionLazySubtrees = new Map();

// exceptionSummary["methodNames"] for the currently active session - a
// node's "frame" field is an integer index into it, same convention as
// cpuDrillDownStats.js's currentCpuMethodNames. Set once per view via
// initExceptionDrillDownMethodNames.
var currentExceptionMethodNames = null;

// Renders exactly one tree node as its own row - never recurses into its
// own children (see buildLazyExceptionDrillDownSubtree for that, deferred
// until the row is actually expanded). Mirrors cpuDrillDownStats.js's
// renderCpuTreeRow, against "count" instead of "totalSamples".
function renderExceptionTreeRow(rowId, roleLabelHtml, frameHtml, indentAttr, node, percentDenominatorCount, grandTotalCount, branchClass, siteTotalCount) {
    var children = node["children"] || [];
    var hasChildren = children.length > 0;

    var toggleHtml = hasChildren
        ? `<span class="leafMethodToggle">&#9656;</span>`
        : `<span class="leafMethodToggle leafMethodToggleEmpty"></span>`;

    var pathCountSuffix = node["distinctStackCount"] > 1
        ? ` <span class="pathCount">(${node["distinctStackCount"].toLocaleString()} call paths)</span>`
        : ``;

    var rowHtml = `<tr class="${roleLabelHtml.rowClass} ${branchClass}"${hasChildren ? ` data-exception-caller-expandable="true" data-exception-caller-target="${rowId}"` : ``}>` +
        // Empty leading <td> - pairs with EXCEPTION_CALLER_TREE_COLGROUP's
        // own leading spacer <col> (see that constant's own comment).
        `<td></td>` +
        `<td${indentAttr}>${toggleHtml}${roleLabelHtml.html}${frameHtml}${pathCountSuffix}</td>` +
        `<td>${node["count"].toLocaleString()}</td>` +
        `<td>${formatExceptionPercentOfSelf(node["count"], percentDenominatorCount)}</td>` +
        `<td>${formatExceptionPercentOfTotal(node["count"], grandTotalCount)}</td>` +
        `</tr>`;

    if (!hasChildren) {
        return rowHtml;
    }

    pendingExceptionLazySubtrees.set(rowId, { node: node, depth: 0, grandTotalCount: grandTotalCount, branchClass: branchClass, siteTotalCount: siteTotalCount });

    return rowHtml + `<tr id="${rowId}" class="callPathsDetail" data-exception-caller-lazy="true"><td colspan="5" class="callerTreeCell"></td></tr>`;
}

const EXCEPTION_THROW_SITE_ROLE = { rowClass: "leafMethodRow", html: `` };
const EXCEPTION_CALLED_BY_ROLE = { rowClass: "callerRow", html: `` };

// Same values as drillDownStats.js's CALLER_INDENT_EM_PER_LEVEL/
// CALLER_INDENT_MAX_EM - see that file's own comment.
const EXCEPTION_CALLER_INDENT_EM_PER_LEVEL = 0.85;
const EXCEPTION_CALLER_INDENT_MAX_EM = 17;

// Renders one caller frame (a tree node at depth >= 1, i.e. a caller of a
// throw site or of another caller) as its own row - mirrors
// cpuDrillDownStats.js's renderCpuCallerRow.
function renderExceptionCallerRow(node, depth, percentDenominatorCount, grandTotalCount, branchClass, siteTotalCount) {
    var children = node["children"] || [];
    var rowId = children.length > 0 ? `exceptionDrillDownCaller${++exceptionCallerRowIdCounter}` : null;
    // depth, not depth+1 - see cpuDrillDownStats.js's renderCpuCallerRow,
    // which needed the identical fix for the identical reason (this tree's
    // own leading columns now line up with the outer ranked table's own -
    // see EXCEPTION_CALLER_TREE_COLGROUP's own comment - so an unconditional
    // +1 level of indent on top of that reads as one extra, unindented tab
    // stop between a throw site's own row and its first caller).
    var uncappedIndentEm = depth * EXCEPTION_CALLER_INDENT_EM_PER_LEVEL;
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
// cpuDrillDownStats.js's buildLazyCpuDrillDownSubtree.
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

// Sets the method-name pool for this Exceptions view session. Called once
// from snapshotGcStats.js when the Exceptions panel is first wired up, so
// every subsequent buildInlineExceptionTypeCallerTree/
// buildLazyExceptionDrillDownSubtree call uses the same shared pool without
// having to thread it through every call site.
function initExceptionDrillDownMethodNames(methodNames) {
    currentExceptionMethodNames = methodNames;
}

// Builds the inline caller tree for one exception type row's expansion -
// entry is exceptionSummary["typeDrillDown"][typeIndex], already loaded from
// the JSON. Returns the HTML of a callerTreeInner <table> to inject into the
// .callerTreeCell <td> of the corresponding callPathsDetail row.
//
// entry.children are throw sites (EXCEPTION_THROW_SITE_ROLE, unindented,
// each its own top-level row) - not callers directly, unlike
// cpuDrillDownStats.js's buildInlineCpuMethodCallerTree, since one exception
// type can have many distinct throw sites, each with its own caller chain.
// No summary line (throw count/distinct-stack count) and no per-type Expand
// All/Collapse All here, matching cpuDrillDownStats.js's own current
// convention - the row that was just clicked already shows its own Count/%
// of Total in the ranked table above, and the master Expand All/Collapse
// All pair above that table already covers a full-depth expand/collapse.
function buildInlineExceptionTypeCallerTree(entry, methodNames, grandTotalCount) {
    currentExceptionMethodNames = methodNames;

    var leafGroups = entry && entry["children"];
    if (!leafGroups || leafGroups.length === 0) {
        return '<p style="padding:8px;margin:0">No caller data available for this type.</p>';
    }

    var totalCount = entry["count"];

    var rows = "";
    for (var rowIndex = 0; rowIndex < leafGroups.length; ++rowIndex) {
        var leafNode = leafGroups[rowIndex];
        var rowId = `exceptionDrillDownLeaf${rowIndex}`;
        var frameHtml = formatExceptionFrameHtml(methodNames[leafNode["frame"]]);

        // No left-indent (depth 0), never tinted at the top level - same
        // reasoning as drillDownStats.js's renderDrillDownTable.
        rows += renderExceptionTreeRow(rowId, EXCEPTION_THROW_SITE_ROLE, frameHtml, ``, leafNode, totalCount, grandTotalCount, "", leafNode["count"]);
    }

    return `<table class="callerTreeInner">${EXCEPTION_CALLER_TREE_COLGROUP}${rows}</table>`;
}
