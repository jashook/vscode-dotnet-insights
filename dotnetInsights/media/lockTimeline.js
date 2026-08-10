////////////////////////////////////////////////////////////////////////////////
// Module: lockTimeline.js
//
// Notes:
// Draws the Contention view's Lock Timeline tab: a Gantt-style track per lock
// (y axis) against capture-relative time (x axis), where each bar is one
// observed lock-ownership window colored by the thread that owned the lock.
// Data comes from contentionSummary.lockTimeline (see
// nettraceParser/Contention/ContentionJsonExporter.cs's WriteLockTimeline).
//
// Hand-drawn on a raw canvas rather than via Chart.js on purpose: this
// codebase is pinned to Chart.js 2.x (see CLAUDE.md), which has no
// floating/range bar type - arbitrary [start, end] bars only arrived in
// Chart.js 3 - so a Gantt is not expressible there at all. Drawing directly
// also keeps a real capture's ~9k segments cheap (one fillRect each, no
// per-element object model) and makes drag-to-zoom a pure viewport change
// rather than a chart teardown/rebuild.
//
// Semantics worth remembering when reading this: a segment is a window where
// `ownerThreadId` held the lock while `waiterThreadId` was BLOCKED on it. The
// CLR only emits contention events for contended locks, so the gaps between
// bars mean "nobody was blocked", not "the lock was free" - the panel says so
// in its own note (see ContentionRenderer.ts's renderLockTimelinePanel).
////////////////////////////////////////////////////////////////////////////////

(function () {
    // Row geometry, in CSS pixels.
    var LOCK_ROW_HEIGHT = 26;
    var LOCK_ROW_GAP = 4;
    var LOCK_LABEL_WIDTH = 150;
    // Time-axis labels live in this top band, not along the bottom: the
    // container scrolls vertically once a capture has more locks than fit
    // (40 tracks is ~1250px, well past the panel's max-height), so a
    // bottom axis is off-screen exactly when the chart is most useful. The
    // top band is what's visible on arrival.
    var CHART_PADDING_TOP = 30;
    var CHART_PADDING_BOTTOM = 8;
    var CHART_PADDING_RIGHT = 16;

    // A bar narrower than this would be invisible (or vanish entirely to a
    // sub-pixel fillRect), so every segment is drawn at least this wide -
    // the overwhelming majority of real contention waits are sub-millisecond
    // against a multi-minute capture, and dropping them would render an
    // almost entirely empty chart that misrepresents the data.
    var MIN_BAR_WIDTH_PX = 2;

    // Distinct, high-contrast hues for owner threads. Deliberately a fixed
    // palette indexed by a stable per-thread slot (not a hash of the thread
    // id) so the same thread keeps its color across redraws and filter
    // changes, and adjacent threads never collide.
    var OWNER_COLORS = [
        '#4e79a7', '#f28e2b', '#e15759', '#76b7b2', '#59a14f',
        '#edc948', '#b07aa1', '#ff9da7', '#9c755f', '#bab0ac',
        '#86bcb6', '#d37295', '#8cd17d', '#b6992d', '#499894',
        '#fabfd2', '#79706e', '#d7b5a6', '#a0cbe8', '#ffbe7d'
    ];

    // Owner thread id 0 means the runtime could not attribute an owner (~12%
    // of waits on a real capture). Rendered in a deliberately desaturated
    // gray so it reads as "unknown" rather than as just another thread.
    var UNKNOWN_OWNER_COLOR = '#8a8a8a';

    var state = null;

    // Undo/redo history of past [viewStartMSec, viewEndMSec] viewports,
    // mirroring flameGraph.js's own flameGraphBackStack/ForwardStack pair
    // and for the same reason: a user can drag-zoom repeatedly, narrowing
    // further each time, so "back" has to mean "undo my last zoom" (one
    // level) rather than "reset to the whole capture". The Reset button and
    // double-click still jump all the way out, but they push history too,
    // so a single step back after a reset returns to where the reset was
    // triggered from.
    var lockZoomBackStack = [];
    var lockZoomForwardStack = [];

    function snapshotView() {
        return { startMSec: state.viewStartMSec, endMSec: state.viewEndMSec };
    }

    // Call BEFORE applying any new viewport change. Clears the forward
    // stack because taking a new action invalidates the old redo future -
    // same rule flameGraph.js's pushFlameGraphHistory follows.
    function pushZoomHistory() {
        lockZoomBackStack.push(snapshotView());
        lockZoomForwardStack = [];
    }

    function applyView(view) {
        state.viewStartMSec = view.startMSec;
        state.viewEndMSec = view.endMSec;
        draw();
    }

    function isZoomed() {
        return state.viewStartMSec > state.fullStartMSec || state.viewEndMSec < state.fullEndMSec;
    }

    function colorForOwner(ownerThreadId, ownerColorSlots) {
        if (ownerThreadId === 0) {
            return UNKNOWN_OWNER_COLOR;
        }

        var slot = ownerColorSlots.get(ownerThreadId);
        if (slot === undefined) {
            slot = ownerColorSlots.size % OWNER_COLORS.length;
            ownerColorSlots.set(ownerThreadId, slot);
        }

        return OWNER_COLORS[slot];
    }

    function formatMSec(valueMSec) {
        if (valueMSec >= 1000) {
            return (valueMSec / 1000).toFixed(2) + ' s';
        }

        if (valueMSec >= 1) {
            return valueMSec.toFixed(2) + ' ms';
        }

        return (valueMSec * 1000).toFixed(1) + ' µs';
    }

    // Visible locks only - the filter checkboxes remove a lock's whole track
    // rather than merely hiding its bars, so the remaining tracks close up
    // and use the full canvas height.
    function visibleLocks() {
        var result = [];
        for (var index = 0; index < state.locks.length; ++index) {
            if (state.visibleByIndex[index]) {
                result.push({ lockEntry: state.locks[index], originalIndex: index });
            }
        }

        return result;
    }

    function draw() {
        if (!state) {
            return;
        }

        var canvas = state.canvas;
        var container = state.container;
        var rows = visibleLocks();

        var cssWidth = container.clientWidth;
        var cssHeight = CHART_PADDING_TOP + CHART_PADDING_BOTTOM + rows.length * (LOCK_ROW_HEIGHT + LOCK_ROW_GAP);
        if (cssHeight < CHART_PADDING_TOP + CHART_PADDING_BOTTOM + LOCK_ROW_HEIGHT) {
            cssHeight = CHART_PADDING_TOP + CHART_PADDING_BOTTOM + LOCK_ROW_HEIGHT;
        }

        // Backing store scaled by devicePixelRatio, CSS box left in CSS
        // pixels - without this the whole chart renders blurry on a HiDPI
        // display, which is the default on the machines this ships to.
        var ratio = window.devicePixelRatio || 1;
        canvas.width = Math.floor(cssWidth * ratio);
        canvas.height = Math.floor(cssHeight * ratio);
        canvas.style.width = cssWidth + 'px';
        canvas.style.height = cssHeight + 'px';

        var ctx = canvas.getContext('2d');
        ctx.setTransform(ratio, 0, 0, ratio, 0, 0);
        ctx.clearRect(0, 0, cssWidth, cssHeight);

        var plotLeft = LOCK_LABEL_WIDTH;
        var plotWidth = cssWidth - LOCK_LABEL_WIDTH - CHART_PADDING_RIGHT;
        if (plotWidth <= 0) {
            return;
        }

        var viewStart = state.viewStartMSec;
        var viewEnd = state.viewEndMSec;
        var viewSpan = viewEnd - viewStart;
        if (viewSpan <= 0) {
            return;
        }

        var bodyStyle = getComputedStyle(document.body);
        var textColor = bodyStyle.getPropertyValue('--vscode-foreground') || '#cccccc';
        var gridColor = 'rgba(128, 128, 128, 0.25)';

        ctx.font = '11px ' + (bodyStyle.fontFamily || 'sans-serif');
        ctx.textBaseline = 'middle';

        // ---- x axis gridlines + labels ----
        var tickCount = 6;
        ctx.strokeStyle = gridColor;
        ctx.fillStyle = textColor;
        ctx.lineWidth = 1;
        ctx.textAlign = 'center';

        for (var tickIndex = 0; tickIndex <= tickCount; ++tickIndex) {
            var tickFraction = tickIndex / tickCount;
            var tickX = plotLeft + tickFraction * plotWidth;
            // +0.5 keeps a 1px line on a crisp pixel boundary instead of
            // straddling two and rendering as a 2px blur.
            var crispX = Math.floor(tickX) + 0.5;

            ctx.beginPath();
            ctx.moveTo(crispX, CHART_PADDING_TOP - 4);
            ctx.lineTo(crispX, cssHeight - CHART_PADDING_BOTTOM);
            ctx.stroke();

            // Nudge the first/last labels inward so they aren't clipped by
            // the canvas edge (centered text at fraction 0/1 would hang half
            // its width outside the plot area).
            ctx.textAlign = tickIndex === 0 ? 'left' : (tickIndex === tickCount ? 'right' : 'center');
            ctx.fillText(formatMSec(viewStart + tickFraction * viewSpan), tickX, CHART_PADDING_TOP - 14);
        }

        // ---- one track per visible lock ----
        state.rowHitBoxes = [];
        ctx.textAlign = 'left';

        for (var rowIndex = 0; rowIndex < rows.length; ++rowIndex) {
            var row = rows[rowIndex];
            var lockEntry = row.lockEntry;
            var rowTop = CHART_PADDING_TOP + rowIndex * (LOCK_ROW_HEIGHT + LOCK_ROW_GAP);

            // Track background - a faint band so an empty stretch still
            // reads as "this lock's row", not as blank page.
            ctx.fillStyle = 'rgba(128, 128, 128, 0.10)';
            ctx.fillRect(plotLeft, rowTop, plotWidth, LOCK_ROW_HEIGHT);

            ctx.fillStyle = textColor;
            var labelText = lockEntry['lockId'];
            ctx.fillText(labelText, 4, rowTop + LOCK_ROW_HEIGHT / 2);

            var segments = lockEntry['segments'];
            var rowBoxes = [];

            for (var segmentIndex = 0; segmentIndex < segments.length; ++segmentIndex) {
                var segment = segments[segmentIndex];
                var segmentStart = segment['startMSec'];
                var segmentEnd = segment['endMSec'];

                if (segmentEnd < viewStart || segmentStart > viewEnd) {
                    continue;
                }

                var barLeft = plotLeft + ((segmentStart - viewStart) / viewSpan) * plotWidth;
                var barRight = plotLeft + ((segmentEnd - viewStart) / viewSpan) * plotWidth;
                var barWidth = barRight - barLeft;

                if (barWidth < MIN_BAR_WIDTH_PX) {
                    barWidth = MIN_BAR_WIDTH_PX;
                }

                // Clamp to the plot area so a segment straddling the viewport
                // edge doesn't paint over the lock labels.
                if (barLeft < plotLeft) {
                    barWidth -= (plotLeft - barLeft);
                    barLeft = plotLeft;
                }

                if (barLeft + barWidth > plotLeft + plotWidth) {
                    barWidth = plotLeft + plotWidth - barLeft;
                }

                if (barWidth <= 0) {
                    continue;
                }

                ctx.fillStyle = colorForOwner(segment['ownerThreadId'], state.ownerColorSlots);
                ctx.fillRect(barLeft, rowTop + 3, barWidth, LOCK_ROW_HEIGHT - 6);

                rowBoxes.push({ left: barLeft, width: barWidth, segment: segment });
            }

            state.rowHitBoxes.push({ top: rowTop, height: LOCK_ROW_HEIGHT, boxes: rowBoxes, lockEntry: lockEntry });
        }

        // ---- drag-to-zoom selection overlay ----
        if (state.dragStartX !== null && state.dragCurrentX !== null) {
            var dragLeft = Math.min(state.dragStartX, state.dragCurrentX);
            var dragRight = Math.max(state.dragStartX, state.dragCurrentX);
            ctx.fillStyle = 'rgba(100, 150, 220, 0.25)';
            ctx.fillRect(dragLeft, CHART_PADDING_TOP, dragRight - dragLeft, cssHeight - CHART_PADDING_TOP - CHART_PADDING_BOTTOM);
        }

        updateZoomLabel();
    }

    function updateZoomLabel() {
        var label = document.getElementById('lockTimelineZoomLabel');
        var resetBtn = document.getElementById('lockTimelineResetZoomBtn');
        if (!label || !resetBtn) {
            return;
        }

        if (isZoomed()) {
            label.textContent = 'Zoomed: ' + formatMSec(state.viewStartMSec) + ' – ' + formatMSec(state.viewEndMSec);
            resetBtn.style.display = '';
        } else {
            label.textContent = '';
            resetBtn.style.display = 'none';
        }
    }

    function pixelToMSec(pixelX) {
        var plotLeft = LOCK_LABEL_WIDTH;
        var plotWidth = state.container.clientWidth - LOCK_LABEL_WIDTH - CHART_PADDING_RIGHT;
        if (plotWidth <= 0) {
            return state.viewStartMSec;
        }

        var fraction = (pixelX - plotLeft) / plotWidth;
        if (fraction < 0) {
            fraction = 0;
        }

        if (fraction > 1) {
            fraction = 1;
        }

        return state.viewStartMSec + fraction * (state.viewEndMSec - state.viewStartMSec);
    }

    function findSegmentAt(offsetX, offsetY) {
        if (!state.rowHitBoxes) {
            return null;
        }

        for (var rowIndex = 0; rowIndex < state.rowHitBoxes.length; ++rowIndex) {
            var row = state.rowHitBoxes[rowIndex];
            if (offsetY < row.top || offsetY > row.top + row.height) {
                continue;
            }

            for (var boxIndex = 0; boxIndex < row.boxes.length; ++boxIndex) {
                var box = row.boxes[boxIndex];
                if (offsetX >= box.left && offsetX <= box.left + box.width) {
                    return { segment: box.segment, lockEntry: row.lockEntry };
                }
            }

            return null;
        }

        return null;
    }

    function showTooltip(hit, clientX, clientY) {
        var tooltip = document.getElementById('lockTimelineTooltip');
        if (!tooltip) {
            return;
        }

        var segment = hit.segment;
        var ownerText = segment['ownerThreadId'] === 0 ? 'unknown' : String(segment['ownerThreadId']);
        var durationMSec = segment['endMSec'] - segment['startMSec'];

        tooltip.innerHTML =
            '<div class="lockTooltipTitle">' + hit.lockEntry['lockId'] + '</div>' +
            '<div>Owner thread: <b>' + ownerText + '</b></div>' +
            '<div>Blocked thread: <b>' + segment['waiterThreadId'] + '</b></div>' +
            '<div>Held: ' + formatMSec(segment['startMSec']) + ' – ' + formatMSec(segment['endMSec']) + '</div>' +
            '<div>Blocked for: <b>' + formatMSec(durationMSec) + '</b></div>';

        tooltip.style.display = 'block';

        var containerRect = state.container.getBoundingClientRect();
        var left = clientX - containerRect.left + 12;
        var top = clientY - containerRect.top + 12;

        // Keep the tooltip inside the container - near the right edge it
        // would otherwise be clipped or force the panel to scroll.
        if (left + tooltip.offsetWidth > state.container.clientWidth) {
            left = state.container.clientWidth - tooltip.offsetWidth - 4;
        }

        tooltip.style.left = left + 'px';
        tooltip.style.top = top + 'px';
    }

    function hideTooltip() {
        var tooltip = document.getElementById('lockTimelineTooltip');
        if (tooltip) {
            tooltip.style.display = 'none';
        }
    }

    // "Zoom all the way out" - the Reset button and double-click. Goes
    // through pushZoomHistory like every other navigation so a step back
    // afterward returns to exactly where the reset was triggered from.
    function resetZoom() {
        if (!isZoomed()) {
            return;
        }

        pushZoomHistory();
        state.viewStartMSec = state.fullStartMSec;
        state.viewEndMSec = state.fullEndMSec;
        draw();
    }

    // Public entry point, called by snapshotGcStats.js when the Lock
    // Timeline tab is first shown (canvas has no layout width until its
    // panel is visible, so this cannot run at injection time).
    window.renderLockTimeline = function (lockTimelineJson) {
        var canvas = document.getElementById('lockTimelineCanvas');
        var container = document.getElementById('lockTimelineContainer');
        if (!canvas || !container || !lockTimelineJson) {
            return;
        }

        var locks = lockTimelineJson['locks'] || [];

        var isFirstInit = state === null || state.canvas !== canvas;

        if (isFirstInit) {
            var visible = [];
            for (var index = 0; index < locks.length; ++index) {
                visible.push(true);
            }

            state = {
                canvas: canvas,
                container: container,
                locks: locks,
                visibleByIndex: visible,
                fullStartMSec: lockTimelineJson['minRelativeMSec'],
                fullEndMSec: lockTimelineJson['maxRelativeMSec'],
                viewStartMSec: lockTimelineJson['minRelativeMSec'],
                viewEndMSec: lockTimelineJson['maxRelativeMSec'],
                ownerColorSlots: new Map(),
                rowHitBoxes: [],
                dragStartX: null,
                dragCurrentX: null
            };

            // A fresh chart starts with no history - otherwise a stack from
            // a previously-rendered capture would let "back" jump to a
            // viewport that has no meaning against this one's time range.
            lockZoomBackStack = [];
            lockZoomForwardStack = [];

            // Assign every owner a stable color slot up front, in the order
            // segments appear, so a thread's color never changes when locks
            // are filtered in and out.
            for (var lockIndex = 0; lockIndex < locks.length; ++lockIndex) {
                var segments = locks[lockIndex]['segments'];
                for (var segmentIndex = 0; segmentIndex < segments.length; ++segmentIndex) {
                    colorForOwner(segments[segmentIndex]['ownerThreadId'], state.ownerColorSlots);
                }
            }

            attachCanvasHandlers(canvas);
            paintFilterSwatches();
        }

        draw();
    };

    // The filter list's swatches are colored from the same per-thread slots
    // the bars use, but a LOCK can have several owners - so the swatch shows
    // the lock's own most frequent owner, which is what its track is mostly
    // painted with.
    function paintFilterSwatches() {
        for (var lockIndex = 0; lockIndex < state.locks.length; ++lockIndex) {
            var swatch = document.querySelector('[data-lock-swatch="' + lockIndex + '"]');
            if (!swatch) {
                continue;
            }

            var segments = state.locks[lockIndex]['segments'];
            var countByOwner = new Map();
            var dominantOwner = 0;
            var dominantCount = -1;

            for (var segmentIndex = 0; segmentIndex < segments.length; ++segmentIndex) {
                var owner = segments[segmentIndex]['ownerThreadId'];
                var nextCount = (countByOwner.get(owner) || 0) + 1;
                countByOwner.set(owner, nextCount);

                if (nextCount > dominantCount) {
                    dominantCount = nextCount;
                    dominantOwner = owner;
                }
            }

            swatch.style.backgroundColor = colorForOwner(dominantOwner, state.ownerColorSlots);
        }
    }

    function attachCanvasHandlers(canvas) {
        canvas.addEventListener('mousedown', function (event) {
            if (event.offsetX < LOCK_LABEL_WIDTH) {
                return;
            }

            state.dragStartX = event.offsetX;
            state.dragCurrentX = event.offsetX;
            hideTooltip();
        });

        canvas.addEventListener('mousemove', function (event) {
            if (state.dragStartX !== null) {
                state.dragCurrentX = event.offsetX;
                draw();
                return;
            }

            var hit = findSegmentAt(event.offsetX, event.offsetY);
            if (hit) {
                showTooltip(hit, event.clientX, event.clientY);
            } else {
                hideTooltip();
            }
        });

        canvas.addEventListener('mouseleave', function () {
            hideTooltip();
        });

        // Zoom commits on mouseup anywhere in the document, not just on the
        // canvas - releasing outside the canvas after a drag is common and
        // would otherwise leave the selection stuck on screen forever.
        document.addEventListener('mouseup', function (event) {
            if (state === null || state.dragStartX === null) {
                return;
            }

            var dragStart = state.dragStartX;
            var dragEnd = state.dragCurrentX;
            state.dragStartX = null;
            state.dragCurrentX = null;

            // A click (rather than a real drag) must not zoom to a zero-width
            // window - that would blank the chart with no way back except the
            // reset button.
            if (dragEnd === null || Math.abs(dragEnd - dragStart) < 4) {
                draw();
                return;
            }

            var startMSec = pixelToMSec(Math.min(dragStart, dragEnd));
            var endMSec = pixelToMSec(Math.max(dragStart, dragEnd));

            if (endMSec > startMSec) {
                pushZoomHistory();
                state.viewStartMSec = startMSec;
                state.viewEndMSec = endMSec;
            }

            draw();
        });

        canvas.addEventListener('dblclick', function () {
            resetZoom();
        });

        window.addEventListener('resize', function () {
            if (state !== null) {
                draw();
            }
        });
    }

    // Called by snapshotGcStats.js's filter wiring.
    window.setLockTimelineLockVisible = function (lockIndex, isVisible) {
        if (state === null) {
            return;
        }

        state.visibleByIndex[lockIndex] = isVisible;
        draw();
    };

    window.resetLockTimelineZoom = function () {
        if (state !== null) {
            resetZoom();
        }
    };

    // Undoes the single most recent viewport change (a drag-zoom, or a
    // prior full reset) rather than jumping straight back to the whole
    // capture - "zoom in twice, go back once" lands on the first zoom.
    // Called from snapshotGcStats.js's performGoBackAction (Backspace /
    // macOS two-finger swipe-back), and follows the same "return true iff
    // it actually did something" contract every other branch there does, so
    // the caller only preventDefault()s a real action.
    window.lockTimelineSwipeZoomOut = function () {
        if (state === null || lockZoomBackStack.length === 0) {
            return false;
        }

        lockZoomForwardStack.push(snapshotView());
        applyView(lockZoomBackStack.pop());
        return true;
    };

    window.lockTimelineSwipeZoomForward = function () {
        if (state === null || lockZoomForwardStack.length === 0) {
            return false;
        }

        lockZoomBackStack.push(snapshotView());
        applyView(lockZoomForwardStack.pop());
        return true;
    };
})();
