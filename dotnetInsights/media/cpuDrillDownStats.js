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

    // A caller of a method that is itself 1.3% of the capture is necessarily a
    // small fraction of it, so toFixed(2) rendered this whole column as
    // "0.00%" on a real capture - a column of zeros that says only "not much",
    // and cannot be told apart from a genuine zero. Below 0.01% it becomes a
    // less-than marker instead; the "% of this method" column beside it is the
    // one with resolution at this depth.
    if (percentage > 0 && percentage < 0.01) {
        return "&lt;0.01%";
    }

    return `${percentage.toFixed(2)}%`;
}

// One fewer column than drillDownStats.js's CALLER_TREE_COLGROUP (no
// separate bytes/ticks column - "samples" is the one metric here, already
// its own column) - same shape as EXCEPTION_CALLER_TREE_COLGROUP.
//
// Leading spacer <col> (width matches .rowHideColumn's own 1.6em in
// snapshot.css) plus a matching empty leading <td> on every row below
// (renderCpuTreeRow) - this tree is inlined directly into the ranked CPU
// Methods table's own row (unlike the Exceptions/Heap Contents drill-down
// trees, which are a separate, un-hide-columned table entirely), and that
// outer table gained its own leading rowHideColumn ✕ column. Without a
// matching offset here, this table's own table-layout:fixed columns (sized
// independently of the outer table) render Samples/%/% flush against the
// LEFT edge of the row exactly as before that outer column existed - which
// visually reads as "opening the stack breaks alignment on the parent" set
// of numeric columns above it, even though nothing here is actually
// broken - it's two independently-sized column systems that used to
// happen to line up and now don't, one <td> short of doing so again.
// The leading spacer's width lives in CSS (.callerTreeSpacerCol), NOT inline
// here, so a view that wants a different gutter can scope one - an inline
// style cannot be overridden by a stylesheet without !important. The default
// stays 1.6em, which is what every tree except the CPU category breakdown
// uses; that one collapses it so its expansion sits flush with the row's hide
// column instead of a gutter's width to the right of it.
// Spacer + name + samples + two percentages. leadingColumns and
// trailingColumns are added either side of the numerics to make the tree span
// its host table exactly - see cpuCallerTreeColgroup.
const CPU_CALLER_TREE_BASE_COLUMNS = 5;

// The tree's three numeric columns are sized as a PERCENTAGE of the inner
// table, which spans the whole ranked row, so they always occupy its rightmost
// three column-widths - and therefore land under whichever three columns the
// ranked table happens to end with. That was fine while the methods table
// ended with (Self Samples, Total %, Total Samples). It stopped being fine the
// moment three columns were appended after those: measured in a browser, a
// sample count "16,615" ended up under "≈ Core %", "100.0%" under "CPU (s)"
// and "1.52%" under "Coverage %", while Self %/Self Samples/Total %/Total
// Samples had nothing under them at all - the name cell spanned straight
// across them. That is what "expanding a row loses columns" looks like.
//
// trailingColumns pads the RIGHT of the tree with that many empty columns, so
// its numerics slide back under the columns they describe. The caller passes
// how many columns its table has after Total Samples; the category tree, whose
// table ends where the tree does, passes 0.
// leadingColumns are empty columns between the name and the tree's own
// numerics; trailingColumns are empty columns after them. Together they make
// the tree span its host table EXACTLY, so every ranked column has a cell
// beneath it and the two grids line up.
//
// Without the leading ones the tree's auto-width name cell runs from the name
// column straight through the host's first numeric column, leaving that column
// with nothing under it and the method name overlapping its header. Without
// the trailing ones the numerics slide right and land under headers that mean
// something else. Both were "expanding a row loses columns".
//
// The methods table needs (1, 3): one numeric before Self Samples, three
// columns after Total Samples. The category table's own numerics start
// immediately and end where the tree does, so it needs (0, 0).
function cpuCallerTreeColgroup(leadingColumns, trailingColumns) {
    var leading = "";
    for (var leadIndex = 0; leadIndex < leadingColumns; ++leadIndex) {
        leading += `<col class="percentColumn">`;
    }

    var padding = "";
    for (var padIndex = 0; padIndex < trailingColumns; ++padIndex) {
        padding += `<col class="percentColumn">`;
    }

    return `<colgroup><col class="callerTreeSpacerCol"><col>${leading}` +
        `<col class="bytesColumn"><col class="percentColumn"><col class="percentColumn">${padding}</colgroup>`;
}

function cpuCallerTreePaddingCells(trailingColumns) {
    var cells = "";
    for (var padIndex = 0; padIndex < trailingColumns; ++padIndex) {
        cells += `<td></td>`;
    }

    return cells;
}

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
function renderCpuTreeRow(rowId, roleLabelHtml, frameHtml, indentAttr, node, percentDenominatorSamples, grandTotalSamples, branchClass, methodTotalSamples, leadingColumns, trailingColumns) {
    var children = node["children"] || [];
    var hasChildren = children.length > 0;

    var toggleHtml = hasChildren
        ? `<span class="leafMethodToggle">&#9656;</span>`
        : `<span class="leafMethodToggle leafMethodToggleEmpty"></span>`;

    var pathCountSuffix = node["distinctStackCount"] > 1
        ? ` <span class="pathCount">(${node["distinctStackCount"].toLocaleString()} call paths)</span>`
        : ``;

    var rowHtml = `<tr class="${roleLabelHtml.rowClass} ${branchClass}"${hasChildren ? ` data-cpu-expandable="true" data-cpu-target="${rowId}"` : ``}>` +
        // Empty leading <td> - pairs with CPU_CALLER_TREE_COLGROUP's own
        // leading spacer <col> (see that constant's own comment).
        `<td></td>` +
        `<td${indentAttr}>${toggleHtml}${roleLabelHtml.html}${frameHtml}${pathCountSuffix}</td>` +
        cpuCallerTreePaddingCells(leadingColumns) +
        `<td>${node["totalSamples"].toLocaleString()}</td>` +
        `<td>${formatCpuPercentOfMethod(node["totalSamples"], percentDenominatorSamples)}</td>` +
        `<td>${formatCpuPercentOfTotal(node["totalSamples"], grandTotalSamples)}</td>` +
        cpuCallerTreePaddingCells(trailingColumns) +
        `</tr>`;

    if (!hasChildren) {
        return rowHtml;
    }

    pendingCpuLazySubtrees.set(rowId, { node: node, depth: 0, grandTotalSamples: grandTotalSamples, branchClass: branchClass, methodTotalSamples: methodTotalSamples, leadingColumns: leadingColumns, trailingColumns: trailingColumns });

    return rowHtml + `<tr id="${rowId}" class="callPathsDetail" data-cpu-lazy="true"><td colspan="${CPU_CALLER_TREE_BASE_COLUMNS + leadingColumns + trailingColumns}" class="callerTreeCell"></td></tr>`;
}

const CPU_CALLED_BY_ROLE = { rowClass: "callerRow", html: `` };

// Same values as drillDownStats.js's CALLER_INDENT_EM_PER_LEVEL/
// CALLER_INDENT_MAX_EM - see that file's own comment.
const CPU_CALLER_INDENT_EM_PER_LEVEL = 0.85;
const CPU_CALLER_INDENT_MAX_EM = 17;

// Renders one caller frame (a tree node at depth >= 1) as its own row -
// mirrors exceptionDrillDownStats.js's renderExceptionCallerRow.
function renderCpuCallerRow(node, depth, percentDenominatorSamples, grandTotalSamples, branchClass, methodTotalSamples, leadingColumns, trailingColumns) {
    var children = node["children"] || [];
    var rowId = children.length > 0 ? `cpuDrillDownCaller${++cpuCallerRowIdCounter}` : null;
    // depth, not depth+1 - the immediate caller (depth 0) starts flush with
    // the tree's own left edge (no indent), with each further level below
    // it stepping in by one CPU_CALLER_INDENT_EM_PER_LEVEL. Used to add a
    // baseline +1 level unconditionally, which read as an extra, unindented
    // tab stop between the hot method's own row and its first caller once
    // this tree's own leading columns were already lined up with the outer
    // ranked table's (see CPU_CALLER_TREE_COLGROUP's own comment on that
    // alignment fix) - two effects that used to partially cancel out now
    // stacked instead.
    var uncappedIndentEm = depth * CPU_CALLER_INDENT_EM_PER_LEVEL;
    var indentEm = uncappedIndentEm < CPU_CALLER_INDENT_MAX_EM ? uncappedIndentEm : CPU_CALLER_INDENT_MAX_EM;
    var frameHtml = formatCpuFrameHtml(currentCpuMethodNames[node["frame"]]);

    var rowHtml = renderCpuTreeRow(rowId, CPU_CALLED_BY_ROLE, frameHtml, ` style="padding-left: ${indentEm}em"`, node, percentDenominatorSamples, grandTotalSamples, branchClass, methodTotalSamples, leadingColumns, trailingColumns);

    if (children.length === 0) {
        return rowHtml;
    }

    // leadingColumns/trailingColumns MUST be carried here. renderCpuTreeRow
    // above already registered a pending entry for this same rowId WITH them,
    // and this call overwrites it - so omitting them silently dropped the
    // padding for every lazily-expanded nested subtree, which then fell back to
    // (0, 0) in buildLazyCpuDrillDownSubtree.
    //
    // The symptom is that the FIRST level of a caller tree lines up correctly
    // and every level opened below it does not: with no padding the inner
    // table has 5 columns against the host row's 9, so its three numerics slide
    // to the far right and land under "~ Core %", "CPU (s)" and "Coverage %".
    // Measured exactly that way - the top row's samples sat under Self Samples
    // while its children's sat three columns over.
    pendingCpuLazySubtrees.set(rowId, { node: node, depth: depth + 1, grandTotalSamples: grandTotalSamples, branchClass: branchClass, methodTotalSamples: methodTotalSamples, leadingColumns: leadingColumns, trailingColumns: trailingColumns });

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
        childRowsHtml += renderCpuCallerRow(children[childIndex], pending.depth, pending.methodTotalSamples, pending.grandTotalSamples, childBranchClass, pending.methodTotalSamples, pending.leadingColumns || 0, pending.trailingColumns || 0);
    }

    return `<table class="callerTreeInner">${cpuCallerTreeColgroup(pending.leadingColumns || 0, pending.trailingColumns || 0)}${childRowsHtml}</table>`;
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
function buildInlineCpuMethodCallerTree(entry, methodNames, grandTotalSamples, rootRowClass, trailingColumns, leadingColumns) {
    trailingColumns = trailingColumns || 0;
    leadingColumns = leadingColumns || 0;
    currentCpuMethodNames = methodNames;

    if (!entry) {
        return '<p style="padding:8px;margin:0">No stack data available for this method.</p>';
    }

    var children = entry["children"] || [];
    var totalSamples = entry["totalSamples"];

    // No summary line (sample/call-stack counts) and no per-method Expand
    // All/Collapse All here - the row that was just clicked already shows
    // its own Self/Total sample counts in the ranked table above, and the
    // master Expand All/Collapse All pair above that table already does a
    // full-depth expand/collapse of every method's own caller tree when
    // clicked (see that function's own call to
    // setAllCpuDrillDownRowsExpanded) - both made this caller tree's own
    // copies pure duplication, just visual clutter every time a row was
    // expanded.
    if (children.length === 0) {
        return '<p style="padding:8px;margin:0">No caller data available for this method.</p>';
    }

    var isBranch = children.length > 1;
    var childRowsHtml = "";
    for (var childIndex = 0; childIndex < children.length; ++childIndex) {
        var branchClass = isBranch
            ? (childIndex % 2 === 1 ? "drillDownAltBranch" : "")
            : "drillDownAltBranch";
        var rootClass = rootRowClass ? `${branchClass} ${rootRowClass}`.trim() : branchClass;
        childRowsHtml += renderCpuCallerRow(children[childIndex], 0, totalSamples, grandTotalSamples, rootClass, totalSamples, leadingColumns, trailingColumns);
    }

    // NOTHING may be added to this table that is not a caller row. A column
    // label row was tried here and had to be moved out: under table-layout
    // auto the leading spacer <col>'s 1.6em is a FLOOR, not a width (the same
    // trap CLAUDE.md records for the ranked tables), so the extra row's text
    // gave the top-level table different column pressure from the nested ones
    // and inflated its spacer from 26px to 30px. Measured in a browser: the
    // first indent step collapsed to 9px while every later step stayed 13px,
    // which read as a child sitting at its own parent's level. Labels now live
    // in a legend above the tree, outside the grid.
    return `<table class="callerTreeInner">${cpuCallerTreeColgroup(leadingColumns, trailingColumns)}${childRowsHtml}</table>`;
}

