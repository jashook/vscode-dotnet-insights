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
// also keeps a real capture's ~10k segments cheap (one fillRect each, no
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
    // Row geometry, in CSS pixels. LOCK_ROW_HEIGHT is the preferred height;
    // draw() shrinks it toward MIN_LOCK_ROW_HEIGHT when many tracks are
    // shown at once (see fitRowHeight).
    var LOCK_ROW_HEIGHT = 26;
    var MIN_LOCK_ROW_HEIGHT = 4;
    var LOCK_ROW_GAP = 4;
    var LOCK_LABEL_WIDTH = 150;
    // Below this row height a lock id is unreadable anyway, so labels are
    // dropped rather than drawn as overlapping smears - the sidebar list and
    // the tooltip still identify every track.
    var LABEL_MIN_ROW_HEIGHT = 11;
    var CHART_PADDING_TOP = 30;
    var CHART_PADDING_BOTTOM = 8;
    var CHART_PADDING_RIGHT = 16;

    // Browsers cap canvas dimensions (Chromium's limit is 32767px per side,
    // and the BACKING store is cssHeight * devicePixelRatio, so a HiDPI
    // display hits it at half the CSS height). Exceeding it silently yields
    // a blank canvas, which is exactly the failure the "All" option would
    // otherwise trigger - 1447 locks at full row height is ~43000px. Both
    // the CSS height and the effective pixel ratio are clamped below.
    var MAX_CANVAS_CSS_HEIGHT = 12000;
    var MAX_CANVAS_BACKING_PX = 16384;

    var MIN_BAR_WIDTH_PX = 2;

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
    // mirroring flameGraph.js's own back/forward stacks and for the same
    // reason: a user can drag-zoom repeatedly, so "back" has to mean "undo
    // my last zoom" (one level) rather than "reset to the whole capture".
    var lockZoomBackStack = [];
    var lockZoomForwardStack = [];

    function snapshotView() {
        return { startMSec: state.viewStartMSec, endMSec: state.viewEndMSec };
    }

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

    // True when this segment survives the current thread filter. The filter
    // matches EITHER role deliberately: "focus on thread N" means both "what
    // was N holding up" and "what was N blocked behind", and needing two
    // separate controls to ask those would be worse than one that answers
    // both at once (the tooltip still names each role explicitly).
    function segmentMatchesThreadFilter(segment) {
        if (state.threadFilterId === null) {
            return true;
        }

        return segment['ownerThreadId'] === state.threadFilterId || segment['waiterThreadId'] === state.threadFilterId;
    }

    function lockHasMatchingSegment(lockEntry) {
        if (state.threadFilterId === null) {
            return true;
        }

        var segments = lockEntry['segments'];
        for (var segmentIndex = 0; segmentIndex < segments.length; ++segmentIndex) {
            if (segmentMatchesThreadFilter(segments[segmentIndex])) {
                return true;
            }
        }

        return false;
    }

    // The locks that actually get a track right now: within the Top-N slice,
    // not unchecked in the sidebar, and (when a thread filter is active)
    // holding at least one matching segment. Locks are already sorted by
    // total wait descending server-side, so "top N" is just a prefix.
    function visibleLocks() {
        var result = [];
        var limit = state.topLockCount === null ? state.locks.length : Math.min(state.topLockCount, state.locks.length);

        for (var index = 0; index < limit; ++index) {
            if (!state.visibleByIndex[index]) {
                continue;
            }

            if (!lockHasMatchingSegment(state.locks[index])) {
                continue;
            }

            result.push({ lockEntry: state.locks[index], originalIndex: index });
        }

        return result;
    }

    // Shrinks the per-row height so every visible track fits inside the
    // canvas size cap, rather than letting a large "Show All" selection
    // silently blank the canvas.
    function fitRowHeight(rowCount) {
        if (rowCount === 0) {
            return LOCK_ROW_HEIGHT;
        }

        var available = MAX_CANVAS_CSS_HEIGHT - CHART_PADDING_TOP - CHART_PADDING_BOTTOM;
        var perRow = available / rowCount;

        if (perRow >= LOCK_ROW_HEIGHT + LOCK_ROW_GAP) {
            return LOCK_ROW_HEIGHT;
        }

        var fitted = Math.floor(perRow) - LOCK_ROW_GAP;
        return fitted < MIN_LOCK_ROW_HEIGHT ? MIN_LOCK_ROW_HEIGHT : fitted;
    }

    function draw() {
        if (!state) {
            return;
        }

        var canvas = state.canvas;
        var container = state.container;
        var rows = visibleLocks();
        var rowHeight = fitRowHeight(rows.length);
        state.rowHeight = rowHeight;

        var cssWidth = container.clientWidth;
        var cssHeight = CHART_PADDING_TOP + CHART_PADDING_BOTTOM + rows.length * (rowHeight + LOCK_ROW_GAP);
        var minHeight = CHART_PADDING_TOP + CHART_PADDING_BOTTOM + LOCK_ROW_HEIGHT;
        if (cssHeight < minHeight) {
            cssHeight = minHeight;
        }

        // Clamp the backing store as well as the CSS box - on a 2x display
        // the backing store is what actually hits the browser's limit first.
        var ratio = window.devicePixelRatio || 1;
        if (cssHeight * ratio > MAX_CANVAS_BACKING_PX) {
            ratio = MAX_CANVAS_BACKING_PX / cssHeight;
        }

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

        // ---- x axis gridlines + labels (top band - see CHART_PADDING_TOP) ----
        var tickCount = 6;
        ctx.strokeStyle = gridColor;
        ctx.fillStyle = textColor;
        ctx.lineWidth = 1;

        for (var tickIndex = 0; tickIndex <= tickCount; ++tickIndex) {
            var tickFraction = tickIndex / tickCount;
            var tickX = plotLeft + tickFraction * plotWidth;
            var crispX = Math.floor(tickX) + 0.5;

            ctx.beginPath();
            ctx.moveTo(crispX, CHART_PADDING_TOP - 4);
            ctx.lineTo(crispX, cssHeight - CHART_PADDING_BOTTOM);
            ctx.stroke();

            ctx.textAlign = tickIndex === 0 ? 'left' : (tickIndex === tickCount ? 'right' : 'center');
            ctx.fillText(formatMSec(viewStart + tickFraction * viewSpan), tickX, CHART_PADDING_TOP - 14);
        }

        // ---- one track per visible lock ----
        state.rowHitBoxes = [];
        ctx.textAlign = 'left';

        var drawLabels = rowHeight >= LABEL_MIN_ROW_HEIGHT;

        for (var rowIndex = 0; rowIndex < rows.length; ++rowIndex) {
            var row = rows[rowIndex];
            var lockEntry = row.lockEntry;
            var rowTop = CHART_PADDING_TOP + rowIndex * (rowHeight + LOCK_ROW_GAP);
            var isSelected = state.selectedLockIndex === row.originalIndex;

            ctx.fillStyle = isSelected ? 'rgba(100, 150, 220, 0.28)' : 'rgba(128, 128, 128, 0.10)';
            ctx.fillRect(plotLeft, rowTop, plotWidth, rowHeight);

            if (drawLabels) {
                ctx.fillStyle = textColor;
                if (isSelected) {
                    ctx.font = 'bold 11px ' + (bodyStyle.fontFamily || 'sans-serif');
                }

                ctx.fillText(lockEntry['lockId'], 4, rowTop + rowHeight / 2);

                if (isSelected) {
                    ctx.font = '11px ' + (bodyStyle.fontFamily || 'sans-serif');
                }
            }

            var segments = lockEntry['segments'];
            var rowBoxes = [];
            var barTop = rowTop + (rowHeight > 8 ? 3 : 1);
            var barHeight = rowHeight - (rowHeight > 8 ? 6 : 2);
            if (barHeight < 1) {
                barHeight = 1;
            }

            for (var segmentIndex = 0; segmentIndex < segments.length; ++segmentIndex) {
                var segment = segments[segmentIndex];

                if (!segmentMatchesThreadFilter(segment)) {
                    continue;
                }

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
                ctx.fillRect(barLeft, barTop, barWidth, barHeight);

                rowBoxes.push({ left: barLeft, width: barWidth, segment: segment });
            }

            state.rowHitBoxes.push({ top: rowTop, height: rowHeight, boxes: rowBoxes, lockEntry: lockEntry, originalIndex: row.originalIndex });
        }

        // ---- drag-to-zoom selection overlay ----
        if (state.dragStartX !== null && state.dragCurrentX !== null) {
            var dragLeft = Math.min(state.dragStartX, state.dragCurrentX);
            var dragRight = Math.max(state.dragStartX, state.dragCurrentX);
            ctx.fillStyle = 'rgba(100, 150, 220, 0.25)';
            ctx.fillRect(dragLeft, CHART_PADDING_TOP, dragRight - dragLeft, cssHeight - CHART_PADDING_TOP - CHART_PADDING_BOTTOM);
        }

        updateZoomLabel();
        updateFilterHeaderLabel(rows.length);
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

    function updateFilterHeaderLabel(shownCount) {
        var header = document.getElementById('lockFilterHeaderLabel');
        if (header) {
            header.textContent = 'Locks (' + shownCount.toLocaleString() + ' shown)';
        }
    }

    function pixelToMSec(pixelX) {
        var plotWidth = state.container.clientWidth - LOCK_LABEL_WIDTH - CHART_PADDING_RIGHT;
        if (plotWidth <= 0) {
            return state.viewStartMSec;
        }

        var fraction = (pixelX - LOCK_LABEL_WIDTH) / plotWidth;
        if (fraction < 0) {
            fraction = 0;
        }

        if (fraction > 1) {
            fraction = 1;
        }

        return state.viewStartMSec + fraction * (state.viewEndMSec - state.viewStartMSec);
    }

    function findRowAt(offsetY) {
        if (!state.rowHitBoxes) {
            return null;
        }

        for (var rowIndex = 0; rowIndex < state.rowHitBoxes.length; ++rowIndex) {
            var row = state.rowHitBoxes[rowIndex];
            if (offsetY >= row.top && offsetY <= row.top + row.height) {
                return row;
            }
        }

        return null;
    }

    function findSegmentAt(offsetX, offsetY) {
        var row = findRowAt(offsetY);
        if (!row) {
            return null;
        }

        for (var boxIndex = 0; boxIndex < row.boxes.length; ++boxIndex) {
            var box = row.boxes[boxIndex];
            if (offsetX >= box.left && offsetX <= box.left + box.width) {
                return { segment: box.segment, lockEntry: row.lockEntry };
            }
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

    function resetZoom() {
        if (!isZoomed()) {
            return;
        }

        pushZoomHistory();
        state.viewStartMSec = state.fullStartMSec;
        state.viewEndMSec = state.fullEndMSec;
        draw();
    }

    // ---- sidebar lock list ----

    // Rebuilt whenever the Top-N slice changes, so the list always describes
    // exactly the tracks that can be drawn. Locks the thread filter has
    // emptied stay in the list (greyed) rather than disappearing - a lock
    // vanishing from the list because of an unrelated control reads as data
    // loss.
    function renderLockFilterList() {
        var list = document.getElementById('lockFilterList');
        if (!list) {
            return;
        }

        var limit = state.topLockCount === null ? state.locks.length : Math.min(state.topLockCount, state.locks.length);
        var html = '';

        for (var index = 0; index < limit; ++index) {
            var lockEntry = state.locks[index];
            var dimmed = lockHasMatchingSegment(lockEntry) ? '' : ' lockFilterItemDimmed';
            var selected = state.selectedLockIndex === index ? ' lockFilterItemSelected' : '';
            var checked = state.visibleByIndex[index] ? ' checked' : '';

            html += '<label class="lockFilterItem' + dimmed + selected + '" data-lock-row="' + index + '">' +
                '<input type="checkbox" class="lockFilterCheckbox" data-lock-index="' + index + '"' + checked + '>' +
                '<span class="lockFilterSwatch" style="background-color:' + dominantColorForLock(lockEntry) + '"></span>' +
                '<span class="lockFilterId" data-lock-select="' + index + '">' + lockEntry['lockId'] + '</span>' +
                '<span class="lockFilterStat">' + lockEntry['totalWaitMSec'].toFixed(1) + ' ms · ' + lockEntry['contentionCount'].toLocaleString() + '</span>' +
                '</label>';
        }

        list.innerHTML = html;
    }

    // A lock can have several owners, so its swatch shows the owner that
    // holds it most often - the color its track is mostly painted with.
    function dominantColorForLock(lockEntry) {
        var segments = lockEntry['segments'];
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

        return colorForOwner(dominantOwner, state.ownerColorSlots);
    }

    // ---- thread filter ----

    function renderThreadFilterOptions() {
        var select = document.getElementById('lockThreadFilterSelect');
        if (!select) {
            return;
        }

        // Counted across every lock, both roles, so the dropdown ranks
        // threads by how involved in contention they actually are rather
        // than by thread id.
        var involvementByThread = new Map();

        for (var lockIndex = 0; lockIndex < state.locks.length; ++lockIndex) {
            var segments = state.locks[lockIndex]['segments'];
            for (var segmentIndex = 0; segmentIndex < segments.length; ++segmentIndex) {
                var segment = segments[segmentIndex];
                var owner = segment['ownerThreadId'];
                var waiter = segment['waiterThreadId'];

                if (owner !== 0) {
                    involvementByThread.set(owner, (involvementByThread.get(owner) || 0) + 1);
                }

                if (waiter !== 0 && waiter !== owner) {
                    involvementByThread.set(waiter, (involvementByThread.get(waiter) || 0) + 1);
                }
            }
        }

        var threads = [];
        involvementByThread.forEach(function (count, threadId) {
            threads.push({ threadId: threadId, count: count });
        });
        threads.sort(function (left, right) { return right.count - left.count; });

        var html = '<option value="all">All threads (' + threads.length + ')</option>';
        for (var threadIndex = 0; threadIndex < threads.length; ++threadIndex) {
            html += '<option value="' + threads[threadIndex].threadId + '">' +
                threads[threadIndex].threadId + ' (' + threads[threadIndex].count.toLocaleString() + ')</option>';
        }

        select.innerHTML = html;
    }

    // ---- per-lock stack panel ----

    // Renders the selected lock's folded caller tree, which the exporter
    // emits in exactly the shape siteDrillDown uses - so this reuses
    // contentionDrillDownStats.js's own buildInlineContentionSiteCallerTree
    // verbatim, and the interior expand clicks are already handled by
    // wireContentionTab's delegated listener on #view-contention.
    function renderStackPanelForLock(lockIndex) {
        var panel = document.getElementById('lockStackPanel');
        var title = document.getElementById('lockStackTitle');
        var body = document.getElementById('lockStackBody');
        if (!panel || !title || !body) {
            return;
        }

        var lockEntry = state.locks[lockIndex];
        var drillDown = lockEntry['drillDown'];

        title.textContent = 'Contended stacks for ' + lockEntry['lockId'] +
            ' — ' + lockEntry['totalWaitMSec'].toFixed(1) + ' ms across ' +
            lockEntry['contentionCount'].toLocaleString() + ' contentions';

        if (!drillDown) {
            // Null (not an empty tree) means no wait on this lock was
            // stack-walked - a fact about the capture, not about the code.
            body.innerHTML = '<p style="padding:8px;margin:0">No stacks were captured for this lock.</p>';
        } else {
            body.innerHTML = buildInlineContentionSiteCallerTree(drillDown, state.methodNames, drillDown['totalWaitMSec']);
        }

        panel.style.display = '';
    }

    function selectLock(lockIndex) {
        state.selectedLockIndex = lockIndex;
        renderStackPanelForLock(lockIndex);
        renderLockFilterList();
        draw();

        var panel = document.getElementById('lockStackPanel');
        if (panel) {
            panel.scrollIntoView({ block: 'nearest' });
        }
    }

    // ---- public entry points ----

    window.renderLockTimeline = function (lockTimelineJson, methodNames) {
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
                methodNames: methodNames || [],
                visibleByIndex: visible,
                topLockCount: 40,
                threadFilterId: null,
                selectedLockIndex: -1,
                rowHeight: LOCK_ROW_HEIGHT,
                fullStartMSec: lockTimelineJson['minRelativeMSec'],
                fullEndMSec: lockTimelineJson['maxRelativeMSec'],
                viewStartMSec: lockTimelineJson['minRelativeMSec'],
                viewEndMSec: lockTimelineJson['maxRelativeMSec'],
                ownerColorSlots: new Map(),
                rowHitBoxes: [],
                dragStartX: null,
                dragCurrentX: null
            };

            lockZoomBackStack = [];
            lockZoomForwardStack = [];

            // Assign every owner a stable color slot up front, in segment
            // order, so a thread keeps its color as locks/threads are
            // filtered in and out.
            for (var lockIndex = 0; lockIndex < locks.length; ++lockIndex) {
                var segments = locks[lockIndex]['segments'];
                for (var segmentIndex = 0; segmentIndex < segments.length; ++segmentIndex) {
                    colorForOwner(segments[segmentIndex]['ownerThreadId'], state.ownerColorSlots);
                }
            }

            attachCanvasHandlers(canvas);
            renderThreadFilterOptions();
            renderLockFilterList();
        }

        draw();
    };

    window.setLockTimelineLockVisible = function (lockIndex, isVisible) {
        if (state === null) {
            return;
        }

        state.visibleByIndex[lockIndex] = isVisible;
        draw();
    };

    window.setLockTimelineTopCount = function (topCountOrNull) {
        if (state === null) {
            return;
        }

        state.topLockCount = topCountOrNull;
        renderLockFilterList();
        draw();
    };

    window.setLockTimelineThreadFilter = function (threadIdOrNull) {
        if (state === null) {
            return;
        }

        state.threadFilterId = threadIdOrNull;
        renderLockFilterList();
        draw();
    };

    window.selectLockTimelineLock = function (lockIndex) {
        if (state !== null) {
            selectLock(lockIndex);
        }
    };

    window.closeLockTimelineStackPanel = function () {
        if (state === null) {
            return;
        }

        state.selectedLockIndex = -1;
        var panel = document.getElementById('lockStackPanel');
        if (panel) {
            panel.style.display = 'none';
        }

        renderLockFilterList();
        draw();
    };

    window.resetLockTimelineZoom = function () {
        if (state !== null) {
            resetZoom();
        }
    };

    // Undoes the single most recent viewport change (a drag-zoom, or a prior
    // full reset) rather than jumping straight back to the whole capture.
    // Called from snapshotGcStats.js's performGoBackAction, with the same
    // "return true iff it actually did something" contract as every other
    // branch there.
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

    function attachCanvasHandlers(canvas) {
        canvas.addEventListener('mousedown', function (event) {
            // Clicks in the label gutter select a lock (and open its stacks)
            // rather than starting a zoom drag - dragging there has no
            // meaningful x-axis interpretation anyway.
            if (event.offsetX < LOCK_LABEL_WIDTH) {
                var row = findRowAt(event.offsetY);
                if (row) {
                    selectLock(row.originalIndex);
                }

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
        document.addEventListener('mouseup', function () {
            if (state === null || state.dragStartX === null) {
                return;
            }

            var dragStart = state.dragStartX;
            var dragEnd = state.dragCurrentX;
            state.dragStartX = null;
            state.dragCurrentX = null;

            // A click (rather than a real drag) must not zoom to a
            // zero-width window - that would blank the chart with no way
            // back except the reset button.
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
})();
