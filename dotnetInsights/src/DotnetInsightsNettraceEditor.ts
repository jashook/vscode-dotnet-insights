import * as child from 'child_process';
import * as crypto from 'crypto';
import * as fs from 'fs';
import * as os from "os";
import * as path from 'path';
import * as vscode from 'vscode';

import { DotnetInsights } from "./dotnetInsights";
import { DotnetInsightsGcDocument } from "./DotnetInsightsGcEditor";
import { renderGcSnapshotWebview } from "./GcSnapshotRenderer";
import { readNettraceJson, ticksBinaryPathFor } from "./NettraceJsonStreamReader";

// Opens a .nettrace file, shells out to the nettraceParser tool to decode it
// into the same JSON shape DotnetInsightsGcSnapshotEditor's XML path produces
// (see nettraceParser/Gc/GcJsonExporter.cs), then renders it with the same
// shared renderer .gcinfo files use.
export class DotnetInsightsNettraceEditor implements vscode.CustomReadonlyEditorProvider {
    public static register(context: vscode.ExtensionContext, insights: DotnetInsights): vscode.Disposable {
        const provider = new DotnetInsightsNettraceEditor(context, insights);
        // Without this, switching to another editor tab and back tears down
        // and reloads the webview's whole DOM/JS state from scratch (VS
        // Code's default) - losing the Detailed tab's injected table, any
        // sort applied to it, and the GC charts' zoom range (see
        // snapshotGcStats.js's gcChartsZoomRange/heapContentsZoomRange).
        const providerRegistration = vscode.window.registerCustomEditorProvider(DotnetInsightsNettraceEditor.viewType, provider, { webviewOptions: { retainContextWhenHidden: true } });
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

        // Timing instrumentation - a .nettrace file's parse cost scales with
        // total event volume (JIT/thread/allocation-tick events etc.), not
        // just GC count, so "few GCs" alone doesn't rule this step out as
        // the source of a slow document open. Logged to the "Dotnet
        // Insights" output channel so it's visible without attaching a
        // debugger or opening webview DevTools.
        const nettraceFileSizeBytes = fs.statSync(nettraceFilePath).size;
        const execStartMs = Date.now();

        var promiseToReturn = new Promise<any>((resolve, reject) => {
            child.exec(command, { maxBuffer: 512 * 1024 * 1024 }, (error: any, stdout: string, stderr: string) => {
                const execElapsedMs = Date.now() - execStartMs;
                this.insights.outputChannel.appendLine(`nettraceParser: ${nettraceFileSizeBytes} bytes in, subprocess took ${execElapsedMs}ms`);

                if (error) {
                    this.insights.outputChannel.appendLine("Failed to execute nettraceParser.");
                    this.insights.outputChannel.appendLine(stderr);
                    resolve(null);
                    return;
                }

                // A plain fs.readFileSync(...).toString() + JSON.parse(...)
                // used to throw "Cannot create a string longer than
                // 0x1fffffe8 characters" for a heavily-allocating capture's
                // output (696MB in one real case) - Node's own maximum
                // string length. That's no longer a concern: nettraceParser
                // now writes the allocation-tick array as a separate binary
                // sidecar file instead of inline JSON (see
                // AllocationJsonExporter.cs's WriteTicks), which drops the
                // JSON itself under 100MB even for the same capture - see
                // NettraceJsonStreamReader.ts for the read side of both files.
                const readStartMs = Date.now();
                const ticksBinaryPath = ticksBinaryPathFor(jsonOutputPath);

                readNettraceJson(jsonOutputPath).then((parsed) => {
                    this.insights.outputChannel.appendLine(`nettraceParser: JSON + ticks binary read took ${Date.now() - readStartMs}ms`);
                    resolve(parsed);
                }).catch((e: any) => {
                    // Logged in full this time - the previous approach
                    // swallowed the exception entirely, which made an
                    // oversized-output crash indistinguishable in the UI
                    // from an actually corrupted/wrong-type file.
                    this.insights.outputChannel.appendLine("Failed to read nettraceParser output.");
                    this.insights.outputChannel.appendLine(e && e.stack ? e.stack : String(e));
                    resolve(null);
                }).finally(() => {
                    try {
                        fs.unlinkSync(jsonOutputPath);
                    }
                    catch (e) {
                        // Best effort cleanup.
                    }

                    try {
                        fs.unlinkSync(ticksBinaryPath);
                    }
                    catch (e) {
                        // Best effort cleanup - won't exist for a .gcinfo/XML
                        // source (that path never calls this function at
                        // all) or if nettraceParser itself failed before
                        // writing anything.
                    }
                });
            });
        });

        return promiseToReturn;
    }

    private getHtmlForWebviewWrapper(document: DotnetInsightsGcDocument, webview: vscode.Webview): Thenable<string> {
        var promiseToReturn = new Promise<string>((resolve, reject) => {
            const totalStartMs = Date.now();

            this.runNettraceParser(document.uri.fsPath).then((gcData: any) => {
                const renderStartMs = Date.now();
                const html = renderGcSnapshotWebview(document, webview, this.context.extensionUri, gcData, "nettrace");
                this.insights.outputChannel.appendLine(`renderGcSnapshotWebview took ${Date.now() - renderStartMs}ms`);
                this.insights.outputChannel.appendLine(`Total document open (nettraceParser + render) took ${Date.now() - totalStartMs}ms`);
                resolve(html);
            });
        });

        return promiseToReturn;
    }
}
