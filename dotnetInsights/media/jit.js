// Script run within the webview itself.
var r2rTimeByBucket = [];
var tier0TimeByBucket = [];
var tier1TimeByBucket = [];
var tier0TierUpTimeByBucket = [];
var tier1TierUpTimeByBucket = [];

var labels = [];

(function () {

    // Get a reference to the VS Code webview api.
    // We use this API to post messages back to our extension.

    // @ts-ignore
    const vscode = acquireVsCodeApi();
  
    console.time("jitParsing");
    var jitData = JSON.parse(document.getElementById("hiddenData").innerHTML.slice(4, document.getElementById("hiddenData").innerHTML.length - 3));
    console.timeEnd("jitParsing");

    labels = jitData[0];

    r2rTimeByBucket = jitData[1];
    tier0TimeByBucket = jitData[2];
    tier1TimeByBucket = jitData[3];
    tier0TierUpTimeByBucket = jitData[4];
    tier1TierUpTimeByBucket = jitData[5];

    console.log(labels);
    console.log(r2rTimeByBucket);
    console.log(tier0TimeByBucket);
    console.log(tier1TimeByBucket);
    console.log(tier0TierUpTimeByBucket);
    console.log(tier1TierUpTimeByBucket);

    var jitStatsChart = document.getElementById("totalJitStatsOverTime");

    const jitStatsChartChartContext = jitStatsChart;
    const context = jitStatsChartChartContext.getContext('2d');

    var gcCountChart = new Chart(context, {
        "type": 'bar',
        data: {
            labels: labels,
            datasets: [
                {
                    label: 'R2R',
                    data: r2rTimeByBucket,
                    borderColor: "rgba(72, 83, 136, 0.3)",
                    backgroundColor: "rgba(72, 83, 136, 0.2)",
                    borderWidth: 1
                },
                {
                    label: 'Tier 0',
                    data: tier0TimeByBucket,
                    borderColor: "rgba(96, 165, 69, 0.3)",
                    backgroundColor: "rgba(96, 165, 69, 0.2)",
                    borderWidth: 1
                },
                {
                    label: 'Tier 1',
                    data: tier1TimeByBucket,
                    borderColor: "rgba(141, 31, 95, 0.3)",
                    backgroundColor: "rgba(141, 31, 95, 0.2)",
                    borderWidth: 1
                },
                {
                    label: 'Tier 0 Rejit',
                    data: tier0TierUpTimeByBucket,
                    borderColor: "rgba(201, 221, 84, 0.3)",
                    backgroundColor: "rgba(201, 221, 84, 0.2)",
                    borderWidth: 1
                },
                {
                    label: 'Tier 1 Rejit',
                    data: tier1TierUpTimeByBucket,
                    borderColor: 'rgba(54, 162, 235, 0.3)',
                    backgroundColor: 'rgba(54, 162, 235, 0.2)',
                    borderWidth: 1
                }
            ]
        },
        options: {
            "maintainAspectRatio": false
        }
    });

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