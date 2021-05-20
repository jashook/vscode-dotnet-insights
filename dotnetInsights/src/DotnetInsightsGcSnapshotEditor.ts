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
        }

        webviewPanel.webview.html = this.getHtmlForWebview(gcDocument, webviewPanel.webview);
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
            vscode.window.showWarningMessage(`${document.uri.fsPath} is corrupted or a incorrect type.`);
            return defaultHtmlReturn;
        }

        // gc data has all of the allocations and gc events that occurred in the
        // window. We will now go through and calculate the interesting data we
        // want from what we were provided.

        const gcs = gcData["gcData"];

        // Time in GC.
        var totalTimeInGc: number = 0.0;
        var timesInEachGc = [];
        var averageTimeInGc = 0;
        var medianTimeInGc = 0;
        var highestTimeInGc = 0;
        var lowestTimeInGc = 0;
        for (var index = 0; index < gcs.length; ++index) {
            timesInEachGc.push(parseFloat(gcs[index]["data"]["PauseDurationMSec"]));
        }

        // Gcs over 50ms
        var expensiveGcs = [];
        for (var index = 0; index < gcs.length; ++index) {
            if (parseFloat(gcs[index]["data"]["PauseDurationMSec"]) > 50) {
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

        // Allocations

        return defaultHtmlReturn;
    }
}