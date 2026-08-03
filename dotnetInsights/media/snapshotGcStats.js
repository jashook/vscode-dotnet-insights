// Script run within the webview itself.

var allocationDatasets = {};

(function () {

    // Get a reference to the VS Code webview api.
    // We use this API to post messages back to our extension.

    // @ts-ignore
    const vscode = acquireVsCodeApi();
  
    console.time("gcParsing");
    var gcs = JSON.parse(document.getElementById("hiddenData").textContent);
    console.timeEnd("gcParsing");

    console.time("gcCountsByGenParsing");
    var gcCountsByGen = JSON.parse(document.getElementById("gcCountsByGen").textContent);
    console.timeEnd("gcCountsByGenParsing");

    var totalTimeInEachGcJson = JSON.parse(document.getElementById("totalTimeInEachGcJson").textContent);

    // null when sourceFormat !== "nettrace" or the capture had zero
    // allocation ticks - see GcSnapshotRenderer.ts's hasHeapContents.
    // Includes every raw allocation tick (see AllocationJsonExporter.cs) -
    // potentially tens of thousands for a busy capture - but JSON.parse
    // itself is cheap regardless (the earlier perf work on this page found
    // DOM construction, not JSON parsing, to be the actual cost - see
    // detailTableHtml below), so this is still parsed eagerly like
    // gcCountsByGen; only the chart/table DOM built from it is deferred to
    // the "Heap Contents" nav button's first click.
    var allocationSummaryJson = JSON.parse(document.getElementById("allocationSummaryJson").textContent);

    // Full human-readable form for tooltips (space isn't constrained there
    // the way it is on an axis tick) - mirrors GcDetailTableRenderer.ts's
    // formatHumanDateTime exactly, e.g. "21-Jul-2026 03:42:13 PM PDT".
    var formatHumanDateTime = function (dateTimeString) {
        if (dateTimeString === undefined || dateTimeString === null) {
            return "";
        }

        if (dateTimeString.charAt(0) === '+') {
            return dateTimeString;
        }

        var parsed = new Date(dateTimeString);
        if (isNaN(parsed.getTime())) {
            return dateTimeString;
        }

        var parts = new Intl.DateTimeFormat('en-US', {
            day: '2-digit',
            month: 'short',
            year: 'numeric',
            hour: '2-digit',
            minute: '2-digit',
            second: '2-digit',
            hour12: true,
            timeZoneName: 'short'
        }).formatToParts(parsed);

        var partsByType = {};
        for (var partIndex = 0; partIndex < parts.length; ++partIndex) {
            partsByType[parts[partIndex].type] = parts[partIndex].value;
        }

        return `${partsByType["day"]}-${partsByType["month"]}-${partsByType["year"]} ${partsByType["hour"]}:${partsByType["minute"]}:${partsByType["second"]} ${partsByType["dayPeriod"]} ${partsByType["timeZoneName"]}`;
    };

    // Tooltip title for every GC-over-time chart - each point carries its
    // own gcId/dateTime (see buildGcPointSeries), since all these charts are
    // now on a shared linear (elapsed-ms) x-axis rather than a category axis
    // keyed by array index.
    var linearGcTooltipTitle = function (tooltipItems, tooltipData) {
        var lines = [];
        for (var itemIndex = 0; itemIndex < tooltipItems.length; ++itemIndex) {
            var tooltipItem = tooltipItems[itemIndex];
            var point = tooltipData.datasets[tooltipItem.datasetIndex].data[tooltipItem.index];
            lines.push(`GC #${point.gcId} — ${formatHumanDateTime(point.dateTime)}`);
        }
        return lines;
    };

    // One point per GC: {x: elapsed ms, y: value, gcId, dateTime} - shared
    // shape for every GC-over-time chart (Usage, Fragmentation, per-Heap) now
    // that they're all on the same linear x-axis as the Pause Time chart
    // (see buildAllPauseTimePulses below). valueFn(gcData) computes the y
    // value for one GC.
    var buildGcPointSeries = function (valueFn) {
        var points = [];
        for (var index = 0; index < gcs.length; ++index) {
            var gcData = gcs[index]["data"];
            points.push({
                x: gcData["PauseStartRelativeMSec"],
                y: valueFn(gcData),
                gcId: gcData["Id"],
                dateTime: gcData["DateTime"]
            });
        }
        return points;
    };

    // ── Shared drag-to-zoom, across the entire webview ────────────────────
    // One zoom range applies across every time-series chart on the page -
    // the GC view's Pause Time/Usage/Fragmentation/per-Heap charts *and* the
    // Heap Contents view's allocation-rate/type-timeline charts (see
    // applySharedZoom below) - plus filters the Detailed tab's rows to the
    // same range. null means "full capture, not zoomed". Declared once here
    // (rather than as two independent per-view variables) so dragging a
    // selection on any chart, in either view, moves every other chart to
    // match - a GC-charts zoom and a Heap-Contents zoom used to be entirely
    // separate state.
    var sharedZoomRange = null;

    // {chart, zoomHandle} for the always-rebuilt charts (Pause Time, Usage,
    // Fragmentation once built) - destroyed/recreated on every zoom change.
    var gcChartHandles = [];

    // heapIndex -> {chart, zoomHandle}, only for heap charts actually built
    // so far (they're lazily constructed via IntersectionObserver - see
    // below) - a zoom change rebuilds only the ones already on screen, and
    // any heap chart built for the first time afterward picks up the
    // then-current sharedZoomRange directly.
    var heapChartHandlesByIndex = {};

    function destroyGcCharts() {
        for (var handleIndex = 0; handleIndex < gcChartHandles.length; ++handleIndex) {
            var handle = gcChartHandles[handleIndex];
            if (handle.zoomHandle) {
                handle.zoomHandle.detach();
            }
            handle.chart.destroy();
        }
        gcChartHandles = [];

        for (var heapIndexKey in heapChartHandlesByIndex) {
            var heapHandle = heapChartHandlesByIndex[heapIndexKey];
            if (heapHandle.zoomHandle) {
                heapHandle.zoomHandle.detach();
            }
            heapHandle.chart.destroy();
        }
        heapChartHandlesByIndex = {};
    }

    function onGcChartsRangeSelected(startMSec, endMSec) {
        applySharedZoom({ startMSec: startMSec, endMSec: endMSec });
    }

    function updateGcZoomStatusUi(zoomRange) {
        var statusBar = document.getElementById("gcZoomStatus");
        if (!statusBar) {
            return;
        }

        if (!zoomRange) {
            statusBar.style.display = "none";
            return;
        }

        statusBar.style.display = "block";
        var label = statusBar.getElementsByClassName("allocationZoomStatusLabel")[0];
        if (label) {
            label.textContent = "Zoomed: " + formatElapsedMs(zoomRange.startMSec) +
                " – " + formatElapsedMs(zoomRange.endMSec) + " (Backspace to reset)";
        }
    }

    // Hides Detailed-tab rows whose GC falls outside sharedZoomRange - a
    // no-op until both the Detailed tab has been opened at least once (see
    // detailTableInjected below) and a zoom is actually applied. Called both
    // on every zoom change and the first time the Detailed tab opens (in
    // case a zoom was already applied on a chart beforehand).
    function filterDetailTableToZoomRange() {
        if (!detailTableInjected) {
            return;
        }

        var detailTable = document.querySelector('#tab-detailed .detailTable table');
        if (!detailTable) {
            return;
        }

        for (var rowIndex = 1; rowIndex < detailTable.rows.length; ++rowIndex) {
            var row = detailTable.rows[rowIndex];
            var elapsedMsec = parseFloat(row.getAttribute('data-elapsed-msec'));
            var isVisible = !sharedZoomRange || (elapsedMsec >= sharedZoomRange.startMSec && elapsedMsec <= sharedZoomRange.endMSec);
            row.style.display = isVisible ? "" : "none";
        }
    }

    // Rebuilds only the GC view's own charts (Pause Time/Usage/Fragmentation
    // + any per-heap chart already built) plus the Detailed table's row
    // filter - called by applySharedZoom below, which also rebuilds the Heap
    // Contents charts, so the two views' charts stay in sync regardless of
    // which one a drag-select actually happened on. zoomRange is null for
    // the full, unzoomed capture. A heap chart built for the first time
    // afterward (scrolled into view) just reads sharedZoomRange directly.
    function renderGcCharts(zoomRange) {
        var previouslyBuiltHeapIndexes = [];
        for (var existingHeapIndexKey in heapChartHandlesByIndex) {
            previouslyBuiltHeapIndexes.push(parseInt(existingHeapIndexKey, 10));
        }

        destroyGcCharts();
        updateGcZoomStatusUi(zoomRange);

        var pauseHandle = renderTotalGcPauseTimeChart(zoomRange);
        if (pauseHandle) {
            gcChartHandles.push(pauseHandle);
        }

        var statsHandle = renderTotalGcStatsChart(zoomRange);
        if (statsHandle) {
            gcChartHandles.push(statsHandle);
        }

        if (document.getElementById("gcFragmentationOverTime")) {
            requestAnimationFrame(function () {
                var fragHandle = renderGcFragmentationChart(zoomRange);
                if (fragHandle) {
                    gcChartHandles.push(fragHandle);
                }
            });
        }

        for (var rebuildIndex = 0; rebuildIndex < previouslyBuiltHeapIndexes.length; ++rebuildIndex) {
            var heapIndexToRebuild = previouslyBuiltHeapIndexes[rebuildIndex];
            heapChartHandlesByIndex[heapIndexToRebuild] = renderHeapChart(heapIndexToRebuild, zoomRange);
        }

        filterDetailTableToZoomRange();
    }

    // Single entry point for every zoom change after page load (drag-select
    // on ANY chart in either view, either Reset Zoom button, or Backspace) -
    // updates sharedZoomRange once, then rebuilds both views' charts so a
    // zoom applied while looking at the GC charts is already in place if the
    // user switches to Heap Contents, and vice versa. renderHeapContentsCharts
    // (defined further below) is a no-op for canvases that don't exist yet
    // (the Heap Contents view is injected lazily on first open - see
    // AllocationSummaryRenderer.ts/renderAllocationTimelineChart's own
    // canvasElement-null guard), so calling it here even before that view
    // has ever been opened is harmless.
    function applySharedZoom(zoomRange) {
        sharedZoomRange = zoomRange;
        renderGcCharts(zoomRange);
        renderHeapContentsCharts(zoomRange);
    }

    var gcStatsChart = document.getElementsByClassName("gcStatsChart")[0];

    const gcStatsChartChartContext = gcStatsChart;
    const context = gcStatsChartChartContext.getContext('2d');

    var gcCountChart = new Chart(context, {
        "type": 'bar',
        data: {
            labels: [
                "0",
                "1",
                "2"
            ],
            datasets: [{
                label: "GC Count By Generation",
                data: gcCountsByGen,
                backgroundColor: [
                    "rgba(72, 83, 136, 0.2)",
                    "rgba(96, 165, 69, 0.2)",
                    "rgba(141, 31, 95, 0.2)"
                ]
            }]
        },
        options: {
            animation: { duration: 0 },
            "maintainAspectRatio": false
        }
    });

    var gcStatsTimeChart = document.getElementsByClassName("gcStatsTimeChart")[0];

    const gcStatsTimeChartChartContext = gcStatsTimeChart;
    const newContext = gcStatsTimeChartChartContext.getContext('2d');

    var gcTimeCountChart = new Chart(newContext, {
        "type": 'bar',
        data: {
            labels: [
                "0",
                "1",
                "2"
            ],
            datasets: [{
                label: "Total Time In GC By Generation",
                data: totalTimeInEachGcJson,
                backgroundColor: [
                    "rgba(72, 83, 136, 0.2)",
                    "rgba(96, 165, 69, 0.2)",
                    "rgba(141, 31, 95, 0.2)"
                ]
            }]
        },
        options: {
            scales: {
                yAxes: [{
                    ticks: {
                        beginAtZero: true
                    },
                    scaleLabel: {
                        display: true,
                        labelString: "Time in ms"
                    }
                }],
            },
            animation: { duration: 0 },
            "maintainAspectRatio": false,
        }
    });

    // LOH isn't a distinct GC generation - LOH is swept as part of Gen 2
    // (full) GCs, so a GC's "generation" field alone can't identify it. A GC
    // counts toward the LOH line only when its trigger Reason indicates LOH
    // allocation pressure specifically.
    var lohTriggerReasons = { "AllocLarge": true, "OutOfSpaceLOH": true };

    // Real captures can have many seconds of true idle time between GCs. A
    // plain category axis (one evenly-spaced tick per GC, like the memory
    // charts use) hides that entirely - and a line chart with one point per
    // GC and no explicit zero in between draws a straight line directly from
    // one GC's pause value to the next, which reads as continuous blocking
    // across the whole capture. Both problems are fixed by putting this
    // chart on a real *linear* (elapsed-ms) x-axis and emitting each GC as a
    // zero-flanked pulse: (start, 0) -> (start, pauseMs) -> (end, pauseMs)
    // -> (end, 0). Consecutive pulses' flanking zeros are equal (both 0), so
    // Chart.js draws a flat baseline between them that's exactly as wide as
    // the real gap.
    // Single pass over gcs builds all five pause-time pulse arrays at once,
    // replacing five separate O(n) passes through buildPauseTimePulses.
    // Point objects are shared across arrays (they're never mutated).
    // Returns [total, gen0, gen1, gen2, loh].
    var buildAllPauseTimePulses = function () {
        var totalPoints = [];
        var gen0Points = [];
        var gen1Points = [];
        var gen2Points = [];
        var lohPoints = [];

        for (var pulseIndex = 0; pulseIndex < gcs.length; ++pulseIndex) {
            var pulseGcData = gcs[pulseIndex]["data"];
            var startMs = pulseGcData["PauseStartRelativeMSec"];
            var endMs = pulseGcData["PauseEndRelativeMSec"];
            var pauseMs = pulseGcData["PauseDurationMSec"];
            var pulseGcId = pulseGcData["Id"];
            var pulseDateTime = pulseGcData["DateTime"];

            var ptRise  = { x: startMs, y: 0,       gcId: pulseGcId, dateTime: pulseDateTime, isGap: true };
            var ptTop0  = { x: startMs, y: pauseMs,  gcId: pulseGcId, dateTime: pulseDateTime, isGap: false };
            var ptTop1  = { x: endMs,   y: pauseMs,  gcId: pulseGcId, dateTime: pulseDateTime, isGap: false };
            var ptFall  = { x: endMs,   y: 0,        gcId: pulseGcId, dateTime: pulseDateTime, isGap: true };

            totalPoints.push(ptRise, ptTop0, ptTop1, ptFall);

            var generation = pulseGcData["generation"];
            if (generation === 0) {
                gen0Points.push(ptRise, ptTop0, ptTop1, ptFall);
            } else if (generation === 1) {
                gen1Points.push(ptRise, ptTop0, ptTop1, ptFall);
            } else if (generation === 2) {
                gen2Points.push(ptRise, ptTop0, ptTop1, ptFall);
            }

            if (lohTriggerReasons[pulseGcData["Reason"]] === true) {
                lohPoints.push(ptRise, ptTop0, ptTop1, ptFall);
            }
        }

        return [totalPoints, gen0Points, gen1Points, gen2Points, lohPoints];
    };

    var formatElapsedMs = function (ms) {
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
    };

    var pauseTimeTooltipTitle = function (tooltipItems, tooltipData) {
        var lines = [];
        for (var itemIndex = 0; itemIndex < tooltipItems.length; ++itemIndex) {
            var tooltipItem = tooltipItems[itemIndex];
            var point = tooltipData.datasets[tooltipItem.datasetIndex].data[tooltipItem.index];
            if (point.isGap) {
                lines.push(`${formatElapsedMs(point.x)} elapsed — idle`);
            } else {
                lines.push(`GC #${point.gcId} — ${formatHumanDateTime(point.dateTime)}`);
            }
        }
        return lines;
    };

    var pauseTimePulses = buildAllPauseTimePulses();

    function renderTotalGcPauseTimeChart(zoomRange) {
        var canvasElement = document.getElementById("totalGcPauseTimeOverTime");
        if (!canvasElement) {
            return null;
        }

        var xAxisTicks = { callback: formatElapsedMs };
        if (zoomRange) {
            xAxisTicks.min = zoomRange.startMSec;
            xAxisTicks.max = zoomRange.endMSec;
        }

        var dragStateHolder = { current: null };

        var chart = new Chart(canvasElement.getContext('2d'), {
            type: 'line',
                data: {
                    datasets: [{
                        label: 'Total Blocking Time',
                        data: pauseTimePulses[0],
                        backgroundColor: [
                            "rgba(220, 53, 69, 0.2)",
                        ],
                        borderColor: "rgba(220, 53, 69, 1)",
                        borderWidth: 1,
                        lineTension: 0,
                        pointRadius: 2,
                        pointHoverRadius: 4
                    },
                    {
                        label: 'Gen 0',
                        data: pauseTimePulses[1],
                        backgroundColor: [
                            "rgba(72, 83, 136, 0.2)",
                        ],
                        borderWidth: 1,
                        lineTension: 0,
                        pointRadius: 2,
                        pointHoverRadius: 4
                    },
                    {
                        label: "Gen 1",
                        data: pauseTimePulses[2],
                        backgroundColor: [
                            "rgba(96, 165, 69, 0.2)",
                        ],
                        borderWidth: 1,
                        lineTension: 0,
                        pointRadius: 2,
                        pointHoverRadius: 4
                    },
                    {
                        label: "Gen 2",
                        data: pauseTimePulses[3],
                        backgroundColor: [
                            "rgba(141, 31, 95, 0.2)",
                        ],
                        borderWidth: 1,
                        lineTension: 0,
                        pointRadius: 2,
                        pointHoverRadius: 4
                    },
                    {
                        label: "LOH",
                        data: pauseTimePulses[4],
                        backgroundColor: [
                            "rgba(201, 221, 84, 0.2)"
                        ],
                        borderWidth: 1,
                        lineTension: 0,
                        pointRadius: 2,
                        pointHoverRadius: 4
                    }
                ]},
                plugins: [createZoomSelectionPlugin(dragStateHolder)],
                options: {
                    title: {
                        display: true,
                        text: `GC Pause Time by Generation`
                    },
                    // Sharp vertical pulses (x,0)->(x,pauseMs) confuse Chart.js's
                    // default bezier curve fitting (lineTension) - it overshoots
                    // through the vertical segments and loops back on itself,
                    // producing the self-crossing "figure eight" artifact. Force
                    // straight-line segments instead, both per-dataset above and
                    // here as a chart-wide default so nothing falls back to it.
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
                            ticks: {
                                beginAtZero: true
                            },
                            scaleLabel: {
                                display: true,
                                labelString: "Time in ms"
                            }
                        }],
                    },
                    tooltips: {
                        callbacks: {
                            title: pauseTimeTooltipTitle
                        }
                    },
                    animation: { duration: 0 },
                    "maintainAspectRatio": false,
                }
        });

        var zoomHandle = attachDragToZoom(chart, canvasElement, dragStateHolder, pixelToMSecLinear, onGcChartsRangeSelected);
        return { chart: chart, zoomHandle: zoomHandle };
    }

    var totalMb = 1024 * 1024;

    var totalGenSizeSeries = [
        buildGcPointSeries(function (gcData) { return gcData["GenerationSize0"] / totalMb; }),
        buildGcPointSeries(function (gcData) { return gcData["GenerationSize1"] / totalMb; }),
        buildGcPointSeries(function (gcData) { return gcData["GenerationSize2"] / totalMb; }),
        buildGcPointSeries(function (gcData) { return gcData["GenerationSizeLOH"] / totalMb; })
    ];

    function renderTotalGcStatsChart(zoomRange) {
        var canvasElement = document.getElementById("totalGcStatsOverTime");
        if (!canvasElement) {
            return null;
        }

        var xAxisTicks = { callback: formatElapsedMs };
        if (zoomRange) {
            xAxisTicks.min = zoomRange.startMSec;
            xAxisTicks.max = zoomRange.endMSec;
        }

        var dragStateHolder = { current: null };

        var chart = new Chart(canvasElement.getContext('2d'), {
            type: 'line',
                data: {
                    datasets: [{
                        label: 'Gen 0',
                        data: totalGenSizeSeries[0],
                        backgroundColor: [
                            "rgba(72, 83, 136, 0.2)",
                        ],
                        borderWidth: 1,
                        pointRadius: 2,
                        pointHoverRadius: 4
                    },
                    {
                        label: "Gen 1",
                        data: totalGenSizeSeries[1],
                        backgroundColor: [
                            "rgba(96, 165, 69, 0.2)",
                        ],
                        borderWidth: 1,
                        pointRadius: 2,
                        pointHoverRadius: 4
                    },
                    {
                        label: "Gen 2",
                        data: totalGenSizeSeries[2],
                        backgroundColor: [
                            "rgba(141, 31, 95, 0.2)",
                        ],
                        borderWidth: 1,
                        pointRadius: 2,
                        pointHoverRadius: 4
                    },
                    {
                        label: "LOH",
                        data: totalGenSizeSeries[3],
                        backgroundColor: [
                            "rgba(201, 221, 84, 0.2)"
                        ],
                        borderWidth: 1,
                        pointRadius: 2,
                        pointHoverRadius: 4
                    }
                ]},
                plugins: [createZoomSelectionPlugin(dragStateHolder)],
                options: {
                    title: {
                        display: true,
                        text: `Total GC Usage by Generation`
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
                            ticks: {
                                beginAtZero: true
                            },
                            scaleLabel: {
                                display: true,
                                labelString: "Memory Usage in MB"
                            }
                        }],
                    },
                    tooltips: {
                        callbacks: {
                            title: linearGcTooltipTitle
                        }
                    },
                    animation: { duration: 0 },
                    "maintainAspectRatio": false,
                }
        });

        var zoomHandle = attachDragToZoom(chart, canvasElement, dragStateHolder, pixelToMSecLinear, onGcChartsRangeSelected);
        return { chart: chart, zoomHandle: zoomHandle };
    }

    // Built once, on first render, and cached - the GC x heap x gen summing
    // below is the expensive part, not chart construction, so every
    // subsequent zoom change (renderGcCharts) reuses these same point arrays
    // and just re-clips the x-axis range (see renderGcFragmentationChart).
    var fragmentationSeriesCache = null;

    function buildFragmentationSeries() {
        var fragGen0Points = [];
        var fragGen1Points = [];
        var fragGen2Points = [];
        var fragLohPoints = [];
        var fragTotalPoints = [];
        var pinnedCountPoints = [];
        var compactionMarkerPoints = [];

        for (var fragIndex = 0; fragIndex < gcs.length; ++fragIndex) {
            var fragGcData = gcs[fragIndex]["data"];
            var fragHeaps = fragGcData["Heaps"];
            var fragX = fragGcData["PauseStartRelativeMSec"];
            var fragGcId = fragGcData["Id"];
            var fragDateTime = fragGcData["DateTime"];

            var fragByGen = [0, 0, 0, 0];
            var sizeAfterByGen = [0, 0, 0, 0];

            for (var fragHeapIndex = 0; fragHeapIndex < fragHeaps.length; ++fragHeapIndex) {
                var fragGens = fragHeaps[fragHeapIndex]["Generations"];
                for (var genIdx = 0; genIdx < 4; ++genIdx) {
                    var fragGen = fragGens[genIdx];
                    if (fragGen) {
                        fragByGen[genIdx] += parseFloat(fragGen["Fragmentation"]) || 0;
                        sizeAfterByGen[genIdx] += parseFloat(fragGen["SizeAfter"]) || 0;
                    }
                }
            }

            var totalHeapSizeBytes = parseFloat(fragGcData["TotalHeapSize"]) || 0;
            var totalFragBytes = fragByGen[0] + fragByGen[1] + fragByGen[2] + fragByGen[3];

            fragGen0Points.push({ x: fragX, y: sizeAfterByGen[0] > 0 ? (fragByGen[0] / sizeAfterByGen[0]) * 100 : 0, gcId: fragGcId, dateTime: fragDateTime });
            fragGen1Points.push({ x: fragX, y: sizeAfterByGen[1] > 0 ? (fragByGen[1] / sizeAfterByGen[1]) * 100 : 0, gcId: fragGcId, dateTime: fragDateTime });
            fragGen2Points.push({ x: fragX, y: sizeAfterByGen[2] > 0 ? (fragByGen[2] / sizeAfterByGen[2]) * 100 : 0, gcId: fragGcId, dateTime: fragDateTime });
            fragLohPoints.push({ x: fragX, y: sizeAfterByGen[3] > 0 ? (fragByGen[3] / sizeAfterByGen[3]) * 100 : 0, gcId: fragGcId, dateTime: fragDateTime });
            fragTotalPoints.push({ x: fragX, y: totalHeapSizeBytes > 0 ? (totalFragBytes / totalHeapSizeBytes) * 100 : 0, gcId: fragGcId, dateTime: fragDateTime });

            pinnedCountPoints.push({ x: fragX, y: parseInt(fragGcData["PinnedObjectCount"]) || 0, gcId: fragGcId, dateTime: fragDateTime });

            var mechanisms = parseInt(fragGcData["GlobalMechanisms"]) || 0;
            // GCGlobalMechanisms.Compaction = 0x2
            compactionMarkerPoints.push((mechanisms & 0x2) !== 0 ? { x: fragX, y: 2, gcId: fragGcId, dateTime: fragDateTime } : null);
        }

        return {
            gen0: fragGen0Points,
            gen1: fragGen1Points,
            gen2: fragGen2Points,
            loh: fragLohPoints,
            total: fragTotalPoints,
            pinnedCount: pinnedCountPoints,
            compactionMarker: compactionMarkerPoints
        };
    }

    function renderGcFragmentationChart(zoomRange) {
        var canvasElement = document.getElementById("gcFragmentationOverTime");
        if (!canvasElement) {
            return null;
        }

        if (!fragmentationSeriesCache) {
            fragmentationSeriesCache = buildFragmentationSeries();
        }

        var xAxisTicks = { callback: formatElapsedMs };
        if (zoomRange) {
            xAxisTicks.min = zoomRange.startMSec;
            xAxisTicks.max = zoomRange.endMSec;
        }

        var dragStateHolder = { current: null };

        var chart = new Chart(canvasElement.getContext('2d'), {
            type: 'line',
            data: {
                datasets: [
                    {
                        label: 'Total',
                        data: fragmentationSeriesCache.total,
                        borderColor: "rgba(220, 53, 69, 1)",
                        backgroundColor: "rgba(220, 53, 69, 0.05)",
                        borderWidth: 2,
                        yAxisID: 'fragPct',
                        fill: false,
                        pointRadius: 2,
                        pointHoverRadius: 4
                    },
                    {
                        label: 'Gen 0',
                        data: fragmentationSeriesCache.gen0,
                        borderColor: "rgba(72, 83, 136, 1)",
                        backgroundColor: "rgba(72, 83, 136, 0.05)",
                        borderWidth: 1,
                        yAxisID: 'fragPct',
                        fill: false,
                        pointRadius: 2,
                        pointHoverRadius: 4
                    },
                    {
                        label: 'Gen 1',
                        data: fragmentationSeriesCache.gen1,
                        borderColor: "rgba(96, 165, 69, 1)",
                        backgroundColor: "rgba(96, 165, 69, 0.05)",
                        borderWidth: 1,
                        yAxisID: 'fragPct',
                        fill: false,
                        pointRadius: 2,
                        pointHoverRadius: 4
                    },
                    {
                        label: 'Gen 2',
                        data: fragmentationSeriesCache.gen2,
                        borderColor: "rgba(141, 31, 95, 1)",
                        backgroundColor: "rgba(141, 31, 95, 0.05)",
                        borderWidth: 1,
                        yAxisID: 'fragPct',
                        fill: false,
                        pointRadius: 2,
                        pointHoverRadius: 4
                    },
                    {
                        label: 'LOH',
                        data: fragmentationSeriesCache.loh,
                        borderColor: "rgba(201, 221, 84, 1)",
                        backgroundColor: "rgba(201, 221, 84, 0.05)",
                        borderWidth: 1,
                        yAxisID: 'fragPct',
                        fill: false,
                        pointRadius: 2,
                        pointHoverRadius: 4
                    },
                    {
                        label: 'Pinned Objects',
                        data: fragmentationSeriesCache.pinnedCount,
                        borderColor: "rgba(255, 165, 0, 0.8)",
                        backgroundColor: "rgba(255, 165, 0, 0.05)",
                        borderWidth: 1,
                        borderDash: [4, 4],
                        yAxisID: 'pinnedCount',
                        fill: false,
                        pointRadius: 2,
                        pointHoverRadius: 4
                    },
                    {
                        label: 'Compaction',
                        data: fragmentationSeriesCache.compactionMarker,
                        backgroundColor: "rgba(255, 165, 0, 0.9)",
                        borderColor: "rgba(255, 165, 0, 1)",
                        pointRadius: 8,
                        pointStyle: 'triangle',
                        showLine: false,
                        yAxisID: 'fragPct',
                        fill: false
                    }
                ]
            },
            plugins: [createZoomSelectionPlugin(dragStateHolder)],
            options: {
                title: {
                    display: true,
                    text: 'Heap Fragmentation by Generation'
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
                    yAxes: [
                        {
                            id: 'fragPct',
                            position: 'left',
                            ticks: {
                                beginAtZero: true,
                                max: 100,
                                callback: function (value) { return value + '%'; }
                            },
                            scaleLabel: {
                                display: true,
                                labelString: 'Fragmentation %'
                            }
                        },
                        {
                            id: 'pinnedCount',
                            position: 'right',
                            ticks: {
                                beginAtZero: true
                            },
                            scaleLabel: {
                                display: true,
                                labelString: 'Pinned Object Count'
                            },
                            gridLines: {
                                drawOnChartArea: false
                            }
                        }
                    ]
                },
                tooltips: {
                    callbacks: {
                        title: linearGcTooltipTitle
                    }
                },
                animation: { duration: 0 },
                maintainAspectRatio: false
            }
        });

        var zoomHandle = attachDragToZoom(chart, canvasElement, dragStateHolder, pixelToMSecLinear, onGcChartsRangeSelected);
        return { chart: chart, zoomHandle: zoomHandle };
    }

    var lohTypesSection = document.getElementById("lohTypesSection");
    if (lohTypesSection && allocationSummaryJson && allocationSummaryJson["topTypes"]) {
        var lohTypes = [];
        var topTypes = allocationSummaryJson["topTypes"];

        for (var topTypeIndex = 0; topTypeIndex < topTypes.length; ++topTypeIndex) {
            if (topTypes[topTypeIndex]["LargeCount"] > 0) {
                lohTypes.push(topTypes[topTypeIndex]);
            }
        }

        lohTypes.sort(function (leftType, rightType) { return rightType["LargeCount"] - leftType["LargeCount"]; });

        if (lohTypes.length === 0) {
            lohTypesSection.innerHTML = '<p>No LOH allocations detected in this capture.</p>';
        } else {
            var lohMb = 1024 * 1024;
            var lohKb = 1024;

            var lohHeaderCells = '<th>Type</th><th>LOH Ticks</th><th>Small Ticks</th><th>Pinned Ticks</th><th>Total Ticks</th><th>Total Sampled Bytes (all kinds)</th>';
            var lohRows = '';

            for (var lohIndex = 0; lohIndex < lohTypes.length; ++lohIndex) {
                var lohType = lohTypes[lohIndex];
                var totalBytes = lohType["TotalBytes"];
                var bytesLabel = totalBytes >= lohMb
                    ? (totalBytes / lohMb).toFixed(2) + ' MB'
                    : (totalBytes / lohKb).toFixed(2) + ' KB';

                lohRows += '<tr>' +
                    '<td>' + lohType["TypeName"] + '</td>' +
                    '<td>' + lohType["LargeCount"] + '</td>' +
                    '<td>' + lohType["SmallCount"] + '</td>' +
                    '<td>' + lohType["PinnedCount"] + '</td>' +
                    '<td>' + lohType["TickCount"] + '</td>' +
                    '<td>' + bytesLabel + '</td>' +
                    '</tr>';
            }

            lohTypesSection.innerHTML = '<div class="detailTable"><table>' +
                '<tr class="tableHeader">' + lohHeaderCells + '</tr>' +
                lohRows +
                '</table></div>';
        }
    }

    // heapIndex -> [gen0, gen1, gen2, loh] point series - built once per
    // heap, the first time that heap's chart is actually rendered (see
    // renderHeapChart), then reused by every later zoom-triggered rebuild.
    var heapChartSeriesCache = {};

    function buildHeapSeries(passedHeapIndex) {
        var mb = 1024;

        return [
            buildGcPointSeries(function (gcData) { return gcData["Heaps"][passedHeapIndex]["Generations"][0]["SizeAfter"] / mb; }),
            buildGcPointSeries(function (gcData) { return gcData["Heaps"][passedHeapIndex]["Generations"][1]["SizeAfter"] / mb; }),
            buildGcPointSeries(function (gcData) { return gcData["Heaps"][passedHeapIndex]["Generations"][2]["SizeAfter"] / mb; }),
            buildGcPointSeries(function (gcData) { return gcData["Heaps"][passedHeapIndex]["Generations"][3]["SizeAfter"] / mb; })
        ];
    }

    function renderHeapChart(passedHeapIndex, zoomRange) {
        if (!heapChartSeriesCache[passedHeapIndex]) {
            heapChartSeriesCache[passedHeapIndex] = buildHeapSeries(passedHeapIndex);
        }
        var series = heapChartSeriesCache[passedHeapIndex];

        var canvasElement = heapCharts[passedHeapIndex];

        var xAxisTicks = { callback: formatElapsedMs };
        if (zoomRange) {
            xAxisTicks.min = zoomRange.startMSec;
            xAxisTicks.max = zoomRange.endMSec;
        }

        var dragStateHolder = { current: null };

        var chart = new Chart(canvasElement.getContext('2d'), {
            type: 'line',
            data: {
                datasets: [{
                    label: 'Gen 0',
                    data: series[0],
                    backgroundColor: [
                        'rgba(54, 162, 235, 0.2)',
                    ],
                    borderWidth: 1,
                    pointRadius: 2,
                    pointHoverRadius: 4
                },
                {
                    label: "Gen 1",
                    data: series[1],
                    backgroundColor: [
                        'rgba(75, 192, 192, 0.2)'
                    ],
                    borderWidth: 1,
                    pointRadius: 2,
                    pointHoverRadius: 4
                },
                {
                    label: "Gen 2",
                    data: series[2],
                    backgroundColor: [
                        'rgba(153, 102, 255, 0.2)'
                    ],
                    borderWidth: 1,
                    pointRadius: 2,
                    pointHoverRadius: 4
                },
                {
                    label: "LOH",
                    data: series[3],
                    backgroundColor: [
                        'rgba(255, 206, 86, 0.2)'
                    ],
                    borderWidth: 1,
                    pointRadius: 2,
                    pointHoverRadius: 4
                }
            ]},
            plugins: [createZoomSelectionPlugin(dragStateHolder)],
            options: {
                title: {
                    display: true,
                    text: `Heap: ${passedHeapIndex}`
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
                        ticks: {
                            beginAtZero: true
                        },
                        scaleLabel: {
                            display: true,
                            labelString: "Memory Usage in MB"
                        }
                    }],
                },
                tooltips: {
                    callbacks: {
                        title: linearGcTooltipTitle
                    }
                },
                animation: { duration: 0 },
                "maintainAspectRatio": false,
            }
        });

        var zoomHandle = attachDragToZoom(chart, canvasElement, dragStateHolder, pixelToMSecLinear, onGcChartsRangeSelected);
        return { chart: chart, zoomHandle: zoomHandle };
    }

    var heapCharts = document.getElementsByClassName("heapChart");

    // Initial (unzoomed) build of the Pause Time / Usage / Fragmentation
    // charts - mirrors renderHeapContentsCharts(null)'s role for the Heap
    // Contents view. Runs before the per-heap setup below so sharedZoomRange
    // is settled (still null, nothing to zoom yet) before any heap chart
    // reads it. Calls renderGcCharts directly, not applySharedZoom - the
    // Heap Contents view has nothing to rebuild yet at page load (it's
    // injected lazily on first open - see the viewNavButton handler below).
    renderGcCharts(null);

    // Click equivalent of the Backspace zoom-reset above - for anyone who
    // doesn't know/want to use the keyboard shortcut. Only visible while a
    // zoom is actually applied (see updateGcZoomStatusUi). The button is
    // part of the GC view's static markup (not lazily injected), so this can
    // be wired up right away.
    var resetGcZoomButton = document.getElementById('resetGcZoomButton');
    if (resetGcZoomButton) {
        resetGcZoomButton.addEventListener('click', function () {
            applySharedZoom(null);
        });
    }

    // Server GC traces can have 16+ heaps, each getting its own Chart.js
    // instance - building all of them synchronously on load is expensive and
    // most users never scroll down to see every heap. Defer each chart's
    // construction until its canvas actually scrolls into view. Whatever
    // zoom is currently applied (sharedZoomRange) is used immediately, so a
    // heap chart that first appears while already zoomed doesn't flash the
    // full range before narrowing.
    if ('IntersectionObserver' in window) {
        var heapChartObserver = new IntersectionObserver(function (entries, observer) {
            for (var entryIndex = 0; entryIndex < entries.length; ++entryIndex) {
                var entry = entries[entryIndex];
                if (entry.isIntersecting) {
                    var heapIndex = parseInt(entry.target.getAttribute('data-heap-index'), 10);
                    heapChartHandlesByIndex[heapIndex] = renderHeapChart(heapIndex, sharedZoomRange);
                    observer.unobserve(entry.target);
                }
            }
        }, { rootMargin: '200px' });

        for (var heapObserveIndex = 0; heapObserveIndex < heapCharts.length; ++heapObserveIndex) {
            heapCharts[heapObserveIndex].setAttribute('data-heap-index', heapObserveIndex);
            heapChartObserver.observe(heapCharts[heapObserveIndex]);
        }
    } else {
        for (var index = 0; index < heapCharts.length; ++index) {
            heapChartHandlesByIndex[index] = renderHeapChart(index, sharedZoomRange);
        }
    }

    // The Detailed tab's markup arrives as inert commented-out text (see
    // GcSnapshotRenderer.ts) rather than live HTML, so the browser doesn't
    // have to parse/build a DOM node for every GC's row (or, for the
    // generation-breakdown tables appended below it, every heap x
    // generation x field combination) on page load - only do that once,
    // the first time the tab is actually opened.
    var detailTableInjected = false;

    var MB = 1024 * 1024;

    // Mirrors GcDetailTableRenderer.ts's severity thresholds exactly, so the
    // GC Number column on the generation-breakdown tables below can carry
    // the same at-a-glance coloring the GC summary table already
    // establishes for that GC, without duplicating whole-row logic here.
    var getSeverityClass = function (pauseTime) {
        if (pauseTime > 200.0) {
            return "expensiveGc";
        }
        if (pauseTime > 100.0) {
            return "warnGc";
        }
        if (pauseTime > 50.0) {
            return "interstingGc";
        }
        if (pauseTime > 20.0) {
            return "somewhatInterestingGc";
        }
        if (pauseTime > 10.0) {
            return "notSomewhatInterestingGc";
        }
        return "";
    };

    // Per-generation fields available on Heaps[].Generations[genIndex] (see
    // GcJsonExporter.cs / gcDataFromXml - both sources produce this same
    // shape). "Common" is the default view; the full list is one click away
    // via #genFieldsToggle rather than always rendering all 56 (14 fields x
    // 4 generations) columns.
    var COMMON_GEN_FIELDS = [
        { key: "NewAllocation", label: "New Allocation" },
        { key: "SurvRate", label: "Surv Rate", isPercent: true },
        { key: "In", label: "In" },
        { key: "Out", label: "Out" },
        { key: "Fragmentation", label: "Fragmentation" }
    ];

    var ALL_GEN_FIELDS = [
        { key: "SizeBefore", label: "Size Before" },
        { key: "ObjSpaceBefore", label: "Obj Space Before" },
        { key: "Fragmentation", label: "Fragmentation" },
        { key: "FreeListSpaceBefore", label: "Free List Space Before" },
        { key: "FreeListSpaceAfter", label: "Free List Space After" },
        { key: "FreeObjSpaceBefore", label: "Free Obj Space Before" },
        { key: "FreeObjSpaceAfter", label: "Free Obj Space After" },
        { key: "ObjSizeAfter", label: "Obj Size After" },
        { key: "In", label: "In" },
        { key: "Out", label: "Out" },
        { key: "NewAllocation", label: "New Allocation" },
        { key: "SurvRate", label: "Surv Rate", isPercent: true },
        { key: "PinnedSurv", label: "Pinned Surv" },
        { key: "NonePinnedSurv", label: "Non-Pinned Surv" }
    ];

    var GENERATION_LABELS = ["Gen0", "Gen1", "Gen2", "LOH"];

    // heapIndex === -1 means "All Heaps": byte fields summed across every
    // heap the GC reports, SurvRate averaged across heaps. There's no
    // separately-decoded GC-level total for these fields the way there is
    // for GenerationSize0-3/LOH/POH (those come from a distinct GCHeapStats
    // event) - summing/averaging the per-heap breakdown is the only way to
    // get one number per generation across the whole GC.
    function computeGenFieldValue(heaps, heapIndex, genIndex, field) {
        if (heapIndex === -1) {
            if (!heaps || heaps.length === 0) {
                return null;
            }

            var total = 0;
            var count = 0;
            for (var h = 0; h < heaps.length; ++h) {
                var gen = heaps[h]["Generations"][genIndex];
                if (gen === undefined || gen === null) {
                    continue;
                }
                total += parseFloat(gen[field.key]) || 0;
                ++count;
            }

            if (count === 0) {
                return null;
            }

            return field.isPercent ? (total / count) : total;
        }

        if (!heaps || heapIndex >= heaps.length) {
            return null;
        }

        var heapGen = heaps[heapIndex]["Generations"][genIndex];
        if (heapGen === undefined || heapGen === null) {
            return null;
        }

        return parseFloat(heapGen[field.key]) || 0;
    }

    function formatGenFieldValue(value, field) {
        if (value === null) {
            return "&ndash;";
        }

        return field.isPercent ? value.toFixed(1) : (value / MB).toFixed(2);
    }

    // Built entirely from data already in `gcs` (hiddenData) - there's no
    // separate server-rendered payload for these at all, so this costs
    // nothing until the Detailed tab is actually opened. Uses the
    // .detailTable *class* (matching renderGcDetailTable's own wrapper), not
    // an id - multiple tables share the Detailed panel now, so an id would
    // collide.
    function buildGenerationBreakdownTable(heapIndex, showAllFields) {
        if (gcs.length === 0) {
            return "<p>No GC events to display.</p>";
        }

        var fields = showAllFields ? ALL_GEN_FIELDS : COMMON_GEN_FIELDS;

        var headerCells = "<th>GC Number</th><th>DateTime</th>";
        for (var genIndex = 0; genIndex < 4; ++genIndex) {
            for (var fieldIndex = 0; fieldIndex < fields.length; ++fieldIndex) {
                var field = fields[fieldIndex];
                var unit = field.isPercent ? "%" : "mb";
                headerCells += `<th>${field.label} ${GENERATION_LABELS[genIndex]} (${unit})</th>`;
            }
        }

        var rows = "";
        for (var index = 0; index < gcs.length; ++index) {
            var gcEntry = gcs[index]["data"];
            var heaps = gcEntry["Heaps"];

            var severityClass = getSeverityClass(parseFloat(gcEntry["PauseDurationMSec"]));
            var rowClass = severityClass ? ` class="${severityClass}"` : "";
            var rowCells = `<td>${gcEntry["Id"]}</td><td>${formatHumanDateTime(gcEntry["DateTime"])}</td>`;
            for (var rowGenIndex = 0; rowGenIndex < 4; ++rowGenIndex) {
                for (var rowFieldIndex = 0; rowFieldIndex < fields.length; ++rowFieldIndex) {
                    var rowField = fields[rowFieldIndex];
                    var value = computeGenFieldValue(heaps, heapIndex, rowGenIndex, rowField);
                    rowCells += `<td>${formatGenFieldValue(value, rowField)}</td>`;
                }
            }

            rows += `<tr${rowClass}>${rowCells}</tr>`;
        }

        return `<div class="detailTable"><table><tr class="tableHeader">${headerCells}</tr>${rows}</table></div>`;
    }

    // "All Heaps" first, then one table per heap in heap-number order, all
    // stacked in the same view rather than split across separate tabs.
    function buildAllGenerationBreakdownTables(showAllFields) {
        var maxHeapCount = 0;
        for (var index = 0; index < gcs.length; ++index) {
            var heaps = gcs[index]["data"]["Heaps"];
            if (heaps && heaps.length > maxHeapCount) {
                maxHeapCount = heaps.length;
            }
        }

        var html = `<h3 class="detailTableHeading">Generations: All Heaps</h3>` + buildGenerationBreakdownTable(-1, showAllFields);
        for (var heapIndex = 0; heapIndex < maxHeapCount; ++heapIndex) {
            html += `<h3 class="detailTableHeading">Generations: Heap ${heapIndex}</h3>` + buildGenerationBreakdownTable(heapIndex, showAllFields);
        }

        return html;
    }

    var showAllGenFields = false;

    function renderGenerationBreakdownSection() {
        document.getElementById("generationBreakdownSection").innerHTML = buildAllGenerationBreakdownTables(showAllGenFields);
    }

    // Click-to-sort for the Detailed tab's per-GC table. The table is only
    // ever built once per webview session (see detailTableHtml above), so
    // this reorders the already-rendered <tr> elements in place rather than
    // re-deriving values from the original gcs array - matching the
    // render-once/mutate-the-DOM approach the rest of this lazy-inject path
    // already uses.
    var currentSortColumnIndex = -1;
    var currentSortAscending = true;

    function detailTableSortValue(cell, sortType) {
        if (sortType === 'date') {
            // The DateTime cell's own data-raw attribute (an ISO-8601
            // timestamp or a zero-padded "+elapsed" string - see
            // GcDetailTableRenderer.ts) sorts correctly as plain text; the
            // human-formatted display text (e.g. "21-Jul-2026 03:42:13 PM
            // PDT") does not.
            return cell.getAttribute('data-raw') || '';
        }

        if (sortType === 'number') {
            var parsed = parseFloat(cell.textContent);
            return isNaN(parsed) ? -Infinity : parsed;
        }

        return cell.textContent.toLowerCase();
    }

    function sortDetailTableByColumn(table, columnIndex, sortType, ascending) {
        var tbody = table.tBodies[0] || table;
        // Snapshots the live HTMLCollection before any row gets moved -
        // table.rows[0] is the header row, left untouched.
        var dataRows = Array.prototype.slice.call(table.rows, 1);

        dataRows.sort(function (rowA, rowB) {
            var valueA = detailTableSortValue(rowA.cells[columnIndex], sortType);
            var valueB = detailTableSortValue(rowB.cells[columnIndex], sortType);

            var comparison = 0;
            if (valueA < valueB) {
                comparison = -1;
            } else if (valueA > valueB) {
                comparison = 1;
            }

            return ascending ? comparison : -comparison;
        });

        // appendChild on a node already in the tree moves it - iterating in
        // the desired final order and re-appending each row leaves the
        // header (never touched) first and every data row following in
        // sorted order.
        for (var rowIndex = 0; rowIndex < dataRows.length; ++rowIndex) {
            tbody.appendChild(dataRows[rowIndex]);
        }
    }

    function setupDetailTableSortHandlers(container) {
        var table = container.querySelector(".detailTable table");
        if (!table) {
            return;
        }

        var headerCells = table.rows[0].cells;
        for (var headerIndex = 0; headerIndex < headerCells.length; ++headerIndex) {
            var headerCell = headerCells[headerIndex];

            (function (columnIndex, headerCell) {
                headerCell.addEventListener('click', function () {
                    var ascending = (currentSortColumnIndex === columnIndex) ? !currentSortAscending : true;
                    sortDetailTableByColumn(table, columnIndex, headerCell.getAttribute('data-sort'), ascending);

                    for (var clearIndex = 0; clearIndex < headerCells.length; ++clearIndex) {
                        headerCells[clearIndex].getElementsByClassName('sortIndicator')[0].textContent = '';
                    }
                    headerCell.getElementsByClassName('sortIndicator')[0].textContent = ascending ? ' ▲' : ' ▼';

                    currentSortColumnIndex = columnIndex;
                    currentSortAscending = ascending;
                });
            })(headerIndex, headerCell);
        }
    }

    var genFieldsToggle = document.getElementById("genFieldsToggle");
    genFieldsToggle.addEventListener('click', function () {
        showAllGenFields = !showAllGenFields;
        genFieldsToggle.textContent = showAllGenFields ? "Show Common Fields" : "Show All Fields";

        // Only the generation-breakdown tables have a curated/full mode -
        // the GC summary table above them is unaffected, so only rebuild
        // this section, and only if the Detailed tab has actually been
        // opened at least once already.
        if (detailTableInjected) {
            renderGenerationBreakdownSection();
        }
    });

    var tabButtons = document.getElementsByClassName("tabButton");
    for (var tabIndex = 0; tabIndex < tabButtons.length; ++tabIndex) {
        tabButtons[tabIndex].addEventListener('click', function (event) {
            var targetTab = event.currentTarget.getAttribute('data-tab');

            var buttons = document.getElementsByClassName("tabButton");
            for (var buttonIndex = 0; buttonIndex < buttons.length; ++buttonIndex) {
                buttons[buttonIndex].classList.remove('active');
            }

            var panels = document.getElementsByClassName("tabPanel");
            for (var panelIndex = 0; panelIndex < panels.length; ++panelIndex) {
                panels[panelIndex].classList.remove('active');
            }

            event.currentTarget.classList.add('active');
            document.getElementById('tab-' + targetTab).classList.add('active');

            // Only the Detailed tab's generation-breakdown tables have a
            // curated/full field mode - no point showing the toggle while
            // looking at Charts.
            genFieldsToggle.style.display = (targetTab === 'detailed') ? 'inline-block' : 'none';

            if (targetTab === 'detailed' && !detailTableInjected) {
                var holder = document.getElementById("detailTableHtml");
                var detailTableHtml = holder.innerHTML.slice(4, holder.innerHTML.length - 3);
                var detailedPanel = document.getElementById('tab-detailed');
                detailedPanel.innerHTML = detailTableHtml + '<div id="generationBreakdownSection"></div>';

                // GcDetailTableRenderer.ts emits the raw DateTime string on
                // each cell rather than pre-formatting it - formatting 1000+
                // rows via Intl.DateTimeFormat is cheap once, here, on first
                // open, but was costing that same work on every
                // extension-host render when done server-side.
                var dateTimeCells = detailedPanel.getElementsByClassName("gcDateTimeCell");
                for (var dateTimeCellIndex = 0; dateTimeCellIndex < dateTimeCells.length; ++dateTimeCellIndex) {
                    var dateTimeCell = dateTimeCells[dateTimeCellIndex];
                    dateTimeCell.textContent = formatHumanDateTime(dateTimeCell.getAttribute('data-raw'));
                }

                setupDetailTableSortHandlers(detailedPanel);

                renderGenerationBreakdownSection();
                detailTableInjected = true;

                // A GC chart zoom may already have been applied on the
                // Charts tab before this tab was ever opened - filter to it
                // immediately rather than showing every row until the next
                // zoom change.
                filterDetailTableToZoomRange();
            }
        });
    }

    // Left-side view switcher (GC / Heap Contents / eventually Profile) -
    // an axis orthogonal to the tabButton/tabPanel handling above: the GC
    // view's own Charts/Detailed tabs are unaffected and live one level
    // deeper, inside #view-gc. Same show/hide-via-active-class mechanism,
    // keyed on data-view/id="view-*" instead of data-tab/id="tab-*".
    var allocationSummaryInjected = false;

    // { chart, zoomHandle } for every currently-rendered Heap Contents
    // chart - detached/destroyed and rebuilt on every zoom change (renderHeapContentsCharts
    // below), since the same <canvas> elements persist across zoom changes
    // and stale listeners/instances would otherwise pile up on them.
    var heapContentsChartHandles = [];

    // Computed once, the first time the Heap Contents view is opened - gcs
    // doesn't change after that, so there's no need to recompute this on
    // every zoom change.
    var gen0GcTimesMSecForCharts = null;
    var gen1GcTimesMSecForCharts = null;

    function destroyHeapContentsCharts() {
        for (var handleIndex = 0; handleIndex < heapContentsChartHandles.length; ++handleIndex) {
            var handle = heapContentsChartHandles[handleIndex];
            if (handle.zoomHandle) {
                handle.zoomHandle.detach();
            }
            handle.chart.destroy();
        }
        heapContentsChartHandles = [];
    }

    function onHeapContentsRangeSelected(startMSec, endMSec) {
        applySharedZoom({ startMSec: startMSec, endMSec: endMSec });
    }

    function updateZoomStatusUi(zoomRange) {
        var statusBar = document.getElementById("allocationZoomStatus");
        if (!statusBar) {
            return;
        }

        if (!zoomRange) {
            statusBar.style.display = "none";
            return;
        }

        statusBar.style.display = "block";
        var label = statusBar.getElementsByClassName("allocationZoomStatusLabel")[0];
        if (label) {
            label.textContent = "Zoomed: " + formatElapsedMsForAllocationChart(zoomRange.startMSec) +
                " – " + formatElapsedMsForAllocationChart(zoomRange.endMSec) + " (Backspace to reset)";
        }
    }

    // Recomputes each type's byte total (and a matching grand total to
    // percentage against) for zoomRange - or, when null, just wraps
    // summaryScope's own server-computed topTypes/totalSampledBytes
    // unchanged. Both branches return the same shape: an array of
    // {typeIndex, TypeName, TotalBytes} sorted by TotalBytes descending
    // (typeIndex is always the *original* position in topTypes/typeDrillDown,
    // preserved through the re-sort so a zoomed row still resolves the right
    // drill-down entry), plus grandTotalBytes to percentage each row against.
    //
    // Only possible because typeTimeline.buckets carries a real per-type
    // byte breakdown per time bucket (see allocationStats.js's
    // renderAllocationTypeTimelineChart, which reads the same field) -
    // there's no equivalent per-bucket breakdown for tick/small/large/
    // pinned counts, so those stay whole-capture-only (see
    // updateRankedTypesTables's ticksOnlyColumn hiding).
    function computeZoomedTypeStats(summaryScope, zoomRange) {
        var topTypes = summaryScope["topTypes"];

        if (!zoomRange) {
            var wholeCaptureTypes = [];
            for (var wholeIndex = 0; wholeIndex < topTypes.length; ++wholeIndex) {
                // Tick/Small/Large/Pinned counts carried straight through
                // (unlike the zoomed branch below, the real whole-capture
                // values are available here - see renderRankedTypesTableRows,
                // which renders them only when present on typeStats).
                wholeCaptureTypes.push({
                    typeIndex: wholeIndex,
                    TypeName: topTypes[wholeIndex]["TypeName"],
                    TotalBytes: topTypes[wholeIndex]["TotalBytes"],
                    TickCount: topTypes[wholeIndex]["TickCount"],
                    SmallCount: topTypes[wholeIndex]["SmallCount"],
                    LargeCount: topTypes[wholeIndex]["LargeCount"],
                    PinnedCount: topTypes[wholeIndex]["PinnedCount"]
                });
            }
            // Already sorted server-side by TotalBytes descending - no
            // re-sort needed.
            return { types: wholeCaptureTypes, grandTotalBytes: summaryScope["totalSampledBytes"] };
        }

        var typeTimeline = summaryScope["typeTimeline"];
        var buckets = typeTimeline["buckets"];
        var bucketWidthMSec = typeTimeline["bucketWidthMSec"] || 0;

        // One extra slot beyond topTypes.length for "Other" (typeTimeline's
        // own last type - see allocationStats.js) - not itself rendered as
        // a ranked row (topTypes never includes it either), but its bytes
        // still belong in grandTotalBytes so the zoomed "% of Sampled"
        // figure means the same thing the unzoomed one does: share of every
        // sampled byte in range, not just the ranked subset of it.
        var bytesByTypeIndex = new Array(typeTimeline["types"].length).fill(0);
        for (var bucketIndex = 0; bucketIndex < buckets.length; ++bucketIndex) {
            var bucket = buckets[bucketIndex];
            var bucketStartMSec = bucket["bucketStartMSec"];
            var bucketEndMSec = bucketStartMSec + bucketWidthMSec;
            // Same overlap test as renderAllocationTypeTimelineChart's own
            // bucket filter (allocationStats.js) - a bucket qualifies if it
            // overlaps the zoom window at all, not just if its own start
            // falls inside it.
            if (bucketEndMSec <= zoomRange.startMSec || bucketStartMSec >= zoomRange.endMSec) {
                continue;
            }

            var bytesByType = bucket["bytesByType"];
            for (var typeIdx = 0; typeIdx < bytesByType.length; ++typeIdx) {
                bytesByTypeIndex[typeIdx] += bytesByType[typeIdx];
            }
        }

        var grandTotalBytes = 0;
        for (var sumIndex = 0; sumIndex < bytesByTypeIndex.length; ++sumIndex) {
            grandTotalBytes += bytesByTypeIndex[sumIndex];
        }

        var zoomedTypes = [];
        for (var topIndex = 0; topIndex < topTypes.length; ++topIndex) {
            zoomedTypes.push({
                typeIndex: topIndex,
                TypeName: topTypes[topIndex]["TypeName"],
                TotalBytes: bytesByTypeIndex[topIndex] || 0
            });
        }
        zoomedTypes.sort(function (left, right) { return right.TotalBytes - left.TotalBytes; });

        return { types: zoomedTypes, grandTotalBytes: grandTotalBytes };
    }

    // Rebuilds one ranked-types table's <tr> rows (everything after its own
    // header row) from zoomedStats, and toggles allocationTypeTableZoomed on
    // the table itself so the CSS-hidden Tick/Small/Large/Pinned columns
    // (ticksOnlyColumn - see AllocationSummaryRenderer.ts) show or hide to
    // match. Matches AllocationSummaryRenderer.ts's renderTypeBreakdownPanel
    // row markup exactly (same classes/attributes), just built client-side
    // from possibly-recomputed data instead of the server-rendered original.
    function renderRankedTypesTableRows(zoomedStats, scope) {
        var mb = 1024 * 1024;
        var rowsHtml = "";

        for (var rowIndex = 0; rowIndex < zoomedStats.types.length; ++rowIndex) {
            var typeStats = zoomedStats.types[rowIndex];
            var totalBytes = typeStats["TotalBytes"];
            var percentOfSampled = zoomedStats.grandTotalBytes > 0 ? (totalBytes * 100.0) / zoomedStats.grandTotalBytes : 0;

            var tdTotalBytes = (totalBytes / mb).toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 });
            var tdPercent = percentOfSampled.toFixed(2);

            // Only the unzoomed branch of computeZoomedTypeStats carries
            // these through (the zoomed branch has no per-bucket data for
            // them - see computeZoomedTypeStats's own comment) - undefined
            // here means "leave the CSS-hidden cells empty", matching the
            // ticksOnlyColumn hide rule these same cells already carry.
            var tdTickCount = typeStats.TickCount !== undefined ? typeStats.TickCount : "";
            var tdSmallCount = typeStats.SmallCount !== undefined ? typeStats.SmallCount : "";
            var tdLargeCount = typeStats.LargeCount !== undefined ? typeStats.LargeCount : "";
            var tdPinnedCount = typeStats.PinnedCount !== undefined ? typeStats.PinnedCount : "";

            rowsHtml += `<tr class="typeRow" data-type-index="${typeStats.typeIndex}" data-scope="${scope}">` +
                `<td>${typeStats.TypeName}</td>` +
                `<td>${tdTotalBytes}</td>` +
                `<td>${tdPercent}</td>` +
                `<td class="ticksOnlyColumn">${tdTickCount}</td><td class="ticksOnlyColumn">${tdSmallCount}</td><td class="ticksOnlyColumn">${tdLargeCount}</td><td class="ticksOnlyColumn">${tdPinnedCount}</td>` +
                `</tr>`;
        }

        return rowsHtml;
    }

    function updateOneRankedTypesTable(summaryScope, scope, zoomRange) {
        var table = document.getElementById("allocationTypeTable-" + scope);
        if (!table) {
            return;
        }

        var zoomedStats = computeZoomedTypeStats(summaryScope, zoomRange);
        var headerRow = table.getElementsByClassName("tableHeader")[0];
        table.innerHTML = "";
        table.appendChild(headerRow);
        table.insertAdjacentHTML("beforeend", renderRankedTypesTableRows(zoomedStats, scope));
        table.classList.toggle("allocationTypeTableZoomed", !!zoomRange);
    }

    // Called on every zoom change (see renderHeapContentsCharts below) -
    // updates both the "all" and "loh" ranked types tables (whichever
    // exist), not just whichever is currently visible behind the All
    // Types/LOH Only toggle, so switching that toggle later shows
    // already-correct data instead of needing its own separate trigger.
    function updateRankedTypesTables(zoomRange) {
        updateOneRankedTypesTable(allocationSummaryJson, "all", zoomRange);

        var lohSummaryForTable = allocationSummaryJson["loh"];
        if (lohSummaryForTable && lohSummaryForTable["topTypes"] && lohSummaryForTable["topTypes"].length > 0) {
            updateOneRankedTypesTable(lohSummaryForTable, "loh", zoomRange);
        }
    }

    // Rebuilds only the Heap Contents view's own charts - called both by the
    // initial Heap Contents open (with the then-current sharedZoomRange, in
    // case a GC chart was already zoomed first) and by applySharedZoom above
    // (which also rebuilds the GC charts, keeping both views in sync
    // regardless of which one a drag-select actually happened on). zoomRange
    // is null for the full, unzoomed capture.
    function renderHeapContentsCharts(zoomRange) {
        destroyHeapContentsCharts();
        updateZoomStatusUi(zoomRange);

        var zoomOptionsForRate = { range: zoomRange, onRangeSelected: onHeapContentsRangeSelected };
        var rateChartHandle = renderAllocationTimelineChart(
            document.getElementById("allocationTimelineChart"),
            allocationSummaryJson["ticks"],
            gen0GcTimesMSecForCharts,
            gen1GcTimesMSecForCharts,
            zoomOptionsForRate);
        if (rateChartHandle) {
            heapContentsChartHandles.push(rateChartHandle);
        }

        // "all" and (if present) "loh" stacked charts are both built up
        // front, not lazily on toggle - see AllocationSummaryRenderer.ts's
        // renderTypeBreakdownPanel. Each gets its own onSegmentClick
        // closure bound to its own scope object so onDrillDownSegmentClick
        // doesn't need any global "which view is active" state to resolve
        // the right drillDown/typeTimeline data.
        var allChartHandle = renderAllocationTypeTimelineChart(
            document.getElementById("allocationTypeTimelineChart-all"),
            allocationSummaryJson["typeTimeline"],
            function (typeIndex, bucketIndex) {
                onDrillDownSegmentClick(typeIndex, bucketIndex, allocationSummaryJson, "All Types");
            },
            { range: zoomRange, onRangeSelected: onHeapContentsRangeSelected });
        if (allChartHandle) {
            heapContentsChartHandles.push(allChartHandle);
        }

        var lohSummary = allocationSummaryJson["loh"];
        if (lohSummary && lohSummary["topTypes"] && lohSummary["topTypes"].length > 0) {
            var lohChartHandle = renderAllocationTypeTimelineChart(
                document.getElementById("allocationTypeTimelineChart-loh"),
                lohSummary["typeTimeline"],
                function (typeIndex, bucketIndex) {
                    onDrillDownSegmentClick(typeIndex, bucketIndex, lohSummary, "LOH Only");
                },
                { range: zoomRange, onRangeSelected: onHeapContentsRangeSelected });
            if (lohChartHandle) {
                heapContentsChartHandles.push(lohChartHandle);
            }
        }

        updateRankedTypesTables(zoomRange);
    }

    var viewNavButtons = document.getElementsByClassName("viewNavButton");
    for (var viewButtonIndex = 0; viewButtonIndex < viewNavButtons.length; ++viewButtonIndex) {
        viewNavButtons[viewButtonIndex].addEventListener('click', function (event) {
            var targetView = event.currentTarget.getAttribute('data-view');

            var buttons = document.getElementsByClassName("viewNavButton");
            for (var buttonIndex = 0; buttonIndex < buttons.length; ++buttonIndex) {
                buttons[buttonIndex].classList.remove('active');
            }

            var panels = document.getElementsByClassName("viewPanel");
            for (var panelIndex = 0; panelIndex < panels.length; ++panelIndex) {
                panels[panelIndex].classList.remove('active');
            }

            event.currentTarget.classList.add('active');
            document.getElementById('view-' + targetView).classList.add('active');

            if (targetView === 'heapContents' && !allocationSummaryInjected) {
                var holder = document.getElementById("allocationSummaryHtml");
                var allocationSummaryHtml = holder.innerHTML.slice(4, holder.innerHTML.length - 3);

                // Tiles -> chart canvas -> table order comes from
                // AllocationSummaryRenderer.ts's own markup now (single
                // source of truth) - this just injects it and wires up the
                // chart once the canvas element actually exists in the DOM.
                document.getElementById('view-heapContents').innerHTML = allocationSummaryHtml;

                // "Allocated before this GC" reference line needs each
                // Gen0/Gen1 GC's own start time - gcs is this file's own
                // parsed data (allocationStats.js has no access to it), so
                // it's extracted here and passed down as plain arrays.
                gen0GcTimesMSecForCharts = [];
                gen1GcTimesMSecForCharts = [];
                for (var gcIndex = 0; gcIndex < gcs.length; ++gcIndex) {
                    var gcEntry = gcs[gcIndex]["data"];
                    if (gcEntry["generation"] === 0) {
                        gen0GcTimesMSecForCharts.push(gcEntry["PauseStartRelativeMSec"]);
                    } else if (gcEntry["generation"] === 1) {
                        gen1GcTimesMSecForCharts.push(gcEntry["PauseStartRelativeMSec"]);
                    }
                }
                gen0GcTimesMSecForCharts.sort(function (left, right) { return left - right; });
                gen1GcTimesMSecForCharts.sort(function (left, right) { return left - right; });

                // Use whatever zoom is already applied (e.g. dragged on a GC
                // chart before Heap Contents was ever opened) rather than
                // always starting unzoomed.
                renderHeapContentsCharts(sharedZoomRange);

                wireHeapContentsInnerTabs();
                allocationSummaryInjected = true;
            }
        });
    }

    // "Charts"/"Drill Down" inner tabs within the Heap Contents view - a
    // third, distinct navigational axis from the GC view's own
    // Charts/Detailed tabs (.tabButton/.tabPanel) and the top-level
    // GC/Heap Contents view switcher (.viewNavButton/.viewPanel) just
    // above, so it's keyed on its own heapContentsTabButton/
    // heapContentsTabPanel classes and "heapContents-tab-*" ids to avoid
    // colliding with either. Wired once, right after
    // AllocationSummaryRenderer.ts's markup is injected above (the buttons
    // don't exist in the DOM before that).
    function switchHeapContentsTab(targetTab) {
        var buttons = document.getElementsByClassName("heapContentsTabButton");
        for (var buttonIndex = 0; buttonIndex < buttons.length; ++buttonIndex) {
            buttons[buttonIndex].classList.remove('active');
            if (buttons[buttonIndex].getAttribute('data-heaptab') === targetTab) {
                buttons[buttonIndex].classList.add('active');
            }
        }

        var panels = document.getElementsByClassName("heapContentsTabPanel");
        for (var panelIndex = 0; panelIndex < panels.length; ++panelIndex) {
            panels[panelIndex].classList.remove('active');
        }
        document.getElementById('heapContents-tab-' + targetTab).classList.add('active');

        var backButton = document.getElementById('backToChartsButton');
        if (backButton) {
            backButton.style.display = (targetTab === 'drilldown') ? 'inline-block' : 'none';
        }
    }

    // The Charts panel has summary tiles and the allocation-rate line chart
    // above the stacked type-timeline (bar) chart a drill-down was reached
    // from - just switching tabs back leaves that scrolled out of view
    // above the fold. Scrolls it back into view so "go back" actually
    // returns to what you were just looking at, not just the top of the
    // panel.
    function goBackToChartsView() {
        switchHeapContentsTab('charts');

        var activeViewButton = document.querySelector('.allocationViewButton.active');
        var activeScope = activeViewButton ? activeViewButton.getAttribute('data-allocview') : 'all';
        var stackedBarChart = document.getElementById('allocationTypeTimelineChart-' + activeScope);
        if (stackedBarChart) {
            stackedBarChart.scrollIntoView({ behavior: 'smooth', block: 'center' });
        }
    }

    // Builds this row's own subtree the first time it's expanded -
    // drillDownStats.js's renderCallerRow/renderDrillDownTable only emit an
    // empty data-lazy="true" placeholder for any row with children, so the
    // nested-table HTML (the expensive part) only ever gets built for a
    // subtree a user actually asked to see, not eagerly for all of them on
    // every drill-down click. Safe to call on an already-built detailRow -
    // it's a no-op (data-lazy is removed once built, so this just falls
    // through).
    function buildDrillDownRowIfLazy(detailRow) {
        if (!detailRow || detailRow.getAttribute('data-lazy') !== 'true') {
            return;
        }

        var builtHtml = buildLazyDrillDownSubtree(detailRow.id);
        if (builtHtml !== null) {
            detailRow.querySelector('.callerTreeCell').innerHTML = builtHtml;
        }
        detailRow.removeAttribute('data-lazy');
    }

    // Bulk-toggles every collapsible row in the drill-down panel at once
    // (see the Expand All/Collapse All buttons in drillDownStats.js's
    // renderDrillDownTable, and the leafMethodRow "reveal the whole story"
    // click below) - every node with at least one child is now individually
    // collapsible (not just real branch points), so a deep tree can take
    // many individual clicks to fully open or close by hand.
    //
    // When expanding, this also has to *build* every still-lazy row under
    // container first - building one level can introduce new
    // still-collapsed-and-unbuilt rows one level deeper (a child that
    // itself has children), so every newly-built row's own lazy children
    // get queued up too, ensuring "expand everything under here" really
    // does reach every depth, not just whatever happened to already exist.
    // Collapsing never needs this - hiding already-built content via CSS is
    // still cheap, and there's no reason to throw already-built DOM away.
    //
    // Deliberately an explicit worklist, not "re-run container.querySelector
    // for the next lazy row until none are left" - re-querying the *whole*
    // container on every single row is a real perf bug that shipped here
    // once already: each call re-scans everything already built too, so a
    // tree with N rows did on the order of N container-wide scans (each one
    // itself O(container size)), which measurably stalled/froze the webview
    // on a large capture's deep or wide call stacks. Scoping each lazy scan
    // to only the subtree that specific build just produced keeps the total
    // work proportional to N, not N^2.
    function setAllDrillDownRowsExpanded(container, expanded) {
        if (expanded) {
            var lazyQueue = [];

            // container is sometimes itself a still-lazy .callPathsDetail row
            // (a leafMethodRow's own detail row, the first time it's
            // expanded) - Element.querySelector[All] only searches
            // *descendants*, never the element it's called on, so container
            // itself has to be queued explicitly rather than only its
            // descendants (e.g. the whole drillDownPanel, for Expand All,
            // which is never itself a lazy row).
            if (container.classList && container.classList.contains('callPathsDetail')) {
                lazyQueue.push(container);
            }

            var initialLazyRows = container.querySelectorAll('.callPathsDetail[data-lazy="true"]');
            for (var initialIndex = 0; initialIndex < initialLazyRows.length; ++initialIndex) {
                lazyQueue.push(initialLazyRows[initialIndex]);
            }

            while (lazyQueue.length > 0) {
                var lazyRow = lazyQueue.pop();
                buildDrillDownRowIfLazy(lazyRow);

                // Scoped to the row just built, not the whole container -
                // this is what keeps the total work linear (see above).
                var newlyLazyRows = lazyRow.querySelectorAll('.callPathsDetail[data-lazy="true"]');
                for (var newIndex = 0; newIndex < newlyLazyRows.length; ++newIndex) {
                    lazyQueue.push(newlyLazyRows[newIndex]);
                }
            }
        }

        var toggleRows = container.querySelectorAll('[data-expandable="true"]');
        for (var rowIndex = 0; rowIndex < toggleRows.length; ++rowIndex) {
            var toggleRow = toggleRows[rowIndex];
            var detailRow = document.getElementById(toggleRow.getAttribute('data-target'));

            if (expanded) {
                toggleRow.classList.add('expanded');
                if (detailRow) {
                    detailRow.classList.add('expanded');
                }
            } else {
                toggleRow.classList.remove('expanded');
                if (detailRow) {
                    detailRow.classList.remove('expanded');
                }
            }
        }
    }

    // Shared by both ways into the Drill Down tab (a chart-segment click and
    // a global-table row click below) - injects the rendered table, reveals
    // the tab button (hidden until there's actually something to show), and
    // switches to it.
    function showDrillDownTab(drillDownHtml) {
        document.getElementById('heapContents-tab-drilldown').innerHTML = drillDownHtml;

        var drillDownTabButton = document.getElementById('drillDownTabButton');
        if (drillDownTabButton) {
            drillDownTabButton.style.display = 'inline-block';
        }

        switchHeapContentsTab('drilldown');
    }

    // Called from allocationStats.js's onClick handler on the type-timeline
    // chart when a real (non-"Other") stacked segment is clicked. Scoped to
    // that one (type, 1-second bucket) cell. summaryScope is either
    // allocationSummaryJson (the "all" chart) or allocationSummaryJson.loh
    // (the "LOH Only" chart) - each chart's onSegmentClick closure binds its
    // own scope (see the renderAllocationTypeTimelineChart calls above), so
    // this never has to guess which toggle state is currently active.
    function onDrillDownSegmentClick(typeIndex, bucketIndex, summaryScope, filterLabel) {
        var drillDown = summaryScope["drillDown"];
        var cellEntry = (drillDown && drillDown["cells"]) ? drillDown["cells"][typeIndex + ":" + bucketIndex] : null;

        var typeTimeline = summaryScope["typeTimeline"];
        var typeName = typeTimeline["types"][typeIndex];
        var bucketLabel = formatElapsedMsForAllocationChart(typeTimeline["buckets"][bucketIndex]["bucketStartMSec"]);

        showDrillDownTab(renderDrillDownTable(cellEntry, typeName, bucketLabel, filterLabel, allocationSummaryJson["methodNames"], summaryScope["totalSampledBytes"]));
    }

    // Called from the click delegation in wireHeapContentsInnerTabs below
    // when a row in a ranked types table (either the "all" or "loh" one -
    // see summaryScope) is clicked. Scoped to that type across the *whole*
    // capture (AllocationJsonExporter.cs's typeDrillDown - a parallel array
    // to topTypes), not one chart cell - merges every bucket's stacks for
    // this type into one view.
    function onTypeDrillDownClick(typeIndex, summaryScope, filterLabel) {
        var typeDrillDown = summaryScope["typeDrillDown"];
        var typeEntry = typeDrillDown ? typeDrillDown[typeIndex] : null;
        var typeName = summaryScope["topTypes"][typeIndex]["TypeName"];

        showDrillDownTab(renderDrillDownTable(typeEntry, typeName, "Whole Capture", filterLabel, allocationSummaryJson["methodNames"], summaryScope["totalSampledBytes"]));
    }

    function wireHeapContentsInnerTabs() {
        var heapContentsTabButtons = document.getElementsByClassName("heapContentsTabButton");
        for (var tabButtonIndex = 0; tabButtonIndex < heapContentsTabButtons.length; ++tabButtonIndex) {
            heapContentsTabButtons[tabButtonIndex].addEventListener('click', function (event) {
                switchHeapContentsTab(event.currentTarget.getAttribute('data-heaptab'));
            });
        }

        var backToChartsButton = document.getElementById('backToChartsButton');
        if (backToChartsButton) {
            backToChartsButton.addEventListener('click', goBackToChartsView);
        }

        // Click equivalent of the Backspace zoom-reset (see the keydown
        // listener further below) - for anyone who doesn't know/want to use
        // the keyboard shortcut. Only visible while a zoom is actually
        // applied (see updateZoomStatusUi).
        var resetZoomButton = document.getElementById('resetZoomButton');
        if (resetZoomButton) {
            resetZoomButton.addEventListener('click', function () {
                applySharedZoom(null);
            });
        }

        // Ranked types table rows (AllocationSummaryRenderer.ts) - both the
        // "all" and "loh" tables are only ever injected once (not rebuilt
        // per click like the drill-down panel itself), so a direct listener
        // on their shared container is fine here rather than needing
        // delegation on something more stable. data-scope on each row (see
        // renderTypeBreakdownPanel) picks which summary object
        // (allocationSummaryJson or its .loh) the click resolves against.
        var chartsPanel = document.getElementById('heapContents-tab-charts');
        if (chartsPanel) {
            chartsPanel.addEventListener('click', function (event) {
                var typeRow = event.target.closest('.typeRow');
                if (!typeRow) {
                    return;
                }

                var isLohRow = typeRow.getAttribute('data-scope') === 'loh';
                var rowScope = isLohRow ? allocationSummaryJson["loh"] : allocationSummaryJson;
                onTypeDrillDownClick(parseInt(typeRow.getAttribute('data-type-index'), 10), rowScope, isLohRow ? "LOH Only" : "All Types");
            });
        }

        // "All Types" / "LOH Only" filter toggle (only rendered at all when
        // allocationSummary.loh has data - see AllocationSummaryRenderer.ts).
        // A pure CSS show/hide between the two pre-rendered panels/charts,
        // not a re-render - both charts already exist by the time this is
        // wired up.
        var allocationViewButtons = document.getElementsByClassName("allocationViewButton");
        for (var allocViewIdx = 0; allocViewIdx < allocationViewButtons.length; ++allocViewIdx) {
            allocationViewButtons[allocViewIdx].addEventListener('click', function (event) {
                var targetScope = event.currentTarget.getAttribute('data-allocview');

                var buttons = document.getElementsByClassName("allocationViewButton");
                for (var buttonIndex = 0; buttonIndex < buttons.length; ++buttonIndex) {
                    buttons[buttonIndex].classList.remove('active');
                }
                event.currentTarget.classList.add('active');

                var panels = document.getElementsByClassName("allocationViewPanel");
                for (var panelIndex = 0; panelIndex < panels.length; ++panelIndex) {
                    panels[panelIndex].classList.remove('active');
                }
                document.getElementById('allocView-' + targetScope).classList.add('active');
            });
        }

        // Event delegation, attached once to the panel itself rather than
        // per-row - drillDownStats.js's renderDrillDownTable rebuilds this
        // panel's entire innerHTML on every chart-segment click, which
        // would otherwise silently drop any listeners attached directly to
        // its rows. [data-expandable="true"] marks a toggleable row at any
        // depth - both the outer leafMethodRow and any deeper callerRow
        // branch point use the same attribute/behavior (see
        // drillDownStats.js's renderCallerChainRows).
        var drillDownPanel = document.getElementById('heapContents-tab-drilldown');
        if (drillDownPanel) {
            drillDownPanel.addEventListener('click', function (event) {
                if (event.target.closest('.drillDownExpandAllBtn')) {
                    setAllDrillDownRowsExpanded(drillDownPanel, true);
                    return;
                }

                if (event.target.closest('.drillDownCollapseAllBtn')) {
                    setAllDrillDownRowsExpanded(drillDownPanel, false);
                    return;
                }

                var leafRow = event.target.closest('[data-expandable="true"]');
                if (!leafRow) {
                    return;
                }

                var detailRow = document.getElementById(leafRow.getAttribute('data-target'));
                if (!detailRow) {
                    return;
                }

                // Expanding/collapsing the root (leaf/allocation-site) row
                // reveals or hides the *entire* call stack beneath it in one
                // click, not just the next level - that's the "show me the
                // whole story for this allocation" action a user reaches
                // for first, and previously meant clicking through every
                // intermediate branch point one at a time even though a
                // straight (non-branching) run already showed itself
                // automatically. A caller row deeper in the tree still
                // toggles just its own immediate children, so a
                // fully-expanded stack can still be selectively
                // re-collapsed one branch at a time.
                if (leafRow.classList.contains('leafMethodRow')) {
                    var willExpand = !leafRow.classList.contains('expanded');
                    setAllDrillDownRowsExpanded(detailRow, willExpand);
                    leafRow.classList.toggle('expanded', willExpand);
                    detailRow.classList.toggle('expanded', willExpand);
                    return;
                }

                // A single caller row's own toggle only reveals its
                // *immediate* children, lazily building just that one level
                // the first time (see drillDownStats.js's renderCallerRow/
                // buildLazyDrillDownSubtree) - deeper levels stay collapsed
                // and unbuilt until expanded themselves.
                var willExpandOneLevel = !leafRow.classList.contains('expanded');
                if (willExpandOneLevel) {
                    buildDrillDownRowIfLazy(detailRow);
                }

                leafRow.classList.toggle('expanded');
                detailRow.classList.toggle('expanded');
            });
        }
    }

    // Backspace has two mutually-exclusive meanings on this page, checked in
    // the same listener so their precedence is explicit rather than relying
    // on two independent listeners never happening to both fire (which
    // they can't anyway, since a tab being active is exclusive - but
    // stating that as one function's own logic is clearer than trusting an
    // invariant across two separate ones):
    //   - Drill Down tab active: return to Charts (existing behavior).
    //   - Charts tab active (either view) and a zoom is applied: reset
    //     sharedZoomRange back to the full capture via applySharedZoom -
    //     only when zoomed, so Backspace does nothing surprising otherwise.
    //     The GC and Heap Contents views are mutually exclusive (only one
    //     .viewPanel is ever active), so this can't conflict with the
    //     Drill Down check above it.
    document.addEventListener('keydown', function (event) {
        if (event.key !== 'Backspace') {
            return;
        }

        var gcViewPanel = document.getElementById('view-gc');
        if (gcViewPanel && gcViewPanel.classList.contains('active') && sharedZoomRange) {
            event.preventDefault();
            applySharedZoom(null);
            return;
        }

        var drillDownPanel = document.getElementById('heapContents-tab-drilldown');
        if (drillDownPanel && drillDownPanel.classList.contains('active')) {
            event.preventDefault();
            goBackToChartsView();
            return;
        }

        var chartsPanelForZoom = document.getElementById('heapContents-tab-charts');
        if (chartsPanelForZoom && chartsPanelForZoom.classList.contains('active') && sharedZoomRange) {
            event.preventDefault();
            applySharedZoom(null);
        }
    });

    // ── Heap Snapshot (gcHeapAnalyzer output) ────────────────────────────────
    // File is read entirely in the webview via FileReader — no extension-host
    // round-trip needed. The tab button is hidden until a snapshot is loaded.

    var HEAP_GEN_LABELS = ['Gen0', 'Gen1', 'Gen2', 'LOH', 'POH'];

    var genLabelFor = function (generation) {
        return (generation >= 0 && generation < HEAP_GEN_LABELS.length)
            ? HEAP_GEN_LABELS[generation] : 'Gen' + generation;
    };

    // Loaded snapshot + the currently-selected generation filter (-1 = "All
    // Generations") for the Free Chunk Distribution section - module-level
    // so the filter buttons can re-render just that section without
    // re-parsing or re-walking the whole snapshot.
    var heapSnapshotData = null;
    var heapSnapshotGenFilter = -1;
    var freeChunkCountChartInstance = null;
    var freeChunkBytesChartInstance = null;

    var formatBytes = function (bytes) {
        if (bytes >= 1073741824) { return (bytes / 1073741824).toFixed(1) + ' GB'; }
        if (bytes >= 1048576)    { return (bytes / 1048576).toFixed(1) + ' MB'; }
        if (bytes >= 1024)       { return (bytes / 1024).toFixed(1) + ' KB'; }
        return bytes + ' B';
    };

    var fragPctClass = function (pct) {
        if (pct > 40) { return 'expensiveGc'; }
        if (pct > 20) { return 'warnGc'; }
        if (pct > 10) { return 'interstingGc'; }
        return '';
    };

    var buildSnapshotSummaryHtml = function (snapshot) {
        var summary = snapshot.summary;
        return '<div class="snapshotCaptureInfo">' +
            '<span class="snapshotProcessName">' + snapshot.processName + '</span>' +
            ' &mdash; captured ' + formatHumanDateTime(snapshot.captureTimeUtc) +
            '</div>' +
            '<div class="summaryGcDiv">' +
                '<div class="total">' +
                    '<div>Heap</div>' +
                    '<div>Committed<span>' + formatBytes(summary.totalCommittedBytes) + '</span></div>' +
                    '<div>Live<span>' + formatBytes(summary.totalObjectBytes) + '</span></div>' +
                    '<div>Free<span>' + formatBytes(summary.totalFreeBytes) + '</span></div>' +
                    '<div>Frag %<span class="' + fragPctClass(summary.fragmentationPct) + '">' + summary.fragmentationPct.toFixed(1) + '%</span></div>' +
                '</div>' +
                '<div class="gen0">' +
                    '<div>Holes</div>' +
                    '<div>Total Chunks<span>' + snapshot.freeChunks.totalCount + '</span></div>' +
                    '<div>Large (&ge;85 KB)<span>' + snapshot.freeChunks.largeChunks.length + '</span></div>' +
                    '<div>Pinned Objects<span>' + summary.pinnedObjectCount + '</span></div>' +
                    '<div>Segments<span>' + summary.segmentCount + '</span></div>' +
                '</div>' +
            '</div>';
    };

    var buildGenerationTableHtml = function (generations) {
        var header = '<tr class="tableHeader">' +
            '<th>Generation</th><th>Committed</th><th>Live</th><th>Free</th>' +
            '<th>Frag %</th><th>Segments</th><th>Free Chunks</th></tr>';
        var rows = '';
        for (var genIdx = 0; genIdx < generations.length; ++genIdx) {
            var gen = generations[genIdx];
            if (gen.committedBytes === 0 && gen.generation === 4) { continue; }
            rows += '<tr>' +
                '<td>' + gen.label + '</td>' +
                '<td>' + formatBytes(gen.committedBytes) + '</td>' +
                '<td>' + formatBytes(gen.objectBytes) + '</td>' +
                '<td>' + formatBytes(gen.freeBytes) + '</td>' +
                '<td class="' + fragPctClass(gen.fragmentationPct) + '">' + gen.fragmentationPct.toFixed(1) + '%</td>' +
                '<td>' + gen.segmentCount + '</td>' +
                '<td>' + gen.freeChunkCount + '</td>' +
                '</tr>';
        }
        return '<div class="detailTable"><table>' + header + rows + '</table></div>';
    };

    var buildFreeChunkTableHtml = function (freeChunks) {
        var header = '<tr class="tableHeader">' +
            '<th>Size Range</th><th>Count</th><th>Total Free</th><th>% of Free</th></tr>';
        var rows = '';
        for (var bucketIdx = 0; bucketIdx < freeChunks.histogram.length; ++bucketIdx) {
            var bucket = freeChunks.histogram[bucketIdx];
            var pct = freeChunks.totalFreeBytes > 0
                ? ((bucket.totalBytes / freeChunks.totalFreeBytes) * 100).toFixed(1)
                : '0.0';
            rows += '<tr>' +
                '<td>' + bucket.label + '</td>' +
                '<td>' + bucket.count + '</td>' +
                '<td>' + formatBytes(bucket.totalBytes) + '</td>' +
                '<td>' + pct + '%</td>' +
                '</tr>';
        }
        return '<div class="detailTable"><table>' + header + rows + '</table></div>';
    };

    // Preceding/Following describe the nearest live object on either side of
    // the hole (see AddToHistogram/LargeFreeChunk in HeapAnalyzer.cs) - a
    // pin badge on either side is the direct root-cause signal for Gen2:
    // the GC can't compact past a pinned object, so a hole bounded by one is
    // permanent regardless of how many more collections run, whereas a hole
    // bounded by two ordinary objects points at a GC that simply hasn't
    // compacted yet, or a free-list size mismatch.
    var buildAdjacencyCellHtml = function (typeName, isPinned) {
        var pinBadge = isPinned ? ' <span class="pinnedBadge" title="Pinned">&#128204;</span>' : '';
        return '<code>' + typeName + '</code>' + pinBadge;
    };

    var buildLargeChunksTableHtml = function (largeChunks) {
        var displayChunks = largeChunks.length > 50 ? largeChunks.slice(0, 50) : largeChunks;
        var header = '<tr class="tableHeader"><th>Address</th><th>Size</th><th>Generation</th>' +
            '<th>Preceding</th><th>Following</th></tr>';
        var rows = '';
        for (var chunkIdx = 0; chunkIdx < displayChunks.length; ++chunkIdx) {
            var chunk = displayChunks[chunkIdx];
            rows += '<tr>' +
                '<td><code>' + chunk.address + '</code></td>' +
                '<td>' + formatBytes(chunk.sizeBytes) + '</td>' +
                '<td>' + genLabelFor(chunk.generation) + '</td>' +
                '<td>' + buildAdjacencyCellHtml(chunk.precedingType, chunk.precedingIsPinned) + '</td>' +
                '<td>' + buildAdjacencyCellHtml(chunk.followingType, chunk.followingIsPinned) + '</td>' +
                '</tr>';
        }
        var note = largeChunks.length > 50
            ? '<p style="margin-top:4px;font-style:italic">Showing first 50 of ' + largeChunks.length + ' large chunks.</p>'
            : '';
        return '<div class="detailTable"><table>' + header + rows + '</table></div>' + note;
    };

    var buildPinnedTableHtml = function (pinnedObjects) {
        var header = '<tr class="tableHeader"><th>Type</th><th>Generation</th><th>Count</th><th>Total Size</th></tr>';
        var rows = '';
        for (var pinnedIdx = 0; pinnedIdx < pinnedObjects.length; ++pinnedIdx) {
            var pinned = pinnedObjects[pinnedIdx];
            rows += '<tr>' +
                '<td>' + pinned.typeName + '</td>' +
                '<td>' + genLabelFor(pinned.generation) + '</td>' +
                '<td>' + pinned.count + '</td>' +
                '<td>' + formatBytes(pinned.totalBytes) + '</td>' +
                '</tr>';
        }
        return '<div class="detailTable"><table>' + header + rows + '</table></div>';
    };

    var occupancyPctClass = function (pct) {
        if (pct < 10) { return 'expensiveGc'; }
        if (pct < 30) { return 'warnGc'; }
        if (pct < 60) { return 'interstingGc'; }
        return '';
    };

    var buildSegmentTableHtml = function (segments) {
        var header = '<tr class="tableHeader"><th>Address</th><th>Generation</th>' +
            '<th>Committed</th><th>Live</th><th>Occupancy %</th></tr>';
        var rows = '';
        for (var segmentIdx = 0; segmentIdx < segments.length; ++segmentIdx) {
            var segment = segments[segmentIdx];
            rows += '<tr>' +
                '<td><code>' + segment.address + '</code></td>' +
                '<td>' + genLabelFor(segment.generation) + '</td>' +
                '<td>' + formatBytes(segment.committedBytes) + '</td>' +
                '<td>' + formatBytes(segment.liveBytes) + '</td>' +
                '<td class="' + occupancyPctClass(segment.occupancyPct) + '">' + segment.occupancyPct.toFixed(1) + '%</td>' +
                '</tr>';
        }
        return '<div class="detailTable"><table>' + header + rows + '</table></div>';
    };

    // Shared by the Top LOH Types and Top POH Types tables - both are plain
    // type-ranked lists with the same {typeName, count, totalBytes} shape
    // (see ReportJsonExporter.cs's SerializeTypeStats, which writes both).
    var buildTypeStatTableHtml = function (typeStats) {
        var header = '<tr class="tableHeader"><th>Type</th><th>Count</th><th>Total Size</th></tr>';
        var rows = '';
        for (var typeIdx = 0; typeIdx < typeStats.length; ++typeIdx) {
            var typeStat = typeStats[typeIdx];
            rows += '<tr>' +
                '<td>' + typeStat.typeName + '</td>' +
                '<td>' + typeStat.count + '</td>' +
                '<td>' + formatBytes(typeStat.totalBytes) + '</td>' +
                '</tr>';
        }
        return '<div class="detailTable"><table>' + header + rows + '</table></div>';
    };

    // Re-rendered on every generation-filter change (see wireGenFilterButtons
    // below), so any previous chart instances must be destroyed first - the
    // canvases persist across re-renders (only innerHTML around them
    // changes), and Chart.js throws if a second chart is attached to a
    // canvas that already has one.
    var buildFreeChunkCharts = function (histogram, titleSuffix) {
        if (freeChunkCountChartInstance) { freeChunkCountChartInstance.destroy(); freeChunkCountChartInstance = null; }
        if (freeChunkBytesChartInstance) { freeChunkBytesChartInstance.destroy(); freeChunkBytesChartInstance = null; }

        var bucketLabels = [];
        var countData = [];
        var bytesData = [];

        for (var bucketIdx = 0; bucketIdx < histogram.length; ++bucketIdx) {
            bucketLabels.push(histogram[bucketIdx].label);
            countData.push(histogram[bucketIdx].count);
            bytesData.push(histogram[bucketIdx].totalBytes);
        }

        var chartColors = [
            'rgba(72, 83, 136, 0.6)',
            'rgba(96, 165, 69, 0.6)',
            'rgba(141, 31, 95, 0.6)',
            'rgba(220, 53, 69, 0.6)',
            'rgba(201, 221, 84, 0.6)'
        ];

        var countCanvas = document.getElementById('freeChunkCountChart');
        if (countCanvas) {
            freeChunkCountChartInstance = new Chart(countCanvas.getContext('2d'), {
                type: 'horizontalBar',
                data: {
                    labels: bucketLabels,
                    datasets: [{
                        label: 'Count',
                        data: countData,
                        backgroundColor: chartColors,
                        borderWidth: 1
                    }]
                },
                options: {
                    title: { display: true, text: 'Free Chunks by Count' + titleSuffix },
                    scales: { xAxes: [{ ticks: { beginAtZero: true } }] },
                    legend: { display: false },
                    animation: { duration: 0 },
                    maintainAspectRatio: false
                }
            });
        }

        var bytesCanvas = document.getElementById('freeChunkBytesChart');
        if (bytesCanvas) {
            freeChunkBytesChartInstance = new Chart(bytesCanvas.getContext('2d'), {
                type: 'horizontalBar',
                data: {
                    labels: bucketLabels,
                    datasets: [{
                        label: 'Bytes',
                        data: bytesData,
                        backgroundColor: chartColors,
                        borderWidth: 1
                    }]
                },
                options: {
                    title: { display: true, text: 'Free Space by Size Bucket' + titleSuffix },
                    scales: {
                        xAxes: [{
                            ticks: {
                                beginAtZero: true,
                                callback: function (value) { return formatBytes(value); }
                            }
                        }]
                    },
                    legend: { display: false },
                    animation: { duration: 0 },
                    maintainAspectRatio: false
                }
            });
        }
    };

    // Free Chunk Distribution + Large Free Holes, scoped to whichever
    // generation is currently selected (heapSnapshotGenFilter, -1 = All).
    // Rebuilds just this section's DOM + charts rather than the whole
    // snapshot panel, since it's the only part a filter change affects.
    var renderFreeChunkSection = function () {
        var snapshot = heapSnapshotData;
        if (!snapshot) { return; }

        var container = document.getElementById('freeChunkSection');
        if (!container) { return; }

        var isAll = heapSnapshotGenFilter < 0;
        var histogram = isAll ? snapshot.freeChunks.histogram : snapshot.generations[heapSnapshotGenFilter].histogram;
        var titleSuffix = isAll ? '' : ' (' + genLabelFor(heapSnapshotGenFilter) + ')';

        var largeChunks = snapshot.freeChunks.largeChunks;
        if (!isAll) {
            largeChunks = [];
            for (var chunkIdx = 0; chunkIdx < snapshot.freeChunks.largeChunks.length; ++chunkIdx) {
                if (snapshot.freeChunks.largeChunks[chunkIdx].generation === heapSnapshotGenFilter) {
                    largeChunks.push(snapshot.freeChunks.largeChunks[chunkIdx]);
                }
            }
        }

        var html = '<div class="freeChunkHistogramRow">' +
                        '<div class="freeChunkHistogramChart"><canvas id="freeChunkCountChart"></canvas></div>' +
                        '<div class="freeChunkHistogramChart"><canvas id="freeChunkBytesChart"></canvas></div>' +
                    '</div>';
        html += buildFreeChunkTableHtml({ histogram: histogram, totalFreeBytes: histogram.reduce(function (sum, bucket) { return sum + bucket.totalBytes; }, 0) });

        if (largeChunks.length > 0) {
            html += '<h4 class="detailTableHeading">Large Free Holes (&ge; 85 KB)' + titleSuffix + ' &mdash; ' +
                    largeChunks.length + ' total</h4>';
            html += buildLargeChunksTableHtml(largeChunks);
        } else {
            html += '<p>No large free holes' + titleSuffix + '.</p>';
        }

        container.innerHTML = html;
        buildFreeChunkCharts(histogram, titleSuffix);
    };

    var wireGenFilterButtons = function () {
        var filterButtons = document.getElementsByClassName('genFilterButton');
        for (var buttonIdx = 0; buttonIdx < filterButtons.length; ++buttonIdx) {
            filterButtons[buttonIdx].addEventListener('click', function (event) {
                var buttons = document.getElementsByClassName('genFilterButton');
                for (var idx = 0; idx < buttons.length; ++idx) {
                    buttons[idx].classList.remove('active');
                }
                event.currentTarget.classList.add('active');

                heapSnapshotGenFilter = parseInt(event.currentTarget.getAttribute('data-gen-filter'), 10);
                renderFreeChunkSection();
            });
        }
    };

    // Real bytes span orders of magnitude (a 20 MB gap next to a 5 KB live
    // run) - a linear width would make every small block invisible, so
    // widths are sqrt-scaled instead: compresses the dynamic range while
    // still keeping larger blocks visibly larger, and a CSS min-width (see
    // .segmentBlock) keeps labels legible regardless.
    var segmentBlockFlexGrow = function (bytes) {
        return Math.max(1, Math.round(Math.sqrt(Math.max(bytes, 1))));
    };

    var buildSegmentBlockHtml = function (block) {
        var flexGrow = segmentBlockFlexGrow(block.bytes);

        if (block.isGap) {
            return '<div class="segmentBlock segmentBlockGap" style="flex-grow:' + flexGrow + '" ' +
                'title="Free / fragmented gap - ' + formatBytes(block.bytes) + '">' +
                '<div class="segmentBlockLabel">Free</div>' +
                '<div class="segmentBlockSize">' + formatBytes(block.bytes) + '</div>' +
                '</div>';
        }

        var moreLabel = block.otherTypeCount > 0 ? ' <span class="segmentBlockMore">+' + block.otherTypeCount + ' more</span>' : '';
        var pinBadge = block.hasPinnedObject ? ' <span class="pinnedBadge" title="Contains a pinned object">&#128204;</span>' : '';
        var classes = 'segmentBlock segmentBlockLive' + (block.hasPinnedObject ? ' segmentBlockPinned' : '');
        var titleText = block.typeName + (block.otherTypeCount > 0 ? ' (+' + block.otherTypeCount + ' more types)' : '') +
            ' - ' + block.objectCount + ' object(s), ' + formatBytes(block.bytes);

        return '<div class="' + classes + '" style="flex-grow:' + flexGrow + '" title="' + titleText + '">' +
            '<div class="segmentBlockLabel">' + block.typeName + moreLabel + pinBadge + '</div>' +
            '<div class="segmentBlockSize">' + formatBytes(block.bytes) + '</div>' +
            '</div>';
    };

    var buildSegmentStripHtml = function (segmentMap, occupancy) {
        var occLabel = occupancy ? (' &mdash; ' + occupancy.occupancyPct.toFixed(1) + '% occupied') : '';
        var heading = '<h4 class="segmentStripHeading">' + genLabelFor(segmentMap.generation) + ' segment ' +
            '<code>' + segmentMap.address + '</code>' + occLabel + ', address-ordered</h4>';

        var blocksHtml = '<div class="segmentStripRow">';
        for (var blockIdx = 0; blockIdx < segmentMap.blocks.length; ++blockIdx) {
            blocksHtml += buildSegmentBlockHtml(segmentMap.blocks[blockIdx]);
        }
        blocksHtml += '</div>';

        return heading + blocksHtml;
    };

    // One address-ordered strip per Gen2/LOH/POH segment (see SegmentMap in
    // HeapAnalyzer.cs) - occupancyByAddress correlates each strip back to
    // its Segment Occupancy table row so the strip's heading can show the
    // same % the Overview tab already reports for that segment.
    var renderSegmentMapSection = function (snapshot) {
        var container = document.getElementById('heapSnapshot-tab-segmentmap');
        if (!container) { return; }

        var occupancyByAddress = {};
        if (snapshot.segments) {
            for (var segIdx = 0; segIdx < snapshot.segments.length; ++segIdx) {
                occupancyByAddress[snapshot.segments[segIdx].address] = snapshot.segments[segIdx];
            }
        }

        var html = '<div class="segmentStripLegend">' +
                '<span class="segmentStripLegendItem"><span class="segmentBlock segmentBlockLive segmentBlockPinned segmentStripLegendSwatch"></span>Pinned live object(s)</span>' +
                '<span class="segmentStripLegendItem"><span class="segmentBlock segmentBlockLive segmentStripLegendSwatch"></span>Live object(s)</span>' +
                '<span class="segmentStripLegendItem"><span class="segmentBlock segmentBlockGap segmentStripLegendSwatch"></span>Free / fragmented gap</span>' +
            '</div>';

        for (var mapIdx = 0; mapIdx < snapshot.segmentMaps.length; ++mapIdx) {
            var segmentMap = snapshot.segmentMaps[mapIdx];
            html += buildSegmentStripHtml(segmentMap, occupancyByAddress[segmentMap.address]);
        }

        container.innerHTML = html;
    };

    var wireHeapSnapshotInnerTabs = function () {
        var tabButtons = document.getElementsByClassName('heapSnapshotInnerTabButton');
        for (var buttonIdx = 0; buttonIdx < tabButtons.length; ++buttonIdx) {
            tabButtons[buttonIdx].addEventListener('click', function (event) {
                var targetTab = event.currentTarget.getAttribute('data-heapsnaptab');

                var buttons = document.getElementsByClassName('heapSnapshotInnerTabButton');
                for (var idx = 0; idx < buttons.length; ++idx) {
                    buttons[idx].classList.remove('active');
                }
                event.currentTarget.classList.add('active');

                var panels = document.getElementsByClassName('heapSnapshotInnerTabPanel');
                for (var panelIdx = 0; panelIdx < panels.length; ++panelIdx) {
                    panels[panelIdx].classList.remove('active');
                }
                document.getElementById('heapSnapshot-tab-' + targetTab).classList.add('active');
            });
        }
    };

    var renderHeapSnapshot = function (snapshot) {
        var panel = document.getElementById('tab-heapSnapshot');
        if (!panel) { return; }

        heapSnapshotData = snapshot;
        heapSnapshotGenFilter = -1;

        var overviewHtml = buildSnapshotSummaryHtml(snapshot);

        overviewHtml += '<h3 class="detailTableHeading">Generation Breakdown</h3>';
        overviewHtml += buildGenerationTableHtml(snapshot.generations);

        if (snapshot.segments && snapshot.segments.length > 0) {
            overviewHtml += '<h3 class="detailTableHeading">Segment Occupancy</h3>' +
                '<p class="segmentOccupancyHint">Sorted least-occupied first - a low-occupancy segment is a fragmentation ' +
                'signal on its own, whatever the cause (a pinned object, an ordinary long-lived anchor, or a GC that ' +
                'simply hasn\'t compacted recently).</p>';
            overviewHtml += buildSegmentTableHtml(snapshot.segments);
        }

        overviewHtml += '<h3 class="detailTableHeading">Free Chunk Distribution</h3>';
        overviewHtml += '<div class="genFilterRow">' +
                    '<button class="genFilterButton active" data-gen-filter="-1">All</button>' +
                    '<button class="genFilterButton" data-gen-filter="0">Gen0</button>' +
                    '<button class="genFilterButton" data-gen-filter="1">Gen1</button>' +
                    '<button class="genFilterButton" data-gen-filter="2">Gen2</button>' +
                    '<button class="genFilterButton" data-gen-filter="3">LOH</button>' +
                    '<button class="genFilterButton" data-gen-filter="4">POH</button>' +
                '</div>';
        overviewHtml += '<div id="freeChunkSection"></div>';

        if (snapshot.pinnedObjects && snapshot.pinnedObjects.length > 0) {
            overviewHtml += '<h3 class="detailTableHeading">Pinned Object Types</h3>';
            overviewHtml += buildPinnedTableHtml(snapshot.pinnedObjects);
        } else {
            overviewHtml += '<h3 class="detailTableHeading">Pinned Objects</h3><p>No pinned objects detected.</p>';
        }

        if (snapshot.topLohTypes && snapshot.topLohTypes.length > 0) {
            overviewHtml += '<h3 class="detailTableHeading">Top LOH Types</h3>';
            overviewHtml += buildTypeStatTableHtml(snapshot.topLohTypes);
        }

        if (snapshot.topPohTypes && snapshot.topPohTypes.length > 0) {
            overviewHtml += '<h3 class="detailTableHeading">Top POH Types</h3>';
            overviewHtml += buildTypeStatTableHtml(snapshot.topPohTypes);
        }

        var hasSegmentMaps = snapshot.segmentMaps && snapshot.segmentMaps.length > 0;

        var html = '<div class="heapSnapshotInnerTabBar">' +
                '<button class="heapSnapshotInnerTabButton active" data-heapsnaptab="overview">Overview</button>' +
                (hasSegmentMaps ? '<button class="heapSnapshotInnerTabButton" data-heapsnaptab="segmentmap">Segment Map</button>' : '') +
            '</div>';
        html += '<div id="heapSnapshot-tab-overview" class="heapSnapshotInnerTabPanel active">' + overviewHtml + '</div>';
        if (hasSegmentMaps) {
            html += '<div id="heapSnapshot-tab-segmentmap" class="heapSnapshotInnerTabPanel"></div>';
        }

        panel.innerHTML = html;
        renderFreeChunkSection();
        wireGenFilterButtons();
        wireHeapSnapshotInnerTabs();

        if (hasSegmentMaps) {
            renderSegmentMapSection(snapshot);
        }

        var tabBtn = document.getElementById('heapSnapshotTabBtn');
        if (tabBtn) {
            tabBtn.style.display = 'inline-block';
            tabBtn.click();
        }
    };

    var loadHeapSnapshotBtn = document.getElementById('loadHeapSnapshotBtn');
    var heapSnapshotInput = document.getElementById('heapSnapshotInput');

    if (loadHeapSnapshotBtn && heapSnapshotInput) {
        loadHeapSnapshotBtn.addEventListener('click', function () {
            heapSnapshotInput.click();
        });

        heapSnapshotInput.addEventListener('change', function (event) {
            var file = event.target.files[0];
            if (!file) { return; }

            var reader = new FileReader();
            reader.onload = function (loadEvent) {
                var snapshot;
                var showSnapshotLoadError = function (msg) {
                    var snapshotPanel = document.getElementById('tab-heapSnapshot');
                    if (snapshotPanel) {
                        snapshotPanel.innerHTML = '<p class="snapshotLoadError">' + msg + '</p>';
                    }
                    var snapshotTabBtn = document.getElementById('heapSnapshotTabBtn');
                    if (snapshotTabBtn) {
                        snapshotTabBtn.style.display = 'inline-block';
                        snapshotTabBtn.click();
                    }
                };

                try {
                    snapshot = JSON.parse(loadEvent.target.result);
                } catch (parseErr) {
                    showSnapshotLoadError('Could not parse heap snapshot: ' + parseErr.message);
                    return;
                }

                if (!snapshot.summary || !snapshot.generations || !snapshot.freeChunks) {
                    showSnapshotLoadError('File does not appear to be a gcHeapAnalyzer output (missing summary / generations / freeChunks).');
                    return;
                }

                renderHeapSnapshot(snapshot);

                // Allow reloading a different snapshot
                heapSnapshotInput.value = '';
            };

            reader.readAsText(file);
        });
    }

    // Handle messages sent from the extension to the webview
    window.addEventListener('message', event => {
        const message = event.data; // The json data that the extension sent
        switch (message.type) {
            case 'update':
                const text = message.text;

                // Then persist state information.
                // This state is returned in the call to `vscode.getState` below when a webview is reloaded.
                vscode.setState({ text });

                return;
        }
    });

    // Webviews are normally torn down when not visible and re-created when they become visible again.
    // State lets us save information across these re-loads
    const state = vscode.getState();

}());