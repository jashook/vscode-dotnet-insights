// Script run within the webview itself.

(function () {

    // Get a reference to the VS Code webview api.
    // We use this API to post messages back to our extension.

    // @ts-ignore
    const vscode = acquireVsCodeApi();

    var gcs = JSON.parse(document.getElementById("hiddenData").innerHTML);

    const maxLength = 30;

    // Take the last 100 to avoid performance issues
    if (gcs.length >= maxLength) {
        gcs = gcs.slice(gcs.length - maxLength, gcs.length);
    }
    
    var timestamps = [];
    for (var index = 0; index < gcs.length; ++index) {
        timestamps.push(gcs[index]["timestamp"]);
    }

    var processMemoryChartDom = document.getElementsByClassName("processMemory")[0];

    const processMemoryChartContext = processMemoryChartDom;
    const context = processMemoryChartContext.getContext('2d');

    var privateBytesDataSet = [];
    var workingSetdataSet = [];
    var pagedMemoryDataSet = [];
    var virtualMemorySet = [];
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
        virtualMemorySet.push(gcs[index]["virtualMemory"] / kb);
        nonPagedSystemMemoryDataSet.push(gcs[index]["nonPagedSystemMemory"] / kb);
        pagedSystemMemoryDataSet.push(gcs[index]["pagedSystemMemory"] / kb);
        totalHeapSizeDataSet.push(gcs[index]["TotalHeapSize"]/ kb);
    }
    console.log(gcs);

    console.log(privateBytesDataSet);
    console.log(workingSetdataSet);
    console.log(pagedMemoryDataSet);
    console.log(virtualMemorySet);
    console.log(nonPagedSystemMemoryDataSet);
    console.log(pagedSystemMemoryDataSet);
    console.log(totalHeapSizeDataSet);

    var processMemoryChart = new Chart(context, {
        type: 'line',
        data: {
            labels: timestamps,
            datasets: [{
                label: 'Private Bytes',
                data: privateBytesDataSet,
                backgroundColor: [
                    'rgb(85, 142, 248)',
                ],
                borderWidth: 1
            }, 
            {
                label: "Working Set",
                data: workingSetdataSet,
                backgroundColor: [
                    'rgb(255, 52, 52)'
                ],
                borderWidth: 1
            },
            {
                label: "Paged Memory",
                data: pagedMemoryDataSet,
                backgroundColor: [
                    'rgb(31, 40, 58)'
                ],
                borderWidth: 1
            },
            {
                label: "Virtual Memory",
                data: virtualMemorySet,
                backgroundColor: [
                    'rgba(248, 250, 151, 0.808)'
                ],
                borderWidth: 1
            },
            {
                label: "Non Paged System Memory",
                data: nonPagedSystemMemoryDataSet,
                backgroundColor: [
                    'rgb(138, 138, 138)'
                ],
                borderWidth: 1
            },
            {
                label: "Paged System Memory",
                data: pagedSystemMemoryDataSet,
                backgroundColor: [
                    'rgb(105, 4, 4)'
                ],
                borderWidth: 1
            },
            {
                label: "Total Heap Size",
                data: totalHeapSizeDataSet,
                backgroundColor: [
                    'rgb(17, 126, 111)'
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
                            labelString: "Memory Usage in KB"
                        }
                    }],
                },
                "maintainAspectRatio": false,
            }
        });

        savedHeapCharts.push(heapChart);
    };
    
    for (var index = 0; index < heapCharts.length; ++index) {
        setChart(index);
    }

    /**
     * Render the document in the webview.
     */
    function updateContent(/** @type {string} */ text) {
        var gcs = JSON.parse(text);
        console.assert(gcs.length == 1);

        var gcDataTable = document.getElementById("gcData");

        var rows = gcDataTable.children[0].children[0];
        
        var kb = 1024 * 1024
        var mb = 1024 * mb;

        for (var index = 0; index < gcs.length; ++index) {
            const gcData = gcs[index].data;

            let tdId = gcData["Id"];
            let tdGen = gcData["generation"];
            let tdType = gcData["Type"];
            let tdPauseTime = parseFloat(gcData["PauseDurationMSec"]).toFixed(2);
            let tdReason = gcData["Reason"];
            let tdGen0Size = (parseInt(gcData["GenerationSize0"]) / kb).toFixed(2);
            let tdGen1Size = (parseInt(gcData["GenerationSize1"]) / kb).toFixed(2);
            let tdGen2Size = (parseInt(gcData["GenerationSize2"]) / kb).toFixed(2);
            let tdLohSize = (parseInt(gcData["GenerationSizeLOH"]) / kb).toFixed(2);
            let tdTotalHeapSize = (parseInt(gcData["TotalHeapSize"]) / kb).toFixed(2);
            let tdGen0MinSize = (parseInt(gcData["Gen0MinSize"]) / kb).toFixed(2);
            let tdTotalPromotedSize0 = (parseInt(gcData["TotalPromotedSize0"]) / kb).toFixed(2);
            let tdTotalPromotedSize1 = (parseInt(gcData["TotalPromotedSize1"]) / kb).toFixed(2);
            let tdTotalPromotedSize2 = (parseInt(gcData["TotalPromotedSize2"]) / kb).toFixed(2);

            var tableRow = document.createElement("tr");

            var tdElement = document.createElement("td");
            tdElement.innerHTML = tdId;

            tableRow.appendChild(tdElement);

            tdElement = document.createElement("td");
            tdElement.innerHTML = tdGen;

            tableRow.appendChild(tdElement);

            tdElement = document.createElement("td");
            tdElement.innerHTML = tdType;

            tableRow.appendChild(tdElement);

            tdElement = document.createElement("td");
            tdElement.innerHTML = tdPauseTime;

            tableRow.appendChild(tdElement);

            tdElement = document.createElement("td");
            tdElement.innerHTML = tdReason;

            tableRow.appendChild(tdElement);

            tdElement = document.createElement("td");
            tdElement.innerHTML = tdGen0Size;

            tableRow.appendChild(tdElement);

            tdElement = document.createElement("td");
            tdElement.innerHTML = tdGen1Size;

            tableRow.appendChild(tdElement);

            tdElement = document.createElement("td");
            tdElement.innerHTML = tdGen2Size;

            tableRow.appendChild(tdElement);

            tdElement = document.createElement("td");
            tdElement.innerHTML = tdLohSize;

            tableRow.appendChild(tdElement);

            tdElement = document.createElement("td");
            tdElement.innerHTML = "NYI";

            tableRow.appendChild(tdElement);

            tdElement = document.createElement("td");
            tdElement.innerHTML = tdTotalHeapSize;

            tableRow.appendChild(tdElement);

            tdElement = document.createElement("td");
            tdElement.innerHTML = tdGen0MinSize;

            tableRow.appendChild(tdElement);

            tdElement = document.createElement("td");
            tdElement.innerHTML = tdTotalPromotedSize0;

            tableRow.appendChild(tdElement);

            tdElement = document.createElement("td");
            tdElement.innerHTML = tdTotalPromotedSize1;

            tableRow.appendChild(tdElement);

            tdElement = document.createElement("td");
            tdElement.innerHTML = tdTotalPromotedSize2;

            tableRow.appendChild(tdElement);

            rows.appendChild(tableRow);
        }

        console.assert(heapCharts.length == savedHeapCharts.length);

        // Change gc percent to reflect new data
        var perecentInGcNode = document.getElementById("percentInGc").children[1];
        console.assert(perecentInGcNode.innerHTML.indexOf("%") != -1);

        perecentInGcNode.innerHTML = gcs[0]["percentInGc"] + " %";

        if (savedHeapCharts[0].data.labels.length >= maxLength) {

            const minusOne = maxLength - 1;

            for (var index = 0; index < savedHeapCharts.length; ++index)
            {
                var heapChart = savedHeapCharts[index];

                heapChart.data.labels = heapChart.data.labels.slice(heapChart.data.labels.length - minusOne, heapChart.data.labels.length);

                for (var datasetIndex = 0; datasetIndex < heapChart.data.datasets.length; ++datasetIndex) {
                    const currentDataset = heapChart.data.datasets[datasetIndex];
                    currentDataset.data = currentDataset.data.slice(currentDataset.data.length - minusOne, currentDataset.data.length);
                }
            }
        }

        var newTimestamps = [];
        for (var index = 0; index < gcs.length; ++index) {
            newTimestamps.push(gcs[index]["timestamp"]);
        }

        var newTimestamp = newTimestamps[newTimestamps.length - 1];

        for (var heapIndex = 0; heapIndex < savedHeapCharts.length; ++heapIndex) {
            savedHeapCharts[heapIndex].data.labels.push(newTimestamp);

        }

        for (var heapIndex = 0; heapIndex < savedHeapCharts.length; ++heapIndex)
        {
            var heapChart = savedHeapCharts[heapIndex];

            var newGen0DataSet = [];
            var newGen1DataSet = [];
            var newGen2DataSet = [];
            var newLohDataSet = [];

            for (var index = 0; index < gcs.length; ++index) {
                var gcData = gcs[index]["data"];

                var currentHeap = gcData["Heaps"][heapIndex]["Generations"];
                for (var generationIndex = 0; generationIndex < currentHeap.length; ++generationIndex) {
                    var currentGeneration = currentHeap[generationIndex];

                    if (currentGeneration["Id"] == 0) {
                        newGen0DataSet.push(currentGeneration["SizeBefore"] / kb);
                    }
                    else if(currentGeneration["Id"] == 1) {
                        newGen1DataSet.push(currentGeneration["SizeBefore"] / kb);
                    }
                    else if(currentGeneration["Id"] == 2) {
                        newGen2DataSet.push(currentGeneration["SizeBefore"] / kb);
                    }
                    else if(currentGeneration["Id"] == 3) {
                        newLohDataSet.push(currentGeneration["SizeBefore"] / kb);
                    }
                }
            }

            for (var index = 0; index < heapChart.data.datasets.length; ++index) {
                var currentDataSet = heapChart.data.datasets[index];

                if (index == 0) {
                    currentDataSet.data.push(newGen0DataSet[newGen0DataSet.length - 1]);
                }
                if (index == 1) {
                    currentDataSet.data.push(newGen1DataSet[newGen1DataSet.length - 1]);
                }
                if (index == 2) {
                    currentDataSet.data.push(newGen2DataSet[newGen2DataSet.length - 1]);
                }
                if (index == 3) {
                    currentDataSet.data.push(newLohDataSet[newLohDataSet.length - 1]);
                }
            }

            heapChart.update();
        }
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