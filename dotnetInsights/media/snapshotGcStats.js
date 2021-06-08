// Script run within the webview itself.

var allocationDatasets = {};

(function () {

    // Get a reference to the VS Code webview api.
    // We use this API to post messages back to our extension.

    // @ts-ignore
    const vscode = acquireVsCodeApi();

    var gcs = JSON.parse(document.getElementById("hiddenData").innerHTML.slice(4, document.getElementById("hiddenData").innerHTML.length - 3));
    var gcCountsByGen = JSON.parse(document.getElementById("gcCountsByGen").innerHTML.slice(4, document.getElementById("gcCountsByGen").innerHTML.length - 3));

    var totalTimeInEachGcJson = JSON.parse(document.getElementById("totalTimeInEachGcJson").innerHTML.slice(4, document.getElementById("totalTimeInEachGcJson").innerHTML.length - 3));

    console.log(gcs);
    console.log(gcCountsByGen);
    console.log(totalTimeInEachGcJson);

    var timestamps = [];
    for (var index = 0; index < gcs.length; ++index) {
        timestamps.push(gcs[index]["timestamp"]);
    }

    var timestampCopy = [];
    for (var index = 0; index < timestamps.length; ++index) {
        timestampCopy.push(timestamps[index]);
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
                "2",
                "LOH"
            ],
            datasets: [{
                label: "GC Count By Generation",
                data: gcCountsByGen,
                backgroundColor: [
                    "rgba(72, 83, 136, 0.2)",
                    "rgba(96, 165, 69, 0.2)",
                    "rgba(141, 31, 95, 0.2)",
                    "rgba(201, 221, 84, 0.2)"
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
                "2",
                "LOH"
            ],
            datasets: [{
                label: "Total Time In GC By Generation",
                data: totalTimeInEachGcJson,
                backgroundColor: [
                    "rgba(72, 83, 136, 0.2)",
                    "rgba(96, 165, 69, 0.2)",
                    "rgba(141, 31, 95, 0.2)",
                    "rgba(201, 221, 84, 0.2)"
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

    // var heapCharts = document.getElementsByClassName("heapChart");
    // var savedHeapCharts = [];

    // var allocCharts = document.getElementsByClassName("allocChart");
    // var savedAllocCharts = [];

    // const setChart = (passedHeapIndex) => {
    //     var gen0DataSet = [];
    //     var gen1DataSet = [];
    //     var gen2DataSet = [];
    //     var lohDataSet = [];

    //     var kb = 1024 * 1024
    //     var mb = 1024 * mb;

    //     for (var index = 0; index < gcs.length; ++index) {
    //         var gcData = gcs[index]["data"];

    //         var currentHeap = gcData["Heaps"][passedHeapIndex]["Generations"];
    //         for (var heapIndex = 0; heapIndex < currentHeap.length; ++heapIndex) {
    //             var currentGeneration = currentHeap[heapIndex];

    //             if (currentGeneration["Id"] == 0) {
    //                 gen0DataSet.push(currentGeneration["SizeBefore"] / kb);
    //             }
    //             else if(currentGeneration["Id"] == 1) {
    //                 gen1DataSet.push(currentGeneration["SizeBefore"] / kb);
    //             }
    //             else if(currentGeneration["Id"] == 2) {
    //                 gen2DataSet.push(currentGeneration["SizeBefore"] / kb);
    //             }
    //             else if(currentGeneration["Id"] == 3) {
    //                 lohDataSet.push(currentGeneration["SizeBefore"] / kb);
    //             }
    //         }
    //     }

    //     var ctx = heapCharts[passedHeapIndex];
    //     ctx = ctx.getContext('2d');
    //     var heapChart = new Chart(ctx, {
    //         type: 'line',
    //         data: {
    //             labels: timestamps.slice(),
    //             datasets: [{
    //                 label: 'Gen 0',
    //                 data: gen0DataSet,
    //                 backgroundColor: [
    //                     'rgba(54, 162, 235, 0.2)',
    //                 ],
    //                 borderWidth: 1
    //             }, 
    //             {
    //                 label: "Gen 1",
    //                 data: gen1DataSet,
    //                 backgroundColor: [
    //                     'rgba(75, 192, 192, 0.2)'
    //                 ],
    //                 borderWidth: 1
    //             },
    //             {
    //                 label: "Gen 2",
    //                 data: gen2DataSet,
    //                 backgroundColor: [
    //                     'rgba(153, 102, 255, 0.2)'
    //                 ],
    //                 borderWidth: 1
    //             },
    //             {
    //                 label: "LOH",
    //                 data: lohDataSet,
    //                 backgroundColor: [
    //                     'rgba(255, 206, 86, 0.2)'
    //                 ],
    //                 borderWidth: 1
    //             }
    //         ]},
    //         options: {
    //             title: {
    //                 display: true,
    //                 text: `Heap: ${passedHeapIndex}`
    //             },
    //             scales: {
    //                 yAxes: [{
    //                     ticks: {
    //                         beginAtZero: true
    //                     },
    //                     scaleLabel: {
    //                         display: true,
    //                         labelString: "Memory Usage in KB"
    //                     }
    //                 }],
    //             },
    //             "maintainAspectRatio": false,
    //         }
    //     });

    //     savedHeapCharts.push(heapChart);
    // };

    // const setAllocChart = (passedHeapIndex) => {
    //     var typeLabels = [];
    //     var currentAllocDataset = undefined;

    //     if (allocationDatasets[passedHeapIndex] == undefined) {
    //         allocationDatasets[passedHeapIndex] = [];

    //         currentAllocDataset = allocationDatasets[passedHeapIndex];
    //     }

    //     var kb = 1024 * 1024
    //     var mb = 1024 * mb;

    //     var allocationsByType = {};

    //     for (var index = 0; index < gcs.length; ++index) {
    //         var allocsByType = gcs[index]["filteredAllocData"]["types"][passedHeapIndex];

    //         if (allocsByType == undefined) {
    //             continue;
    //         }

    //         var allocTypes = Object.keys(allocsByType);

    //         for (var allocIndex = 0; allocIndex < allocTypes.length; ++allocIndex) {
    //             const type = allocTypes[allocIndex];
    //             const typeAllocation = allocsByType[type];

    //             if (allocationsByType[type] == undefined) {
    //                 allocationsByType[type] = 0;
    //             }

    //             for (var typeAllocIndex = 0; typeAllocIndex < typeAllocation.length; ++typeAllocIndex) {
    //                 allocationsByType[type] += typeAllocation[typeAllocIndex];
    //             }
    //         }
    //     }

    //     var typeKeys = Object.keys(allocationsByType);

    //     var largestValue = 0;

    //      // Build the datasets
    //      for (var index = 0; index < typeKeys.length; ++index) {
    //         const type = typeKeys[index];
    //         allocationsByType[type] /= kb;

    //         largestValue = allocationsByType[type] > largestValue ? allocationsByType[type] : largestValue;
    //     }

    //     // Build the datasets
    //     for (var index = 0; index < typeKeys.length; ++index) {
    //         const type = typeKeys[index];
    //         const typeAllocation = allocationsByType[type];

    //         if ((typeAllocation / largestValue) > .05) {
    //             currentAllocDataset.push(typeAllocation);
    //             typeLabels.push(type);
    //         }
    //     }

    //     console.log("Labels: " + typeLabels + typeLabels.length);
    //     console.log(currentAllocDataset);

    //     var backgroundColors = ['rgba(54, 162, 235, 0.8)', 'rgba(75, 192, 192, 0.8)', 'rgba(153, 102, 255, 0.8)', 'rgba(255, 206, 86, 0.8)'];

    //     var colors = [];
    //     for (var index = 0; index < typeKeys.length; ++index) {
    //         colors.push(backgroundColors[index % backgroundColors.length]);
    //     }

    //     var datasetsToPass = [
    //         {
    //             label: `Allocation`,
    //             data: currentAllocDataset.slice(),
    //             backgroundColor: Object.values(colors)
    //         }
    //     ];

    //     console.log(currentAllocDataset);
    //     console.log(datasetsToPass);

    //     const dataToPass = {
    //         labels: typeLabels,
    //         datasets: datasetsToPass
    //     };

    //     var ctx = allocCharts[passedHeapIndex];
    //     ctx = ctx.getContext('2d');
    //     var allocChart = new Chart(ctx, {
    //         type: 'pie',
    //         data: dataToPass,
    //         options: {
    //             title: {
    //                 display: true,
    //                 text: `Allocations by type Heap: ${passedHeapIndex}`
    //             },
                
    //             "maintainAspectRatio": false,
    //         }
    //     });

    //     savedAllocCharts.push(allocChart);
    // };
    
    // for (var index = 0; index < heapCharts.length; ++index) {
    //     setChart(index);
    // }

    // for (var index = 0; index < allocCharts.length; ++index) {
    //     setAllocChart(index);
    // }

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