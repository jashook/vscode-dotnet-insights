import * as vscode from 'vscode';

import { GcData } from "./GcListener";

import { DotnetInsightsGcDocument } from "./DotnetInsightsGcEditor";
import { computeAllocationAmountStats, computePauseTimeStats } from "./GcStatsCalculations";

// Renders the summary tiles + Chart.js graphs shared by every "static GC
// snapshot" input source (DotnetInsightsGcSnapshotEditor's .gcinfo/XML path,
// DotnetInsightsNettraceEditor's .nettrace path). gcData must already be in
// the shape { processName, allocations, gcData: [{ data: {...} }] } - each
// caller is responsible for getting its own input format into that shape;
// this function doesn't care where it came from.
export function renderGcSnapshotWebview(document: DotnetInsightsGcDocument, webview: vscode.Webview, extensionUri: vscode.Uri, gcData: any): string {
    const defaultHtmlReturn = /* html */`
    <!DOCTYPE html>
    <html lang="en">
    <head>
        <meta charset="UTF-8">
        <!--
        Use a content security policy to only allow loading images from https or from our extension directory,
        and only allow scripts that have a specific nonce.
        -->

        <meta http-equiv="Content-Security-Policy"
        content="default-src * vscode-resource: https: 'unsafe-inline' 'unsafe-eval';
        script-src vscode-webview-resource: https: 'unsafe-inline' 'unsafe-eval';
        style-src vscode-webview-resource: https: 'unsafe-inline';
        img-src vscode-resource: https:;
        connect-src vscode-resource: https: http:;">

        <meta name="viewport" content="width=device-width, initial-scale=1.0">
    </head>
    <body>

    </body>
    </html>`;

    if (gcData === null || gcData["allocations"] == null || gcData["gcData"] === null) {
        vscode.window.showWarningMessage(`${document.uri.fsPath} is corrupted or a incorrect type.`);
        return defaultHtmlReturn;
    }

    // gc data has all of the allocations and gc events that occurred in the
    // window. We will now go through and calculate the interesting data we
    // want from what we were provided.

    const gcs = gcData["gcData"];

    var totalNumbers = computePauseTimeStats(gcs);

    let gen0Numbers = computePauseTimeStats(gcs, 0);
    let gen1Numbers = computePauseTimeStats(gcs, 1);
    let gen2Numbers = computePauseTimeStats(gcs, 2);

    var allocationAmountTotal = computeAllocationAmountStats(gcs);
    var allocationAmountGen0 = computeAllocationAmountStats(gcs, 0);
    var allocationAmountGen1 = computeAllocationAmountStats(gcs, 1);
    var allocationAmountGen2 = computeAllocationAmountStats(gcs, 2);
    var allocationAmountLOH = computeAllocationAmountStats(gcs, 3);

    var dataValue = "kb";

    var totalTotalValue = "mb";

    if (allocationAmountTotal[1][0].toFixed(2).length > 8) {
        dataValue = "mb";

        allocationAmountTotal[1][0] /= 1024;
        allocationAmountTotal[1][1] /= 1024;
        allocationAmountTotal[1][2] /= 1024;
        allocationAmountTotal[1][3] /= 1024;
        allocationAmountTotal[1][4] /= 1024;

        allocationAmountGen0[1][0] /= 1024;
        allocationAmountGen0[1][1] /= 1024;
        allocationAmountGen0[1][2] /= 1024;
        allocationAmountGen0[1][3] /= 1024;
        allocationAmountGen0[1][4] /= 1024;

        allocationAmountGen1[1][0] /= 1024;
        allocationAmountGen1[1][1] /= 1024;
        allocationAmountGen1[1][2] /= 1024;
        allocationAmountGen1[1][3] /= 1024;
        allocationAmountGen1[1][4] /= 1024;

        allocationAmountGen2[1][0] /= 1024;
        allocationAmountGen2[1][1] /= 1024;
        allocationAmountGen2[1][2] /= 1024;
        allocationAmountGen2[1][3] /= 1024;
        allocationAmountGen2[1][4] /= 1024;

        allocationAmountLOH[1][0] /= 1024;
        allocationAmountLOH[1][1] /= 1024;
        allocationAmountLOH[1][2] /= 1024;
        allocationAmountLOH[1][3] /= 1024;
        allocationAmountLOH[1][4] /= 1024;
    }

    if (allocationAmountTotal[1][0].toFixed(2).length > 8) {
        totalTotalValue = "gb";

        allocationAmountTotal[1][0] /= 1024;
        allocationAmountGen0[1][0] /= 1024;
        allocationAmountGen1[1][0] /= 1024;
        allocationAmountGen2[1][0] /= 1024;
        allocationAmountLOH[1][0] /= 1024;
    }

    var allocTotal = allocationAmountTotal[1][0].toFixed(2);
    var allocAverage = allocationAmountTotal[1][1].toFixed(2);
    var allocMedian = allocationAmountTotal[1][2].toFixed(2);
    var allocHighest = allocationAmountTotal[1][3].toFixed(2);
    var allocLowest = allocationAmountTotal[1][4].toFixed(2);
    var allocByGc = allocationAmountTotal[0];

    var allocGen0Total = allocationAmountGen0[1][0].toFixed(2);
    var allocGen0Average = allocationAmountGen0[1][1].toFixed(2);
    var allocGen0Median = allocationAmountGen0[1][2].toFixed(2);
    var allocGen0Highest = allocationAmountGen0[1][3].toFixed(2);
    var allocGen0Lowest = allocationAmountGen0[1][4].toFixed(2);
    var allocGen0ByGc = allocationAmountGen0[0];

    var allocGen1Total = allocationAmountGen1[1][0].toFixed(2);
    var allocGen1Average = allocationAmountGen1[1][1].toFixed(2);
    var allocGen1Median = allocationAmountGen1[1][2].toFixed(2);
    var allocGen1Highest = allocationAmountGen1[1][3].toFixed(2);
    var allocGen1Lowest = allocationAmountGen1[1][4].toFixed(2);
    var allocGen1ByGc = allocationAmountGen1[0];

    var allocGen2Total = allocationAmountGen2[1][0].toFixed(2);
    var allocGen2Average = allocationAmountGen2[1][1].toFixed(2);
    var allocGen2Median = allocationAmountGen2[1][2].toFixed(2);
    var allocGen2Highest = allocationAmountGen2[1][3].toFixed(2);
    var allocGen2Lowest = allocationAmountGen2[1][4].toFixed(2);
    var allocGen2ByGc = allocationAmountGen2[0];

    var allocLOHTotal = allocationAmountLOH[1][0].toFixed(2);
    var allocLOHAverage = allocationAmountLOH[1][1].toFixed(2);
    var allocLOHMedian = allocationAmountLOH[1][2].toFixed(2);
    var allocLOHHighest = allocationAmountLOH[1][3].toFixed(2);
    var allocLOHLowest = allocationAmountLOH[1][4].toFixed(2);
    var allocLOHByGc = allocationAmountLOH[0];

    // Time in GC.

    var totalTimeInGc = totalNumbers[1][0].toFixed(2);
    var averageTimeInGc = totalNumbers[1][1].toFixed(2);
    var medianTimeInGc = totalNumbers[1][2].toFixed(2);
    var highestTimeInGc = totalNumbers[1][3].toFixed(2);
    var lowestTimeInGc = totalNumbers[1][4].toFixed(2);
    var timeinsideEachGc = totalNumbers[0];

    var gen0TotalTimeInGc = gen0Numbers[1][0].toFixed(2);
    var gen0TimesInEachGc = gen0Numbers[0];
    var gen0AverageTimeInGc = gen0Numbers[1][1].toFixed(2);
    var gen0MedianTimeInGc = gen0Numbers[1][2].toFixed(2);
    var gen0HighestTimeInGc = gen0Numbers[1][3].toFixed(2);
    var gen0LowestTimeInGc = gen0Numbers[1][4].toFixed(2);

    var gen1TotalTimeInGc = gen1Numbers[1][0].toFixed(2);
    var gen1TimesInEachGc = gen1Numbers[0];
    var gen1AverageTimeInGc = gen1Numbers[1][1].toFixed(2);
    var gen1MedianTimeInGc = gen1Numbers[1][2].toFixed(2);
    var gen1HighestTimeInGc = gen1Numbers[1][3].toFixed(2);
    var gen1LowestTimeInGc = gen1Numbers[1][4].toFixed(2);

    var gen2TotalTimeInGc = gen2Numbers[1][0].toFixed(2);
    var gen2TimesInEachGc = gen2Numbers[0];
    var gen2AverageTimeInGc = gen2Numbers[1][1].toFixed(2);
    var gen2MedianTimeInGc = gen2Numbers[1][2].toFixed(2);
    var gen2HighestTimeInGc = gen2Numbers[1][3].toFixed(2);
    var gen2LowestTimeInGc = gen2Numbers[1][4].toFixed(2);

    const nonce = getNonce();

    const mainUri = webview.asWebviewUri(vscode.Uri.joinPath(extensionUri, 'media', 'snapshot.css'));
    const styleResetUri = webview.asWebviewUri(vscode.Uri.joinPath(extensionUri, 'media', 'reset.css'));
    const styleVSCodeUri = webview.asWebviewUri(vscode.Uri.joinPath(extensionUri, 'media', 'vscode.css'));

    const scriptUri = webview.asWebviewUri(vscode.Uri.joinPath(extensionUri, 'media', 'snapshotGcStats.js'));

    const chartjs = webview.asWebviewUri(vscode.Uri.joinPath(extensionUri, 'node_modules', 'chart.js', 'dist', 'Chart.min.js'));

    var canvasData = "";
    if (gcs.length > 0) {
        canvasData += `<div class="heapChartParentMultiple"><canvas class="gcStatsChart"></canvas></div>`;
        canvasData += `<div class="allocChartParent heapChartNextLine"><canvas class="gcStatsTimeChart"></canvas></div>`;
    }

    var totalCanvasData = "";
    if (gcs.length > 0) {
        totalCanvasData += `<div class="gcStats"><canvas id="totalGcStatsOverTime"></canvas></div>`;
    }

    var perHeapCanvasData = "";
    if (gcs.length > 0) {
        const gcData = gcs[0].data;

        for (var innerIndex = 0; innerIndex < gcData["Heaps"].length; ++innerIndex) {
            perHeapCanvasData += `<div class="heapChartParentMultiple"><canvas class="heapChart"></canvas></div>`;

            if (innerIndex + 1 != gcData["Heaps"].length) {
                ++innerIndex;
                perHeapCanvasData += `<div class="allocChartParent heapChartNextLine"><canvas class="heapChart"></canvas></div>`;
            }
        }
    }

    const gcCountsByGen = JSON.stringify([gen0TimesInEachGc.length, gen1TimesInEachGc.length, gen2TimesInEachGc.length]);

    var gcsToSerialize = [] as GcData[];
    for (var index = 0; index < gcs.length; ++index) {
        var gcDataNew = new GcData(gcs[index]);

        gcsToSerialize.push(gcDataNew);
    }

    var hiddenData = null;

    try {
        hiddenData = JSON.stringify(gcsToSerialize);
    }
    catch(e) {
        var i = 0;
    }

    var totalTimeInEachGc = [
        gen0TotalTimeInGc,
        gen1TotalTimeInGc,
        gen2TotalTimeInGc
    ];

    const totalTimeInEachGcJson = JSON.stringify(totalTimeInEachGc);

    // Allocations

    var htmlToReturn = /* html */`
    <!DOCTYPE html>
    <html lang="en">
        <head>
            <meta charset="UTF-8">
            <!--
            Use a content security policy to only allow loading images from https or from our extension directory,
            and only allow scripts that have a specific nonce.
            -->

            <!--<meta http-equiv="Content-Security-Policy" content="default-src 'none'; img-src ${webview.cspSource}; style-src ${webview.cspSource}; script-src 'nonce-${nonce}';">-->

            <meta http-equiv="Content-Security-Policy"
            content="default-src * vscode-resource: https: 'unsafe-inline' 'unsafe-eval';
            script-src vscode-webview-resource: https: 'unsafe-inline' 'unsafe-eval';
            style-src vscode-webview-resource: https: 'unsafe-inline';
            img-src vscode-resource: https:;
            connect-src vscode-resource: https: http:;">

            <meta name="viewport" content="width=device-width, initial-scale=1.0">
            <link href="${styleResetUri}" rel="stylesheet" />
            <link href="${mainUri}" rel="stylesheet" />
            <link href="${styleVSCodeUri}" rel="stylesheet" />
        </head>
        <body>
            <span style="display:none" id="hiddenData"><!--${hiddenData}--></span>
            <span style="display:none" id="gcCountsByGen"><!--${gcCountsByGen}--></span>
            <span style="display:none" id="totalTimeInEachGcJson"><!--${totalTimeInEachGcJson}--></span>
            <h2 class="divider">${gcData["processName"]}</h2>

            <div id="timeSummary">Allocation Amount by Generation</div>

            <div class="summaryGcDiv">
                <div class="total">
                    <div>Total</div>
                    <div>Total<span>${allocTotal} ${totalTotalValue}</span></div>
                    <div>Largest<span>${allocHighest} ${dataValue}</span></div>
                    <div>Smallest<span>${allocLowest} ${dataValue}</span></div>
                    <div>Average<span>${allocAverage} ${dataValue}</span></div>
                    <div>Median<span>${allocMedian} ${dataValue}</span></div>
                </div>
                <div class="gen0">
                    <div>Gen 0</div>
                    <div>Total<span>${allocGen0Total} ${totalTotalValue}</span></div>
                    <div>Largest<span>${allocGen0Highest} ${dataValue}</span></div>
                    <div>Smallest<span>${allocGen0Lowest} ${dataValue}</span></div>
                    <div>Average<span>${allocGen0Average} ${dataValue}</span></div>
                    <div>Median<span>${allocGen0Median} ${dataValue}</span></div>
                </div>
                <div class="gen1">
                    <div>Gen 1</div>
                    <div>Total<span>${allocGen1Total} ${totalTotalValue}</span></div>
                    <div>Largest<span>${allocGen1Highest} ${dataValue}</span></div>
                    <div>Smallest<span>${allocGen1Lowest} ${dataValue}</span></div>
                    <div>Average<span>${allocGen1Average} ${dataValue}</span></div>
                    <div>Median<span>${allocGen1Median} ${dataValue}</span></div>
                </div>
                <div class="gen2">
                    <div>Gen 2</div>
                    <div>Total<span>${allocGen2Total} ${totalTotalValue}</span></div>
                    <div>Largest<span>${allocGen2Highest} ${dataValue}</span></div>
                    <div>Smallest<span>${allocGen2Lowest} ${dataValue}</span></div>
                    <div>Average<span>${allocGen2Average} ${dataValue}</span></div>
                    <div>Median<span>${allocGen2Median} ${dataValue}</span></div>
                </div>
                <div class="loh">
                    <div>LOH</div>
                    <div>Total<span>${allocLOHTotal} ${totalTotalValue}</span></div>
                    <div>Largest<span>${allocLOHHighest} ${dataValue}</span></div>
                    <div>Smallest<span>${allocLOHLowest} ${dataValue}</span></div>
                    <div>Average<span>${allocLOHAverage} ${dataValue}</span></div>
                    <div>Median<span>${allocLOHMedian} ${dataValue}</span></div>
                </div>
            </div>

            <div id="timeSummary">Time Spent by Generation</div>

            <div class="summaryGcDiv time">
                <div class="total">
                    <div>Total</div>
                    <div>Count<span>${timeinsideEachGc.length}</span></div>
                    <div>Total<span>${totalTimeInGc} ms</span></div>
                    <div>Largest<span>${highestTimeInGc} ms</span></div>
                    <div>Smallest<span>${lowestTimeInGc} ms</span></div>
                    <div>Average<span>${averageTimeInGc} ms</span></div>
                    <div>Median<span>${medianTimeInGc} ms</span></div>
                </div>
                <div class="gen0">
                    <div>Gen 0</div>
                    <div>Count<span>${gen0TimesInEachGc.length}</span></div>
                    <div>Total<span>${gen0TotalTimeInGc} ms</span></div>
                    <div>Largest<span>${gen0HighestTimeInGc} ms</span></div>
                    <div>Smallest<span>${gen0LowestTimeInGc} ms</span></div>
                    <div>Average<span>${gen0AverageTimeInGc} ms</span></div>
                    <div>Median<span>${gen0MedianTimeInGc} ms</span></div>
                </div>
                <div class="gen1">
                    <div>Gen 1</div>
                    <div>Count<span>${gen1TimesInEachGc.length}</span></div>
                    <div>Total<span>${gen1TotalTimeInGc} ms</span></div>
                    <div>Largest<span>${gen1HighestTimeInGc} ms</span></div>
                    <div>Smallest<span>${gen1LowestTimeInGc} ms</span></div>
                    <div>Average<span>${gen1AverageTimeInGc} ms</span></div>
                    <div>Median<span>${gen1MedianTimeInGc} ms</span></div>
                </div>
                <div class="gen2">
                    <div>Gen 2</div>
                    <div>Count<span>${gen2TimesInEachGc.length}</span></div>
                    <div>Total<span>${gen2TotalTimeInGc} ms</span></div>
                    <div>Largest<span>${gen2HighestTimeInGc} ms</span></div>
                    <div>Smallest<span>${gen2LowestTimeInGc} ms</span></div>
                    <div>Average<span>${gen2AverageTimeInGc} ms</span></div>
                    <div>Median<span>${gen2MedianTimeInGc} ms</span></div>
                </div>
            </div>

            <div class="spacer"></div>

            <div class="gcDataContainer">
                ${canvasData}
                <script src="${chartjs}"></script>
            </div>

            <h2 class="divider">GC Usage Over Time</h2>

            <div class="gcDataContainer" id="nextSpacer">
                ${totalCanvasData}
                <script src="${chartjs}"></script>
            </div>

            <h2 class="divider">Per Heap GC Usage Over Time</h2>

            <div class="gcDataContainer">
                ${perHeapCanvasData}
                <script src="${chartjs}"></script>
            </div>

            <script nonce="${nonce}" src="${scriptUri}"></script>
        </body>
    </html>`;

    return htmlToReturn;
}

export function getNonce() {
    let text = '';
    const possible = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789';
    for (let i = 0; i < 32; i++) {
        text += possible.charAt(Math.floor(Math.random() * possible.length));
    }
    return text;
}
