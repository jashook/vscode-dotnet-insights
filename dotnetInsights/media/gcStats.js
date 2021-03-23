// Script run within the webview itself.

(function () {

    // Get a reference to the VS Code webview api.
    // We use this API to post messages back to our extension.

    // @ts-ignore
    const vscode = acquireVsCodeApi();

    var gcs = JSON.parse(document.getElementById("hiddenData").innerHTML);

    const maxLength = 100;

    // Take the last 100 to avoid performance issues
    if (gcs.length >= maxLength) {
        gcs = gcs.slice(gcs.length - maxLength, gcs.length);
    }
    
    var timestamps = [];
    for (var index = 0; index < gcs.length; ++index) {
        timestamps.push(gcs[index]["timestamp"]);
    }

    var gen0DataSet = [];
    var gen1DataSet = [];
    var gen2DataSet = [];
    var lohDataSet = [];

    var kb = 1024 * 1024
    var mb = 1024 * mb;

    for (var index = 0; index < gcs.length; ++index) {
        var gcData = gcs[index]["data"];

        // TODO, do per heap
        var firstHeap = gcData["Heaps"][0]["Generations"];
        for (var heapIndex = 0; heapIndex < firstHeap.length; ++heapIndex) {
            var currentHeap = firstHeap[heapIndex];

            if (currentHeap["Id"] == 0) {
                gen0DataSet.push(currentHeap["SizeBefore"] / kb);
            }
            else if(currentHeap["Id"] == 1) {
                gen1DataSet.push(currentHeap["SizeBefore"] / kb);
            }
            else if(currentHeap["Id"] == 2) {
                gen2DataSet.push(currentHeap["SizeBefore"] / kb);
            }
            else if(currentHeap["Id"] == 3) {
                lohDataSet.push(currentHeap["SizeBefore"] / kb);
            }
        }
    }

    var ctx = document.getElementById('myChart');
    ctx = ctx.getContext('2d');
    var myChart = new Chart(ctx, {
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

    const notesContainer = /** @type {HTMLElement} */ (document.querySelector('.notes'));

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

        if (gcs.length >= maxLength) {
            gcs = gcs.slice(gcs.length - maxLength, gcs.length);

            const minusOne = maxLength - 1;

            myChart.data.labels = myChart.data.labels.slice(gcs.length - minusOne, gcs.length);

            for (var index = 0; index < myChart.data.datasets.length; ++index) {
                const currentDataset = myChart.data.datasets[index];
                currentDataset.data = currentDataset.data.slice(gcs.length - minusOne, gcs.length);
            }
        }

        var newTimestamps = [];
        for (var index = 0; index < gcs.length; ++index) {
            newTimestamps.push(gcs[index]["timestamp"]);
        }

        var newGen0DataSet = [];
        var newGen1DataSet = [];
        var newGen2DataSet = [];
        var newLohDataSet = [];

        for (var index = 0; index < gcs.length; ++index) {
            var gcData = gcs[index]["data"];

            // TODO, do per heap
            var firstHeap = gcData["Heaps"][0]["Generations"];
            for (var heapIndex = 0; heapIndex < firstHeap.length; ++heapIndex) {
                var currentHeap = firstHeap[heapIndex];

                if (currentHeap["Id"] == 0) {
                    newGen0DataSet.push(currentHeap["SizeBefore"] / kb);
                }
                else if(currentHeap["Id"] == 1) {
                    newGen1DataSet.push(currentHeap["SizeBefore"] / kb);
                }
                else if(currentHeap["Id"] == 2) {
                    newGen2DataSet.push(currentHeap["SizeBefore"] / kb);
                }
                else if(currentHeap["Id"] == 3) {
                    newLohDataSet.push(currentHeap["SizeBefore"] / kb);
                }
            }
        }

        var doUpdate = false;

        if (newGen0DataSet.length != gen0DataSet.length || newGen0DataSet.length == maxLength) {
            // Check if the process has been torn down
            if (newGen0DataSet.length != gen0DataSet.length + 1 && newGen0DataSet.length != maxLength) {
                // The process has ben cycled. Delete the old chart and create a
                // new one.

                throw exception("NYI.");
            }

            gen0DataSet = newGen0DataSet;
            gen1DataSet = newGen1DataSet;
            gen2DataSet = newGen2DataSet;
            lohDataSet = newLohDataSet;

            timestamps = newTimestamps;

            var newTimestamp = newTimestamps[newTimestamps.length - 1];

            myChart.data.labels.push(newTimestamp);

            for (var index = 0; index < myChart.data.datasets.length; ++index) {
                var currentDataSet = myChart.data.datasets[index];

                if (index == 0) {
                    currentDataSet.data.push(gen0DataSet[gen0DataSet.length - 1]);
                }
                if (index == 1) {
                    currentDataSet.data.push(gen1DataSet[gen1DataSet.length - 1]);
                }
                if (index == 2) {
                    currentDataSet.data.push(gen2DataSet[gen2DataSet.length - 1]);
                }
                if (index == 3) {
                    currentDataSet.data.push(lohDataSet[lohDataSet.length - 1]);
                }
            }

            myChart.update();
            console.log("Updated.");
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