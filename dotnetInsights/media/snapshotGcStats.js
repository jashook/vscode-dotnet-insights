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

    // DateTime is a real calendar date/time (in the parsing machine's local
    // timezone - see GcJsonExporter.cs) for .nettrace sources, or a
    // "+elapsed since capture start" string for .gcinfo (XML) sources, which
    // have no absolute time anchor available - see gcDataFromXml.
    var formatGcAxisTime = function (dateTimeString) {
        if (dateTimeString === undefined || dateTimeString === null) {
            return "";
        }

        if (dateTimeString.charAt(0) === '+') {
            // Already a compact elapsed-time string (.gcinfo/XML source).
            return dateTimeString;
        }

        var parsed = new Date(dateTimeString);
        if (isNaN(parsed.getTime())) {
            return "";
        }

        return parsed.toLocaleTimeString();
    };

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

    var timestamps = [];
    var gcDateTimes = [];
    // Chart.js 2.x renders an array label as multiple stacked lines under the
    // tick, so each axis tick shows both the GC number and its time.
    var chartLabels = [];
    for (var index = 0; index < gcs.length; ++index) {
        var gcId = gcs[index]["data"]["Id"];
        var gcDateTime = gcs[index]["data"]["DateTime"];

        timestamps.push(gcId);
        gcDateTimes.push(gcDateTime);
        chartLabels.push([`${gcId}`, formatGcAxisTime(gcDateTime)]);
    }

    var gcTooltipTitle = function (tooltipItems) {
        var lines = [];
        for (var itemIndex = 0; itemIndex < tooltipItems.length; ++itemIndex) {
            var gcIndex = tooltipItems[itemIndex].index;
            lines.push(`GC #${timestamps[gcIndex]} — ${formatHumanDateTime(gcDateTimes[gcIndex])}`);
        }
        return lines;
    };

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

    var totalGcPauseTimeOverTime = document.getElementById("totalGcPauseTimeOverTime");
    const totalGcPauseTimeOverTimeContext = totalGcPauseTimeOverTime.getContext('2d');

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

    var totalGcPauseTimeOverTimeChart = new Chart(totalGcPauseTimeOverTimeContext, {
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
                        ticks: {
                            callback: formatElapsedMs
                        },
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

    var totalGcStatsOverTime = document.getElementById("totalGcStatsOverTime");
    const totalGcStatsOverTimeContext = totalGcStatsOverTime.getContext('2d');

    var totalGen0DataSet = [];
    var totalGen1DataSet = [];
    var totalGen2DataSet = [];
    var totalLohDataSet = [];

    var totalMb = 1024 * 1024;

    for (var index = 0; index < gcs.length; ++index) {
        var gcData = gcs[index]["data"];
        

        totalGen0DataSet.push(gcData["GenerationSize0"] / totalMb);
        totalGen1DataSet.push(gcData["GenerationSize1"] / totalMb);
        totalGen2DataSet.push(gcData["GenerationSize2"] / totalMb);
        totalLohDataSet.push(gcData["GenerationSizeLOH"] / totalMb);
    }

    var totalGcStatsOverTimeChart = new Chart(totalGcStatsOverTimeContext, {
        type: 'line',
            data: {
                labels: chartLabels,
                datasets: [{
                    label: 'Gen 0',
                    data: totalGen0DataSet,
                    backgroundColor: [
                        "rgba(72, 83, 136, 0.2)",
                    ],
                    borderWidth: 1,
                    pointRadius: 2,
                    pointHoverRadius: 4
                },
                {
                    label: "Gen 1",
                    data: totalGen1DataSet,
                    backgroundColor: [
                        "rgba(96, 165, 69, 0.2)",
                    ],
                    borderWidth: 1,
                    pointRadius: 2,
                    pointHoverRadius: 4
                },
                {
                    label: "Gen 2",
                    data: totalGen2DataSet,
                    backgroundColor: [
                        "rgba(141, 31, 95, 0.2)",
                    ],
                    borderWidth: 1,
                    pointRadius: 2,
                    pointHoverRadius: 4
                },
                {
                    label: "LOH",
                    data: totalLohDataSet,
                    backgroundColor: [
                        "rgba(201, 221, 84, 0.2)"
                    ],
                    borderWidth: 1,
                    pointRadius: 2,
                    pointHoverRadius: 4
                }
            ]},
            options: {
                title: {
                    display: true,
                    text: `Total GC Usage by Generation`
                },
                scales: {
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
                        title: gcTooltipTitle
                    }
                },
                animation: { duration: 0 },
                "maintainAspectRatio": false,
            }
    });

    if (document.getElementById("gcFragmentationOverTime")) {
      // The fragmentation chart is below the fold - deferring its GC x heap x
      // gen dataset computation to after the above-fold charts have painted
      // avoids blocking the initial render for work the user may not
      // immediately scroll to see.
      requestAnimationFrame(function () {
        var fragGen0Dataset = [];
        var fragGen1Dataset = [];
        var fragGen2Dataset = [];
        var fragLohDataset = [];
        var fragTotalDataset = [];
        var pinnedCountDataset = [];
        var compactionMarkerDataset = [];

        for (var fragIndex = 0; fragIndex < gcs.length; ++fragIndex) {
            var fragGcData = gcs[fragIndex]["data"];
            var fragHeaps = fragGcData["Heaps"];

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

            fragGen0Dataset.push(sizeAfterByGen[0] > 0 ? (fragByGen[0] / sizeAfterByGen[0]) * 100 : 0);
            fragGen1Dataset.push(sizeAfterByGen[1] > 0 ? (fragByGen[1] / sizeAfterByGen[1]) * 100 : 0);
            fragGen2Dataset.push(sizeAfterByGen[2] > 0 ? (fragByGen[2] / sizeAfterByGen[2]) * 100 : 0);
            fragLohDataset.push(sizeAfterByGen[3] > 0 ? (fragByGen[3] / sizeAfterByGen[3]) * 100 : 0);
            fragTotalDataset.push(totalHeapSizeBytes > 0 ? (totalFragBytes / totalHeapSizeBytes) * 100 : 0);

            pinnedCountDataset.push(parseInt(fragGcData["PinnedObjectCount"]) || 0);

            var mechanisms = parseInt(fragGcData["GlobalMechanisms"]) || 0;
            // GCGlobalMechanisms.Compaction = 0x2
            compactionMarkerDataset.push((mechanisms & 0x2) !== 0 ? 2 : null);
        }

        var gcFragmentationOverTime = document.getElementById("gcFragmentationOverTime");
        var gcFragmentationContext = gcFragmentationOverTime.getContext('2d');

        var gcFragmentationChart = new Chart(gcFragmentationContext, {
            type: 'line',
            data: {
                labels: chartLabels,
                datasets: [
                    {
                        label: 'Total',
                        data: fragTotalDataset,
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
                        data: fragGen0Dataset,
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
                        data: fragGen1Dataset,
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
                        data: fragGen2Dataset,
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
                        data: fragLohDataset,
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
                        data: pinnedCountDataset,
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
                        data: compactionMarkerDataset,
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
            options: {
                title: {
                    display: true,
                    text: 'Heap Fragmentation by Generation'
                },
                scales: {
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
                        title: gcTooltipTitle
                    }
                },
                animation: { duration: 0 },
                maintainAspectRatio: false
            }
        });
      });
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

    const setChart = (passedHeapIndex) => {
        var gen0DataSet = [];
        var gen1DataSet = [];
        var gen2DataSet = [];
        var lohDataSet = [];

        var mb = 1024;

        for (var index = 0; index < gcs.length; ++index) {
            var gcData = gcs[index]["data"];

            var currentHeap = gcData["Heaps"][passedHeapIndex]["Generations"];
            
            gen0DataSet.push(currentHeap[0]["SizeAfter"] / mb);
            gen1DataSet.push(currentHeap[1]["SizeAfter"] / mb);
            gen2DataSet.push(currentHeap[2]["SizeAfter"] / mb);
            lohDataSet.push(currentHeap[3]["SizeAfter"] / mb);
        }

        var ctx = heapCharts[passedHeapIndex];
        ctx = ctx.getContext('2d');
        var heapChart = new Chart(ctx, {
            type: 'line',
            data: {
                labels: chartLabels,
                datasets: [{
                    label: 'Gen 0',
                    data: gen0DataSet,
                    backgroundColor: [
                        'rgba(54, 162, 235, 0.2)',
                    ],
                    borderWidth: 1,
                    pointRadius: 2,
                    pointHoverRadius: 4
                },
                {
                    label: "Gen 1",
                    data: gen1DataSet,
                    backgroundColor: [
                        'rgba(75, 192, 192, 0.2)'
                    ],
                    borderWidth: 1,
                    pointRadius: 2,
                    pointHoverRadius: 4
                },
                {
                    label: "Gen 2",
                    data: gen2DataSet,
                    backgroundColor: [
                        'rgba(153, 102, 255, 0.2)'
                    ],
                    borderWidth: 1,
                    pointRadius: 2,
                    pointHoverRadius: 4
                },
                {
                    label: "LOH",
                    data: lohDataSet,
                    backgroundColor: [
                        'rgba(255, 206, 86, 0.2)'
                    ],
                    borderWidth: 1,
                    pointRadius: 2,
                    pointHoverRadius: 4
                }
            ]},
            options: {
                title: {
                    display: true,
                    text: `Heap: ${passedHeapIndex}`
                },
                scales: {
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
                        title: gcTooltipTitle
                    }
                },
                animation: { duration: 0 },
                "maintainAspectRatio": false,
            }
        });
    };

    var heapCharts = document.getElementsByClassName("heapChart");

    // Server GC traces can have 16+ heaps, each getting its own Chart.js
    // instance - building all of them synchronously on load is expensive and
    // most users never scroll down to see every heap. Defer each chart's
    // construction until its canvas actually scrolls into view.
    if ('IntersectionObserver' in window) {
        var heapChartObserver = new IntersectionObserver(function (entries, observer) {
            for (var entryIndex = 0; entryIndex < entries.length; ++entryIndex) {
                var entry = entries[entryIndex];
                if (entry.isIntersecting) {
                    var heapIndex = parseInt(entry.target.getAttribute('data-heap-index'), 10);
                    setChart(heapIndex);
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
            setChart(index);
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

                renderGenerationBreakdownSection();
                detailTableInjected = true;
            }
        });
    }

    // Left-side view switcher (GC / Heap Contents / eventually Profile) -
    // an axis orthogonal to the tabButton/tabPanel handling above: the GC
    // view's own Charts/Detailed tabs are unaffected and live one level
    // deeper, inside #view-gc. Same show/hide-via-active-class mechanism,
    // keyed on data-view/id="view-*" instead of data-tab/id="tab-*".
    var allocationSummaryInjected = false;

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
                var gen0GcTimesMSec = [];
                var gen1GcTimesMSec = [];
                for (var gcIndex = 0; gcIndex < gcs.length; ++gcIndex) {
                    var gcEntry = gcs[gcIndex]["data"];
                    if (gcEntry["generation"] === 0) {
                        gen0GcTimesMSec.push(gcEntry["PauseStartRelativeMSec"]);
                    } else if (gcEntry["generation"] === 1) {
                        gen1GcTimesMSec.push(gcEntry["PauseStartRelativeMSec"]);
                    }
                }
                gen0GcTimesMSec.sort(function (left, right) { return left - right; });
                gen1GcTimesMSec.sort(function (left, right) { return left - right; });

                renderAllocationTimelineChart(document.getElementById("allocationTimelineChart"), allocationSummaryJson["ticks"], gen0GcTimesMSec, gen1GcTimesMSec);
                renderAllocationTypeTimelineChart(document.getElementById("allocationTypeTimelineChart"), allocationSummaryJson["typeTimeline"], onDrillDownSegmentClick);
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

    // Called from allocationStats.js's onClick handler on the type-timeline
    // chart when a real (non-"Other") stacked segment is clicked.
    function onDrillDownSegmentClick(typeIndex, bucketIndex) {
        var drillDown = allocationSummaryJson["drillDown"];
        var cellStacks = (drillDown && drillDown["cells"]) ? drillDown["cells"][typeIndex + ":" + bucketIndex] : null;

        var typeTimeline = allocationSummaryJson["typeTimeline"];
        var typeName = typeTimeline["types"][typeIndex];
        var bucketLabel = formatElapsedMsForAllocationChart(typeTimeline["buckets"][bucketIndex]["bucketStartMSec"]);

        document.getElementById('heapContents-tab-drilldown').innerHTML = renderDrillDownTable(cellStacks, typeName, bucketLabel);

        var drillDownTabButton = document.getElementById('drillDownTabButton');
        if (drillDownTabButton) {
            drillDownTabButton.style.display = 'inline-block';
        }

        switchHeapContentsTab('drilldown');
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
            backToChartsButton.addEventListener('click', function () {
                switchHeapContentsTab('charts');
            });
        }
    }

    // Backspace returns to Charts, but only while the Drill Down tab is
    // actually the active one - otherwise this would hijack Backspace
    // everywhere else on the page for no reason.
    document.addEventListener('keydown', function (event) {
        if (event.key !== 'Backspace') {
            return;
        }

        var drillDownPanel = document.getElementById('heapContents-tab-drilldown');
        if (drillDownPanel && drillDownPanel.classList.contains('active')) {
            event.preventDefault();
            switchHeapContentsTab('charts');
        }
    });

    // ── Heap Snapshot (gcHeapAnalyzer output) ────────────────────────────────
    // File is read entirely in the webview via FileReader — no extension-host
    // round-trip needed. The tab button is hidden until a snapshot is loaded.

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

    var buildLargeChunksTableHtml = function (largeChunks) {
        var GEN_LABELS = ['Gen0', 'Gen1', 'Gen2', 'LOH', 'POH'];
        var displayChunks = largeChunks.length > 50 ? largeChunks.slice(0, 50) : largeChunks;
        var header = '<tr class="tableHeader"><th>Address</th><th>Size</th><th>Generation</th></tr>';
        var rows = '';
        for (var chunkIdx = 0; chunkIdx < displayChunks.length; ++chunkIdx) {
            var chunk = displayChunks[chunkIdx];
            var genLabel = (chunk.generation >= 0 && chunk.generation < GEN_LABELS.length)
                ? GEN_LABELS[chunk.generation] : 'Gen' + chunk.generation;
            rows += '<tr>' +
                '<td><code>' + chunk.address + '</code></td>' +
                '<td>' + formatBytes(chunk.sizeBytes) + '</td>' +
                '<td>' + genLabel + '</td>' +
                '</tr>';
        }
        var note = largeChunks.length > 50
            ? '<p style="margin-top:4px;font-style:italic">Showing first 50 of ' + largeChunks.length + ' large chunks.</p>'
            : '';
        return '<div class="detailTable"><table>' + header + rows + '</table></div>' + note;
    };

    var buildPinnedTableHtml = function (pinnedObjects) {
        var GEN_LABELS = ['Gen0', 'Gen1', 'Gen2', 'LOH', 'POH'];
        var header = '<tr class="tableHeader"><th>Type</th><th>Generation</th><th>Count</th><th>Total Size</th></tr>';
        var rows = '';
        for (var pinnedIdx = 0; pinnedIdx < pinnedObjects.length; ++pinnedIdx) {
            var pinned = pinnedObjects[pinnedIdx];
            var genLabel = (pinned.generation >= 0 && pinned.generation < GEN_LABELS.length)
                ? GEN_LABELS[pinned.generation] : 'Gen' + pinned.generation;
            rows += '<tr>' +
                '<td>' + pinned.typeName + '</td>' +
                '<td>' + genLabel + '</td>' +
                '<td>' + pinned.count + '</td>' +
                '<td>' + formatBytes(pinned.totalBytes) + '</td>' +
                '</tr>';
        }
        return '<div class="detailTable"><table>' + header + rows + '</table></div>';
    };

    var buildLohTypeTableHtml = function (topLohTypes) {
        var header = '<tr class="tableHeader"><th>Type</th><th>Count</th><th>Total Size</th></tr>';
        var rows = '';
        for (var lohTypeIdx = 0; lohTypeIdx < topLohTypes.length; ++lohTypeIdx) {
            var lohType = topLohTypes[lohTypeIdx];
            rows += '<tr>' +
                '<td>' + lohType.typeName + '</td>' +
                '<td>' + lohType.count + '</td>' +
                '<td>' + formatBytes(lohType.totalBytes) + '</td>' +
                '</tr>';
        }
        return '<div class="detailTable"><table>' + header + rows + '</table></div>';
    };

    var buildFreeChunkCharts = function (histogram) {
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
            new Chart(countCanvas.getContext('2d'), {
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
                    title: { display: true, text: 'Free Chunks by Count' },
                    scales: { xAxes: [{ ticks: { beginAtZero: true } }] },
                    legend: { display: false },
                    animation: { duration: 0 },
                    maintainAspectRatio: false
                }
            });
        }

        var bytesCanvas = document.getElementById('freeChunkBytesChart');
        if (bytesCanvas) {
            new Chart(bytesCanvas.getContext('2d'), {
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
                    title: { display: true, text: 'Free Space by Size Bucket' },
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

    var renderHeapSnapshot = function (snapshot) {
        var panel = document.getElementById('tab-heapSnapshot');
        if (!panel) { return; }

        var html = buildSnapshotSummaryHtml(snapshot);

        html += '<h3 class="detailTableHeading">Generation Breakdown</h3>';
        html += buildGenerationTableHtml(snapshot.generations);

        html += '<h3 class="detailTableHeading">Free Chunk Distribution</h3>';
        html += '<div class="freeChunkHistogramRow">' +
                    '<div class="freeChunkHistogramChart"><canvas id="freeChunkCountChart"></canvas></div>' +
                    '<div class="freeChunkHistogramChart"><canvas id="freeChunkBytesChart"></canvas></div>' +
                '</div>';
        html += buildFreeChunkTableHtml(snapshot.freeChunks);

        if (snapshot.freeChunks.largeChunks.length > 0) {
            html += '<h3 class="detailTableHeading">Large Free Holes (&ge; 85 KB) &mdash; ' +
                    snapshot.freeChunks.largeChunks.length + ' total</h3>';
            html += buildLargeChunksTableHtml(snapshot.freeChunks.largeChunks);
        }

        if (snapshot.pinnedObjects && snapshot.pinnedObjects.length > 0) {
            html += '<h3 class="detailTableHeading">Pinned Object Types</h3>';
            html += buildPinnedTableHtml(snapshot.pinnedObjects);
        } else {
            html += '<h3 class="detailTableHeading">Pinned Objects</h3><p>No pinned objects detected.</p>';
        }

        if (snapshot.topLohTypes && snapshot.topLohTypes.length > 0) {
            html += '<h3 class="detailTableHeading">Top LOH Types</h3>';
            html += buildLohTypeTableHtml(snapshot.topLohTypes);
        }

        panel.innerHTML = html;
        buildFreeChunkCharts(snapshot.freeChunks.histogram);

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