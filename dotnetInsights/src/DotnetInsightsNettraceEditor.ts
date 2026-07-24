import * as child from 'child_process';
import * as crypto from 'crypto';
import * as fs from 'fs';
import * as os from "os";
import * as path from 'path';
import * as vscode from 'vscode';

import { DotnetInsights } from "./dotnetInsights";
import { DotnetInsightsGcDocument } from "./DotnetInsightsGcEditor";
import { renderGcSnapshotWebview } from "./GcSnapshotRenderer";

// Opens a .nettrace file, shells out to the nettraceParser tool to decode it
// into the same JSON shape DotnetInsightsGcSnapshotEditor's XML path produces
// (see nettraceParser/Gc/GcJsonExporter.cs), then renders it with the same
// shared renderer .gcinfo files use.
export class DotnetInsightsNettraceEditor implements vscode.CustomReadonlyEditorProvider {
    public static register(context: vscode.ExtensionContext, insights: DotnetInsights): vscode.Disposable {
        const provider = new DotnetInsightsNettraceEditor(context, insights);
        const providerRegistration = vscode.window.registerCustomEditorProvider(DotnetInsightsNettraceEditor.viewType, provider);
        return providerRegistration;
    }

    public static readonly viewType = 'dotnetInsightsNettrace.edit';

    constructor(
        private readonly context: vscode.ExtensionContext,
        private readonly insights: DotnetInsights
    ) {
    }

    openCustomDocument(uri: vscode.Uri, openContext: vscode.CustomDocumentOpenContext, token: vscode.CancellationToken): vscode.CustomDocument | Thenable<vscode.CustomDocument> {
        var filename = path.basename(uri.path);
        var endofLine = os.platform() === "win32" ? vscode.EndOfLine.CRLF : vscode.EndOfLine.LF;

        var document = new DotnetInsightsGcDocument(uri,
                                                    filename,
                                                    false,
                                                    "nettrace",
                                                    1,
                                                    false,
                                                    true,
                                                    endofLine,
                                                    0,
                                                    0,
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

        this.getHtmlForWebviewWrapper(gcDocument, webviewPanel.webview).then((str) => {
            webviewPanel.webview.html = str;
        });
    }

    private runNettraceParser(nettraceFilePath: string): Thenable<any> {
        if (!fs.existsSync(this.insights.nettraceParserOutputPath)) {
            fs.mkdirSync(this.insights.nettraceParserOutputPath, { recursive: true });
        }

        const id = crypto.randomBytes(16).toString("hex");
        const jsonOutputPath = path.join(this.insights.nettraceParserOutputPath, `${id}.json`);

        // nettraceParser is distributed as a self-contained native executable
        // (like roslynHelper), invoked directly - not "dotnet <dll>".
        const command = `"${this.insights.nettraceParserPath}" "${nettraceFilePath}" --json "${jsonOutputPath}"`;
        this.insights.outputChannel.appendLine(command);

        var promiseToReturn = new Promise<any>((resolve, reject) => {
            child.exec(command, { maxBuffer: 512 * 1024 * 1024 }, (error: any, stdout: string, stderr: string) => {
                if (error) {
                    this.insights.outputChannel.appendLine("Failed to execute nettraceParser.");
                    this.insights.outputChannel.appendLine(stderr);
                    resolve(null);
                    return;
                }

                try {
                    const fileContents = fs.readFileSync(jsonOutputPath);
                    resolve(JSON.parse(fileContents.toString()));
                }
                catch (e) {
                    this.insights.outputChannel.appendLine("Failed to read nettraceParser output.");
                    resolve(null);
                }
                finally {
                    try {
                        fs.unlinkSync(jsonOutputPath);
                    }
                    catch (e) {
                        // Best effort cleanup.
                    }
                }
            });
        });

        return promiseToReturn;
    }

    private getHtmlForWebviewWrapper(document: DotnetInsightsGcDocument, webview: vscode.Webview): Thenable<string> {
        var promiseToReturn = new Promise<string>((resolve, reject) => {
            this.runNettraceParser(document.uri.fsPath).then((gcData: any) => {
                resolve(renderGcSnapshotWebview(document, webview, this.context.extensionUri, gcData));
            });
        });

        return promiseToReturn;
    }
}
