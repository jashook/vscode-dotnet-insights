// Script run within the webview itself - renders the "Drill Down" tab's
// resolved-stacks view against one cell of
// gcData["allocationSummary"]["drillDown"]["cells"] (see
// AllocationJsonExporter.cs's AllocationSummaryBuilder.WriteDrillDown).
// Unlike every other table on this page, this has no server-rendered HTML
// to lazily inject - which cell to show is only known once a
// stacked-chart segment is actually clicked (see allocationStats.js's
// onClick handler on the type-timeline chart), so it's built here,
// entirely client-side, from data already present in allocationSummaryJson.
//
// Deliberately not a PerfView-style root-to-leaf call tree. The question a
// user clicking a chart segment actually has is "which method is
// allocating this?", not "show me every possible call path first" - so
// this leads with a ranked list of distinct LEAF (allocating) methods.
// Expanding a leaf reveals its callers as a real tree (one row per frame,
// shared call prefixes merged so two paths through the same caller don't
// duplicate rows) - but a row only gets a collapse toggle where the tree
// actually branches. A straight, non-branching chain of callers just flows
// as consecutive rows with no interaction required at all, since there's
// nothing to hide.

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

// Regroups a cell's flat list of distinct stacks (each already aggregated
// by exact StackId - see WriteDrillDown) by their leaf (allocating) frame,
// since several distinct call paths commonly share the same immediate
// allocator. Returns groups sorted by total bytes descending.
function groupStacksByLeaf(cellStacks) {
    var groupsByLeaf = new Map();
    var leafOrder = [];

    for (var stackIndex = 0; stackIndex < cellStacks.length; ++stackIndex) {
        var stackEntry = cellStacks[stackIndex];
        var frames = stackEntry["frames"];
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

// Renders one frame (node) as its own row, then recurses. A node with more
// than one child is a real branch point - its children each start their
// own (initially collapsed) sub-chain, one indent level deeper, behind a
// single toggle on this row. A node with exactly one child is a straight
// continuation with no decision to make, so that child's row is appended
// immediately at the *same* indent level rather than opening a new nested,
// initially-hidden section for something with nothing to hide.
function renderCallerChainRows(node, depth, mb) {
    var childEntries = Array.from(node.children.values());
    childEntries.sort(function (left, right) { return right.totalBytes - left.totalBytes; });

    var hasBranch = childEntries.length > 1;
    var rowId = hasBranch ? `drillDownCaller${++callerRowIdCounter}` : null;
    var indentEm = (depth + 1) * 1.5;

    var toggleHtml = hasBranch
        ? `<span class="leafMethodToggle">&#9656;</span>`
        : `<span class="leafMethodToggle leafMethodToggleEmpty"></span>`;

    var rowHtml = `<tr class="callerRow"${hasBranch ? ` data-expandable="true" data-target="${rowId}"` : ``}>` +
        `<td style="padding-left: ${indentEm}em">${toggleHtml}${formatFrameHtml(node.frameName)}</td>` +
        `<td>${(node.totalBytes / mb).toFixed(2)}</td>` +
        `<td>${node.tickCount}</td>` +
        `</tr>`;

    if (childEntries.length === 0) {
        return rowHtml;
    }

    if (childEntries.length === 1) {
        return rowHtml + renderCallerChainRows(childEntries[0], depth, mb);
    }

    var childRowsHtml = "";
    for (var childIndex = 0; childIndex < childEntries.length; ++childIndex) {
        childRowsHtml += renderCallerChainRows(childEntries[childIndex], depth + 1, mb);
    }

    return rowHtml + `<tr id="${rowId}" class="callPathsDetail"><td colspan="3" class="callerTreeCell"><table class="callerTreeInner">${CALLER_TREE_COLGROUP}${childRowsHtml}</table></td></tr>`;
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
    const leafGroups = groupStacksByLeaf(cellStacks);
    callerRowIdCounter = 0;

    var totalBytes = 0;
    var totalTicks = 0;
    for (var totalsIndex = 0; totalsIndex < leafGroups.length; ++totalsIndex) {
        totalBytes += leafGroups[totalsIndex].totalBytes;
        totalTicks += leafGroups[totalsIndex].tickCount;
    }

    const methodWord = leafGroups.length === 1 ? "method" : "methods";
    const summary = `<p class="drillDownSummary">${totalTicks.toLocaleString()} ticks, ${(totalBytes / mb).toFixed(2)} MB across ${leafGroups.length} allocating ${methodWord}.</p>`;

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

        var isExpandable = topLevelCallers.length > 1;
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

        rows += `<tr class="leafMethodRow"${isExpandable ? ` data-expandable="true" data-target="${rowId}"` : ``}>` +
            `<td>${toggleHtml}${formatFrameHtml(group.leafFrame)}${pathCountSuffix}</td>` +
            `<td>${(group.totalBytes / mb).toFixed(2)}</td>` +
            `<td>${group.tickCount}</td>` +
            `</tr>`;

        if (topLevelCallers.length === 0) {
            continue;
        }

        var callerRowsHtml = "";
        if (topLevelCallers.length === 1) {
            // Single immediate caller - straight continuation, shown
            // inline right away just like every other non-branching hop.
            callerRowsHtml = renderCallerChainRows(topLevelCallers[0], 0, mb);
        } else {
            for (var callerIndex = 0; callerIndex < topLevelCallers.length; ++callerIndex) {
                callerRowsHtml += renderCallerChainRows(topLevelCallers[callerIndex], 0, mb);
            }
        }

        var expandedClass = isExpandable ? `` : ` expanded`;
        rows += `<tr id="${rowId}" class="callPathsDetail${expandedClass}"><td colspan="3" class="callerTreeCell"><table class="callerTreeInner">${CALLER_TREE_COLGROUP}${callerRowsHtml}</table></td></tr>`;
    }

    const header = `<tr class="tableHeader"><th>Allocating Method</th><th>Total Bytes (mb)</th><th>Tick Count</th></tr>`;

    return `${heading}${summary}<div class="detailTable drillDownTable"><table>${CALLER_TREE_COLGROUP}${header}${rows}</table></div>`;
}
