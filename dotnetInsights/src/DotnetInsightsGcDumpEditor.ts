import * as child from 'child_process';
import * as crypto from 'crypto';
import * as fs from 'fs';
import * as os from "os";
import * as path from 'path';
import * as readline from 'readline';
import * as vscode from 'vscode';

import { DotnetInsights } from "./dotnetInsights";
import { DotnetInsightsGcDocument } from "./DotnetInsightsGcEditor";
import { renderGcDumpWebview } from "./GcDumpRenderer";
import { renderNettraceLoadingHtml } from "./NettraceLoadingRenderer";
import { NettraceProgressTracker, NettraceProgressUpdate, parseProgressLine, JSON_READ_RANGE, RENDER_RANGE, SWAP_RANGE } from "./NettraceProgress";

// Opens a .gcdump file (a `dotnet-gcdump collect` heap snapshot), shells out
// to nettraceParser's --gcdump mode to decode and analyze it, then renders the
// result with GcDumpRenderer.
//
// Structurally this is DotnetInsightsNettraceEditor with a different child
// process argument and a different renderer, and deliberately so - the
// loading-document-first sequence, the PROGRESS-line-driven bar, the
// nettraceLoadingReady handshake and the disposal handling are all subtle
// enough (see that file's header, and NettraceProgress.ts's) that solving them
// a second way here would be a liability rather than reuse. Everything except
// the two differences above is shared code, not copied code.
//
// Unlike the .nettrace path there is NO binary sidecar: nettraceParser's
// --gcdump output is aggregated to the type level in C# before it is written
// (see nettraceParser/GcDump/GcDumpAnalysis.cs), so even a ten-million-object
// heap produces a payload of a few thousand rows that a plain JSON.parse
// handles comfortably. The binary container exists to avoid materializing
// millions of values; here there are never millions of values to materialize.
export class DotnetInsightsGcDumpEditor implements vscode.CustomReadonlyEditorProvider {
    public static register(context: vscode.ExtensionContext, insights: DotnetInsights): vscode.Disposable {
        const provider = new DotnetInsightsGcDumpEditor(context, insights);

        // Same reason as the .nettrace editor: without retainContextWhenHidden
        // switching tabs away and back tears the webview's DOM and JS state
        // down and rebuilds it, losing any filter typed, tree expanded or
        // column sorted.
        return vscode.window.registerCustomEditorProvider(
            DotnetInsightsGcDumpEditor.viewType,
            provider,
            { webviewOptions: { retainContextWhenHidden: true } });
    }

    public static readonly viewType = 'dotnetInsightsGcDump.edit';

    constructor(
        private readonly context: vscode.ExtensionContext,
        private readonly insights: DotnetInsights
    ) {
    }

    openCustomDocument(uri: vscode.Uri, openContext: vscode.CustomDocumentOpenContext, token: vscode.CancellationToken): vscode.CustomDocument | Thenable<vscode.CustomDocument> {
        const filename = path.basename(uri.path);
        const endofLine = os.platform() === "win32" ? vscode.EndOfLine.CRLF : vscode.EndOfLine.LF;

        return new DotnetInsightsGcDocument(uri,
                                            filename,
                                            false,
                                            "gcdump",
                                            1,
                                            false,
                                            true,
                                            endofLine,
                                            0,
                                            0,
                                            null);
    }

    resolveCustomEditor(document: vscode.CustomDocument, webviewPanel: vscode.WebviewPanel, token: vscode.CancellationToken): void | Thenable<void> {
        webviewPanel.webview.options = {
            enableScripts: true,
            localResourceRoots: [
                vscode.Uri.joinPath(this.context.extensionUri, 'media')
            ]
        };

        const gcDocument = document as DotnetInsightsGcDocument;

        // Assigned synchronously, before the parser is even spawned, so there
        // is a live document able to receive postMessage progress while the
        // parse is still running - see DotnetInsightsNettraceEditor.ts's
        // header for the full reasoning, which applies unchanged.
        webviewPanel.webview.html = renderNettraceLoadingHtml(gcDocument.fileName, webviewPanel.webview, this.context.extensionUri);

        let disposed = false;
        const tracker = new NettraceProgressTracker();

        const postProgress = (update: NettraceProgressUpdate) => {
            if (disposed) {
                return;
            }
            webviewPanel.webview.postMessage({ type: 'nettraceProgress', percent: update.percent, label: update.label });
        };
        const postError = (message: string) => {
            if (disposed) {
                return;
            }
            webviewPanel.webview.postMessage({ type: 'nettraceError', message: message });
        };
        const setHtml = (html: string) => {
            if (disposed) {
                return;
            }
            webviewPanel.webview.html = html;
        };

        // A postMessage sent before the loading document has finished loading
        // and run its own script is silently dropped, with no VS Code-side
        // buffering - so the loading view signals when it is ready and the
        // host replays whatever progress has happened by then.
        const messageSubscription = webviewPanel.webview.onDidReceiveMessage((message: any) => {
            if (message && message.type === 'nettraceLoadingReady') {
                postProgress(tracker.current);
            }
        });

        let spawnedProcess: child.ChildProcess | null = null;

        webviewPanel.onDidDispose(() => {
            disposed = true;
            messageSubscription.dispose();
            spawnedProcess?.kill();
        });

        this.runGcDumpParser(gcDocument.uri.fsPath, tracker, postProgress, (proc) => { spawnedProcess = proc; })
            .then(async (gcDumpData: any) => {
                if (disposed) {
                    return;
                }

                if (gcDumpData === null) {
                    postError('Failed to parse this .gcdump file - see the "Dotnet Insights" output channel for details.');
                    setHtml(renderGcDumpWebview(gcDocument.fileName, webviewPanel.webview, this.context.extensionUri, null));
                    return;
                }

                postProgress(tracker.recordHostStage(1, JSON_READ_RANGE, 'Reading results'));

                postProgress(tracker.recordHostStage(0, RENDER_RANGE, 'Rendering'));
                // Yields so the message queued above actually reaches the
                // webview before the synchronous HTML build below blocks the
                // extension host's event loop.
                await new Promise<void>((resolve) => setImmediate(resolve));

                const html = renderGcDumpWebview(gcDocument.fileName, webviewPanel.webview, this.context.extensionUri, gcDumpData);
                postProgress(tracker.recordHostStage(1, RENDER_RANGE, 'Rendering'));

                postProgress(tracker.recordHostStage(0, SWAP_RANGE, 'Finishing up'));
                await new Promise<void>((resolve) => setImmediate(resolve));

                setHtml(html);
                postProgress(tracker.recordHostStage(1, SWAP_RANGE, 'Done'));
            });
    }

    // Resolves to the parsed analysis object, or null on any failure.
    private runGcDumpParser(gcDumpFilePath: string, tracker: NettraceProgressTracker, postProgress: (update: NettraceProgressUpdate) => void, onProcessSpawned: (proc: child.ChildProcess) => void): Thenable<any> {
        if (this.insights.nettraceParserPath === undefined || this.insights.nettraceParserPath === null || this.insights.nettraceParserPath === "") {
            this.insights.outputChannel.appendLine("nettraceParser is not available; .gcdump files cannot be opened. Set dotnet-insights.nettraceParserPath to a local build, or wait for the tool download to complete.");
            return Promise.resolve(null);
        }

        if (!fs.existsSync(this.insights.nettraceParserOutputPath)) {
            fs.mkdirSync(this.insights.nettraceParserOutputPath, { recursive: true });
        }

        const id = crypto.randomBytes(16).toString("hex");
        const jsonOutputPath = path.join(this.insights.nettraceParserOutputPath, `${id}.gcdump.json`);

        this.insights.outputChannel.appendLine(`"${this.insights.nettraceParserPath}" --gcdump "${gcDumpFilePath}" --json "${jsonOutputPath}"`);

        const gcDumpFileSizeBytes = fs.statSync(gcDumpFilePath).size;
        const execStartMs = Date.now();

        return new Promise<any>((resolve) => {
            // spawn with an argument array rather than a shell string, so a
            // path containing quotes or spaces needs no manual escaping.
            const proc = child.spawn(this.insights.nettraceParserPath, ["--gcdump", gcDumpFilePath, "--json", jsonOutputPath]);
            onProcessSpawned(proc);

            let stderrTail = "";
            const maxStderrTailLength = 8192;

            // Drained even though --gcdump --json writes nothing there: an
            // unconsumed stdout pipe can block the child once its OS-level
            // buffer fills.
            proc.stdout?.resume();

            const stderrLines = readline.createInterface({ input: proc.stderr! });
            stderrLines.on('line', (line: string) => {
                const parsed = parseProgressLine(line);
                if (parsed !== null) {
                    postProgress(tracker.recordChildPercent(parsed.percent, parsed.label));
                    return;
                }

                stderrTail += line + "\n";
                if (stderrTail.length > maxStderrTailLength) {
                    stderrTail = stderrTail.slice(stderrTail.length - maxStderrTailLength);
                }
            });

            proc.on('error', (error: Error) => {
                this.insights.outputChannel.appendLine(`nettraceParser --gcdump: ${gcDumpFileSizeBytes} bytes in, subprocess took ${Date.now() - execStartMs}ms`);
                this.insights.outputChannel.appendLine("Failed to execute nettraceParser.");
                this.insights.outputChannel.appendLine(error.message);
                resolve(null);
            });

            proc.on('close', (exitCode: number | null) => {
                this.insights.outputChannel.appendLine(`nettraceParser --gcdump: ${gcDumpFileSizeBytes} bytes in, subprocess took ${Date.now() - execStartMs}ms`);

                if (exitCode !== 0) {
                    this.insights.outputChannel.appendLine("Failed to execute nettraceParser.");
                    this.insights.outputChannel.appendLine(stderrTail);
                    resolve(null);
                    return;
                }

                const readStartMs = Date.now();

                try {
                    const parsed = JSON.parse(fs.readFileSync(jsonOutputPath).toString());
                    this.insights.outputChannel.appendLine(`nettraceParser --gcdump: JSON read took ${Date.now() - readStartMs}ms`);
                    resolve(parsed);
                }
                catch (e: any) {
                    // Logged in full rather than swallowed - an unreadable
                    // output file and a genuinely corrupt input look identical
                    // in the UI otherwise.
                    this.insights.outputChannel.appendLine("Failed to read nettraceParser --gcdump output.");
                    this.insights.outputChannel.appendLine(e && e.stack ? e.stack : String(e));
                    resolve(null);
                }
                finally {
                    try {
                        fs.unlinkSync(jsonOutputPath);
                    }
                    catch (e) {
                        // Best effort - it may never have been written.
                    }
                }
            });
        });
    }
}
