import * as child from 'child_process';
import * as crypto from 'crypto';
import * as fs from 'fs';
import * as os from "os";
import * as path from 'path';
import * as readline from 'readline';
import * as vscode from 'vscode';

import { DotnetInsights } from "./dotnetInsights";
import { DotnetInsightsGcDocument } from "./DotnetInsightsGcEditor";
import { renderGcSnapshotWebview } from "./GcSnapshotRenderer";
import { readNettraceJson, ticksBinaryPathFor } from "./NettraceJsonStreamReader";
import { renderNettraceLoadingHtml } from "./NettraceLoadingRenderer";
import { JSON_READ_RANGE, NettraceProgressTracker, NettraceProgressUpdate, parseProgressLine, RENDER_RANGE, SWAP_RANGE } from "./NettraceProgress";

// Opens a .nettrace file, shells out to the nettraceParser tool to decode it
// into the same JSON shape DotnetInsightsGcSnapshotEditor's XML path produces
// (see nettraceParser/Gc/GcJsonExporter.cs), then renders it with the same
// shared renderer .gcinfo files use.
//
// The webview's own document is set TWICE, not once: a lightweight loading
// placeholder (NettraceLoadingRenderer.ts) is assigned SYNCHRONOUSLY as soon
// as resolveCustomEditor runs, before nettraceParser is even spawned - this
// is what lets a live progress bar exist at all, since a webview has to
// have an actual document loaded and running its own script before it can
// receive postMessage updates. Once parsing (and the extension host's own
// remaining work - reading the JSON back, rendering the real HTML) finishes,
// webview.html is assigned a second time with the real content, fully
// replacing the loading document - the same "assign webview.html once, with
// final content" pattern this codebase already uses elsewhere
// (DotnetInsightsGcEditor.ts), just with an extra assignment in front of it
// now. Not an in-place DOM patch: media/snapshotGcStats.js calls
// acquireVsCodeApi() itself, which throws if called a second time in the
// same document, so the two documents can't coexist - see
// NettraceLoadingRenderer.ts's own header comment.
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
                vscode.Uri.joinPath(this.context.extensionUri, 'media'),
                // nettraceParser's own output directory. The webview fetches
                // the binary container (see nettraceParser/Binary/
                // BinaryCaptureFormat.cs) directly from here rather than
                // having the extension host read it, parse it and re-embed
                // it in the HTML - asWebviewUri can only produce a servable
                // URI for a file underneath one of these roots.
                vscode.Uri.file(this.insights.nettraceParserOutputPath)
            ]
        };

        var gcDocument = document as DotnetInsightsGcDocument;

        // Immediate, synchronous - see this class's own header comment for
        // why this has to happen before any of the async work below, not
        // after it the way the single html assignment used to.
        webviewPanel.webview.html = renderNettraceLoadingHtml(gcDocument.fileName, webviewPanel.webview, this.context.extensionUri);

        let disposed = false;
        const tracker = new NettraceProgressTracker();

        // .html assignment and postMessage both throw if called after the
        // panel is disposed (e.g. the user closed the tab mid-parse) -
        // every call site below goes through these two guarded helpers
        // instead of touching webviewPanel.webview directly, so that one
        // check doesn't need repeating at every call site.
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

        // Ready handshake: nettraceLoadingView.js's own document has to
        // finish loading and run its own script before it can receive
        // postMessage at all - there is no VS Code-side buffering of
        // messages sent before that point, they're just silently dropped.
        // Replaying `tracker.current` here covers the (normally rare, but
        // possible if the child process reports progress unusually fast)
        // case where progress already happened before this handshake
        // completes.
        const messageSubscription = webviewPanel.webview.onDidReceiveMessage((message: any) => {
            if (message && message.type === 'nettraceLoadingReady') {
                postProgress(tracker.current);
            }
        });

        webviewPanel.onDidDispose(() => {
            disposed = true;
            messageSubscription.dispose();
            spawnedProcess?.kill();

            if (binaryOutputPath !== null) {
                try {
                    fs.unlinkSync(binaryOutputPath);
                }
                catch (e) {
                    // Best effort - it may never have been written if the
                    // parse failed or the panel closed mid-run.
                }
            }
        });

        let spawnedProcess: child.ChildProcess | null = null;
        // Kept alive for the webview to fetch (see runNettraceParser's own
        // onBinaryWritten comment), so this panel owns its cleanup.
        let binaryOutputPath: string | null = null;

        this.runNettraceParser(gcDocument.uri.fsPath, tracker, postProgress, (proc) => { spawnedProcess = proc; }, (binaryPath) => { binaryOutputPath = binaryPath; })
            .then(async (gcData: any) => {
                if (disposed) {
                    return;
                }

                if (gcData === null) {
                    // Matches renderGcSnapshotWebview's own existing
                    // null-gcData handling (the "corrupted or a incorrect
                    // type" warning + a blank page) - this postMessage is
                    // purely an ADDITIONAL, more persistent failure
                    // indicator inside the tab itself (a toast is easy to
                    // miss or dismiss), not a replacement for it.
                    postError('Failed to parse this .nettrace file - see the "Dotnet Insights" output channel for details.');
                    const blankHtml = renderGcSnapshotWebview(gcDocument, webviewPanel.webview, this.context.extensionUri, gcData, "nettrace");
                    setHtml(blankHtml);
                    return;
                }

                postProgress(tracker.recordHostStage(1, JSON_READ_RANGE, 'Reading results'));

                postProgress(tracker.recordHostStage(0, RENDER_RANGE, 'Rendering'));
                // Yields to the event loop so the postMessage just queued
                // above actually reaches the webview before the
                // synchronous, potentially-large HTML build below runs -
                // renderGcSnapshotWebview blocks the extension host's own
                // event loop for its whole duration, during which no
                // postMessage can be delivered.
                await new Promise<void>((resolve) => setImmediate(resolve));

                const html = renderGcSnapshotWebview(gcDocument, webviewPanel.webview, this.context.extensionUri, gcData, "nettrace", binaryOutputPath);
                postProgress(tracker.recordHostStage(1, RENDER_RANGE, 'Rendering'));

                postProgress(tracker.recordHostStage(0, SWAP_RANGE, 'Finishing up'));
                await new Promise<void>((resolve) => setImmediate(resolve));

                setHtml(html);
                postProgress(tracker.recordHostStage(1, SWAP_RANGE, 'Done'));
            });
    }

    // Returns a Promise resolving to the parsed gcData object (or null on
    // any failure - matches this method's own pre-existing contract), plus
    // reports progress along the way via postProgress:
    //   - CHILD_PROCESS_RANGE (0-80, see NettraceProgress.ts): every
    //     "PROGRESS <percent> <label>" line nettraceParser itself writes to
    //     stderr while it runs (see nettraceParser/Progress/ProgressReporter.cs).
    //   - JSON_READ_RANGE (80-90): reading the JSON + ticks-binary output
    //     back into the extension host (readNettraceJson) - a single
    //     snap-only stage (start/end, no fractional tracking within it),
    //     the same "phase too small/hard to subdivide economically gets a
    //     snap instead of internal tracking" convention
    //     nettraceParser/Progress/ProgressPlan.cs already uses on the C#
    //     side for its own small phases.
    // onProcessSpawned hands the caller the live ChildProcess handle (for
    // killing it if the panel is disposed mid-parse - see
    // resolveCustomEditor's own onDidDispose) as soon as it exists, since
    // spawn() (unlike the previous exec()-based version) returns a real,
    // killable handle rather than only a callback.
    // onBinaryWritten hands back the binary container's path as soon as it is
    // known. Unlike the JSON, this file is NOT deleted when parsing finishes -
    // the webview fetches it itself, after the host is done, so it has to
    // outlive this method and is cleaned up on panel dispose instead.
    private runNettraceParser(nettraceFilePath: string, tracker: NettraceProgressTracker, postProgress: (update: NettraceProgressUpdate) => void, onProcessSpawned: (proc: child.ChildProcess) => void, onBinaryWritten: (binaryPath: string) => void): Thenable<any> {
        if (!fs.existsSync(this.insights.nettraceParserOutputPath)) {
            fs.mkdirSync(this.insights.nettraceParserOutputPath, { recursive: true });
        }

        const id = crypto.randomBytes(16).toString("hex");
        const jsonOutputPath = path.join(this.insights.nettraceParserOutputPath, `${id}.json`);
        // Written alongside the JSON, not instead of it: sections are being
        // migrated off JSON one at a time (see nettraceParser/Binary/
        // BinaryCaptureFormat.cs), so whatever hasn't moved yet still comes
        // through the JSON path.
        const binaryOutputPath = path.join(this.insights.nettraceParserOutputPath, `${id}.bin`);

        this.insights.outputChannel.appendLine(`"${this.insights.nettraceParserPath}" "${nettraceFilePath}" --json "${jsonOutputPath}" --binary "${binaryOutputPath}"`);

        // Timing instrumentation - a .nettrace file's parse cost scales with
        // total event volume (JIT/thread/allocation-tick events etc.), not
        // just GC count, so "few GCs" alone doesn't rule this step out as
        // the source of a slow document open. Logged to the "Dotnet
        // Insights" output channel so it's visible without attaching a
        // debugger or opening webview DevTools.
        const nettraceFileSizeBytes = fs.statSync(nettraceFilePath).size;
        const execStartMs = Date.now();

        var promiseToReturn = new Promise<any>((resolve, reject) => {
            // spawn() with an argument array (not a shell-interpolated
            // command string) - unlike the previous exec()-based call, this
            // needs no manual quoting (a file path containing `"` used to
            // silently break that), and gives a real, killable process
            // handle back immediately rather than only a completion
            // callback.
            // Native symbol resolution for a v6 (`dotnet-trace collect-linux`)
            // capture. The cache lives in the extension's own globalStorage
            // rather than the parser's default so it is cleaned up with the
            // extension, and it is keyed by build id, so it is shared across
            // every capture from the same runtime build - a first open pays a
            // one-time ~138MB download for libcoreclr.so and every open after
            // that is free. Ignored entirely by a v5 capture, which needs no
            // symbol server at all.
            const parserArgs = [nettraceFilePath, "--json", jsonOutputPath, "--binary", binaryOutputPath,
                                "--symbol-cache", this.insights.nettraceSymbolCachePath];

            const configuration = vscode.workspace.getConfiguration("dotnet-insights");

            if (configuration.get<boolean>("downloadNativeSymbols") === false) {
                parserArgs.push("--no-symbol-download");
            }

            const extraSymbolServers = configuration.get<string[]>("symbolServers") || [];

            for (const symbolServer of extraSymbolServers) {
                parserArgs.push("--symbol-server", symbolServer);
            }

            const proc = child.spawn(this.insights.nettraceParserPath, parserArgs);
            onProcessSpawned(proc);
            onBinaryWritten(binaryOutputPath);

            let stderrTail = "";
            const maxStderrTailLength = 8192;

            // stdout is explicitly drained (even though --json mode itself
            // writes nothing there today) - an unconsumed stdout pipe can
            // block the child once its OS-level buffer fills, the same way
            // unconsumed stderr would.
            proc.stdout?.resume();

            const stderrLines = readline.createInterface({ input: proc.stderr! });
            stderrLines.on('line', (line: string) => {
                const parsed = parseProgressLine(line);
                if (parsed !== null) {
                    postProgress(tracker.recordChildPercent(parsed.percent, parsed.label));
                    return;
                }

                // Not a PROGRESS line - either the final "Timing: ..."
                // diagnostic line, or (on failure) a real error - kept for
                // the existing "log full stderr on failure" behavior below,
                // bounded so a pathological amount of stderr output can't
                // grow this unboundedly.
                stderrTail += line + "\n";
                if (stderrTail.length > maxStderrTailLength) {
                    stderrTail = stderrTail.slice(stderrTail.length - maxStderrTailLength);
                }
            });

            proc.on('error', (error: Error) => {
                const execElapsedMs = Date.now() - execStartMs;
                this.insights.outputChannel.appendLine(`nettraceParser: ${nettraceFileSizeBytes} bytes in, subprocess took ${execElapsedMs}ms`);
                this.insights.outputChannel.appendLine("Failed to execute nettraceParser.");
                this.insights.outputChannel.appendLine(error.message);
                resolve(null);
            });

            proc.on('close', (exitCode: number | null) => {
                const execElapsedMs = Date.now() - execStartMs;
                this.insights.outputChannel.appendLine(`nettraceParser: ${nettraceFileSizeBytes} bytes in, subprocess took ${execElapsedMs}ms`);

                if (exitCode !== 0) {
                    this.insights.outputChannel.appendLine("Failed to execute nettraceParser.");
                    this.insights.outputChannel.appendLine(stderrTail);
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
}
