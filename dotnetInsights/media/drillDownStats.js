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
// DOM), only not building it at all does. Every row - leaf rows included -
// expands exactly one level per click; only the "Expand All" button walks
// the whole tree (see snapshotGcStats.js's setAllDrillDownRowsExpanded).

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

// "% of Site" column: this row's share of the ALLOCATION SITE it sits
// under - for a leaf row, this scope's own total (entry.totalBytes; there's
// no allocation site above a leaf, so it falls back to "how much of this
// scope does this one site account for"), and for every caller row beneath
// it, that leaf's own totalBytes, held constant for the entire chain.
//
// This used to be share-of-IMMEDIATE-PARENT (flame-graph style). That is
// 100% by definition for any node that is its parent's only child, so a
// long non-branching stack - the common case, since most call stacks don't
// branch at every frame - rendered as a wall of "100.0%" that conveyed
// nothing and read as a broken column. Measuring against the fixed
// allocation-site total instead keeps the number meaningful and directly
// comparable at every depth: it only drops below 100% where callers
// genuinely split, and the size of that drop is exactly how much of the
// site that branch accounts for.
function formatPercentOfSelf(rowBytes, siteTotalBytes) {
    if (!(siteTotalBytes > 0)) {
        return "";
    }

    var percentage = (rowBytes / siteTotalBytes) * 100;
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
function renderTreeRow(rowId, roleLabelHtml, frameHtml, indentAttr, node, percentDenominatorBytes, grandTotalBytes, mb, branchClass, siteTotalBytes) {
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

    // branchClass (see .drillDownAltBranch in snapshot.css) alternates
    // across sibling branches so each one - a top-level allocation site's
    // whole chain, or a deeper branch point's own children and everything
    // under them - reads as a distinct visual group. "" for the
    // non-alternating half, never omitted from the class list entirely, so
    // the space-join below stays simple.
    var rowHtml = `<tr class="${roleLabelHtml.rowClass} ${branchClass}"${hasChildren ? ` data-expandable="true" data-target="${rowId}"` : ``}>` +
        `<td${indentAttr}>${toggleHtml}${roleLabelHtml.html}${frameHtml}${pathCountSuffix}</td>` +
        `<td>${formatBytes(node["totalBytes"], mb)}</td>` +
        `<td>${formatPercentOfSelf(node["totalBytes"], percentDenominatorBytes)}</td>` +
        `<td>${formatPercentOfTotal(node["totalBytes"], grandTotalBytes)}</td>` +
        `<td>${node["tickCount"]}</td>` +
        `</tr>`;

    if (!hasChildren) {
        return rowHtml;
    }

    pendingLazySubtrees.set(rowId, { node: node, depth: 0, mb: mb, grandTotalBytes: grandTotalBytes, branchClass: branchClass, siteTotalBytes: siteTotalBytes });

    // Deliberately NOT tinted with branchClass, unlike the visible row
    // above - this row is purely a structural wrapper around the next
    // nested level's own <table> (see buildLazyDrillDownSubtree), and that
    // nested table's own rows already carry the correct (possibly
    // re-alternated) tint themselves. Tinting this wrapper too used to
    // stack a second copy of the same semi-transparent background behind
    // every nested level, compounding darker with each level of depth -
    // a long non-branching chain could visibly darken from a faint gray
    // toward black by 15-20 frames down, which also made it look like
    // hovering one row was "spreading" a highlight into its children when
    // it was really just this pre-existing static darkening sitting right
    // where a user happened to click to expand.
    return rowHtml + `<tr id="${rowId}" class="callPathsDetail" data-lazy="true"><td colspan="5" class="callerTreeCell"></td></tr>`;
}

// Row roles now carry only a CSS class, no prefix content of their own.
//
// These used to render an icon (a filled circle / an up-arrow) plus a text
// label ("Allocated in" / "Called by") before every method name. Both were
// dropped. The icons competed visually with the leafMethodToggle triangle
// (see renderTreeRow), which was actively confusing on caller rows: every
// such row is simultaneously a child of the row above it AND a parent of
// the row below it, so it carried two arrow-like glyphs pointing in
// unrelated directions. The text label was then pure noise on its own -
// every caller row in the table reads the identical "Called by", so it
// spent horizontal space repeating a word that never distinguished one row
// from another. The triangle plus indentation carries the structure, and
// the one genuinely distinct row (the allocation site) is still marked by
// .leafMethodRow's own styling.
const ALLOCATION_SITE_ROLE = { rowClass: "leafMethodRow", html: `` };
const CALLED_BY_ROLE = { rowClass: "callerRow", html: `` };

// Per-level indent for caller rows (see renderCallerRow). Deliberately
// smaller than one full text indent: every hop now steps right, and a real
// stack runs 20-30 frames, so a large step would consume the Call Stack
// column before the chain finished. Small enough to fit that depth, big
// enough to still read as a step.
const CALLER_INDENT_EM_PER_LEVEL = 0.85;

// Ceiling on that accumulated indent. Past this depth rows stop stepping
// right and stack vertically instead - losing the depth cue is a far
// better failure mode than squeezing long fully-qualified method names into
// a sliver of column, and by that depth the reader is following one chain
// anyway rather than comparing indent levels.
const CALLER_INDENT_MAX_EM = 17;

// Renders one caller frame (a tree node at depth >= 1) as its own row.
//
// depth advances on EVERY hop, so a row is always indented one step further
// than the row that expanded into it - the same relationship the first
// level already showed against its allocation site, applied at every level
// below that. This used to advance only at real branch points, on the
// theory that a deep non-branching stack (common - most call stacks don't
// branch at every frame) would run out of horizontal room otherwise. That
// left a whole 20+ frame chain sharing one indent, which made a parent and
// its child visually indistinguishable - the exact thing indentation is
// for. Room is bought back with a smaller per-level step (see
// CALLER_INDENT_EM_PER_LEVEL) plus a hard ceiling (CALLER_INDENT_MAX_EM),
// so depth stays readable without pushing long method names off the
// column.
function renderCallerRow(node, depth, mb, percentDenominatorBytes, grandTotalBytes, branchClass, siteTotalBytes) {
    var children = node["children"] || [];
    var rowId = children.length > 0 ? `drillDownCaller${++callerRowIdCounter}` : null;
    var uncappedIndentEm = (depth + 1) * CALLER_INDENT_EM_PER_LEVEL;
    var indentEm = uncappedIndentEm < CALLER_INDENT_MAX_EM ? uncappedIndentEm : CALLER_INDENT_MAX_EM;
    var frameHtml = formatFrameHtml(currentMethodNames[node["frame"]]);

    var rowHtml = renderTreeRow(rowId, CALLED_BY_ROLE, frameHtml, ` style="padding-left: ${indentEm}em"`, node, percentDenominatorBytes, grandTotalBytes, mb, branchClass, siteTotalBytes);

    if (children.length === 0) {
        return rowHtml;
    }

    pendingLazySubtrees.set(rowId, { node: node, depth: depth + 1, mb: mb, grandTotalBytes: grandTotalBytes, branchClass: branchClass, siteTotalBytes: siteTotalBytes });

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

    // Every hop down the caller tree flips shade relative to THIS node's
    // own branchClass - see .drillDownAltBranch in snapshot.css. At a real
    // branch point (more than one child), the first child keeps this
    // node's own shade (continuing the branch that was already
    // established) and every other child flips to the toggled shade,
    // alternating from there. A node with exactly one child isn't a
    // branch, but it still flips - a long non-branching chain reads as a
    // continuous zebra stripe going deeper, one flip per hop, rather than
    // one flat color for the entire chain. Only the toggle target itself
    // depends on pending.branchClass, not two hardcoded values, so this
    // keeps working correctly no matter how many hops deep a chain
    // already is by the time it gets here.
    var isBranch = children.length > 1;
    var toggledClass = pending.branchClass === "drillDownAltBranch" ? "" : "drillDownAltBranch";

    var childRowsHtml = "";
    for (var childIndex = 0; childIndex < children.length; ++childIndex) {
        var childBranchClass = isBranch
            ? (childIndex % 2 === 1 ? toggledClass : pending.branchClass)
            : toggledClass;
        childRowsHtml += renderCallerRow(children[childIndex], pending.depth, pending.mb, pending.siteTotalBytes, pending.grandTotalBytes, childBranchClass, pending.siteTotalBytes);
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
        //
        // Top-level leaf rows are never tinted (always "" here) -
        // .drillDownAltBranch (see snapshot.css) is meant to distinguish
        // sibling call-path branches once a row is expanded, not to zebra-
        // stripe the ranked list of allocation sites itself. Each leaf's
        // own direct children still alternate correctly the first time
        // it's expanded (see buildLazyDrillDownSubtree's isBranch case,
        // which computes a fresh alternation among a node's own children
        // independent of the node's own branchClass) - only the top-level
        // list itself opts out.
        // percentDenominator for a leaf row is this whole scope's total
        // (there's no allocation site "above" a leaf); siteTotalBytes is
        // the leaf's OWN total, which every caller row beneath it then
        // measures against - see formatPercentOfSelf.
        rows += renderTreeRow(rowId, ALLOCATION_SITE_ROLE, frameHtml, ``, leafNode, totalBytes, grandTotalBytes, mb, "", leafNode["totalBytes"]);
    }

    // "Call Stack" rather than "Allocating Method" - this column now holds
    // both the allocation-site row and its "Called by" rows underneath (see
    // ALLOCATION_SITE_ROLE/CALLED_BY_ROLE above), not just allocating
    // methods.
    const header = `<tr class="tableHeader"><th>Call Stack</th><th>Total Bytes (mb)</th><th>% of Site</th><th>% of Total</th><th>Tick Count</th></tr>`;

    return `${heading}<div class="detailTable drillDownTable"><table>${CALLER_TREE_COLGROUP}${header}${rows}</table></div>`;
}
