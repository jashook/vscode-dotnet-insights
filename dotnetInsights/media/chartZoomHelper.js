// Hand-rolled drag-to-zoom for Chart.js 2.9.4 charts. There is no bundled
// zoom plugin here (chartjs-plugin-zoom is a separate package this project
// doesn't depend on, matching the general preference elsewhere in this repo
// for a small hand-rolled implementation over a new webview dependency) -
// this attaches plain mousedown/mousemove/mouseup listeners directly to a
// chart's own canvas, converts the dragged pixel range to data values via
// the chart's own x-axis scale, and reports the result once on mouseup.
//
// Two chart types need two different pixel->value conversions (see
// pixelToMSecLinear/pixelToMSecCategory below): the allocation-rate chart's
// x-axis is `linear` (Chart.js's Chart.js:13069 getValueForPixel returns a
// real data value directly), while the type-timeline chart's x-axis is a
// plain `category` scale (Chart.js:12658 getValueForPixel returns a
// *clamped index* into the labels array, not a value) - verified directly
// against the bundled node_modules/chart.js/dist/Chart.js source rather
// than assumed, since the two scale types' contracts differ silently.

// Default x-axis id Chart.js assigns when a chart's xAxes[0] config doesn't
// set one explicitly (see Chart.js:5182/5794) - both charts here rely on
// this default rather than setting an id themselves.
var CHART_ZOOM_DEFAULT_X_AXIS_ID = "x-axis-0";

// Minimum drag distance (in pixels) before a mousedown+mouseup is treated as
// a zoom-select rather than a plain click - below this, the gesture is left
// alone so the chart's own onClick (e.g. the type-timeline chart's
// drill-down) still fires normally.
var CHART_ZOOM_DRAG_THRESHOLD_PX = 8;

function pixelToMSecLinear(chart, pixelX) {
    var xScale = chart.scales[CHART_ZOOM_DEFAULT_X_AXIS_ID];
    return xScale.getValueForPixel(pixelX);
}

// bucketStartMSecs: parallel array to the category chart's labels, giving
// each label's real bucketStartMSec (labels themselves are pre-formatted
// display strings - see formatElapsedMsForAllocationChart - not usable as
// data values on their own).
function pixelToMSecCategory(chart, pixelX, bucketStartMSecs) {
    var xScale = chart.scales[CHART_ZOOM_DEFAULT_X_AXIS_ID];
    var index = xScale.getValueForPixel(pixelX);
    var clampedIndex = Math.max(0, Math.min(bucketStartMSecs.length - 1, Math.round(index)));
    return bucketStartMSecs[clampedIndex];
}

// Chart.js 2.x plugin (registered per-instance via the chart's own
// constructor-time `plugins: [...]` array - see the existing
// tickCountAnnotationPlugin precedent in allocationStats.js) that draws the
// in-progress drag selection as a translucent rectangle spanning the full
// height of the chart area.
//
// dragStateHolder is a plain { current: null | {startPixelX, currentPixelX} }
// object shared with attachDragToZoom below, not a snapshot - this plugin
// has to be constructed and handed to `new Chart(...)` *before* the chart
// instance exists, but attachDragToZoom needs the chart instance to attach
// its listeners, so the two can't share state via a closure over the chart
// itself. A shared mutable holder object breaks that ordering dependency.
function createZoomSelectionPlugin(dragStateHolder) {
    return {
        afterDraw: function (chartInstance) {
            var dragState = dragStateHolder.current;
            if (!dragState) {
                return;
            }

            var ctx = chartInstance.ctx;
            var area = chartInstance.chartArea;
            var left = Math.min(dragState.startPixelX, dragState.currentPixelX);
            var right = Math.max(dragState.startPixelX, dragState.currentPixelX);

            ctx.save();
            ctx.fillStyle = "rgba(72, 83, 136, 0.25)";
            ctx.fillRect(left, area.top, right - left, area.bottom - area.top);
            ctx.strokeStyle = "rgba(72, 83, 136, 0.8)";
            ctx.lineWidth = 1;
            ctx.strokeRect(left, area.top, right - left, area.bottom - area.top);
            ctx.restore();
        }
    };
}

// Attaches drag-to-select zoom behavior to one Chart.js instance's canvas.
//   chart            - the Chart.js instance already bound to canvasElement.
//   canvasElement    - that same chart's <canvas>.
//   dragStateHolder  - the same holder object passed to
//                       createZoomSelectionPlugin for this chart, so the
//                       plugin can see this drag as it happens.
//   pixelToMSecFn(chart, pixelX) - pixelToMSecLinear or a bound call to
//                       pixelToMSecCategory, depending on this chart's axis.
//   onRangeSelected(startMSec, endMSec) - called once, on mouseup, only if
//                       the drag moved at least CHART_ZOOM_DRAG_THRESHOLD_PX.
//
// Returns { detach() } - callers must call detach() before destroying the
// chart (chart.destroy() does not remove listeners this function attached
// separately via addEventListener), since the same <canvas> element
// persists across zoom changes and gets a *new* Chart instance bound to it
// each time - without detaching first, old listeners referencing the
// stale/destroyed chart would keep piling up.
function attachDragToZoom(chart, canvasElement, dragStateHolder, pixelToMSecFn, onRangeSelected) {
    var redrawScheduled = false;
    var suppressNextClick = false;

    function scheduleRedraw() {
        if (redrawScheduled) {
            return;
        }
        redrawScheduled = true;
        window.requestAnimationFrame(function () {
            redrawScheduled = false;
            // draw(), not update() - the data/scales haven't changed, only
            // the selection-box overlay has, so a full layout recalculation
            // (what update() does) is unnecessary work on every mousemove.
            chart.draw();
        });
    }

    function onMouseDown(event) {
        var rect = canvasElement.getBoundingClientRect();
        var pixelX = event.clientX - rect.left;
        var area = chart.chartArea;
        if (!area || pixelX < area.left || pixelX > area.right) {
            return;
        }

        dragStateHolder.current = { startPixelX: pixelX, currentPixelX: pixelX };
    }

    function onMouseMove(event) {
        if (!dragStateHolder.current) {
            return;
        }

        var rect = canvasElement.getBoundingClientRect();
        var area = chart.chartArea;
        var pixelX = event.clientX - rect.left;
        dragStateHolder.current.currentPixelX = Math.max(area.left, Math.min(area.right, pixelX));
        scheduleRedraw();
    }

    function onMouseUp() {
        var dragState = dragStateHolder.current;
        if (!dragState) {
            return;
        }

        dragStateHolder.current = null;
        chart.draw();

        var distance = Math.abs(dragState.currentPixelX - dragState.startPixelX);
        if (distance < CHART_ZOOM_DRAG_THRESHOLD_PX) {
            return;
        }

        var leftPixel = Math.min(dragState.startPixelX, dragState.currentPixelX);
        var rightPixel = Math.max(dragState.startPixelX, dragState.currentPixelX);
        var startMSec = pixelToMSecFn(chart, leftPixel);
        var endMSec = pixelToMSecFn(chart, rightPixel);

        if (endMSec <= startMSec) {
            return;
        }

        // The mouseup that ends a real drag still fires a native 'click'
        // event right afterward - suppressed below (capture-phase listener,
        // running before Chart.js's own bubble-phase onClick) so a drag
        // doesn't *also* trigger the type-timeline chart's drill-down click
        // handler for whatever segment happened to be under the cursor.
        suppressNextClick = true;

        onRangeSelected(startMSec, endMSec);
    }

    function onClickCapture(event) {
        if (!suppressNextClick) {
            return;
        }

        suppressNextClick = false;
        event.stopImmediatePropagation();
        event.preventDefault();
    }

    canvasElement.addEventListener("mousedown", onMouseDown);
    // mousemove/mouseup on window rather than just the canvas, so a fast
    // drag that leaves the canvas bounds (or releases outside it) still
    // completes cleanly instead of leaving dragStateHolder stuck non-null.
    window.addEventListener("mousemove", onMouseMove);
    window.addEventListener("mouseup", onMouseUp);
    canvasElement.addEventListener("click", onClickCapture, true);

    return {
        detach: function () {
            canvasElement.removeEventListener("mousedown", onMouseDown);
            window.removeEventListener("mousemove", onMouseMove);
            window.removeEventListener("mouseup", onMouseUp);
            canvasElement.removeEventListener("click", onClickCapture, true);
        }
    };
}
