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
            
            console.log(gcs);
            console.log(passedHeapIndex);
            console.log(gcData["Heaps"]);
            console.log(gcData["Heaps"][passedHeapIndex]);

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
        console.log(heapCharts);
        console.log(ctx);
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
        console.log(index);
        setChart(index);
    }

    /**
     * Render the document in the webview.
     */
    function updateContent(/** @type {string} */ text) {
        var gcs = JSON.parse(text);

        var gcDataTable = document.getElementById("gcData");

        var rows = gcDataTable.children[0].children[0];

        var rowsCopy = [];
        for (var index = 1; index < rows.children.length; ++index) {
            rowsCopy.push(rows.children[index]);
        }

        if (rows.children.length > 1) {
            for (var index = 0; index < rowsCopy.length; ++index) {
                var currentRow = rowsCopy[index];

                rows.removeChild(rowsCopy[index]);
            }
        }

        for (var index = 0; index < gcs.length; ++index) {
            const gcData = gcs[index].data;

            var tableRow = document.createElement("tr");

            var tdElement = document.createElement("td");
            tdElement.innerHTML = gcData["Id"];

            tableRow.appendChild(tdElement);

            tdElement = document.createElement("td");
            tdElement.innerHTML = gcData["generation"]

            tableRow.appendChild(tdElement);

            tdElement = document.createElement("td");
            tdElement.innerHTML = gcData["kind"]

            tableRow.appendChild(tdElement);

            tdElement = document.createElement("td");
            tdElement.innerHTML = gcData["Reason"]

            tableRow.appendChild(tdElement);

            tdElement = document.createElement("td");
            tdElement.innerHTML = gcData["GenerationSize0"]

            tableRow.appendChild(tdElement);

            tdElement = document.createElement("td");
            tdElement.innerHTML = gcData["GenerationSize1"]

            tableRow.appendChild(tdElement);

            tdElement = document.createElement("td");
            tdElement.innerHTML = gcData["GenerationSize2"]

            tableRow.appendChild(tdElement);

            tdElement = document.createElement("td");
            tdElement.innerHTML = gcData["GenerationSizeLOH"]

            tableRow.appendChild(tdElement);

            tdElement = document.createElement("td");
            tdElement.innerHTML = "NYI";

            tableRow.appendChild(tdElement);

            tdElement = document.createElement("td");
            tdElement.innerHTML = gcData["PauseDurationMSec"];

            tableRow.appendChild(tdElement);

            tdElement = document.createElement("td");
            tdElement.innerHTML = gcData["TotalHeapSize"];

            tableRow.appendChild(tdElement);

            tdElement = document.createElement("td");
            tdElement.innerHTML = gcData["Gen0MinSize"];

            tableRow.appendChild(tdElement);

            rows.appendChild(tableRow);
        }

        console.assert(heapCharts.length == savedHeapCharts.length);

        if (gcs.length >= maxLength) {
            gcs = gcs.slice(gcs.length - maxLength, gcs.length);

            const minusOne = maxLength - 1;

            for (var index = 0; index < savedHeapCharts.length; ++index)
            {
                var heapChart = savedHeapCharts[index];

                heapChart.data.labels = heapChart.data.labels.slice(gcs.length - minusOne, gcs.length);

                for (var datasetIndex = 0; datasetIndex < heapChart.data.datasets.length; ++datasetIndex) {
                    const currentDataset = heapChart.data.datasets[datasetIndex];
                    currentDataset.data = currentDataset.data.slice(gcs.length - minusOne, gcs.length);
                }
            }
        }

        var newTimestamps = [];
        for (var index = 0; index < gcs.length; ++index) {
            newTimestamps.push(gcs[index]["timestamp"]);
        }

        var newTimestamp = newTimestamps[newTimestamps.length - 1];
        savedHeapCharts[0].data.labels.push(newTimestamp);

        for (var heapIndex = 0; heapIndex < savedHeapCharts.length; ++heapIndex)
        {
            var heapChart = savedHeapCharts[heapIndex];

            var newGen0DataSet = [];
            var newGen1DataSet = [];
            var newGen2DataSet = [];
            var newLohDataSet = [];

            var kb = 1024 * 1024
            var mb = 1024 * mb;

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
            console.log(`Updated Heap: ${heapIndex}`);
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