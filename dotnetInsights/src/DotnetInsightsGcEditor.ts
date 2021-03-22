import * as child from 'child_process';
import * as fs from 'fs';
import * as os from "os";
import * as path from 'path';
import * as vscode from 'vscode';
import * as assert from "assert";

import { DotnetInsights } from "./dotnetInsights";
import { GcListener, ProcessInfo, GcData } from "./GcListener";

export class DotnetInsightsGcEditor implements vscode.CustomReadonlyEditorProvider {
    public static register(context: vscode.ExtensionContext, insights: DotnetInsights, listener: GcListener): vscode.Disposable {
        const provider = new DotnetInsightsGcEditor(context, insights, listener);
        const providerRegistration = vscode.window.registerCustomEditorProvider(DotnetInsightsGcEditor.viewType, provider);
        return providerRegistration;
    }

    public static readonly viewType = 'dotnetInsightsGc.edit';
    
    constructor(
        private readonly context: vscode.ExtensionContext,
        private readonly insights: DotnetInsights,
        private readonly listener: GcListener
    ) { }

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
                                                    processId);

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

        function updateWebview() {
            var data = listener.processes.get(pid)?.data;

            if (data != undefined) {
                const jsonData = JSON.stringify(data);
                
                webviewPanel.webview.postMessage({
                    type: 'update',
                    text: jsonData
                });
            }
        }

        webviewPanel.webview.html = this.getHtmlForWebview(gcDocument, webviewPanel.webview);

        const callBackTreeListener = listener.treeView?.onDidChangeTreeData(e => {
            updateWebview();
        });

        webviewPanel.onDidDispose(() => {
            callBackTreeListener?.dispose();
        });

        // Receive message from the webview.
        webviewPanel.webview.onDidReceiveMessage(e => {
            // switch (e.type) {
            //     case 'add':
            //         this.addNewScratch(document);
            //         return;

            //     case 'delete':
            //         this.deleteScratch(document, e.id);
            //         return;
            // }
        });

        //updateWebview();
    }

    private getHtmlForWebview(document: DotnetInsightsGcDocument, webview: vscode.Webview): string {
        var fileName = document.fileName;

        const processInfo: ProcessInfo | undefined = this.listener.processes.get(document.processId);
        const gcs: GcData[] | undefined = processInfo?.data;

        var data = "";
        var hiddenData = JSON.stringify(gcs);

        if (gcs != undefined) {

            data += `<table>`;
            data += `<tr class="tableHeader"><th>GC Number</th><th>Collection Generation</th><th>Type</th><th>Reason</th><th>Generation 0 Size</th><th>Generation 1 Size</th><th>Generation 2 Size</th><th>LOH Size</th><th>POH Size</th><th>Pause Time</th><th>Total Heap Size</th><th>Gen 0 Min Budget</th></tr>`;
            for (var index = 0; index < gcs.length; ++index) {
                const gcData = gcs[index].data;

                data += `<tr><td>${gcData["Id"]}</td><td>${gcData["generation"]}</td><td>${gcData["Type"]}</td><td>${gcData["kind"]}</td><td>${gcData["GenerationSize0"]}</td><td>${gcData["GenerationSize1"]}</td><td>${gcData["GenerationSize2"]}</td><td>${gcData["GenerationSizeLOH"]}</td><td>NYI</td><td>${gcData["PauseDurationMSec"]}</td><td>${gcData["TotalHeapSize"]}</td><td>${gcData["Gen0MinSize"]}</td></tr>`;

                // for (var innerIndex = 0; innerIndex < gcData["Heaps"].length; ++innerIndex) {
                //     const heap = gcData["Heaps"][innerIndex];
                //     data += `<div>HeapIndex: ${heap["Index"]}</div>`;

                //     for (var genIndex = 0; genIndex < heap["Generations"].length; ++genIndex) {
                //         const generation = heap["Generations"][genIndex];
                //         data += `<div>Gen ID: ${generation["Id"]} Size Before: ${generation["SizeBefore"]} SizeAfter: ${generation["SizeAfter"]} NewAllocation: ${generation["NewAllocation"]}</div>`
                //     }
                // }
            }

            data += `</table>`;
        }

        const nonce = this.getNonce();
        const nonce2 = this.getNonce();

        const mainUri = webview.asWebviewUri(vscode.Uri.joinPath(this.context.extensionUri, 'media', 'main.css'));
        const scriptUri = webview.asWebviewUri(vscode.Uri.joinPath(this.context.extensionUri, 'media', 'gcStats.js'));
        const styleResetUri = webview.asWebviewUri(vscode.Uri.joinPath(this.context.extensionUri, 'media', 'reset.css'));
        const styleVSCodeUri = webview.asWebviewUri(vscode.Uri.joinPath(this.context.extensionUri, 'media', 'vscode.css'));
        
        const chartjs = webview.asWebviewUri(vscode.Uri.joinPath(this.context.extensionUri, 'node_modules', 'chart.js', 'dist', 'Chart.min.js'));

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
                <span style="display:none" id="hiddenData">${hiddenData}</span>
                <div id=gcDataContainer>
                    <canvas id="myChart" width="400" height="400"></canvas>
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
        processId: number
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
}