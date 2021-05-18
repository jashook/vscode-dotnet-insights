import * as child from 'child_process';
import * as fs from 'fs';
import * as os from "os";
import * as path from 'path';
import * as vscode from 'vscode';
import * as assert from "assert";

import { DotnetInsights } from "./dotnetInsights";
import { GcListener, ProcessInfo, GcData, AllocData } from "./GcListener";

export class DotnetInsightsGcEditor implements vscode.CustomEditorProvider {
    public static register(context: vscode.ExtensionContext, insights: DotnetInsights, listener: GcListener): vscode.Disposable {
        const provider = new DotnetInsightsGcEditor(context, insights, listener, null);
        const providerRegistration = vscode.window.registerCustomEditorProvider(DotnetInsightsGcEditor.viewType, provider);
        return providerRegistration;
    }

    public static readonly viewType = 'dotnetInsightsGc.edit';

    private timeInGc: number;
    private allocData: AllocData[] | undefined;
    
    constructor(
        private readonly context: vscode.ExtensionContext,
        private readonly insights: DotnetInsights,
        private readonly listener: GcListener,
        private gcData: any
    ) {
        this.timeInGc = 0;
        this.allocData = undefined;
    }

    onDidChangeCustomDocument() : any {

    }

    revertCustomDocument(document: vscode.CustomDocument, cancellation: vscode.CancellationToken): Thenable<void> {
        throw new Error('Method not implemented.');
    }
    backupCustomDocument(document: vscode.CustomDocument, context: vscode.CustomDocumentBackupContext, cancellation: vscode.CancellationToken): Thenable<vscode.CustomDocumentBackup> {
        throw new Error('Method not implemented.');
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
                                                    this.listener);

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

        var listener = this.listener;
        var gcEditor = this;

        var lastDataCount = listener.processes.get(pid)?.data.length;

        function updateWebview() {
            var currentData = listener.processes.get(pid)?.data;

            if (currentData == undefined) return;

            // Reconcile the last data with the current data.
            // We will only send the differnece of the two for performance
            // reasons.

            // No update needed.
            if (gcEditor.gcData.length == currentData.length) {
                return;
            }

            if (gcEditor.gcData.length + 1 != currentData.length) {
                console.assert(gcEditor.gcData.length + 1 == currentData.length);
                console.log(gcEditor.gcData.length);
                console.log(currentData.length);
            }

            const startTime = parseFloat(gcEditor.gcData[0].data["PauseStartRelativeMSec"]);
            const currentTime = parseFloat(gcEditor.gcData[gcEditor.gcData.length - 1].data["PauseEndRelativeMSec"]);

            // Make sure we only pass the latest update.
            currentData = currentData.slice(currentData.length - 1);

            gcEditor.timeInGc += parseFloat(currentData[0].data["PauseDurationMSec"]);
            let percentOfTimeInGc = ((gcEditor.timeInGc / (currentTime - startTime)) * 100).toFixed(2);

            currentData[0].percentInGc = percentOfTimeInGc;

            const allocations: AllocData[] | undefined = [];
            
            const allocDataForGc = currentData[0].allocData;

            for (var innerIndex = 0; innerIndex < allocDataForGc.length; ++innerIndex) {
                allocations.push(allocDataForGc[innerIndex]);
            }

            var allocationsByType: any = {};
            allocationsByType["totalAllocations"] = 0;
            allocationsByType["types"] = {};

            if (allocations != undefined) {
                for (var allocIndex = 0; allocIndex < currentData[0].allocData.length; ++allocIndex) {
                    const currentAllocData = currentData[0].allocData[allocIndex];

                    const heapIndex = parseInt(currentAllocData.data.data["heapIndex"]);
                    const allocType = currentAllocData.data.data["typeName"];
                    const allocSizeInBytes = parseInt(currentAllocData.data.data["allocSizeBytes"]);

                    if (allocationsByType["types"][heapIndex] == undefined) {
                        allocationsByType["types"][heapIndex] = {};
                    }

                    if (allocationsByType["types"][heapIndex][allocType] == undefined) {
                        allocationsByType["types"][heapIndex][allocType] = [] as string[];
                    }
                    
                    allocationsByType["types"][heapIndex][allocType].push(allocSizeInBytes);
                    allocationsByType["totalAllocations"] += allocSizeInBytes;
                }

                currentData[0].filteredAllocData = allocationsByType;
            }

            gcEditor.gcData.push(currentData[0]);
            const jsonData = JSON.stringify(currentData);
                
            webviewPanel.webview.postMessage({
                type: 'update',
                text: jsonData
            });
        }

        webviewPanel.webview.html = this.getHtmlForWebview(gcDocument, webviewPanel.webview);

        const callBackTreeListener = listener.treeView?.onDidChangeTreeData(e => {
            updateWebview();
        });

        webviewPanel.onDidDispose(() => {
            callBackTreeListener?.dispose();
        });
    }

    private getHtmlForWebview(document: DotnetInsightsGcDocument, webview: vscode.Webview): string {
        var fileName = document.fileName;

        const processInfo: ProcessInfo | undefined = this.listener.processes.get(document.processId);
        const gcs: GcData[] | undefined = processInfo?.data;
        const allocations: AllocData[] | undefined = [];

        this.gcData = [] as GcData[];
        if (gcs != undefined) {
            for (var index = 0; index < gcs?.length; ++index) {
                this.gcData.push(gcs[index]);
            }

            for (var index = 0; index < gcs?.length; ++index) {
                const allocDataForGc = gcs[index].allocData;

                for (var innerIndex = 0; innerIndex < allocDataForGc.length; ++innerIndex) {
                    allocations.push(allocDataForGc[innerIndex]);
                }
            }
        }

        var data = "";
        
        var canvasData = "";
        this.timeInGc = 0;

        const kb = 1024 * 1024;
        var percentInGcNumber: any;
        var totalAllocationsByType: any = {};
        totalAllocationsByType["totalAllocations"] = 0;
        totalAllocationsByType["types"] = {};

        if (gcs != undefined && gcs?.length > 0 && processInfo != undefined) {
            const startTime = gcs[0].data["PauseStartRelativeMSec"];
            const currentTime = gcs[gcs.length - 1].data["PauseEndRelativeMSec"];

            data += `<table>`;
            data += `<tr class="tableHeader"><th>GC Number</th><th>Collection Generation</th><th>Type</th><th>Pause Time (mSec)</th><th>Reason</th><th>Generation 0 Size (kb)</th><th>Generation 1 Size (kb)</th><th>Generation 2 Size (kb)</th><th>LOH Size (kb)</th><th>POH Size (kb)</th><th>Total Heap Size (kb)</th><th>Gen 0 Min Budget (kb)</th><th>Promoted Gen0 (kb)</th><th>Promoted Gen1 (kb)</th><th>Promoted Gen2 (kb)</th></tr>`;
            for (var index = 0; index < gcs.length; ++index) {
                const gcData = gcs[index].data;

                let pauseTime = parseFloat(gcData["PauseDurationMSec"]);

                this.timeInGc += pauseTime;

                let tdId = gcData["Id"];
                let tdGen = gcData["generation"];
                let tdType = gcData["Type"];
                let tdPauseTime = pauseTime.toFixed(2);
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

                var expensiveGc = "";
                if (pauseTime > 200.0) {
                    expensiveGc = ` class="expensiveGc"`;
                }
                else if (pauseTime > 100.0) {
                    expensiveGc = ` class="warnGc"`;
                }
                else if (pauseTime > 50.0) {
                    expensiveGc = ` class="interstingGc"`;
                }
                else if (pauseTime > 20.0) {
                    expensiveGc = ` class="somewhatInterestingGc"`;
                }
                else if (pauseTime > 10.0) {
                    expensiveGc = ` class="notSomewhatInterestingGc"`;
                }

                var blockingGc = "";

               // if (tdType == "NonBlocking")

                var allocationsByType: any = {};
                allocationsByType["totalAllocations"] = 0;
                allocationsByType["types"] = {};

                if (allocations != undefined) {
                    for (var allocIndex = 0; allocIndex < gcs[index].allocData.length; ++allocIndex) {
                        const currentAllocData = gcs[index].allocData[allocIndex];

                        const heapIndex = parseInt(currentAllocData.data.data["heapIndex"]);
                        const allocType = currentAllocData.data.data["typeName"];
                        const allocSizeInBytes = parseInt(currentAllocData.data.data["allocSizeBytes"]);

                        if (totalAllocationsByType["types"][allocType] == undefined) {
                            totalAllocationsByType["types"][allocType] = [] as string[];
                        }

                        totalAllocationsByType["types"][allocType].push({heapIndex: heapIndex, allocSizeInBytes: allocSizeInBytes});

                        if (allocationsByType["types"][heapIndex] == undefined) {
                            allocationsByType["types"][heapIndex] = {};
                        }

                        if (allocationsByType["types"][heapIndex][allocType] == undefined) {
                            allocationsByType["types"][heapIndex][allocType] = [] as string[];
                        }
                        
                        allocationsByType["types"][heapIndex][allocType].push(allocSizeInBytes);
                        allocationsByType["totalAllocations"] += allocSizeInBytes;
                        totalAllocationsByType["totalAllocations"] += allocSizeInBytes;
                    }

                    gcs[index].filteredAllocData = allocationsByType;
                }

                data += `<tr${expensiveGc}><td${blockingGc}>${tdId}</td><td>${tdGen}</td><td>${tdType}</td><td>${tdPauseTime}</td><td>${tdReason}</td><td>${tdGen0Size}</td><td>${tdGen1Size}</td><td>${tdGen2Size}</td><td>${tdLohSize}</td><td>NYI</td><td>${tdTotalHeapSize}</td><td>${tdGen0MinSize}</td><td>${tdTotalPromotedSize0}</td><td>${tdTotalPromotedSize1}</td><td>${tdTotalPromotedSize2}</td></tr>`;
            }

            data += `</table>`;

            let elapsedTimeInMs = (currentTime - startTime);
            percentInGcNumber = (this.timeInGc / elapsedTimeInMs) * 100;

            if (gcs.length > 0) {
                const gcData = gcs[0].data;

                canvasData += `<div id="processMemoryStatistics"><canvas class="processMemory"></canvas></div>`;

                // if (gcData["Heaps"].length > 1) {
                //     for (var innerIndex = 0; innerIndex < gcData["Heaps"].length; ++innerIndex) {
                //         const heap = gcData["Heaps"][innerIndex];

                //         if (innerIndex % 2 == 0) {
                //             canvasData += `<div class="heapChartParentMultiple heapChartNextLine"><canvas class="heapChart"></canvas></div>`;
                //         }
                //         else {
                //             canvasData += `<div class="heapChartParentMultiple"><canvas class="heapChart"></canvas></div>`;
                //         }
                //     }
                // }
                // else {
                //     canvasData += `<div class="heapChartParent"><canvas class="heapChart"></canvas></div>`;
                // }

                for (var innerIndex = 0; innerIndex < gcData["Heaps"].length; ++innerIndex) {
                    canvasData += `<div class="heapChartParentMultiple"><canvas class="heapChart"></canvas></div>`;
                    canvasData += `<div class="allocChartParent heapChartNextLine"><canvas class="allocChart"></canvas></div>`;
                }

                canvasData += `<div id="heapCharPadding"></div>`;
            }
        }
        else {
            const htmlReturn = /* html */`
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
                <title>${fileName}</title>
            </head>
            <body>
                
            </body>
            </html>`;

            return htmlReturn;
        }
        
        var gcsToSerialize = [] as GcData[];
        for (var index = 0; index < gcs.length; ++index) {
            var gcDataNew = new GcData(gcs[index]);

            gcsToSerialize.push(gcDataNew);
        }

        var hiddenData = JSON.stringify(gcsToSerialize);

        const nonce = this.getNonce();
        const nonce2 = this.getNonce();

        const mainUri = webview.asWebviewUri(vscode.Uri.joinPath(this.context.extensionUri, 'media', 'main.css'));
        const scriptUri = webview.asWebviewUri(vscode.Uri.joinPath(this.context.extensionUri, 'media', 'gcStats.js'));
        const styleResetUri = webview.asWebviewUri(vscode.Uri.joinPath(this.context.extensionUri, 'media', 'reset.css'));
        const styleVSCodeUri = webview.asWebviewUri(vscode.Uri.joinPath(this.context.extensionUri, 'media', 'vscode.css'));
        
        const chartjs = webview.asWebviewUri(vscode.Uri.joinPath(this.context.extensionUri, 'node_modules', 'chart.js', 'dist', 'Chart.min.js'));

        let percentInGc = percentInGcNumber.toFixed(2);

        var returnValue = /* html */`
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
                <title>${fileName}</title>
            </head>
            <body>
                <span style="display:none" id="hiddenData"><!--${hiddenData}--></span>
                <div id="processCommandLine">${processInfo?.processCommandLine}</div>
                <div id="percentInGc">
                    <div>Time in GC</div>
                    <div>${percentInGc}%</div>
                </div>
                <label class="switch">
                    <div>Allocations</div>
                    <input type="checkbox">
                    <span class="slider round"></span>
                </label>
                <div id="gcDataContainer">
                    ${canvasData}
                    <script src="${chartjs}"></script>
                    <div id="gcData">
                        ${data}
                    </div>
                </div>

                <script nonce="${nonce}" src="${scriptUri}"></script>
            </body>
            </html>`;

        return returnValue;
    }

    getNonce() {
        let text = '';
        const possible = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789';
        for (let i = 0; i < 32; i++) {
            text += possible.charAt(Math.floor(Math.random() * possible.length));
        }
        return text;
    }

    saveCustomDocument(document: DotnetInsightsGcDocument, cancellation: any): Thenable<void>
    {
        var promise = new Promise<void>((resolve, reject) => {
            const processInfo: ProcessInfo | undefined = this.listener.processes.get(document.processId);
            const gcs: GcData[] | undefined = processInfo?.data;
            const allocations: AllocData[] | undefined = [];

            var gcData = [] as GcData[];
            if (gcs != undefined) {
                for (var index = 0; index < gcs?.length; ++index) {
                    gcData.push(gcs[index]);
                }

                for (var index = 0; index < gcs?.length; ++index) {
                    const allocDataForGc = gcs[index].allocData;

                    for (var innerIndex = 0; innerIndex < allocDataForGc.length; ++innerIndex) {
                        allocations.push(allocDataForGc[innerIndex]);
                    }
                }
            }

            // At this point we will serialize the gc information and write to 
            // disk.
            if (!fs.existsSync(this.insights.gcDataSaveLocation)) {
                vscode.window.showWarningMessage("GC stats location does not exist, please check the settings configuration for a valid path.");
                return;
            }

            fs.readdir(this.insights.gcDataSaveLocation, (err, files) => {
                // Calculate the save file name.
                var fileName = document.processId.toString();
                if (processInfo != undefined) {
                    fileName = processInfo?.processName + "_" + document.processId.toString();
                }

                for(var index = 0; index < files.length; ++index) {
                    const file = files[index];
                    if (file.indexOf(fileName) != -1) {
                        // This process already exists we will want to attach
                        // the date to better distinguish
                        fileName = fileName + "_" + new Date().toUTCString();

                        break;
                    }
                }

                var dataToWrite = {
                    "gcData": gcData,
                    "allocations": allocations
                };

                const jsonString = JSON.stringify(dataToWrite);

                fileName += ".gcinfo";

                const fullPath = path.join(this.insights.gcDataSaveLocation, fileName);

                fs.writeFile(fullPath, jsonString, (err) => {
                    if (err != null) {
                        vscode.window.showWarningMessage("Failed to write file: " + err.toString());
                    }
                    else {
                        this.insights.outputChannel.appendLine(`${fullPath} written to disk`);
                    }
                });
            });
        });

        return promise;
    }

    saveCustomDocumentAs(document: vscode.CustomDocument, destination: vscode.Uri, cancellation: vscode.CancellationToken): Thenable<void> {
        throw new Error('Method not implemented.');
    }
}

class DotnetInsightsGcDocument extends vscode.Disposable implements vscode.TextDocument {
    uri: vscode.Uri;
    fileName: string;
    isUntitled: boolean;
    languageId: string;
    version: number;
    isDirty: boolean;
    isClosed: boolean;
    eol: vscode.EndOfLine;
    lineCount: number;
    processId: number;
    listener: GcListener

    constructor(
        uri: vscode.Uri,
        fileName: string,
        isUntitled: boolean,
        languageId: string,
        version: number,
        isDirty: boolean,
        isClosed: boolean,
        eol: vscode.EndOfLine,
        lineCount: number,
        processId: number,
        listener: GcListener,
    ) {
        super(() => {
            console.log("Tearing down ILDasmDocument");
        });

        this.uri = uri;
        this.fileName = fileName;
        this.isUntitled = isUntitled;
        this.languageId = languageId;
        this.version = version;
        this.isDirty = isDirty;
        this.isClosed = isClosed;
        this.eol = eol,
        this.lineCount = lineCount;
        this.processId = processId;
        this.listener = listener;
    }

    lineAt(position: any): vscode.TextLine {
        throw new Error('Method not implemented.');
    }
    
    save(): Thenable<boolean> {
        // Do nothing.

        return Promise.resolve(true);
    }

    offsetAt(position: vscode.Position): number {
        throw new Error('Method not implemented.');
    }

    positionAt(offset: number): vscode.Position {
        throw new Error('Method not implemented.');
    }

    getText(range?: vscode.Range): string {
        // if (range == undefined) {
            return "";
        // }


        //return this.text.substring(range.start, range.end);
    }

    getWordRangeAtPosition(position: vscode.Position, regex?: RegExp): vscode.Range | undefined {
        throw new Error('Method not implemented.');
    }

    validateRange(range: vscode.Range): vscode.Range {
        throw new Error('Method not implemented.');
    }
    
    validatePosition(position: vscode.Position): vscode.Position {
        throw new Error('Method not implemented.');
    }

    saveCustomDocument(document: DotnetInsightsGcDocument, cancellation: any): Thenable<void>
    {
        var promise = new Promise<void>((resolve, reject) => {
            const processInfo: ProcessInfo | undefined = this.listener.processes.get(document.processId);
            const gcs: GcData[] | undefined = processInfo?.data;
            const allocations: AllocData[] | undefined = [];

            var gcData = [] as GcData[];
            if (gcs != undefined) {
                for (var index = 0; index < gcs?.length; ++index) {
                    gcData.push(gcs[index]);
                }

                for (var index = 0; index < gcs?.length; ++index) {
                    const allocDataForGc = gcs[index].allocData;

                    for (var innerIndex = 0; innerIndex < allocDataForGc.length; ++innerIndex) {
                        allocations.push(allocDataForGc[innerIndex]);
                    }
                }
            }

            var i = 0;
        });

        return promise;
    }
}