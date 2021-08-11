import * as fs from "fs";
import * as os from "os";
import * as path from 'path';
import * as vscode from 'vscode';

import { DotnetInsights } from "./dotnetInsights";
import { JitMethodInfo, ProcessInfo } from "./GcListener";

import { DotnetInsightsGcDocument } from "./DotnetInsightsGcEditor";

export class DotnetInsightsRuntimeLoadEventsEditor implements vscode.CustomReadonlyEditorProvider {
    public static register(context: vscode.ExtensionContext, insights: DotnetInsights): vscode.Disposable {
        const provider = new DotnetInsightsRuntimeLoadEventsEditor(context, insights, null);
        const providerRegistration = vscode.window.registerCustomEditorProvider(DotnetInsightsRuntimeLoadEventsEditor.viewType, provider);
        return providerRegistration;
    }

    public static readonly viewType = 'dotnetInsightsRuntimeEditor.edit';

    private timeInJit: number;
    private loadData: JitMethodInfo[] | undefined;
    
    constructor(
        private readonly context: vscode.ExtensionContext,
        private readonly insights: DotnetInsights,
        private jitData: any
    ) {
        this.loadData = undefined;
        this.timeInJit = 0;
    }

    openCustomDocument(uri: vscode.Uri, openContext: vscode.CustomDocumentOpenContext, token: vscode.CancellationToken): vscode.CustomDocument | Thenable<vscode.CustomDocument> {
        var filename = path.basename(uri.path);
        var endofLine = os.platform() === "win32" ? vscode.EndOfLine.CRLF : vscode.EndOfLine.LF;

        var processId = parseInt(filename.split("---")[0]);

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
        };

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

    
    private getHtmlForWebview(document: DotnetInsightsGcDocument, webview: vscode.Webview) : string {
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

        // We should have the json representation of the gc stats
        const fileContents = fs.readFileSync(document.uri.fsPath);
        try
        {
            this.loadData = JSON.parse(fileContents.toString());

            if (this.loadData === null || 
                this.loadData === undefined ||
                this.loadData.length === 0) {
                throw new Error("Json error.");
            }

            for (var index = 0; index < this.loadData?.length; ++index) {
                if (this.loadData[index].tier !== 5) {
                    this.timeInJit += this.loadData[index].loadDuration;
                }
            }
        }
        catch(e) {
            vscode.window.showWarningMessage(`${document.uri.fsPath} is corrupted or a incorrect type.`);
            return defaultHtmlReturn;
        }

        // All the load events are nicely parsed. All we need to do is
        // setup a timeline chart. We will have a line for each method load
        // by method id

        const nonce = this.getNonce();

        const mainUri = webview.asWebviewUri(vscode.Uri.joinPath(this.context.extensionUri, 'media', 'snapshot.css'));
        const styleResetUri = webview.asWebviewUri(vscode.Uri.joinPath(this.context.extensionUri, 'media', 'reset.css'));
        const styleVSCodeUri = webview.asWebviewUri(vscode.Uri.joinPath(this.context.extensionUri, 'media', 'vscode.css'));

        const scriptUri = webview.asWebviewUri(vscode.Uri.joinPath(this.context.extensionUri, 'media', 'snapshotGcStats.js'));

        const chartjs = webview.asWebviewUri(vscode.Uri.joinPath(this.context.extensionUri, 'node_modules', 'chart.js', 'dist', 'Chart.min.js'));

        var canvasData = "";
        if (this.loadData.length > 0) {
            canvasData = `<div class="gcStats"><canvas id="totalGcStatsOverTime"></canvas></div>`;
        }

        const processData = document.listener?.processes.get(document.processId);
        processData?.processName;

        var numberOfMethods = 0;
        var totalLoadTime = 0;
        var highestLoadTime = 0;
        var lowestLoadTime = 0;
        var averageLoadTime = 0;
        var medianLoadTime = 0;

        var r2rNumberOfMethods = 0;
        var r2rTotalLoadTime = 0;
        var r2rHighestLoadTime = 0;
        var r2rLowestLoadTime = 0;
        var r2rAverageLoadTime = 0;
        var r2rMedianLoadTime = 0;

        var tierZeroNumberOfMethods = 0;
        var tierZeroTotalLoadTime = 0;
        var tierZeroHighestLoadTime = 0;
        var tierZeroLowestLoadTime = 0;
        var tierZeroAverageLoadTime = 0;
        var tierZeroMedianLoadTime = 0;

        var tierOneNumberOfMethods = 0;
        var tierOneTotalLoadTime = 0;
        var tierOneHighestLoadTime = 0;
        var tierOneLowestLoadTime = 0;
        var tierOneAverageLoadTime = 0;
        var tierOneMedianLoadTime = 0;

        var percentMethodsInR2R = 0;
        var percentMethodsInTier0 = 0;
        var percentMethodsInTier1 = 0;
        var r2rNumberOfTrappedMethods = 0;
        var tierZeroNumberOfTrappedMethods = 0;
        var tierOneNumberOfTrappedMethods = 0;

        const dataValue = "ms";

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
                <span style="display:none" id="hiddenData"><!--${fileContents}--></span>

                <h2 class="divider">${processData?.processName;}</h2>
                <div id="timeSummary">Jit Events (Time to JIT) and Load Events for R2R</div>

                <div class="summaryGcDiv">
                    <div class="total">
                        <div>Total Time (JIT + R2R)</div>
                        <div>Number Loaded<span>${numberOfMethods}</span></div>
                        <div>Total<span>${totalLoadTime} ${dataValue}</span></div>
                        <div>Largest<span>${highestLoadTime} ${dataValue}</span></div>
                        <div>Smallest<span>${lowestLoadTime} ${dataValue}</span></div>
                        <div>Average<span>${averageLoadTime} ${dataValue}</span></div>
                        <div>Median<span>${medianLoadTime} ${dataValue}</span></div>
                    </div>
                    <div class="gen0">
                        <div>R2R</div>
                        <div>Number Loaded<span>${r2rNumberOfMethods}</span></div>
                        <div>Total<span>${r2rTotalLoadTime} ${dataValue}</span></div>
                        <div>Largest<span>${r2rHighestLoadTime} ${dataValue}</span></div>
                        <div>Smallest<span>${r2rLowestLoadTime} ${dataValue}</span></div>
                        <div>Average<span>${r2rAverageLoadTime} ${dataValue}</span></div>
                        <div>Median<span>${r2rMedianLoadTime} ${dataValue}</span></div>
                    </div>
                    <div class="gen1">
                        <div>Tier 0</div>
                        <div>Number Loaded<span>${tierZeroNumberOfMethods}</span></div>
                        <div>Total<span>${tierZeroTotalLoadTime} ${dataValue}</span></div>
                        <div>Largest<span>${tierZeroHighestLoadTime} ${dataValue}</span></div>
                        <div>Smallest<span>${tierZeroLowestLoadTime} ${dataValue}</span></div>
                        <div>Average<span>${tierZeroAverageLoadTime} ${dataValue}</span></div>
                        <div>Median<span>${tierZeroMedianLoadTime} ${dataValue}</span></div>
                    </div>
                    <div class="gen2">
                        <div>Tier 1</div>
                        <div>Number Loaded<span>${tierOneNumberOfMethods}</span></div>
                        <div>Total<span>${tierOneTotalLoadTime} ${dataValue}</span></div>
                        <div>Largest<span>${tierOneHighestLoadTime} ${dataValue}</span></div>
                        <div>Smallest<span>${tierOneLowestLoadTime} ${dataValue}</span></div>
                        <div>Average<span>${tierOneAverageLoadTime} ${dataValue}</span></div>
                        <div>Median<span>${tierOneMedianLoadTime} ${dataValue}</span></div>
                    </div>
                    <div class="loh">
                        <div>Method Information</div>
                        <div>R2R Methods<span>${r2rNumberOfTrappedMethods}</span></div>
                        <div>Tier 0 Methods<span>${tierZeroNumberOfTrappedMethods}</span></div>
                        <div>Tier 1 Methods<span>${tierOneNumberOfTrappedMethods}</span></div>
                        <div>R2R %<span>${percentMethodsInR2R}%</span></div>
                        <div>Tier 0 %<span>${percentMethodsInTier0}%</span></div>
                        <div>Tier 1 %<span>${percentMethodsInTier1}%</span></div>
                    </div>
                </div>

                <div class="spacer"></div>

                <div class="gcDataContainer">
                    ${canvasData}
                    <script src="${chartjs}"></script>
                </div>
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