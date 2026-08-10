// Script run within the webview itself - builds and manages the "Profile"
// view's Flame Graph tab (gcData["cpuProfile"]["flameTree"], produced by
// Cpu/CpuProfileJsonExporter.cs) as plain, absolutely-positioned DOM rows -
// one row per stack depth, each frame's box width proportional to its own
// totalSamples share of whatever node is currently "zoomed to fill" (the
// synthetic root initially, or a clicked frame after a zoom) - the standard
// flamegraph.pl/speedscope click-to-zoom interaction, built from scratch
// here rather than pulling in a charting/flame-graph library (this codebase
// avoids third-party JS beyond Chart.js 2.x - see CLAUDE.md).
//
// flameTree's own node shape ({ frame, totalSamples, totalChildCount,
// children }) is root-to-leaf (see Cpu/CpuProfileJsonExporter.cs's own
// header comment on why this is the one place that inverts
// AllocationJsonExporter's leaf-first caller-tree direction) - depth 0 here
// is always a real top-level entry point (Main, thread-pool dispatch,
// etc.), never the leaf/currently-executing frame.
//
// Every child's totalSamples is a true subset of its parent's (a sample
// counted in a child was, by construction, also counted in every one of its
// ancestors - see CpuProfileJsonExporter.Write's own per-sample loop), so a
// frame's width can be computed directly against a single fixed denominator
// (whatever node is currently the zoom target) at every depth, rather than
// compounding "percent of immediate parent" at each level - mathematically
// identical, but avoids accumulating floating-point rounding error across a
// deep chain.
//
// Called once, lazily, the first time the Profile view's Flame Graph tab is
// shown (see snapshotGcStats.js's view switcher) - mirrors
// drillDownStats.js's own lazy-build-on-first-use discipline.

// Real .NET type/method names can legitimately contain HTML-significant
// characters (compiler-generated names like "Program.<Main>$" are common) -
// anything from cpuProfile data must be escaped before going into innerHTML.
function escapeHtmlForFlameGraph(value) {
    return String(value)
        .replace(/&/g, "&amp;")
        .replace(/</g, "&lt;")
        .replace(/>/g, "&gt;");
}

// The immediate declaring type's own last segment plus the method name
// (e.g. "System.Threading.LowLevelLifoSemaphore.WaitForSignal" ->
// "LowLevelLifoSemaphore.WaitForSignal") - just the method name alone
// (e.g. "WaitForSignal") reads as ambiguous once several unrelated types
// share a common method name like "Write"/"Read"/"ToString". The full
// namespace is still dropped (a flame graph frame is narrow, and the full
// DisplayName is available in the tooltip on hover regardless - see
// showFlameGraphTooltip). Mirrors drillDownStats.js's own
// "<no stack captured>" placeholder handling.
function flameGraphFrameLabel(rawName) {
    if (rawName === "<no stack captured>") {
        return rawName;
    }

    var lastDotIndex = rawName.lastIndexOf(".");
    if (lastDotIndex === -1) {
        return rawName;
    }

    var methodName = rawName.slice(lastDotIndex + 1);
    var beforeMethod = rawName.slice(0, lastDotIndex);

    // Constructors/static constructors (".ctor"/".cctor") carry their own
    // leading dot as part of the REAL CLR method name, so the string has
    // TWO consecutive dots at the type/method boundary (e.g.
    // "System.String" + "." + ".ctor") - lastDotIndex above lands on the
    // second (ctor's own) dot, leaving beforeMethod ending in the FIRST
    // (the real separator) dot still attached. Strip it before searching
    // for ITS own last dot, or that same trailing dot gets found again -
    // an off-by-one that silently sliced out an empty type name for every
    // constructor before this fix (verified: "System.String..ctor" ->
    // "ctor" with no type, instead of "String.ctor").
    if (beforeMethod.endsWith(".")) {
        beforeMethod = beforeMethod.slice(0, -1);
    }

    var typeLastDotIndex = beforeMethod.lastIndexOf(".");
    var typeName = typeLastDotIndex === -1 ? beforeMethod : beforeMethod.slice(typeLastDotIndex + 1);

    return typeName.length > 0 ? typeName + "." + methodName : methodName;
}

var FLAME_ROW_HEIGHT_PX = 22;

// Below this width, a frame is neither rendered nor recursed into - a deep/
// wide real capture's flame tree (even after Cpu/CpuProfileJsonExporter.cs's
// own server-side node budget) can still have far more distinct frames than
// would ever be individually visible at typical viewport widths; skipping
// sub-pixel-scale boxes keeps the DOM small and avoids rendering thousands
// of unreadable slivers. The skipped frame's width is still reserved (its
// siblings' positions aren't affected), so the remaining boxes' proportions
// stay accurate even though the tiny ones are invisible.
var FLAME_MIN_VISIBLE_WIDTH_PERCENT = 0.1;

// { cpuProfile, containerElement, breadcrumbElement, resetButtonElement,
//   tooltipElement, zoomChain, currentFrames, viewportStart, viewportEnd,
//   dragOverlayElement } - module-level since this whole file only ever
// manages one flame graph instance (the Profile view's single Flame Graph
// tab), the same single-instance assumption drillDownStats.js's own
// module-level pendingLazySubtrees/currentMethodNames make.
//
// Two independent, layered zoom mechanisms share this state:
//   - zoomChain (click-to-zoom): jumps the view to a specific NODE's own
//     subtree - see layoutFlameGraph. Changes which data is shown.
//   - viewportStart/viewportEnd (drag-to-zoom, see setupFlameGraphDragZoom):
//     a pure magnification of whatever zoomChain is CURRENTLY showing, both
//     0-100 within that view's own already-computed left/width percentages
//     (see renderFlameGraphFrames). Changes nothing about which data is
//     shown, only how much of the current picture is visible at once - the
//     same relationship a map's "pan the current view" has to "navigate to
//     a different place", mirroring the GC timeline charts' own drag-to-
//     zoom (chartZoomHelper.js) but adapted to this page's percentage-of-
//     total coordinates instead of a time axis.
var currentFlameGraphState = null;

// Full undo/redo history of past { zoomChain, viewportStart, viewportEnd }
// snapshots - unlike snapshotGcStats.js's own sharedZoomRange/
// zoomRangeForForward pair for the GC charts (which only ever holds ONE
// level, since those charts have just one axis of "zoomed or not"), the
// flame graph has two composable, independently-repeatable zoom actions
// (click into a node, drag-narrow the viewport - see
// currentFlameGraphState's own comment) that a user can chain several times
// in a row (zoom into a node, then zoom into a child of that, then drag-
// narrow further). Backspace/swipe-back (flameGraphSwipeZoomOut) pops ONE
// level at a time from flameGraphBackStack - "undo my last zoom", not
// "reset everything" - mirroring a browser's own back button rather than a
// single-slot toggle. flameGraphForwardStack is the redo side, cleared
// whenever a NEW zoom action is taken (same "a fresh branch of history
// invalidates the old future" rule any undo/redo stack follows) - see
// pushFlameGraphHistory.
var flameGraphBackStack = [];
var flameGraphForwardStack = [];

// Entry point - called from snapshotGcStats.js's view switcher the first
// time the Flame Graph tab is shown. cpuProfile is
// gcData["cpuProfile"] (parsed once already - see that file's own
// cpuProfileJson).
function renderFlameGraph(containerElement, breadcrumbElement, resetButtonElement, tooltipElement, cpuProfile) {
    var dragOverlayElement = document.createElement('div');
    dragOverlayElement.className = 'flameGraphDragOverlay';

    currentFlameGraphState = {
        cpuProfile: cpuProfile,
        containerElement: containerElement,
        breadcrumbElement: breadcrumbElement,
        resetButtonElement: resetButtonElement,
        tooltipElement: tooltipElement,
        zoomChain: [{ node: cpuProfile.flameTree, label: "All Samples", isSyntheticRoot: true }],
        currentFrames: [],
        viewportStart: 0,
        viewportEnd: 100,
        dragOverlayElement: dragOverlayElement
    };
    flameGraphBackStack = [];
    flameGraphForwardStack = [];

    resetButtonElement.addEventListener('click', function () {
        resetFlameGraphZoomAll();
    });

    // Event delegation on the container (one listener for potentially
    // hundreds of frame boxes) rather than one listener per box - same
    // reasoning as this page's other delegated click handlers
    // (snapshotGcStats.js's drill-down panels).
    containerElement.addEventListener('click', function (event) {
        var frameElement = event.target.closest('.flameGraphFrame');
        if (!frameElement) {
            return;
        }

        var frameIndex = parseInt(frameElement.getAttribute('data-frame-index'), 10);
        var frame = currentFlameGraphState.currentFrames[frameIndex];
        if (!frame) {
            return;
        }

        pushFlameGraphHistory();

        currentFlameGraphState.zoomChain = frame.fullChain;
        // A fresh node navigation resets any in-progress viewport
        // magnification too (see currentFlameGraphState's own comment on
        // why these are two independent mechanisms) - a magnified sub-range
        // of the OLD view has no coherent meaning against the NEW one's own
        // scale, so showing the new subtree at full width is the only
        // sensible default.
        currentFlameGraphState.viewportStart = 0;
        currentFlameGraphState.viewportEnd = 100;
        renderCurrentFlameGraphZoom();
    });

    // Double-click on empty space (never on a frame itself - see the
    // frameElement check below) jumps all the way back to "All Samples",
    // same action as the Reset Zoom button. Deliberately excludes clicks
    // on an actual frame: overloading double-click there would mean each
    // half of it fires as a normal single click first (there's no reliable
    // way to tell a double-click's first click apart from a real single
    // click without delaying every plain zoom-in by the double-click
    // detection window), so it would first zoom in one level and only THEN
    // reset - a confusing flash rather than a clean gesture. Restricting
    // this to non-frame space (the flame graph's own background, reachable
    // wherever a branch doesn't reach every depth this capture's tallest
    // branch does) sidesteps that entirely, since a plain click there
    // already does nothing today.
    containerElement.addEventListener('dblclick', function (event) {
        if (event.target.closest('.flameGraphFrame')) {
            return;
        }

        resetFlameGraphZoomAll();
    });

    containerElement.addEventListener('mousemove', function (event) {
        var frameElement = event.target.closest('.flameGraphFrame');
        if (!frameElement) {
            hideFlameGraphTooltip();
            return;
        }

        var frameIndex = parseInt(frameElement.getAttribute('data-frame-index'), 10);
        var frame = currentFlameGraphState.currentFrames[frameIndex];
        if (!frame) {
            hideFlameGraphTooltip();
            return;
        }

        showFlameGraphTooltip(frame, event.clientX, event.clientY);
    });

    containerElement.addEventListener('mouseleave', function () {
        hideFlameGraphTooltip();
    });

    setupFlameGraphDragZoom(containerElement);

    renderCurrentFlameGraphZoom();
}

// Hand-rolled drag-to-zoom, mirroring media/chartZoomHelper.js's own
// attachDragToZoom for the GC timeline charts - plain mousedown/mousemove/
// mouseup listeners (no plugin/canvas machinery needed here, since this
// isn't a Chart.js instance), converting the dragged pixel range to a
// magnified sub-range of whatever's currently displayed. Unlike a click on
// a specific frame (layoutFlameGraph, which jumps to a NODE), this changes
// nothing about which node is zoomed to - only how much of its current
// picture is visible (viewportStart/viewportEnd - see
// currentFlameGraphState's own comment).
var FLAME_DRAG_THRESHOLD_PX = 8;

function setupFlameGraphDragZoom(containerElement) {
    var dragStartClientX = null;
    var dragCurrentClientX = null;
    var suppressNextClick = false;

    function updateDragOverlay() {
        var overlay = currentFlameGraphState.dragOverlayElement;
        var rect = containerElement.getBoundingClientRect();
        var leftClientX = Math.min(dragStartClientX, dragCurrentClientX);
        var rightClientX = Math.max(dragStartClientX, dragCurrentClientX);

        overlay.style.left = (leftClientX - rect.left) + 'px';
        overlay.style.width = (rightClientX - leftClientX) + 'px';
        overlay.style.display = 'block';
    }

    containerElement.addEventListener('mousedown', function (event) {
        // Left button only - a right-click/context-menu or middle-click
        // shouldn't start a drag selection.
        if (event.button !== 0) {
            return;
        }

        dragStartClientX = event.clientX;
        dragCurrentClientX = event.clientX;
    });

    // mousemove/mouseup on window rather than just the container, so a fast
    // drag that leaves the container bounds (or releases outside it) still
    // completes cleanly instead of leaving the drag state stuck active -
    // same reasoning as chartZoomHelper.js's own attachDragToZoom.
    window.addEventListener('mousemove', function (event) {
        if (dragStartClientX === null) {
            return;
        }

        dragCurrentClientX = event.clientX;
        updateDragOverlay();
    });

    window.addEventListener('mouseup', function () {
        if (dragStartClientX === null) {
            return;
        }

        currentFlameGraphState.dragOverlayElement.style.display = 'none';

        var distance = Math.abs(dragCurrentClientX - dragStartClientX);
        var startClientX = dragStartClientX;
        var endClientX = dragCurrentClientX;
        dragStartClientX = null;
        dragCurrentClientX = null;

        if (distance < FLAME_DRAG_THRESHOLD_PX) {
            return;
        }

        var rect = containerElement.getBoundingClientRect();
        if (rect.width <= 0) {
            return;
        }

        var leftPixel = Math.max(0, Math.min(rect.width, Math.min(startClientX, endClientX) - rect.left));
        var rightPixel = Math.max(0, Math.min(rect.width, Math.max(startClientX, endClientX) - rect.left));
        var leftDisplayPercent = (leftPixel / rect.width) * 100;
        var rightDisplayPercent = (rightPixel / rect.width) * 100;

        applyFlameGraphDragZoom(leftDisplayPercent, rightDisplayPercent);

        // The mouseup that ends a real drag still fires a native 'click'
        // event right afterward - suppressed below (capture-phase listener,
        // running before the bubble-phase click-to-zoom listener registered
        // in renderFlameGraph) so a drag doesn't *also* jump to whichever
        // frame happened to be under the cursor at the drag's end point.
        suppressNextClick = true;
    });

    containerElement.addEventListener('click', function (event) {
        if (!suppressNextClick) {
            return;
        }

        suppressNextClick = false;
        event.stopImmediatePropagation();
        event.preventDefault();
    }, true);
}

// leftDisplayPercent/rightDisplayPercent are 0-100 within whatever's
// CURRENTLY rendered (i.e. already inside the existing viewport, if any) -
// mapped here into absolute percentages of the zoomed node's own total
// (the same space viewportStart/viewportEnd themselves live in), so
// repeated drags compound (each one narrows further) rather than each
// being measured against the original unmagnified view.
function applyFlameGraphDragZoom(leftDisplayPercent, rightDisplayPercent) {
    var state = currentFlameGraphState;
    var currentSpan = state.viewportEnd - state.viewportStart;

    var newStart = state.viewportStart + (leftDisplayPercent / 100) * currentSpan;
    var newEnd = state.viewportStart + (rightDisplayPercent / 100) * currentSpan;

    // A degenerate (near-zero-width) selection is almost certainly an
    // imprecise drag, not a deliberate "magnify to nothing" - ignore it
    // rather than leaving the view in a useless state.
    if (newEnd - newStart < 0.01) {
        return;
    }

    pushFlameGraphHistory();

    state.viewportStart = newStart;
    state.viewportEnd = newEnd;
    renderCurrentFlameGraphZoom();
}

function showFlameGraphTooltip(frame, clientX, clientY) {
    var cpuProfile = currentFlameGraphState.cpuProfile;
    var rawName = frame.node.frame === -1 ? "<no stack captured>" : cpuProfile.methodNames[frame.node.frame];
    var totalSamples = frame.node.totalSamples;
    var percentOfCapture = cpuProfile.totalSampleCount > 0 ? (totalSamples * 100.0 / cpuProfile.totalSampleCount) : 0;

    var tooltipElement = currentFlameGraphState.tooltipElement;
    tooltipElement.innerHTML = `<div class="flameGraphTooltipName">${escapeHtmlForFlameGraph(rawName)}</div>` +
        `<div class="flameGraphTooltipStats">${totalSamples.toLocaleString()} samples (${percentOfCapture.toFixed(2)}% of capture)</div>`;

    tooltipElement.style.display = 'block';
    // Offset from the cursor so the tooltip never sits directly under it
    // (which would otherwise immediately trigger a mouseleave on itself,
    // since the tooltip isn't a descendant of containerElement).
    tooltipElement.style.left = (clientX + 12) + 'px';
    tooltipElement.style.top = (clientY + 12) + 'px';
}

function hideFlameGraphTooltip() {
    if (currentFlameGraphState) {
        currentFlameGraphState.tooltipElement.style.display = 'none';
    }
}

// A fresh single-entry zoomChain pointing at the synthetic root - shared by
// every place that resets to "All Samples" (initial render, Reset Zoom
// button, swipe-back) so they can't drift apart.
function makeSyntheticRootZoomChain() {
    return [{ node: currentFlameGraphState.cpuProfile.flameTree, label: "All Samples", isSyntheticRoot: true }];
}

// True whenever EITHER zoom mechanism (see currentFlameGraphState's own
// comment) is currently doing anything - shared by the Reset button's
// visibility and the swipe-back/forward gate below, so both agree on what
// "zoomed" means.
function isFlameGraphZoomed(state) {
    return state.zoomChain.length > 1 || state.viewportStart > 0 || state.viewportEnd < 100;
}

// Snapshots just the { zoomChain, viewportStart, viewportEnd } piece of
// state - safe to hold onto indefinitely in the history stacks below
// without it being mutated out from under them later, since every
// navigation action always REPLACES zoomChain/viewportStart/viewportEnd
// with fresh values rather than mutating the existing ones in place (see
// e.g. the frame-click handler above).
function snapshotFlameGraphView(state) {
    return { zoomChain: state.zoomChain, viewportStart: state.viewportStart, viewportEnd: state.viewportEnd };
}

// Call BEFORE applying any new zoom-changing action (node click, breadcrumb
// jump, drag-narrow, or a full reset) - pushes the state being LEFT onto
// flameGraphBackStack so a later step-back can return to it, and clears
// flameGraphForwardStack, since taking a new action from here invalidates
// whatever "redo" future the forward stack was holding (same rule a
// browser's own history follows: back then navigate somewhere new drops the
// old forward history).
function pushFlameGraphHistory() {
    flameGraphBackStack.push(snapshotFlameGraphView(currentFlameGraphState));
    flameGraphForwardStack = [];
}

function applyFlameGraphView(view) {
    currentFlameGraphState.zoomChain = view.zoomChain;
    currentFlameGraphState.viewportStart = view.viewportStart;
    currentFlameGraphState.viewportEnd = view.viewportEnd;
    renderCurrentFlameGraphZoom();
}

// The "zoom all the way out" action - the Reset Zoom button and the
// double-click-on-empty-space gesture (see renderFlameGraph) both call this
// directly. Goes through the same pushFlameGraphHistory as every other
// navigation (rather than a separate stash-and-clear), so stepping back
// afterward returns to exactly where the reset was triggered from, one
// step, same as undoing any other single action.
function resetFlameGraphZoomAll() {
    if (!isFlameGraphZoomed(currentFlameGraphState)) {
        return;
    }

    pushFlameGraphHistory();
    currentFlameGraphState.zoomChain = makeSyntheticRootZoomChain();
    currentFlameGraphState.viewportStart = 0;
    currentFlameGraphState.viewportEnd = 100;
    renderCurrentFlameGraphZoom();
}

// Undoes the single most recent zoom action (node click, breadcrumb jump,
// drag-narrow, OR a prior full reset - see pushFlameGraphHistory) rather
// than jumping all the way back to "All Samples" - "zoom in twice, go back
// once" lands on the first zoom, not the unzoomed root. Called from
// snapshotGcStats.js's performGoBackAction (macOS two-finger swipe-back /
// Backspace) - same "only fire if there's actually somewhere to go back to"
// contract as that file's own GC-chart/drill-down branches. Returns true
// iff it actually changed anything, so the caller only preventDefault()s a
// real action.
function flameGraphSwipeZoomOut() {
    if (!currentFlameGraphState || flameGraphBackStack.length === 0) {
        return false;
    }

    flameGraphForwardStack.push(snapshotFlameGraphView(currentFlameGraphState));
    var previousView = flameGraphBackStack.pop();
    applyFlameGraphView(previousView);
    return true;
}

// The forward/redo counterpart, called from performGoForwardAction - steps
// forward through whatever flameGraphSwipeZoomOut (or the Reset Zoom
// button, via resetFlameGraphZoomAll) most recently stepped back past.
function flameGraphSwipeZoomForward() {
    if (!currentFlameGraphState || flameGraphForwardStack.length === 0) {
        return false;
    }

    flameGraphBackStack.push(snapshotFlameGraphView(currentFlameGraphState));
    var nextView = flameGraphForwardStack.pop();
    applyFlameGraphView(nextView);
    return true;
}

// Rebuilds the breadcrumb + frame boxes for whatever
// currentFlameGraphState.zoomChain/viewportStart/viewportEnd currently is -
// called on initial render, on every zoom-in (frame click, drag-select),
// and on Reset Zoom.
function renderCurrentFlameGraphZoom() {
    var state = currentFlameGraphState;
    var layout = layoutFlameGraph(state.zoomChain);

    state.currentFrames = layout.frames;
    state.resetButtonElement.style.display = isFlameGraphZoomed(state) ? 'inline-block' : 'none';

    renderFlameGraphBreadcrumb();
    renderFlameGraphFrames(layout.frames);
}

function renderFlameGraphBreadcrumb() {
    var zoomChain = currentFlameGraphState.zoomChain;

    var parts = [];
    for (var chainIndex = 0; chainIndex < zoomChain.length; ++chainIndex) {
        var label = zoomChain[chainIndex].isSyntheticRoot ? zoomChain[chainIndex].label : flameGraphFrameLabel(zoomChain[chainIndex].label);
        parts.push(`<span class="flameGraphBreadcrumbEntry" data-chain-index="${chainIndex}">${escapeHtmlForFlameGraph(label)}</span>`);
    }

    currentFlameGraphState.breadcrumbElement.innerHTML = parts.join(' <span class="flameGraphBreadcrumbSep">&rsaquo;</span> ');

    var entries = currentFlameGraphState.breadcrumbElement.getElementsByClassName('flameGraphBreadcrumbEntry');
    for (var entryIndex = 0; entryIndex < entries.length; ++entryIndex) {
        entries[entryIndex].addEventListener('click', function (event) {
            var chainIndex = parseInt(event.currentTarget.getAttribute('data-chain-index'), 10);

            pushFlameGraphHistory();

            currentFlameGraphState.zoomChain = currentFlameGraphState.zoomChain.slice(0, chainIndex + 1);
            // Same reasoning as the frame-click handler in renderFlameGraph -
            // a direct jump to an ancestor is its own navigation: resets any
            // in-progress viewport magnification, not a reset.
            currentFlameGraphState.viewportStart = 0;
            currentFlameGraphState.viewportEnd = 100;
            renderCurrentFlameGraphZoom();
        });
    }
}

// left/width on each rendered frame are remapped from their RAW percentages
// (frame.left/frame.width, unaffected by the viewport - see layoutFlameGraph)
// into DISPLAY percentages against the current viewportStart/viewportEnd
// magnification (see currentFlameGraphState's own comment). A frame
// entirely outside [0, 100] after that remap is skipped rather than
// rendered off-screen, keeping the DOM small while zoomed into a narrow
// slice of a large tree.
function renderFlameGraphFrames(frames) {
    var state = currentFlameGraphState;
    var containerElement = state.containerElement;
    var viewportSpan = state.viewportEnd - state.viewportStart;

    var maxDepth = 0;
    var html = "";

    for (var index = 0; index < frames.length; ++index) {
        var frame = frames[index];
        var displayLeft = ((frame.left - state.viewportStart) / viewportSpan) * 100;
        var displayWidth = (frame.width / viewportSpan) * 100;

        if (displayLeft + displayWidth <= 0 || displayLeft >= 100) {
            continue;
        }

        if (frame.depth > maxDepth) {
            maxDepth = frame.depth;
        }

        var rawName = frame.node.frame === -1 ? "<no stack captured>" : state.cpuProfile.methodNames[frame.node.frame];
        var label = flameGraphFrameLabel(rawName);
        var unresolvedClass = rawName === "<no stack captured>" ? " flameGraphFrameUnresolved" : "";

        html += `<div class="flameGraphFrame${unresolvedClass}" data-frame-index="${index}" ` +
            `style="top:${frame.depth * FLAME_ROW_HEIGHT_PX}px; left:${displayLeft}%; width:${displayWidth}%;">` +
            `<span class="flameGraphFrameLabel">${escapeHtmlForFlameGraph(label)}</span>` +
            `</div>`;
    }

    containerElement.style.height = ((maxDepth + 1) * FLAME_ROW_HEIGHT_PX) + 'px';
    containerElement.innerHTML = html;

    // The drag-selection overlay (see setupFlameGraphDragZoom) is a
    // persistent element OUTSIDE this generated html - innerHTML above just
    // wiped it out along with everything else, so it needs re-attaching
    // after every render, not just the first one.
    containerElement.appendChild(state.dragOverlayElement);
}

// Returns { frames, total } - frames is a flat list (every visible node
// across every depth, in no particular render order) of
// { node, depth, left, width, fullChain }, where left/width are percentages
// of containerElement's own width and fullChain is the complete
// zoomChain-shaped ancestor path (from the TRUE synthetic root down to this
// exact node) a click on this frame should zoom to - see
// renderFlameGraph's click handler.
function layoutFlameGraph(zoomChain) {
    var frames = [];
    var zoomEntry = zoomChain[zoomChain.length - 1];

    if (zoomEntry.isSyntheticRoot) {
        var totalFromRoot = currentFlameGraphState.cpuProfile.totalSampleCount;
        layoutFlameGraphSiblings(zoomEntry.node.children, 0, totalFromRoot, 0, zoomChain, frames);
        return { frames: frames, total: totalFromRoot };
    }

    var total = zoomEntry.node.totalSamples;
    frames.push({ node: zoomEntry.node, depth: 0, left: 0, width: 100, fullChain: zoomChain });
    layoutFlameGraphSiblings(zoomEntry.node.children, 0, total, 1, zoomChain, frames);
    return { frames: frames, total: total };
}

function layoutFlameGraphSiblings(nodes, leftPercent, total, depth, ancestorChain, frames) {
    if (!nodes || total <= 0) {
        return;
    }

    var cursor = leftPercent;
    for (var index = 0; index < nodes.length; ++index) {
        var node = nodes[index];
        var widthPercent = (node.totalSamples / total) * 100;

        if (widthPercent >= FLAME_MIN_VISIBLE_WIDTH_PERCENT) {
            var rawName = node.frame === -1 ? "<no stack captured>" : currentFlameGraphState.cpuProfile.methodNames[node.frame];
            var chain = ancestorChain.concat([{ node: node, label: rawName, isSyntheticRoot: false }]);

            frames.push({ node: node, depth: depth, left: cursor, width: widthPercent, fullChain: chain });
            layoutFlameGraphSiblings(node.children, cursor, total, depth + 1, chain, frames);
        }

        cursor += widthPercent;
    }
}
