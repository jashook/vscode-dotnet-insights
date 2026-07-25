// Script run within the webview itself.

var allocationDatasets = {};

(function () {

    // Get a reference to the VS Code webview api.
    // We use this API to post messages back to our extension.

    // @ts-ignore
    const vscode = acquireVsCodeApi();
  
    console.time("gcParsing");
    var gcs = JSON.parse(document.getElementById("hiddenData").innerHTML.slice(4, document.getElementById("hiddenData").innerHTML.length - 3));
    console.timeEnd("gcParsing");

    console.time("gcCountsByGenParsing");
    var gcCountsByGen = JSON.parse(document.getElementById("gcCountsByGen").innerHTML.slice(4, document.getElementById("gcCountsByGen").innerHTML.length - 3));
    console.timeEnd("gcCountsByGenParsing");

    var totalTimeInEachGcJson = JSON.parse(document.getElementById("totalTimeInEachGcJson").innerHTML.slice(4, document.getElementById("totalTimeInEachGcJson").innerHTML.length - 3));

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
            lines.push(`GC #${timestamps[gcIndex]} — ${gcDateTimes[gcIndex]}`);
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
    var buildPauseTimePulses = function (matches) {
        var points = [];
        for (var index = 0; index < gcs.length; ++index) {
            var pulseGcData = gcs[index]["data"];
            if (!matches(pulseGcData)) {
                continue;
            }

            var startMs = pulseGcData["PauseStartRelativeMSec"];
            var endMs = pulseGcData["PauseEndRelativeMSec"];
            var pauseMs = pulseGcData["PauseDurationMSec"];
            var gcId = pulseGcData["Id"];
            var gcDateTime = pulseGcData["DateTime"];

            points.push({ x: startMs, y: 0, gcId: gcId, dateTime: gcDateTime, isGap: true });
            points.push({ x: startMs, y: pauseMs, gcId: gcId, dateTime: gcDateTime, isGap: false });
            points.push({ x: endMs, y: pauseMs, gcId: gcId, dateTime: gcDateTime, isGap: false });
            points.push({ x: endMs, y: 0, gcId: gcId, dateTime: gcDateTime, isGap: true });
        }
        return points;
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
                lines.push(`GC #${point.gcId} — ${point.dateTime}`);
            }
        }
        return lines;
    };

    var totalGcPauseTimeOverTimeChart = new Chart(totalGcPauseTimeOverTimeContext, {
        type: 'line',
            data: {
                datasets: [{
                    label: 'Total Blocking Time',
                    data: buildPauseTimePulses(function () { return true; }),
                    backgroundColor: [
                        "rgba(220, 53, 69, 0.2)",
                    ],
                    borderColor: "rgba(220, 53, 69, 1)",
                    borderWidth: 1,
                    lineTension: 0
                },
                {
                    label: 'Gen 0',
                    data: buildPauseTimePulses(function (d) { return d["generation"] === 0; }),
                    backgroundColor: [
                        "rgba(72, 83, 136, 0.2)",
                    ],
                    borderWidth: 1,
                    lineTension: 0
                },
                {
                    label: "Gen 1",
                    data: buildPauseTimePulses(function (d) { return d["generation"] === 1; }),
                    backgroundColor: [
                        "rgba(96, 165, 69, 0.2)",
                    ],
                    borderWidth: 1,
                    lineTension: 0
                },
                {
                    label: "Gen 2",
                    data: buildPauseTimePulses(function (d) { return d["generation"] === 2; }),
                    backgroundColor: [
                        "rgba(141, 31, 95, 0.2)",
                    ],
                    borderWidth: 1,
                    lineTension: 0
                },
                {
                    label: "LOH",
                    data: buildPauseTimePulses(function (d) { return lohTriggerReasons[d["Reason"]] === true; }),
                    backgroundColor: [
                        "rgba(201, 221, 84, 0.2)"
                    ],
                    borderWidth: 1,
                    lineTension: 0
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
                    borderWidth: 1
                }, 
                {
                    label: "Gen 1",
                    data: totalGen1DataSet,
                    backgroundColor: [
                        "rgba(96, 165, 69, 0.2)",
                    ],
                    borderWidth: 1
                },
                {
                    label: "Gen 2",
                    data: totalGen2DataSet,
                    backgroundColor: [
                        "rgba(141, 31, 95, 0.2)",
                    ],
                    borderWidth: 1
                },
                {
                    label: "LOH",
                    data: totalLohDataSet,
                    backgroundColor: [
                        "rgba(201, 221, 84, 0.2)"
                    ],
                    borderWidth: 1
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
                "maintainAspectRatio": false,
            }
    });

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
                    borderWidth: 1
                }, 
                {
                    label: "Gen 1",
                    data: gen1DataSet,
                    backgroundColor: [
                        'rgba(75, 192, 192, 0.2)'
                    ],
                    borderWidth: 1
                },
                {
                    label: "Gen 2",
                    data: gen2DataSet,
                    backgroundColor: [
                        'rgba(153, 102, 255, 0.2)'
                    ],
                    borderWidth: 1
                },
                {
                    label: "LOH",
                    data: lohDataSet,
                    backgroundColor: [
                        'rgba(255, 206, 86, 0.2)'
                    ],
                    borderWidth: 1
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
                "maintainAspectRatio": false,
            }
        });
    };

    var heapCharts = document.getElementsByClassName("heapChart");
    
    for (var index = 0; index < heapCharts.length; ++index) {
        setChart(index);
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