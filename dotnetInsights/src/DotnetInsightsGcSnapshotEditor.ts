import * as fs from "fs";
import * as os from "os";
import * as path from 'path';
import * as vscode from 'vscode';

import { DotnetInsights } from "./dotnetInsights";
import { GcListener, ProcessInfo, GcData, AllocData } from "./GcListener";

import { DotnetInsightsGcDocument } from "./DotnetInsightsGcEditor";

export class DotnetInsightsGcSnapshotEditor implements vscode.CustomReadonlyEditorProvider {
    public static register(context: vscode.ExtensionContext, insights: DotnetInsights): vscode.Disposable {
        const provider = new DotnetInsightsGcSnapshotEditor(context, insights, null);
        const providerRegistration = vscode.window.registerCustomEditorProvider(DotnetInsightsGcSnapshotEditor.viewType, provider);
        return providerRegistration;
    }

    public static readonly viewType = 'dotnetInsightsGcSnapshot.edit';

    private timeInGc: number;
    private allocData: AllocData[] | undefined;
    
    constructor(
        private readonly context: vscode.ExtensionContext,
        private readonly insights: DotnetInsights,
        private gcData: any
    ) {
        this.timeInGc = 0;
        this.allocData = undefined;
    }

    openCustomDocument(uri: vscode.Uri, openContext: vscode.CustomDocumentOpenContext, token: vscode.CancellationToken): vscode.CustomDocument | Thenable<vscode.CustomDocument> {
        var filename = path.basename(uri.path);
        var endofLine = os.platform() == "win32" ? vscode.EndOfLine.CRLF : vscode.EndOfLine.LF;

        var processId = parseInt(filename.split(".gcstats")[0]);

        var document = new DotnetInsightsGcDocument(uri,
                                                    filename,
                                                    false,
                                                    "ildasm",
                                                    1,
                                                    false,
                                                    true,
                                                    endofLine,
                                                    0,
                                                    processId,
                                                    null);

        return document;
    }

    resolveCustomEditor(document: vscode.CustomDocument, webviewPanel: vscode.WebviewPanel, token: vscode.CancellationToken): void | Thenable<void> {
        webviewPanel.webview.options = {
            enableScripts: true,
            localResourceRoots: [
                vscode.Uri.joinPath(this.context.extensionUri, 'node_modules', 'chart.js', 'dist'),
                vscode.Uri.joinPath(this.context.extensionUri, 'media')
            ]
        }

        var gcDocument = document as DotnetInsightsGcDocument;
        const pid = gcDocument.processId;

        var gcEditor = this;

        function updateWebview() {
            // There are no updates possible for this view type.

            webviewPanel.webview.postMessage({
                type: 'update'
            });
        }

        webviewPanel.webview.html = this.getHtmlForWebview(gcDocument, webviewPanel.webview);

        updateWebview();
    }

    private parseFromXml(fileContents: Buffer): any {
        
        return null;
    }

    private getHtmlForWebview(document: DotnetInsightsGcDocument, webview: vscode.Webview): string {
        const fileContents = fs.readFileSync(document.uri.fsPath);

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

        var gcData = null;

        // We should have the json representation of the gc stats
        try
        {
            gcData = JSON.parse(fileContents.toString());

            if (gcData["allocations"] == null || gcData["gcData"] == null) {
                throw new Error("Json error.");
            }
        }
        catch(e) {
            // Check to see if this is a valid xml file
            gcData = this.parseFromXml(fileContents);
            if (gcData == null) {
                vscode.window.showWarningMessage(`${document.uri.fsPath} is corrupted or a incorrect type.`);
                return defaultHtmlReturn;
            }
            
            // Else we can recover
        }

        // gc data has all of the allocations and gc events that occurred in the
        // window. We will now go through and calculate the interesting data we
        // want from what we were provided.

        const gcs = gcData["gcData"];

        let getValues = (generation: number) : [number[], number[]] => {
            var totalTimeInGc: number = 0.0;
            var timesInEachGc = [];
            var averageTimeInGc: number = 0;
            var medianTimeInGc = 0;
            var highestTimeInGc = 0;
            var lowestTimeInGc = 0;

            for (var index = 0; index < gcs.length; ++index) {
                if (gcs[index]["data"]["generation"] == generation) {
                    timesInEachGc.push(parseFloat(gcs[index]["data"]["PauseDurationMSec"]));
                }
            }
    
            // Gcs over 50ms
            var expensiveGcs = [];
            for (var index = 0; index < timesInEachGc.length; ++index) {
                if (timesInEachGc[index] > 50) {
                    expensiveGcs.push(gcs[index]["data"]);
                }
            }
    
            lowestTimeInGc = timesInEachGc[0];
            for (var index = 0; index < timesInEachGc.length; ++index) {
                totalTimeInGc += timesInEachGc[index];
    
                if (timesInEachGc[index] < lowestTimeInGc) {
                    lowestTimeInGc = timesInEachGc[index];
                }
    
                if (timesInEachGc[index] > highestTimeInGc) {
                    highestTimeInGc = timesInEachGc[index];
                }
            }
    
            timesInEachGc.sort();
            var half = Math.floor(timesInEachGc.length / 2);
            medianTimeInGc = timesInEachGc[half];
    
            averageTimeInGc = totalTimeInGc / timesInEachGc.length;

            if (timesInEachGc.length == 0) {
                totalTimeInGc = 0;
                timesInEachGc = [];
                averageTimeInGc = 0;
                medianTimeInGc = 0;
                highestTimeInGc = 0;
                lowestTimeInGc = 0;
            }

            return [timesInEachGc, [totalTimeInGc, averageTimeInGc, medianTimeInGc, highestTimeInGc, lowestTimeInGc]];
        };

        let gen0Numbers = getValues(0);
        let gen1Numbers = getValues(1);
        let gen2Numbers = getValues(2);
        let gen3Numbers = getValues(3);

        // Time in GC.
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

        var gen3TotalTimeInGc = gen3Numbers[1][0].toFixed(2);
        var gen3TimesInEachGc = gen3Numbers[0];
        var gen3AverageTimeInGc = gen3Numbers[1][1].toFixed(2);
        var gen3MedianTimeInGc = gen3Numbers[1][2].toFixed(2);
        var gen3HighestTimeInGc = gen3Numbers[1][3].toFixed(2);
        var gen3LowestTimeInGc = gen3Numbers[1][4].toFixed(2);

        const nonce = this.getNonce();

        const mainUri = webview.asWebviewUri(vscode.Uri.joinPath(this.context.extensionUri, 'media', 'snapshot.css'));
        const styleResetUri = webview.asWebviewUri(vscode.Uri.joinPath(this.context.extensionUri, 'media', 'reset.css'));
        const styleVSCodeUri = webview.asWebviewUri(vscode.Uri.joinPath(this.context.extensionUri, 'media', 'vscode.css'));

        const scriptUri = webview.asWebviewUri(vscode.Uri.joinPath(this.context.extensionUri, 'media', 'snapshotGcStats.js'));

        const chartjs = webview.asWebviewUri(vscode.Uri.joinPath(this.context.extensionUri, 'node_modules', 'chart.js', 'dist', 'Chart.min.js'));

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
                    perHeapCanvasData += `<div class="allocChartParent heapChartNextLine"><canvas class="heapChart"></canvas></div>`;
                }
            }
        }

        const gcCountsByGen = JSON.stringify([gen0TimesInEachGc.length, gen1TimesInEachGc.length, gen2TimesInEachGc.length, gen3TimesInEachGc.length]);

        var gcsToSerialize = [] as GcData[];
        for (var index = 0; index < gcs.length; ++index) {
            var gcDataNew = new GcData(gcs[index]);

            gcsToSerialize.push(gcDataNew);
        }

        var hiddenData = JSON.stringify(gcsToSerialize);

        var totalTimeInEachGc = [
            gen0TotalTimeInGc,
            gen1TotalTimeInGc,
            gen2TotalTimeInGc,
            gen3TotalTimeInGc
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

                <h2 class="divider">GC Summary</h2>
                <div id="summaryGcDiv">
                    <div id="gen0">
                        <div>Gen 0</div>
                        <div>Count<span>${gen0TimesInEachGc.length}</span></div>
                        <div>Total<span>${gen0TotalTimeInGc} ms</span></div>
                        <div>Largest<span>${gen0HighestTimeInGc} ms</span></div>
                        <div>Smallest<span>${gen0LowestTimeInGc} ms</span></div>
                        <div>Average<span>${gen0AverageTimeInGc} ms</span></div>
                        <div>Median<span>${gen0MedianTimeInGc} ms</span></div>
                    </div>
                    <div id="gen1">
                        <div>Gen 1</div>
                        <div>Count<span>${gen1TimesInEachGc.length}</span></div>
                        <div>Total<span>${gen1TotalTimeInGc} ms</span></div>
                        <div>Largest<span>${gen1HighestTimeInGc} ms</span></div>
                        <div>Smallest<span>${gen1LowestTimeInGc} ms</span></div>
                        <div>Average<span>${gen1AverageTimeInGc} ms</span></div>
                        <div>Median<span>${gen1MedianTimeInGc} ms</span></div>
                    </div>
                    <div id="gen2">
                        <div>Gen 2</div>
                        <div>Count<span>${gen2TimesInEachGc.length}</span></div>
                        <div>Total<span>${gen2TotalTimeInGc} ms</span></div>
                        <div>Largest<span>${gen2HighestTimeInGc} ms</span></div>
                        <div>Smallest<span>${gen2LowestTimeInGc} ms</span></div>
                        <div>Average<span>${gen2AverageTimeInGc} ms</span></div>
                        <div>Median<span>${gen2MedianTimeInGc} ms</span></div>
                    </div>
                    <div id="LOH">
                        <div>LOH</div>
                        <div>Count<span>${gen3TimesInEachGc.length}</span></div>
                        <div>Total<span>${gen3TotalTimeInGc} ms</span></div>
                        <div>Largest<span>${gen3HighestTimeInGc} ms</span></div>
                        <div>Smallest<span>${gen3LowestTimeInGc} ms</span></div>
                        <div>Average<span>${gen3AverageTimeInGc} ms</span></div>
                        <div>Median<span>${gen3MedianTimeInGc} ms</span></div>
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

    getNonce() {
        let text = '';
        const possible = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789';
        for (let i = 0; i < 32; i++) {
            text += possible.charAt(Math.floor(Math.random() * possible.length));
        }
        return text;
    }
}