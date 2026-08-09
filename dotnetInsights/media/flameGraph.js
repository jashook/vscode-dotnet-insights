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

// Just the method name (no type prefix) - a flame graph frame is already
// narrow, and the type prefix is available in the tooltip/breadcrumb on
// hover/zoom instead. Mirrors drillDownStats.js's own
// "<no stack captured>" placeholder handling.
function flameGraphFrameLabel(rawName) {
    if (rawName === "<no stack captured>") {
        return rawName;
    }

    var lastDotIndex = rawName.lastIndexOf(".");
    return lastDotIndex === -1 ? rawName : rawName.slice(lastDotIndex + 1);
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
//   tooltipElement, zoomChain, currentFrames } - module-level since this
// whole file only ever manages one flame graph instance (the Profile view's
// single Flame Graph tab), the same single-instance assumption
// drillDownStats.js's own module-level pendingLazySubtrees/
// currentMethodNames make.
var currentFlameGraphState = null;

// The zoomChain most recently cleared by flameGraphSwipeZoomOut - lets a
// single forward swipe restore it, mirroring snapshotGcStats.js's own
// sharedZoomRange/zoomRangeForForward pair for the GC charts' swipe-back/
// forward gesture (see that file's performGoBackAction/
// performGoForwardAction, which call into the two functions below). Only
// ever holds ONE level of history, same as that pattern - not a full
// undo stack.
var flameGraphZoomChainForForward = null;

// Entry point - called from snapshotGcStats.js's view switcher the first
// time the Flame Graph tab is shown. cpuProfile is
// gcData["cpuProfile"] (parsed once already - see that file's own
// cpuProfileJson).
function renderFlameGraph(containerElement, breadcrumbElement, resetButtonElement, tooltipElement, cpuProfile) {
    currentFlameGraphState = {
        cpuProfile: cpuProfile,
        containerElement: containerElement,
        breadcrumbElement: breadcrumbElement,
        resetButtonElement: resetButtonElement,
        tooltipElement: tooltipElement,
        zoomChain: [{ node: cpuProfile.flameTree, label: "All Samples", isSyntheticRoot: true }],
        currentFrames: []
    };
    flameGraphZoomChainForForward = null;

    resetButtonElement.addEventListener('click', function () {
        resetFlameGraphZoom();
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

        currentFlameGraphState.zoomChain = frame.fullChain;
        // A fresh navigation, not a reset - drops any stale forward-restore
        // target rather than leaving it silently pointing at an unrelated
        // zoom state, same as snapshotGcStats.js's own applySharedZoom.
        flameGraphZoomChainForForward = null;
        renderCurrentFlameGraphZoom();
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

// Resets to "All Samples", stashing the zoomChain being cleared for one
// level of forward-restore - mirrors snapshotGcStats.js's own
// applySharedZoom(null) exactly (see that function's own comment): clearing
// an active zoom stashes it for forward, and any call here always resets
// (there's no separate "isForwardRestore" caller for the flame graph, since
// flameGraphSwipeZoomForward below restores directly via zoomChain
// assignment rather than routing back through this function).
function resetFlameGraphZoom() {
    flameGraphZoomChainForForward = currentFlameGraphState.zoomChain.length > 1 ? currentFlameGraphState.zoomChain : null;
    currentFlameGraphState.zoomChain = makeSyntheticRootZoomChain();
    renderCurrentFlameGraphZoom();
}

// Called from snapshotGcStats.js's performGoBackAction (macOS two-finger
// swipe-back / Backspace) - same "only fire if this view is actually
// zoomed, so going back does nothing surprising otherwise" contract as that
// file's own GC-chart/drill-down branches. Returns true iff it actually
// changed anything, so the caller only preventDefault()s a real action.
function flameGraphSwipeZoomOut() {
    if (!currentFlameGraphState || currentFlameGraphState.zoomChain.length <= 1) {
        return false;
    }

    resetFlameGraphZoom();
    return true;
}

// The forward counterpart, called from performGoForwardAction - restores
// whatever flameGraphSwipeZoomOut (or the Reset Zoom button) most recently
// cleared. Only meaningful while currently unzoomed, same as
// snapshotGcStats.js's own performGoForwardAction gates on
// `!sharedZoomRange`.
function flameGraphSwipeZoomForward() {
    if (!currentFlameGraphState || currentFlameGraphState.zoomChain.length > 1 || !flameGraphZoomChainForForward) {
        return false;
    }

    currentFlameGraphState.zoomChain = flameGraphZoomChainForForward;
    flameGraphZoomChainForForward = null;
    renderCurrentFlameGraphZoom();
    return true;
}

// Rebuilds the breadcrumb + frame boxes for whatever
// currentFlameGraphState.zoomChain currently ends with - called on initial
// render, on every zoom-in (frame click), and on Reset Zoom.
function renderCurrentFlameGraphZoom() {
    var state = currentFlameGraphState;
    var layout = layoutFlameGraph(state.zoomChain);

    state.currentFrames = layout.frames;
    state.resetButtonElement.style.display = state.zoomChain.length > 1 ? 'inline-block' : 'none';

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
            currentFlameGraphState.zoomChain = currentFlameGraphState.zoomChain.slice(0, chainIndex + 1);
            // Same reasoning as the frame-click handler above - a direct
            // jump to an ancestor is its own navigation, not a reset.
            flameGraphZoomChainForForward = null;
            renderCurrentFlameGraphZoom();
        });
    }
}

function renderFlameGraphFrames(frames) {
    var containerElement = currentFlameGraphState.containerElement;

    var maxDepth = 0;
    for (var frameIndex = 0; frameIndex < frames.length; ++frameIndex) {
        if (frames[frameIndex].depth > maxDepth) {
            maxDepth = frames[frameIndex].depth;
        }
    }

    containerElement.style.height = ((maxDepth + 1) * FLAME_ROW_HEIGHT_PX) + 'px';

    var html = "";
    for (var index = 0; index < frames.length; ++index) {
        var frame = frames[index];
        var rawName = frame.node.frame === -1 ? "<no stack captured>" : currentFlameGraphState.cpuProfile.methodNames[frame.node.frame];
        var label = flameGraphFrameLabel(rawName);
        var unresolvedClass = rawName === "<no stack captured>" ? " flameGraphFrameUnresolved" : "";

        html += `<div class="flameGraphFrame${unresolvedClass}" data-frame-index="${index}" ` +
            `style="top:${frame.depth * FLAME_ROW_HEIGHT_PX}px; left:${frame.left}%; width:${frame.width}%;">` +
            `<span class="flameGraphFrameLabel">${escapeHtmlForFlameGraph(label)}</span>` +
            `</div>`;
    }

    containerElement.innerHTML = html;
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
