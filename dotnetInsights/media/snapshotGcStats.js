// Script run within the webview itself.

var allocationDatasets = {};

(function () {

    // Get a reference to the VS Code webview api.
    // We use this API to post messages back to our extension.

    // @ts-ignore
    const vscode = acquireVsCodeApi();

    var gcs = JSON.parse(document.getElementById("hiddenData").innerHTML.slice(4, document.getElementById("hiddenData").innerHTML.length - 3));
    
    var timestamps = [];
    for (var index = 0; index < gcs.length; ++index) {
        timestamps.push(gcs[index]["timestamp"]);
    }

    var timestampCopy = [];
    for (var index = 0; index < timestamps.length; ++index) {
        timestampCopy.push(timestamps[index]);
    }

    var processMemoryChartDom = document.getElementsByClassName("processMemory")[0];

    const processMemoryChartContext = processMemoryChartDom;
    const context = processMemoryChartContext.getContext('2d');

    var privateBytesDataSet = [];
    var workingSetdataSet = [];
    var pagedMemoryDataSet = [];
    //var virtualMemorySet = [];
    var nonPagedSystemMemoryDataSet = [];
    var pagedSystemMemoryDataSet = [];
    var totalHeapSizeDataSet = [];

    var kb = 1024 * 1024
    var mb = 1024 * mb;

    for (var index = 0; index < gcs.length; ++index) {
        var gcData = gcs[index]["data"];

        privateBytesDataSet.push(gcs[index]["privateBytes"] / kb);
        workingSetdataSet.push(gcs[index]["workingSet"] / kb);
        pagedMemoryDataSet.push(gcs[index]["pagedMemory"] / kb);
        //virtualMemorySet.push(gcs[index]["virtualMemory"] / kb);
        nonPagedSystemMemoryDataSet.push(gcs[index]["nonPagedSystemMemory"] / kb);
        pagedSystemMemoryDataSet.push(gcs[index]["pagedSystemMemory"] / kb);
        totalHeapSizeDataSet.push(gcs[index]["TotalHeapSize"]/ kb);
    }

    //console.log(timestampCopy);

    var processMemoryChart = new Chart(context, {
        type: 'line',
        data: {
            labels: timestampCopy,
            datasets: [{
                label: 'Private Bytes',
                data: privateBytesDataSet,
                backgroundColor: [
                    'rgba(85, 142, 248, 0.2)',
                ],
                borderWidth: 1
            }, 
            {
                label: "Working Set",
                data: workingSetdataSet,
                backgroundColor: [
                    'rgba(255, 52, 52, 0.2)'
                ],
                borderWidth: 1
            },
            {
                label: "Paged Memory",
                data: pagedMemoryDataSet,
                backgroundColor: [
                    'rgba(31, 40, 58, 0.2)'
                ],
                borderWidth: 1
            },
            // {
            //     label: "Virtual Memory",
            //     data: virtualMemorySet,
            //     backgroundColor: [
            //         'rgba(248, 250, 151, 0.808)'
            //     ],
            //     borderWidth: 1
            // },
            {
                label: "Non Paged System Memory",
                data: nonPagedSystemMemoryDataSet,
                backgroundColor: [
                    'rgba(138, 138, 138, 0.2)'
                ],
                borderWidth: 1
            },
            {
                label: "Paged System Memory",
                data: pagedSystemMemoryDataSet,
                backgroundColor: [
                    'rgba(105, 4, 4, 0.2)'
                ],
                borderWidth: 1
            },
            {
                label: "Total Heap Size",
                data: totalHeapSizeDataSet,
                backgroundColor: [
                    'rgba(17, 126, 111, 0.2)'
                ],
                borderWidth: 1
            },
        ]},
        options: {
            title: {
                display: true,
                text: `Process Memory Statistics`
            },
            scales: {
                yAxes: [{
                    ticks: {
                        beginAtZero: true
                    },
                    scaleLabel: {
                        display: true,
                        labelString: "Memory Usage in KB"
                    }
                }],
            },
            "maintainAspectRatio": false,
        }
    });

    var heapCharts = document.getElementsByClassName("heapChart");
    var savedHeapCharts = [];

    var allocCharts = document.getElementsByClassName("allocChart");
    var savedAllocCharts = [];

    const setChart = (passedHeapIndex) => {
        var gen0DataSet = [];
        var gen1DataSet = [];
        var gen2DataSet = [];
        var lohDataSet = [];

        var kb = 1024 * 1024
        var mb = 1024 * mb;

        for (var index = 0; index < gcs.length; ++index) {
            var gcData = gcs[index]["data"];

            var currentHeap = gcData["Heaps"][passedHeapIndex]["Generations"];
            for (var heapIndex = 0; heapIndex < currentHeap.length; ++heapIndex) {
                var currentGeneration = currentHeap[heapIndex];

                if (currentGeneration["Id"] == 0) {
                    gen0DataSet.push(currentGeneration["SizeBefore"] / kb);
                }
                else if(currentGeneration["Id"] == 1) {
                    gen1DataSet.push(currentGeneration["SizeBefore"] / kb);
                }
                else if(currentGeneration["Id"] == 2) {
                    gen2DataSet.push(currentGeneration["SizeBefore"] / kb);
                }
                else if(currentGeneration["Id"] == 3) {
                    lohDataSet.push(currentGeneration["SizeBefore"] / kb);
                }
            }
        }

        var ctx = heapCharts[passedHeapIndex];
        ctx = ctx.getContext('2d');
        var heapChart = new Chart(ctx, {
            type: 'line',
            data: {
                labels: timestamps.slice(),
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
                            labelString: "Memory Usage in KB"
                        }
                    }],
                },
                "maintainAspectRatio": false,
            }
        });

        savedHeapCharts.push(heapChart);
    };

    const setAllocChart = (passedHeapIndex) => {
        var typeLabels = [];
        var currentAllocDataset = undefined;

        if (allocationDatasets[passedHeapIndex] == undefined) {
            allocationDatasets[passedHeapIndex] = [];

            currentAllocDataset = allocationDatasets[passedHeapIndex];
        }

        var kb = 1024 * 1024
        var mb = 1024 * mb;

        var allocationsByType = {};

        for (var index = 0; index < gcs.length; ++index) {
            var allocsByType = gcs[index]["filteredAllocData"]["types"][passedHeapIndex];

            if (allocsByType == undefined) {
                continue;
            }

            var allocTypes = Object.keys(allocsByType);

            for (var allocIndex = 0; allocIndex < allocTypes.length; ++allocIndex) {
                const type = allocTypes[allocIndex];
                const typeAllocation = allocsByType[type];

                if (allocationsByType[type] == undefined) {
                    allocationsByType[type] = 0;
                }

                for (var typeAllocIndex = 0; typeAllocIndex < typeAllocation.length; ++typeAllocIndex) {
                    allocationsByType[type] += typeAllocation[typeAllocIndex];
                }
            }
        }

        var typeKeys = Object.keys(allocationsByType);

        var largestValue = 0;

         // Build the datasets
         for (var index = 0; index < typeKeys.length; ++index) {
            const type = typeKeys[index];
            allocationsByType[type] /= kb;

            largestValue = allocationsByType[type] > largestValue ? allocationsByType[type] : largestValue;
        }

        // Build the datasets
        for (var index = 0; index < typeKeys.length; ++index) {
            const type = typeKeys[index];
            const typeAllocation = allocationsByType[type];

            if ((typeAllocation / largestValue) > .05) {
                currentAllocDataset.push(typeAllocation);
                typeLabels.push(type);
            }
        }

        console.log("Labels: " + typeLabels + typeLabels.length);
        console.log(currentAllocDataset);

        var backgroundColors = ['rgba(54, 162, 235, 0.8)', 'rgba(75, 192, 192, 0.8)', 'rgba(153, 102, 255, 0.8)', 'rgba(255, 206, 86, 0.8)'];

        var colors = [];
        for (var index = 0; index < typeKeys.length; ++index) {
            colors.push(backgroundColors[index % backgroundColors.length]);
        }

        var datasetsToPass = [
            {
                label: `Allocation`,
                data: currentAllocDataset.slice(),
                backgroundColor: Object.values(colors)
            }
        ];

        console.log(currentAllocDataset);
        console.log(datasetsToPass);

        const dataToPass = {
            labels: typeLabels,
            datasets: datasetsToPass
        };

        var ctx = allocCharts[passedHeapIndex];
        ctx = ctx.getContext('2d');
        var allocChart = new Chart(ctx, {
            type: 'pie',
            data: dataToPass,
            options: {
                title: {
                    display: true,
                    text: `Allocations by type Heap: ${passedHeapIndex}`
                },
                
                "maintainAspectRatio": false,
            }
        });

        savedAllocCharts.push(allocChart);
    };
    
    for (var index = 0; index < heapCharts.length; ++index) {
        setChart(index);
    }

    for (var index = 0; index < allocCharts.length; ++index) {
        setAllocChart(index);
    }

    // Handle messages sent from the extension to the webview
    window.addEventListener('message', event => {
        const message = event.data; // The json data that the extension sent
        switch (message.type) {
            case 'update':
                const text = message.text;

                // Update our webview's content
                updateContent(text);

                // Then persist state information.
                // This state is returned in the call to `vscode.getState` below when a webview is reloaded.
                vscode.setState({ text });

                return;
        }
    });

    // Webviews are normally torn down when not visible and re-created when they become visible again.
    // State lets us save information across these re-loads
    const state = vscode.getState();
    if (state) {
        updateContent(state.text);
    }
}());