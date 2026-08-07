// Script run within the webview itself - renders the "Drill Down" tab's
// resolved-stacks view against either scope AllocationJsonExporter.cs's
// AllocationSummaryBuilder exports: a single (type, 1-second bucket) chart
// cell (gcData["allocationSummary"]["drillDown"]["cells"], reached by
// clicking a stacked-chart segment - see allocationStats.js's onClick
// handler), or a whole type across the entire capture
// (gcData["allocationSummary"]["typeDrillDown"], reached by clicking a row
// in the global ranked types table - see snapshotGcStats.js's
// onTypeDrillDownClick). Both are a { totalBytes, totalTickCount,
// distinctStackCount, children: [{frame, totalBytes, tickCount,
// distinctStackCount, children: [...]}, ...] } object by the time they
// reach renderDrillDownTable below, so one render path serves both entry
// points. totalBytes/totalTickCount/distinctStackCount (both at the top
// level and on every individual node) are TRUE totals across every
// distinct raw call stack the C# side folded into the tree - see
// AllocationJsonExporter.cs's BuildCallerTree/WriteCallerTreeChildren -
// computed BEFORE any per-node children array is capped at
// DrillDownTreeChildrenLimit, so a node's own totalBytes/tickCount/
// distinctStackCount always reflect every real stack through it even when
// its own children list had to be truncated. Unlike every other table on
// this page, this has no server-rendered HTML to lazily inject - which
// scope to show is only known at click time - so it's built here, entirely
// client-side, from data already present in allocationSummaryJson.
//
// The tree itself (which frames merge into which nodes, and every node's
// true aggregate totals) is now built entirely server-side
// (AllocationJsonExporter.cs's BuildCallerTree) - this file used to build
// it here instead, from a flat list of the top ~100 raw individual call
// stacks the server picked by their own individual bytes. That design had
// a real, confirmed bug: picking even one "representative" raw stack per
// distinct leaf frame (an intermediate version of the server-side fold)
// silently discarded every *other* real caller for that leaf - verified
// against a real capture where PerfView showed System.Uri as a top caller
// of System.String.Ctor, invisible here because the one kept stack for
// String.Ctor happened to go through a different caller entirely. Folding
// every real raw stack into one shared tree server-side, not just ranking/
// picking among them, is what actually fixes that: two stacks sharing a
// leaf-first common prefix always merge into the same chain of nodes, and
// where they genuinely diverge, the tree actually branches - no caller
// permutation is ever silently dropped in favor of another, only a node's
// own *children list* is breadth-capped (at DrillDownTreeChildrenLimit),
// with the node's own totals always staying true regardless.
//
// Deliberately not a PerfView-style root-to-leaf call tree. The question a
// user clicking a chart segment (or a row in the ranked types table) has
// is "which method is allocating this?", not "show me every possible call
// path first" - so this leads with a ranked list of distinct LEAF
// (allocating) methods (the tree's own top-level children). Expanding a
// leaf reveals its callers as a real tree (one row per frame, shared call
// prefixes merged so two paths through the same caller don't duplicate
// rows) - every row with at least one child gets its own collapse toggle,
// so any step can be independently hidden, not just where the tree
// actually branches. Every row also shows its share of its *immediate*
// parent's bytes (leaf rows: share of the whole scope's total; a caller
// row: share of the row that expanded into it) - flame-graph style, not a
// share of the grand total at every level.
//
// Every row's caller subtree starts both collapsed AND unbuilt - only the
// row's own <tr> is real HTML up front; its detail row is an empty
// data-lazy="true" placeholder (see renderTreeRow) until a user actually
// expands it, at which point buildLazyDrillDownSubtree (called from
// snapshotGcStats.js's click handler) builds exactly one level of children
// on demand. This is a real perf fix, not just a cosmetic default: a
// capture with many distinct allocating methods, each with a deep
// non-branching stack, used to recursively stringify every frame of every
// one of them on every single click into this tab, before the user had
// asked to see any of it - CSS-only display:none on an already-built
// subtree does not avoid that cost (browsers still parse/construct hidden
// DOM), only not building it at all does. Expanding a leaf row still
// reveals its *entire* chain in one click (see setAllDrillDownRowsExpanded's
// build-then-expand loop) - that just now means "build everything under
// here, on demand" instead of "it was already built".

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

// MethodSymbolTable.Resolve's DisplayName is "{fully-qualified type}.{method
// name}" (the rundown event's own Namespace field is really the type's full
// name, not just a namespace) - splitting on the *last* "." reliably
// separates them, letting the type prefix render muted and the method name
// (what a user actually scans for) render bold. "<unresolved 0x...>" and
// "<no stack captured>" placeholders (see AllocationJsonExporter.cs) aren't
// real method names, so they get distinct muted/italic styling instead of
// looking like a real answer.
function formatFrameHtml(rawFrameName) {
    if (rawFrameName === "<no stack captured>" || rawFrameName.indexOf("<unresolved") === 0) {
        return `<span class="unresolvedFrame">${escapeHtmlForDrillDown(rawFrameName)}</span>`;
    }

    var lastDotIndex = rawFrameName.lastIndexOf(".");
    if (lastDotIndex === -1) {
        return `<span class="methodName">${escapeHtmlForDrillDown(rawFrameName)}</span>`;
    }

    var typePrefix = rawFrameName.slice(0, lastDotIndex + 1);
    var methodName = rawFrameName.slice(lastDotIndex + 1);
    return `<span class="methodTypePrefix">${escapeHtmlForDrillDown(typePrefix)}</span><span class="methodName">${escapeHtmlForDrillDown(methodName)}</span>`;
}

// Bytes cell content for one row: just the raw mb figure - see
// formatPercentOfSelf below for this row's share of whatever total
// directly contains it, its own column rather than folded into this cell.
function formatBytes(totalBytes, mb) {
    return (totalBytes / mb).toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 });
}

// "% of Self" column: this row's share of its immediate containing total -
// for a leaf row, this type's own grand total (entry.totalBytes, the same
// value formatPercentOfTotal below compares against a *different*,
// whole-capture denominator for); for a caller row, its immediate parent
// frame's total (flame-graph style - a row's share of the hop immediately
// before it, not of the overall total, so this changes meaning at every
// depth unlike formatPercentOfTotal's fixed denominator).
function formatPercentOfSelf(rowBytes, parentTotalBytes) {
    if (!(parentTotalBytes > 0)) {
        return "";
    }

    var percentage = (rowBytes / parentTotalBytes) * 100;
    return `${percentage.toFixed(1)}%`;
}

// "% of Total" column - unlike formatPercentOfSelf above (whose denominator
// changes meaning at every depth), this is the same fixed denominator (the
// whole scope's totalSampledBytes, across every type - see
// renderDrillDownTable's grandTotalBytes) at every row and every depth,
// letting a row's share of the entire capture be read directly instead of
// hand-multiplying this type's own "% of Sampled" (shown in the ranked
// types table) by whatever "% of Self" shows at each level down to it.
function formatPercentOfTotal(rowBytes, grandTotalBytes) {
    if (!(grandTotalBytes > 0)) {
        return "";
    }

    var percentage = (rowBytes / grandTotalBytes) * 100;
    return `${percentage.toFixed(2)}%`;
}

// Shared by the outer table and every nested .callerTreeInner table so
// their Bytes/% columns land at identical widths regardless of nesting
// depth - table-layout:fixed sizes columns from explicit <col> widths
// rather than per-row content, which is what actually makes a caller row's
// numbers line up under the leaf row's numbers above it. The first (Method)
// column is intentionally left unset in both - with the rest pinned and a
// nested table's total width always matching its containing colspan cell
// exactly, it converges to the same width in both places without needing
// to state it twice. Two percentColumn entries: % of Self, then % of Total
// (see the header built in renderDrillDownTable).
const CALLER_TREE_COLGROUP = `<colgroup><col><col class="bytesColumn"><col class="percentColumn"><col class="percentColumn"><col class="ticksColumn"></colgroup>`;

var callerRowIdCounter = 0;

// rowId -> { node, depth, mb, grandTotalBytes } - everything
// buildLazyDrillDownSubtree needs to render exactly one level of a row's
// children (node.children, already fully resolved/sorted server-side),
// deferred until that row is actually expanded. Reset at the start of
// every renderDrillDownTable call (a fresh drill-down click replaces the
// whole panel, so any previous pending state is dead anyway). Both a leaf
// row and a caller row register into this the exact same way - a leaf row
// is really just "the tree's root's own child", structurally identical to
// any deeper node once you're past the role-label/indent presentation.
var pendingLazySubtrees = new Map();

// methodNames for whichever renderDrillDownTable call is currently active -
// a node's "frame" field is an integer index into it (see
// AllocationJsonExporter.cs's MethodNameInterner), resolved lazily (only
// when a row actually gets built) rather than up front, matching this
// file's own lazy-build performance approach. Module-level (not threaded
// through every call) because buildLazyDrillDownSubtree is invoked later,
// from snapshotGcStats.js's click handler, well after the methodNames
// array used to render this particular tree was passed in.
var currentMethodNames = null;

// Renders exactly one tree node as its own row - never recurses into the
// node's own children (see buildLazyDrillDownSubtree for that, deferred
// until the row is actually expanded). Shared by both a leaf
// (allocation-site) row and a caller row; the only real differences are
// the role label ("Allocated in" vs "Called by"), whether there's a
// left-indent, and what "parent" totalBytes means for % of Self (see
// formatPercentOfSelf's own comment) - both callers below supply those.
function renderTreeRow(rowId, roleLabelHtml, frameHtml, indentAttr, node, parentTotalBytes, grandTotalBytes, mb) {
    var children = node["children"] || [];
    var hasChildren = children.length > 0;

    var toggleHtml = hasChildren
        ? `<span class="leafMethodToggle">&#9656;</span>`
        : `<span class="leafMethodToggle leafMethodToggleEmpty"></span>`;

    // A node with more than one distinct raw stack folded into it has real
    // diversity beneath it even before it's expanded (either more of its
    // own children, or just more ticks sharing this exact same chain) -
    // worth surfacing at a glance rather than only after expanding.
    var pathCountSuffix = node["distinctStackCount"] > 1
        ? ` <span class="pathCount">(${node["distinctStackCount"].toLocaleString()} call paths)</span>`
        : ``;

    var rowHtml = `<tr class="${roleLabelHtml.rowClass}"${hasChildren ? ` data-expandable="true" data-target="${rowId}"` : ``}>` +
        `<td${indentAttr}>${toggleHtml}${roleLabelHtml.html}${frameHtml}${pathCountSuffix}</td>` +
        `<td>${formatBytes(node["totalBytes"], mb)}</td>` +
        `<td>${formatPercentOfSelf(node["totalBytes"], parentTotalBytes)}</td>` +
        `<td>${formatPercentOfTotal(node["totalBytes"], grandTotalBytes)}</td>` +
        `<td>${node["tickCount"]}</td>` +
        `</tr>`;

    if (!hasChildren) {
        return rowHtml;
    }

    pendingLazySubtrees.set(rowId, { node: node, depth: 0, mb: mb, grandTotalBytes: grandTotalBytes });
    return rowHtml + `<tr id="${rowId}" class="callPathsDetail" data-lazy="true"><td colspan="5" class="callerTreeCell"></td></tr>`;
}

// Explicit role labels, not just indentation - every step away from the
// allocation site (the leaf row) is a caller of the step before it,
// reading top-to-bottom as "called by, called by, ...".
const ALLOCATION_SITE_ROLE = { rowClass: "leafMethodRow", html: `<span class="stackRoleLabel allocationSiteLabel">&#9679; Allocated in</span>` };
const CALLED_BY_ROLE = { rowClass: "callerRow", html: `<span class="stackRoleLabel calledByLabel">&#8593; Called by</span>` };

// Renders one caller frame (a tree node at depth >= 1) as its own row.
// depth only advances at a real branch point (more than one child) - a
// long straight chain of single-child continuations stays at one indent
// level instead of pushing further right on every single hop. A deep
// non-branching stack (common - most call stacks don't branch at every
// frame) would otherwise run out of horizontal room within a handful of
// frames.
function renderCallerRow(node, depth, mb, parentTotalBytes, grandTotalBytes) {
    var children = node["children"] || [];
    var rowId = children.length > 0 ? `drillDownCaller${++callerRowIdCounter}` : null;
    var indentEm = (depth + 1) * 1.5;
    var frameHtml = formatFrameHtml(currentMethodNames[node["frame"]]);

    var rowHtml = renderTreeRow(rowId, CALLED_BY_ROLE, frameHtml, ` style="padding-left: ${indentEm}em"`, node, parentTotalBytes, grandTotalBytes, mb);

    if (children.length === 0) {
        return rowHtml;
    }

    // Every child's percentage is measured against *this* node's bytes,
    // regardless of whether this row itself is a branch point or a
    // straight continuation - flame-graph style, one hop's share of the
    // hop immediately before it, not a share of the overall total.
    var isBranch = children.length > 1;
    pendingLazySubtrees.set(rowId, { node: node, depth: isBranch ? depth + 1 : depth, mb: mb, grandTotalBytes: grandTotalBytes });

    return rowHtml;
}

// Builds exactly one level of a lazily-registered row's children (already
// fully resolved and sorted server-side - see AllocationJsonExporter.cs's
// WriteCallerTreeChildren) and returns the
// <table class="callerTreeInner">...</table> HTML ready to drop into that
// row's (currently empty) .callerTreeCell - or null if rowId has already
// been built (or was never lazy to begin with, e.g. a race from a
// double-click). Consumes the pending entry - each subtree is only ever
// built once, matching the rest of this page's render-once approach.
function buildLazyDrillDownSubtree(rowId) {
    var pending = pendingLazySubtrees.get(rowId);
    if (!pending) {
        return null;
    }
    pendingLazySubtrees.delete(rowId);

    var children = pending.node["children"] || [];
    var parentTotalBytes = pending.node["totalBytes"];

    var childRowsHtml = "";
    for (var childIndex = 0; childIndex < children.length; ++childIndex) {
        childRowsHtml += renderCallerRow(children[childIndex], pending.depth, pending.mb, parentTotalBytes, pending.grandTotalBytes);
    }

    return `<table class="callerTreeInner">${CALLER_TREE_COLGROUP}${childRowsHtml}</table>`;
}

// entry: either gcData["allocationSummary"]["drillDown"]["cells"]["{typeIndex}:{bucketIndex}"]
// (one chart cell - undefined/empty when that exact cell has no drillDown
// entry, e.g. every tick in it landed under "Other", which isn't drillable
// in the first place, so this shouldn't normally happen for a cell the
// chart actually let the user click) or
// gcData["allocationSummary"]["typeDrillDown"][typeIndex] (one type, whole
// capture) - both a { totalBytes, totalTickCount, distinctStackCount,
// children } object (see this file's header comment). scopeLabel is a
// formatted bucket time for the former, "Whole Capture" for the latter.
// filterLabel is "All Types" or "LOH Only", reflecting which of
// allocationSummaryJson/allocationSummaryJson.loh the caller resolved
// entry/typeName from (see snapshotGcStats.js) - shown alongside typeName
// so it's never ambiguous whether the stacks below came from every
// allocation of this type or only its LOH-kind ones. methodNames is always
// allocationSummaryJson["methodNames"] (top-level, shared by both the
// unfiltered and LOH-only scopes - see AllocationJsonExporter.cs) regardless
// of which scope entry itself came from. grandTotalBytes is the calling
// scope's own totalSampledBytes (allocationSummaryJson or .loh - the same
// denominator the ranked types table's own "% of Sampled" column already
// uses), threaded down to every row (leaf and caller alike) as a fixed-
// denominator "% of Total" column alongside "% of Self" (see
// formatPercentOfSelf), whose own denominator changes meaning at every
// depth (flame-graph style).
//
// This header is sticky (position: sticky, see snapshot.css's
// .drillDownHeader) so it stays visible while scrolling a long list of
// allocating methods below - losing track of which type/filter you're
// looking at was the specific complaint this addressed.
function renderDrillDownTable(entry, typeName, scopeLabel, filterLabel, methodNames, grandTotalBytes) {
    const scopeLine = `<p class="drillDownScopeLine">Scope: ${escapeHtmlForDrillDown(scopeLabel)} &nbsp;&bull;&nbsp; Filter: ${escapeHtmlForDrillDown(filterLabel)}</p>`;
    const typeHeading = `<h3 class="detailTableHeading drillDownTypeHeading">Type: ${escapeHtmlForDrillDown(typeName)}</h3>`;

    const leafGroups = entry && entry["children"];
    if (!leafGroups || leafGroups.length === 0) {
        return `<div class="drillDownHeader">${typeHeading}${scopeLine}</div><div class="detailTable"><p>No captured stacks for this selection.</p></div>`;
    }

    const mb = 1024 * 1024;
    callerRowIdCounter = 0;
    pendingLazySubtrees.clear();
    currentMethodNames = methodNames;

    // The true scope totals (every distinct raw call stack the C# side
    // folded into the tree - see BuildCallerTree) - NOT a sum over
    // leafGroups, which only covers what's actually shown when the
    // top-level children array was breadth-capped
    // (DrillDownTreeChildrenLimit). Every percentage below is measured
    // against totalBytes so it stays consistent with the chart bar this
    // table was opened from, even when some long-tail leaf groups aren't
    // individually listed.
    const totalBytes = entry["totalBytes"];
    const totalTicks = entry["totalTickCount"];
    const distinctStackCount = entry["distinctStackCount"];

    // entry.totalChildCount is the *true* number of distinct leaf frames
    // (before AllocationJsonExporter.cs's WriteCallerTreeChildren applies
    // DrillDownTreeChildrenLimit to what's actually written) - only flag
    // truncation once that's actually bigger than what was shipped, the
    // same reconciliation pattern every node in the tree supports, not
    // just the root.
    const isTruncated = entry["totalChildCount"] > leafGroups.length;

    const methodWord = leafGroups.length === 1 ? "method" : "methods";
    const truncationNote = isTruncated
        ? ` <span class="drillDownTruncationNote">(showing top ${leafGroups.length.toLocaleString()} allocating methods by bytes - some long-tail allocation sites aren't individually listed below, but are still counted in every total/percentage.)</span>`
        : ``;
    const totalMbFormatted = (totalBytes / mb).toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 });
    const summaryLine = `<p class="drillDownSummary">${totalTicks.toLocaleString()} ticks, ${totalMbFormatted} MB across ${leafGroups.length} allocating ${methodWord} (${distinctStackCount.toLocaleString()} distinct call stacks).${truncationNote}</p>`;

    // Bulk-toggles every collapsible row at once (see snapshotGcStats.js's
    // click delegation on the drill-down panel) - now that every node with
    // at least one child is individually collapsible (not just real branch
    // points), a deep tree can take a lot of individual clicks to fully
    // open or close by hand.
    const expandCollapseControls = `<div class="drillDownExpandControls">` +
        `<button class="drillDownExpandControlButton drillDownExpandAllBtn" type="button">Expand All</button>` +
        `<button class="drillDownExpandControlButton drillDownCollapseAllBtn" type="button">Collapse All</button>` +
        `</div>`;

    // Sticky (see snapshot.css's .drillDownHeader) so type/scope/filter/
    // summary/controls all stay visible while scrolling a long list of
    // allocating methods below.
    const heading = `<div class="drillDownHeader">${typeHeading}${scopeLine}${summaryLine}${expandCollapseControls}</div>`;

    var rows = "";
    for (var rowIndex = 0; rowIndex < leafGroups.length; ++rowIndex) {
        var leafNode = leafGroups[rowIndex];
        var rowId = `drillDownLeaf${rowIndex}`;
        var frameHtml = formatFrameHtml(methodNames[leafNode["frame"]]);

        // A leaf row's "% of Self" is measured against totalBytes (this
        // type's own grand total, entry.totalBytes) - there's no real
        // "immediate parent" above a leaf, so this is the closest
        // equivalent: how much of this type's own allocations this one
        // allocation site accounts for. No left-indent (depth 0).
        rows += renderTreeRow(rowId, ALLOCATION_SITE_ROLE, frameHtml, ``, leafNode, totalBytes, grandTotalBytes, mb);
    }

    // "Call Stack" rather than "Allocating Method" - this column now holds
    // both the allocation-site row and its "Called by" rows underneath (see
    // ALLOCATION_SITE_ROLE/CALLED_BY_ROLE above), not just allocating
    // methods.
    const header = `<tr class="tableHeader"><th>Call Stack</th><th>Total Bytes (mb)</th><th>% of Self</th><th>% of Total</th><th>Tick Count</th></tr>`;

    return `${heading}<div class="detailTable drillDownTable"><table>${CALLER_TREE_COLGROUP}${header}${rows}</table></div>`;
}
