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

    var timestamps = [];
    for (var index = 0; index < gcs.length; ++index) {
        timestamps.push(gcs[index]["data"]["Id"]);
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
                labels: timestamps,
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
                labels: timestamps,
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