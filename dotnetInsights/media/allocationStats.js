// Script run within the webview itself - renders the "Heap Contents" view's
// allocation-rate chart (ticks coalesced into fixed-width time buckets,
// each a bar annotated with its event count) against
// gcData["allocationSummary"]["ticks"] (every raw GC/AllocationTick sample
// - see AllocationJsonExporter.cs's AllocationSummaryBuilder.Build). Kept
// as its own file (not folded into the already ~760-line
// snapshotGcStats.js) since it only applies to nettrace sources - called
// from snapshotGcStats.js's view-switcher click handler the first time the
// "Heap Contents" nav button is clicked.

var DEFAULT_BUCKET_WIDTH_MSEC = 1000;

// One color per typeTimeline column, in order - typeTimeline.types is
// always [...top ChartTopTypesLimit types, "Other"] (AllocationJsonExporter.cs),
// so this needs at most ChartTopTypesLimit + 1 = 9 entries; the last one
// (gray) is reserved for "Other" by always landing on that final slot.
// Deliberately a different palette from the gen0/gen1/gen2/LOH colors used
// elsewhere on this page - those mean "GC generation" and reusing them here
// (for "allocated type") would be actively misleading.
var TYPE_TIMELINE_COLORS = [
    "rgba(31, 119, 180, 0.8)",
    "rgba(255, 127, 14, 0.8)",
    "rgba(44, 160, 44, 0.8)",
    "rgba(214, 39, 40, 0.8)",
    "rgba(148, 103, 189, 0.8)",
    "rgba(140, 86, 75, 0.8)",
    "rgba(227, 119, 194, 0.8)",
    "rgba(188, 189, 34, 0.8)",
    "rgba(127, 127, 127, 0.8)"
];

function formatElapsedMsForAllocationChart(ms) {
    if (ms < 1000) {
        return `${Math.round(ms)}ms`;
    }

    var totalSeconds = ms / 1000;
    if (totalSeconds < 60) {
        return `${totalSeconds.toFixed(1)}s`;
    }

    var minutes = Math.floor(totalSeconds / 60);
    var seconds = Math.round(totalSeconds % 60);
    return `${minutes}m ${seconds}s`;
}

// Sums AllocationAmount for every raw tick with RelativeMSec in
// [startMSec, endMSec) - ticks is already sorted by RelativeMSec
// (AllocationJsonExporter.cs), so this could binary-search, but a busy
// capture's tick count (tens of thousands) is still cheap to scan linearly
// against the handful of GC boundaries this is called for.
function sumTicksInRange(ticks, startMSec, endMSec) {
    var totalBytes = 0;
    for (var tickIndex = 0; tickIndex < ticks.length; ++tickIndex) {
        var relativeMSec = ticks[tickIndex]["RelativeMSec"];
        if (relativeMSec >= startMSec && relativeMSec < endMSec) {
            totalBytes += ticks[tickIndex]["AllocationAmount"];
        }
    }
    return totalBytes;
}

// One disjoint horizontal segment per GC in gcTimesMSec - NOT a connected
// staircase: each segment spans [previous same-gen GC's time, this GC's
// time] (or [0, this GC's time] for the first one) at a height equal to the
// bytes allocated in just that window, then a null point breaks the line so
// Chart.js doesn't connect it to the next segment's (different) height.
// "how much was allocated before this GC triggered", plotted as its own
// isolated line per collection, not a running/cumulative total.
function buildAllocationBeforeGcSegments(ticks, gcTimesMSec, mb) {
    var points = [];
    var previousTimeMSec = 0;
    for (var gcIndex = 0; gcIndex < gcTimesMSec.length; ++gcIndex) {
        var gcTimeMSec = gcTimesMSec[gcIndex];
        var totalBytes = sumTicksInRange(ticks, previousTimeMSec, gcTimeMSec);
        var heightMb = totalBytes / mb;

        points.push({ x: previousTimeMSec, y: heightMb });
        points.push({ x: gcTimeMSec, y: heightMb });
        points.push({ x: gcTimeMSec, y: null });

        previousTimeMSec = gcTimeMSec;
    }
    return points;
}

// Coalesces raw ticks into fixed-width time buckets (default 1s - a
// per-tick plot is unreadable and, per Chart.js 2.9.4's `bar` controller,
// unreliable to render at 10k+ irregularly-spaced points anyway). Each
// bucket carries both the mb allocated and how many ticks contributed to
// it, so the chart can annotate bar height with event count.
function buildTickBuckets(ticks, bucketWidthMSec) {
    if (ticks.length === 0) {
        return [];
    }

    var captureEndMSec = ticks[ticks.length - 1]["RelativeMSec"];
    var bucketCount = Math.floor(captureEndMSec / bucketWidthMSec) + 1;

    var bucketBytes = new Array(bucketCount);
    var bucketTickCounts = new Array(bucketCount);
    for (var initIndex = 0; initIndex < bucketCount; ++initIndex) {
        bucketBytes[initIndex] = 0;
        bucketTickCounts[initIndex] = 0;
    }

    for (var tickIndex = 0; tickIndex < ticks.length; ++tickIndex) {
        var tick = ticks[tickIndex];

        var bucketIndex = Math.floor(tick["RelativeMSec"] / bucketWidthMSec);
        if (bucketIndex >= bucketCount) {
            bucketIndex = bucketCount - 1;
        }

        bucketBytes[bucketIndex] += tick["AllocationAmount"];
        ++bucketTickCounts[bucketIndex];
    }

    var buckets = [];
    for (var bucketIdx = 0; bucketIdx < bucketCount; ++bucketIdx) {
        buckets.push({
            startMSec: bucketIdx * bucketWidthMSec,
            totalBytes: bucketBytes[bucketIdx],
            tickCount: bucketTickCounts[bucketIdx]
        });
    }
    return buckets;
}

// One vertical "spike" per bucket: (x, 0) -> (x, value, tickCount) ->
// (x, null) - same disjoint-segment technique as
// buildAllocationBeforeGcSegments. Chart.js 2.9.4's `bar` controller
// unconditionally positions bars via scale.getPixelForValue(null, index,
// ...) (an index/category-scale assumption - see getRuler in
// node_modules/chart.js/dist/Chart.js), which resolves to NaN on our
// `linear` x-axis and silently renders nothing. A thick-stroked line spike
// reuses the rendering path already proven to work on this axis instead.
function buildBucketSpikes(buckets, mb) {
    var points = [];
    for (var bucketIndex = 0; bucketIndex < buckets.length; ++bucketIndex) {
        var bucket = buckets[bucketIndex];
        var heightMb = bucket.totalBytes / mb;

        points.push({ x: bucket.startMSec, y: 0 });
        points.push({ x: bucket.startMSec, y: heightMb, tickCount: bucket.tickCount });
        points.push({ x: bucket.startMSec, y: null });
    }
    return points;
}

// Draws each spike's tick count just above its peak - scoped to this one
// chart instance (passed via the `plugins` array in the Chart config below,
// not Chart.plugins.register) so it doesn't affect the memory/pause-time
// charts elsewhere on this page. Only the peak point of each spike carries
// a `tickCount` (the baseline/null points from buildBucketSpikes don't), so
// no index-arithmetic is needed to skip them.
var tickCountAnnotationPlugin = {
    afterDatasetsDraw: function (chartInstance) {
        var meta = chartInstance.getDatasetMeta(0);
        if (!meta || meta.hidden) {
            return;
        }

        var dataset = chartInstance.data.datasets[0];
        var ctx = chartInstance.ctx;

        ctx.save();
        ctx.fillStyle = "rgba(120, 120, 120, 0.9)";
        ctx.font = "10px sans-serif";
        ctx.textAlign = "center";
        ctx.textBaseline = "bottom";

        for (var pointIndex = 0; pointIndex < meta.data.length; ++pointIndex) {
            var point = dataset.data[pointIndex];
            if (!point || point.tickCount === undefined) {
                continue;
            }

            var position = meta.data[pointIndex].tooltipPosition();
            ctx.fillText(point.tickCount, position.x, position.y - 4);
        }

        ctx.restore();
    }
};

// gen0GcTimesMSec/gen1GcTimesMSec: ascending PauseStartRelativeMSec values
// for Gen0/Gen1 GCs (see snapshotGcStats.js's view-switcher click handler -
// allocationStats.js has no access to the parsed gcs array itself). Both are
// optional; a generation with no GCs simply gets no overlay line.
//
// zoomOptions (optional): { range: {startMSec, endMSec} | null,
// onRangeSelected: function(startMSec, endMSec) }. When range is set, the
// x-axis is clipped to it (via ticks.min/max - a `linear` scale can do this
// directly without filtering the underlying data at all, unlike the
// category-scale type-timeline chart below, which has to filter its bucket
// array instead - see renderAllocationTypeTimelineChart's own comment).
// When onRangeSelected is set, dragging across the chart calls it with the
// selected range (see chartZoomHelper.js) - omit zoomOptions entirely for a
// plain, non-interactive chart.
//
// Returns { chart, zoomHandle } (zoomHandle is null unless zoomOptions.
// onRangeSelected was provided) - callers that re-render on zoom changes
// need both: chart.destroy() before creating the replacement, and
// zoomHandle.detach() first, since the same <canvas> persists across
// zoom changes and stale listeners would otherwise pile up on it.
function renderAllocationTimelineChart(canvasElement, ticks, gen0GcTimesMSec, gen1GcTimesMSec, zoomOptions) {
    if (canvasElement === null || canvasElement === undefined || !ticks || ticks.length === 0) {
        return null;
    }

    var mb = 1024 * 1024;

    var buckets = buildTickBuckets(ticks, DEFAULT_BUCKET_WIDTH_MSEC);

    // Two y-axes: even bucketed per-second totals can be a very different
    // scale from a GC-boundary segment's total (which can span many
    // seconds' worth of allocation) - separate axes keep either from
    // flattening the other.
    var datasets = [{
        type: 'line',
        label: 'Allocated per Second (mb)',
        data: buildBucketSpikes(buckets, mb),
        yAxisID: 'tickAxis',
        fill: false,
        lineTension: 0,
        spanGaps: false,
        pointRadius: 0,
        // Thick enough to read as a bar rather than a thin line, since
        // buckets are now few and sparse (see buildBucketSpikes).
        borderWidth: 10,
        borderColor: "rgba(174, 207, 27, 0.7)",
        backgroundColor: "rgba(174, 207, 27, 0.7)"
    }];

    if (gen0GcTimesMSec && gen0GcTimesMSec.length > 0) {
        datasets.push({
            type: 'line',
            label: 'Allocated Before Gen0 GC (mb)',
            data: buildAllocationBeforeGcSegments(ticks, gen0GcTimesMSec, mb),
            yAxisID: 'gcAxis',
            fill: false,
            // Straight lines only - each segment is a flat horizontal pair
            // of points, so bezier fitting has nothing to add and (per the
            // GC pause-time chart's own sharp-vertical-pulse lesson) can
            // only introduce overshoot artifacts around the null breaks.
            lineTension: 0,
            spanGaps: false,
            borderColor: "rgba(72, 83, 136, 1)",
            backgroundColor: "rgba(72, 83, 136, 1)",
            borderWidth: 2,
            pointRadius: 2
        });
    }

    if (gen1GcTimesMSec && gen1GcTimesMSec.length > 0) {
        datasets.push({
            type: 'line',
            label: 'Allocated Before Gen1 GC (mb)',
            data: buildAllocationBeforeGcSegments(ticks, gen1GcTimesMSec, mb),
            yAxisID: 'gcAxis',
            fill: false,
            lineTension: 0,
            spanGaps: false,
            borderColor: "rgba(141, 31, 95, 1)",
            backgroundColor: "rgba(141, 31, 95, 1)",
            borderWidth: 2,
            pointRadius: 2
        });
    }

    var context = canvasElement.getContext('2d');

    var zoomRange = zoomOptions && zoomOptions.range;
    var xAxisTicks = {
        callback: formatElapsedMsForAllocationChart
    };
    if (zoomRange) {
        // `linear` scale - clipping the visible range this way needs no
        // filtering of the underlying ticks/buckets data at all, unlike the
        // category-scale type-timeline chart.
        xAxisTicks.min = zoomRange.startMSec;
        xAxisTicks.max = zoomRange.endMSec;
    }

    // Shared with the zoom-selection plugin below (see chartZoomHelper.js's
    // own header comment for why this can't just be a closure variable -
    // the plugin has to exist before the Chart instance it's attached to).
    var dragStateHolder = { current: null };
    var plugins = [tickCountAnnotationPlugin];
    if (zoomOptions && zoomOptions.onRangeSelected) {
        plugins.push(createZoomSelectionPlugin(dragStateHolder));
    }

    var chart = new Chart(context, {
        type: 'line',
        data: {
            datasets: datasets
        },
        // Scoped to this chart instance only - see tickCountAnnotationPlugin.
        plugins: plugins,
        options: {
            title: {
                display: true,
                text: `Sampled Allocation Rate Over Time (per ${(DEFAULT_BUCKET_WIDTH_MSEC / 1000).toFixed(0)}s, bar label = tick count)`
            },
            // Instant redraws while dragging a zoom selection (see
            // chartZoomHelper.js's scheduleRedraw) - an animated transition
            // firing on every mousemove would make the selection box lag
            // visibly behind the cursor.
            animation: {
                duration: 0
            },
            // Chart-wide fallback matching each line dataset's own
            // lineTension: 0 - see buildAllocationBeforeGcSegments.
            elements: {
                line: {
                    tension: 0
                }
            },
            scales: {
                xAxes: [{
                    type: 'linear',
                    position: 'bottom',
                    ticks: xAxisTicks,
                    scaleLabel: {
                        display: true,
                        labelString: "Capture Time Elapsed"
                    }
                }],
                yAxes: [{
                    id: 'tickAxis',
                    position: 'left',
                    ticks: {
                        beginAtZero: true,
                        // Default tick generation shows raw floating-point mb
                        // values (many decimal places) - cap display at 2.
                        callback: function (value) {
                            return value.toFixed(2);
                        }
                    },
                    scaleLabel: {
                        display: true,
                        labelString: "Allocated per Second (mb)"
                    }
                }, {
                    id: 'gcAxis',
                    position: 'right',
                    ticks: {
                        beginAtZero: true,
                        callback: function (value) {
                            return value.toFixed(2);
                        }
                    },
                    scaleLabel: {
                        display: true,
                        labelString: "Allocated Before GC (mb)"
                    },
                    // Avoid doubled-up gridlines from two overlapping axes -
                    // tickAxis's gridlines are enough.
                    gridLines: {
                        drawOnChartArea: false
                    }
                }]
            },
            tooltips: {
                // The GC-boundary lines' null points (the break between one
                // disjoint segment and the next - see
                // buildAllocationBeforeGcSegments) aren't real values and
                // shouldn't show up as a "NaN" tooltip line.
                filter: function (tooltipItem, tooltipData) {
                    var point = tooltipData.datasets[tooltipItem.datasetIndex].data[tooltipItem.index];
                    return point && point.y !== null;
                },
                callbacks: {
                    // Default label callback would print the raw
                    // unrounded mb value - same 2-decimal cap as the axis.
                    label: function (tooltipItem, tooltipData) {
                        var dataset = tooltipData.datasets[tooltipItem.datasetIndex];
                        var value = `${dataset.label}: ${parseFloat(tooltipItem.yLabel).toFixed(2)}`;

                        var point = dataset.data[tooltipItem.index];
                        if (point && point.tickCount !== undefined) {
                            value += ` (${point.tickCount} ticks)`;
                        }

                        return value;
                    }
                }
            },
            "maintainAspectRatio": false
        }
    });

    var zoomHandle = null;
    if (zoomOptions && zoomOptions.onRangeSelected) {
        zoomHandle = attachDragToZoom(chart, canvasElement, dragStateHolder, pixelToMSecLinear, zoomOptions.onRangeSelected);
    }

    return { chart: chart, zoomHandle: zoomHandle };
}

// Stacked bar chart, one bar per bucket, one stacked segment per type -
// against gcData["allocationSummary"]["typeTimeline"] (see
// AllocationJsonExporter.cs's AllocationSummaryBuilder.BuildTypeTimeline).
// Unlike renderAllocationTimelineChart above, this uses a real Chart.js
// `bar` chart on a plain category x-axis (one label per bucket) rather than
// a linear one - stacking genuinely needs the bar controller (there's no
// line-based approximation for it), and the bar controller's width
// calculation (getRuler -> scale.getPixelForValue(null, index, ...)) only
// works correctly against a category scale, which resolves that null value
// via index instead of trying to use it as a linear value (see
// renderAllocationTimelineChart's own comment on why a `linear` x-axis
// broke bars entirely). Buckets are evenly spaced by construction, so a
// category axis loses nothing here.
// onSegmentClick(typeIndex, bucketIndex) - called when a real (non-"Other")
// stacked segment is clicked, so the caller can look up and render that
// cell's drillDown data (see snapshotGcStats.js's view-switcher wiring and
// drillDownStats.js's renderDrillDownTable). Optional - omit for a
// read-only chart. bucketIndex is always the *absolute* index into the
// server's original typeTimeline.buckets/drillDown.cells numbering, even
// when zoomOptions.range narrows what's actually displayed (see below) -
// AllocationJsonExporter.cs's drillDown.cells keys are
// "{typeIndex}:{bucketIndex}" against the full, unfiltered bucket list, so
// a locally-re-indexed (0-based within just the zoomed subset) bucketIndex
// would silently look up the wrong cell - or none at all - once zoomed in.
//
// zoomOptions (optional): { range: {startMSec, endMSec} | null,
// onRangeSelected: function(startMSec, endMSec) }. Unlike the rate chart's
// linear x-axis (which can just clip its visible range via ticks.min/max),
// a category scale has no such "narrow the view but keep the same
// underlying axis" concept - the labels/data arrays *are* the axis - so
// zooming here means filtering typeTimeline.buckets down to the sub-range
// before ever building labels/datasets from it.
function renderAllocationTypeTimelineChart(canvasElement, typeTimeline, onSegmentClick, zoomOptions) {
    if (canvasElement === null || canvasElement === undefined || !typeTimeline || !typeTimeline["buckets"] || typeTimeline["buckets"].length === 0) {
        return null;
    }

    var mb = 1024 * 1024;
    var types = typeTimeline["types"];
    var allBuckets = typeTimeline["buckets"];

    // firstVisibleBucketAbsoluteIndex is added back onto every local
    // (filtered-array) index before it's ever handed to onSegmentClick or
    // used to compute a zoom-drag's bucketStartMSecs lookup, so both stay
    // correct against the server's absolute numbering regardless of the
    // current zoom range.
    var zoomRange = zoomOptions && zoomOptions.range;
    var firstVisibleBucketAbsoluteIndex = 0;
    var buckets = allBuckets;
    if (zoomRange) {
        // A bucket qualifies if it *overlaps* [startMSec, endMSec), not just
        // if its own start falls inside that window - a zoom range dragged
        // on a different chart (e.g. a GC chart's much finer elapsed-ms
        // axis, now that one zoom range is shared across the whole webview -
        // see snapshotGcStats.js's applySharedZoom) has no reason to align
        // to this chart's bucket boundaries, and a narrow range landing
        // entirely inside one wide bucket (bucketStartMSec before
        // zoomRange.startMSec, but bucketStartMSec + bucketWidthMSec still
        // after it) used to match zero buckets under a start-only test,
        // silently returning null below and making the whole chart vanish.
        var bucketWidthMSec = typeTimeline["bucketWidthMSec"] || 0;
        buckets = [];
        for (var scanIndex = 0; scanIndex < allBuckets.length; ++scanIndex) {
            var bucketStartMSec = allBuckets[scanIndex]["bucketStartMSec"];
            var bucketEndMSec = bucketStartMSec + bucketWidthMSec;
            if (bucketEndMSec <= zoomRange.startMSec || bucketStartMSec >= zoomRange.endMSec) {
                continue;
            }
            if (buckets.length === 0) {
                firstVisibleBucketAbsoluteIndex = scanIndex;
            }
            buckets.push(allBuckets[scanIndex]);
        }

        if (buckets.length === 0) {
            return null;
        }
    }

    var labels = [];
    var bucketStartMSecs = [];
    for (var bucketIndex = 0; bucketIndex < buckets.length; ++bucketIndex) {
        labels.push(formatElapsedMsForAllocationChart(buckets[bucketIndex]["bucketStartMSec"]));
        bucketStartMSecs.push(buckets[bucketIndex]["bucketStartMSec"]);
    }

    var datasets = [];
    for (var typeIndex = 0; typeIndex < types.length; ++typeIndex) {
        var typeData = [];
        for (var bucketIdx = 0; bucketIdx < buckets.length; ++bucketIdx) {
            typeData.push(buckets[bucketIdx]["bytesByType"][typeIndex] / mb);
        }

        datasets.push({
            type: 'bar',
            label: types[typeIndex],
            data: typeData,
            backgroundColor: TYPE_TIMELINE_COLORS[typeIndex % TYPE_TIMELINE_COLORS.length],
            borderWidth: 0
        });
    }

    var context = canvasElement.getContext('2d');

    // Shared with the zoom-selection plugin below (see chartZoomHelper.js's
    // own header comment for why this can't just be a closure variable -
    // the plugin has to exist before the Chart instance it's attached to).
    var dragStateHolder = { current: null };
    var plugins = [];
    if (zoomOptions && zoomOptions.onRangeSelected) {
        plugins.push(createZoomSelectionPlugin(dragStateHolder));
    }

    var chart = new Chart(context, {
        type: 'bar',
        data: {
            labels: labels,
            datasets: datasets
        },
        plugins: plugins,
        options: {
            title: {
                display: true,
                text: `Allocated by Type Over Time (per ${(typeTimeline["bucketWidthMSec"] / 1000).toFixed(0)}s)`
            },
            // Instant redraws while dragging a zoom selection - see the rate
            // chart's own identical comment on this.
            animation: {
                duration: 0
            },
            scales: {
                xAxes: [{
                    stacked: true,
                    scaleLabel: {
                        display: true,
                        labelString: "Capture Time Elapsed"
                    }
                }],
                yAxes: [{
                    stacked: true,
                    ticks: {
                        beginAtZero: true,
                        callback: function (value) {
                            return value.toFixed(2);
                        }
                    },
                    scaleLabel: {
                        display: true,
                        labelString: "Allocated (mb)"
                    }
                }]
            },
            tooltips: {
                callbacks: {
                    label: function (tooltipItem, tooltipData) {
                        var datasetLabel = tooltipData.datasets[tooltipItem.datasetIndex].label;
                        return `${datasetLabel}: ${parseFloat(tooltipItem.yLabel).toFixed(2)} mb`;
                    }
                }
            },
            onClick: function (event) {
                if (!onSegmentClick) {
                    return;
                }

                var elements = this.getElementAtEvent(event);
                if (!elements || elements.length === 0) {
                    return;
                }

                var clickedTypeIndex = elements[0]._datasetIndex;
                var clickedLocalBucketIndex = elements[0]._index;

                // Last dataset is always "Other" (AllocationJsonExporter.cs) -
                // a heterogeneous catch-all across many types, not drillable.
                if (clickedTypeIndex === datasets.length - 1) {
                    return;
                }

                // Translate back to the server's absolute bucket numbering -
                // see this function's own header comment on why the local
                // (filtered-array) index isn't safe to hand to onSegmentClick
                // directly once zoomed in.
                onSegmentClick(clickedTypeIndex, clickedLocalBucketIndex + firstVisibleBucketAbsoluteIndex);
            },
            "maintainAspectRatio": false
        }
    });

    var zoomHandle = null;
    if (zoomOptions && zoomOptions.onRangeSelected) {
        zoomHandle = attachDragToZoom(chart, canvasElement, dragStateHolder, function (chartArg, pixelX) {
            return pixelToMSecCategory(chartArg, pixelX, bucketStartMSecs);
        }, zoomOptions.onRangeSelected);
    }

    return { chart: chart, zoomHandle: zoomHandle };
}
