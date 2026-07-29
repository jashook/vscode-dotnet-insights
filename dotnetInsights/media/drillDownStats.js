// Script run within the webview itself - renders the "Drill Down" tab's
// resolved-stacks view against either scope AllocationJsonExporter.cs's
// AllocationSummaryBuilder exports: a single (type, 1-second bucket) chart
// cell (gcData["allocationSummary"]["drillDown"]["cells"], reached by
// clicking a stacked-chart segment - see allocationStats.js's onClick
// handler), or a whole type across the entire capture
// (gcData["allocationSummary"]["typeDrillDown"], reached by clicking a row
// in the global ranked types table - see snapshotGcStats.js's
// onTypeDrillDownClick). Both are a { totalBytes, totalTickCount,
// distinctStackCount, stacks: [{frames, totalBytes, tickCount}, ...] }
// object by the time they reach renderDrillDownTable below, so one render
// path serves both entry points. totalBytes/totalTickCount/distinctStackCount
// are the TRUE totals across every distinct call stack the C# side
// aggregated, computed BEFORE WriteCellDrillDown/WriteTypeDrillDown cap the
// "stacks" array at DrillDownStacksPerCellLimit/DrillDownStacksPerTypeLimit -
// summing only the (possibly truncated) stacks array instead would silently
// shrink both the displayed total and every percentage's denominator below
// the real total the chart bar was drawn from, which is exactly what made
// the drill-down view's percentages disagree with the bar's actual size on
// captures with many distinct call sites for one type/cell. Unlike every
// other table on this page, this has no server-rendered HTML to lazily
// inject - which scope to show is only known at click time - so it's built
// here, entirely client-side, from data already present in
// allocationSummaryJson.
//
// Deliberately not a PerfView-style root-to-leaf call tree. The question a
// user clicking a chart segment (or a row in the ranked types table) has
// is "which method is allocating this?", not "show me every possible call
// path first" - so this leads with a ranked list of distinct LEAF
// (allocating) methods. Expanding a leaf reveals its callers as a real
// tree (one row per frame, shared call prefixes merged so two paths
// through the same caller don't duplicate rows) - every row with at least
// one child gets its own collapse toggle, so any step can be independently
// hidden, not just where the tree actually branches. A straight,
// non-branching chain of callers still *starts* expanded (there's nothing
// to decide yet, so nothing needs a click the first time a leaf is opened),
// but that's just the initial state - it can still be collapsed like any
// other row. Every row also shows its share of its *immediate* parent's
// bytes (leaf rows: share of the whole scope's total; a caller row: share
// of the row that expanded into it) - flame-graph style, not a share of the
// grand total at every level.

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

// Regroups a flat list of distinct stacks (each already aggregated by
// exact StackId - see WriteStackAggregate) by their leaf (allocating)
// frame, since several distinct call paths commonly share the same
// immediate allocator. Returns groups sorted by total bytes descending.
// methodNames is allocationSummaryJson["methodNames"] (see
// AllocationJsonExporter.cs's MethodNameInterner) - each stack's "frames"
// is an array of integer indices into it, not raw strings, so every frame
// is resolved back to its real name here, once, before anything downstream
// (tree-building, Map keying, display) needs to know it's dealing with
// method names at all.
function groupStacksByLeaf(stacks, methodNames) {
    var groupsByLeaf = new Map();
    var leafOrder = [];

    for (var stackIndex = 0; stackIndex < stacks.length; ++stackIndex) {
        var stackEntry = stacks[stackIndex];
        var frameIndices = stackEntry["frames"];
        var frames = new Array(frameIndices.length);
        for (var frameIdx = 0; frameIdx < frameIndices.length; ++frameIdx) {
            frames[frameIdx] = methodNames[frameIndices[frameIdx]];
        }
        var leafFrame = frames[0];

        var group = groupsByLeaf.get(leafFrame);
        if (!group) {
            group = { leafFrame: leafFrame, totalBytes: 0, tickCount: 0, paths: [] };
            groupsByLeaf.set(leafFrame, group);
            leafOrder.push(leafFrame);
        }

        group.totalBytes += stackEntry["totalBytes"];
        group.tickCount += stackEntry["tickCount"];
        group.paths.push({
            callerFrames: frames.slice(1),
            totalBytes: stackEntry["totalBytes"],
            tickCount: stackEntry["tickCount"]
        });
    }

    var groups = [];
    for (var leafIndex = 0; leafIndex < leafOrder.length; ++leafIndex) {
        groups.push(groupsByLeaf.get(leafOrder[leafIndex]));
    }

    groups.sort(function (left, right) { return right.totalBytes - left.totalBytes; });

    return groups;
}

// Merges a leaf's distinct call paths (each just a flat list of caller
// frames, immediate caller first) into a tree, so two paths that share the
// same immediate caller(s) share the same node(s) instead of rendering as
// separate duplicate chains. Each node's totalBytes/tickCount is the sum
// over every path passing through it - standard "inclusive" call-tree
// aggregation. Returns a synthetic root node (never rendered itself) whose
// children are the leaf's distinct immediate callers.
function buildCallerTree(paths) {
    var root = { frameName: null, totalBytes: 0, tickCount: 0, children: new Map() };

    for (var pathIndex = 0; pathIndex < paths.length; ++pathIndex) {
        var path = paths[pathIndex];
        var node = root;

        for (var frameIndex = 0; frameIndex < path.callerFrames.length; ++frameIndex) {
            var frameName = path.callerFrames[frameIndex];

            var child = node.children.get(frameName);
            if (!child) {
                child = { frameName: frameName, totalBytes: 0, tickCount: 0, children: new Map() };
                node.children.set(frameName, child);
            }

            child.totalBytes += path.totalBytes;
            child.tickCount += path.tickCount;
            node = child;
        }
    }

    return root;
}

// Bytes cell content for one row: the raw mb figure plus, when a parent
// total is known, that row's share of it in parentheses - e.g.
// "1.23 (45.6%)". Folded into the existing Bytes column as text rather
// than a new column so every row's column widths stay identical (see
// CALLER_TREE_COLGROUP below) regardless of nesting depth.
function formatBytesWithPercentage(totalBytes, parentTotalBytes, mb) {
    var bytesText = (totalBytes / mb).toFixed(2);

    if (!(parentTotalBytes > 0)) {
        return bytesText;
    }

    var percentage = (totalBytes / parentTotalBytes) * 100;
    return `${bytesText} <span class="percentOfParent">(${percentage.toFixed(1)}%)</span>`;
}

// Shared by the outer table and every nested .callerTreeInner table so
// their Bytes/Ticks columns land at identical widths regardless of
// nesting depth - table-layout:fixed sizes columns from explicit <col>
// widths rather than per-row content, which is what actually makes a
// caller row's numbers line up under the leaf row's numbers above it.
// The first (Method) column is intentionally left unset in both - with
// the other two pinned and a nested table's total width always matching
// its containing colspan cell exactly, it converges to the same width in
// both places without needing to state it twice.
const CALLER_TREE_COLGROUP = `<colgroup><col><col class="bytesColumn"><col class="ticksColumn"></colgroup>`;

var callerRowIdCounter = 0;

// Renders one frame (node) as its own row, then recurses. Every node with
// at least one child gets its own toggle, so any step in the chain can be
// collapsed independently - not just real branch points (more than one
// child). A straight, non-branching continuation still starts expanded by
// default (there's no decision to make yet, so nothing needs a click to
// become visible the *first* time a leaf is opened) - only a real branch
// point (more than one child) still starts collapsed - but unlike before,
// that default is just a starting state, not the only state: every node's
// subtree can be individually toggled either way from here.
//
// depth only advances at a real branch point (more than one child) - a
// long straight chain of single-child continuations, wrapped in its own
// collapsible container or not, stays at one indent level instead of
// pushing further right on every single hop. A deep non-branching stack
// (common - most call stacks don't branch at every frame) would otherwise
// run out of horizontal room within a handful of frames.
function renderCallerChainRows(node, depth, mb, parentTotalBytes) {
    var childEntries = Array.from(node.children.values());
    childEntries.sort(function (left, right) { return right.totalBytes - left.totalBytes; });

    var hasChildren = childEntries.length > 0;
    var isBranch = childEntries.length > 1;
    var startsExpanded = childEntries.length === 1;
    var rowId = hasChildren ? `drillDownCaller${++callerRowIdCounter}` : null;
    var indentEm = (depth + 1) * 1.5;

    var toggleHtml = hasChildren
        ? `<span class="leafMethodToggle">&#9656;</span>`
        : `<span class="leafMethodToggle leafMethodToggleEmpty"></span>`;

    // Explicit role label, not just indentation - every step away from the
    // allocation site (depth 0, the leaf row above) is a caller of the step
    // before it, reading top-to-bottom as "called by, called by, ...".
    var calledByLabel = `<span class="stackRoleLabel calledByLabel">&#8593; Called by</span>`;

    var expandedClass = startsExpanded ? ` expanded` : ``;

    var rowHtml = `<tr class="callerRow${expandedClass}"${hasChildren ? ` data-expandable="true" data-target="${rowId}"` : ``}>` +
        `<td style="padding-left: ${indentEm}em">${toggleHtml}${calledByLabel}${formatFrameHtml(node.frameName)}</td>` +
        `<td>${formatBytesWithPercentage(node.totalBytes, parentTotalBytes, mb)}</td>` +
        `<td>${node.tickCount}</td>` +
        `</tr>`;

    if (!hasChildren) {
        return rowHtml;
    }

    // Every child's percentage is measured against *this* node's bytes,
    // regardless of whether this row itself is a branch point or a
    // straight continuation - flame-graph style, one hop's share of the
    // hop immediately before it, not a share of the overall total.
    var childDepth = isBranch ? depth + 1 : depth;

    var childRowsHtml = "";
    for (var childIndex = 0; childIndex < childEntries.length; ++childIndex) {
        childRowsHtml += renderCallerChainRows(childEntries[childIndex], childDepth, mb, node.totalBytes);
    }

    return rowHtml + `<tr id="${rowId}" class="callPathsDetail${expandedClass}"><td colspan="3" class="callerTreeCell"><table class="callerTreeInner">${CALLER_TREE_COLGROUP}${childRowsHtml}</table></td></tr>`;
}

// entry: either gcData["allocationSummary"]["drillDown"]["cells"]["{typeIndex}:{bucketIndex}"]
// (one chart cell - undefined/empty when that exact cell has no drillDown
// entry, e.g. every tick in it landed under "Other", which isn't drillable
// in the first place, so this shouldn't normally happen for a cell the
// chart actually let the user click) or
// gcData["allocationSummary"]["typeDrillDown"][typeIndex] (one type, whole
// capture) - both a { totalBytes, totalTickCount, distinctStackCount,
// stacks } object (see this file's header comment). scopeLabel is a
// formatted bucket time for the former, "Whole Capture" for the latter.
// filterLabel is "All Types" or "LOH Only", reflecting which of
// allocationSummaryJson/allocationSummaryJson.loh the caller resolved
// entry/typeName from (see snapshotGcStats.js) - shown alongside typeName
// so it's never ambiguous whether the stacks below came from every
// allocation of this type or only its LOH-kind ones. methodNames is always
// allocationSummaryJson["methodNames"] (top-level, shared by both the
// unfiltered and LOH-only scopes - see AllocationJsonExporter.cs) regardless
// of which scope entry itself came from.
//
// This header is sticky (position: sticky, see snapshot.css's
// .drillDownHeader) so it stays visible while scrolling a long list of
// allocating methods below - losing track of which type/filter you're
// looking at was the specific complaint this addressed.
function renderDrillDownTable(entry, typeName, scopeLabel, filterLabel, methodNames) {
    const scopeLine = `<p class="drillDownScopeLine">Scope: ${escapeHtmlForDrillDown(scopeLabel)} &nbsp;&bull;&nbsp; Filter: ${escapeHtmlForDrillDown(filterLabel)}</p>`;
    const typeHeading = `<h3 class="detailTableHeading drillDownTypeHeading">Type: ${escapeHtmlForDrillDown(typeName)}</h3>`;

    const stacks = entry && entry["stacks"];
    if (!stacks || stacks.length === 0) {
        return `<div class="drillDownHeader">${typeHeading}${scopeLine}</div><div class="detailTable"><p>No captured stacks for this selection.</p></div>`;
    }

    const mb = 1024 * 1024;
    const leafGroups = groupStacksByLeaf(stacks, methodNames);
    callerRowIdCounter = 0;

    // The true scope totals (every distinct call stack the C# side
    // aggregated, before it capped the "stacks" array) - NOT a sum over
    // leafGroups, which only covers what's actually shown when the array
    // was truncated. Every percentage below is measured against totalBytes
    // so it stays consistent with the chart bar this table was opened
    // from, even when some long-tail stacks aren't individually listed.
    const totalBytes = entry["totalBytes"];
    const totalTicks = entry["totalTickCount"];
    const distinctStackCount = entry["distinctStackCount"];
    const isTruncated = distinctStackCount > stacks.length;

    const methodWord = leafGroups.length === 1 ? "method" : "methods";
    const truncationNote = isTruncated
        ? ` <span class="drillDownTruncationNote">(showing top ${stacks.length.toLocaleString()} of ${distinctStackCount.toLocaleString()} distinct call stacks by bytes - some long-tail stacks aren't individually listed below, but are still counted in every total/percentage.)</span>`
        : ``;
    const summaryLine = `<p class="drillDownSummary">${totalTicks.toLocaleString()} ticks, ${(totalBytes / mb).toFixed(2)} MB across ${leafGroups.length} allocating ${methodWord}.${truncationNote}</p>`;

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
        var group = leafGroups[rowIndex];

        // A leaf with exactly one path and no captured caller frames (the
        // "<no stack captured>" sentinel, or a real leaf whose stack walk
        // just didn't go any deeper) has nothing further to show.
        var hasCallerContext = !(group.paths.length === 1 && group.paths[0].callerFrames.length === 0);
        var callerTreeRoot = hasCallerContext ? buildCallerTree(group.paths) : null;
        var topLevelCallers = callerTreeRoot ? Array.from(callerTreeRoot.children.values()) : [];
        topLevelCallers.sort(function (left, right) { return right.totalBytes - left.totalBytes; });

        // Every leaf with at least one top-level caller gets a real toggle -
        // not just ones with more than one (a real branch) - so any leaf's
        // caller chain can be collapsed independently. A single top-level
        // caller still starts expanded by default (nothing to decide yet),
        // matching renderCallerChainRows' own default for a straight
        // continuation one level down.
        var isExpandable = topLevelCallers.length > 0;
        var startsExpanded = topLevelCallers.length === 1;
        var rowId = `drillDownLeaf${rowIndex}`;

        var toggleHtml = isExpandable
            ? `<span class="leafMethodToggle">&#9656;</span>`
            : `<span class="leafMethodToggle leafMethodToggleEmpty"></span>`;

        // Path count folds into the method cell as inline text (only when
        // there's more than one) rather than its own table column - every
        // row, leaf or caller, at any depth, uses the exact same 3-column
        // shape (Method/Bytes/Ticks) so their columns line up with each
        // other (see .callerTreeInner's matching pinned widths in
        // snapshot.css); a 4th column only leaf rows had would throw that
        // alignment off between a row and its own expanded detail beneath
        // it.
        var pathCountSuffix = group.paths.length > 1
            ? ` <span class="pathCount">(${group.paths.length} call paths)</span>`
            : ``;

        // Explicit role label - this is the method that directly performed
        // the allocation (frames[0] in the underlying stack, before any
        // grouping/tree-building), not one of its callers. Paired with
        // "Called by" on every row underneath it (see
        // renderCallerChainRows), so the causal direction reads
        // unambiguously top-to-bottom without needing to infer it from
        // indentation alone.
        var allocationSiteLabel = `<span class="stackRoleLabel allocationSiteLabel">&#9679; Allocated in</span>`;
        var expandedClass = startsExpanded ? ` expanded` : ``;

        rows += `<tr class="leafMethodRow${expandedClass}"${isExpandable ? ` data-expandable="true" data-target="${rowId}"` : ``}>` +
            `<td>${toggleHtml}${allocationSiteLabel}${formatFrameHtml(group.leafFrame)}${pathCountSuffix}</td>` +
            `<td>${formatBytesWithPercentage(group.totalBytes, totalBytes, mb)}</td>` +
            `<td>${group.tickCount}</td>` +
            `</tr>`;

        if (topLevelCallers.length === 0) {
            continue;
        }

        var callerRowsHtml = "";
        for (var callerIndex = 0; callerIndex < topLevelCallers.length; ++callerIndex) {
            callerRowsHtml += renderCallerChainRows(topLevelCallers[callerIndex], 0, mb, group.totalBytes);
        }

        rows += `<tr id="${rowId}" class="callPathsDetail${expandedClass}"><td colspan="3" class="callerTreeCell"><table class="callerTreeInner">${CALLER_TREE_COLGROUP}${callerRowsHtml}</table></td></tr>`;
    }

    // "Call Stack" rather than "Allocating Method" - this column now holds
    // both the allocation-site row and its "Called by" rows underneath (see
    // allocationSiteLabel/calledByLabel above), not just allocating methods.
    const header = `<tr class="tableHeader"><th>Call Stack</th><th>Total Bytes (mb)</th><th>Tick Count</th></tr>`;

    return `${heading}<div class="detailTable drillDownTable"><table>${CALLER_TREE_COLGROUP}${header}${rows}</table></div>`;
}
