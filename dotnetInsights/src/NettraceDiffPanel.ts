// Hosts the two-capture comparison webview.
//
// Mirrors DotnetInsightsNettraceEditor.ts's proven flow (loading placeholder
// first, spawn the parser, drive a progress bar off its stderr PROGRESS lines,
// then swap in the real document) but hangs off a plain
// `vscode.window.createWebviewPanel` rather than a CustomEditor: a diff is not
// a document, and there is no single file to associate it with.
//
// The parser is invoked once with `--diff`, not twice with `--json`. Both
// captures are reduced inside that one process and only a compact comparison
// payload comes back - a single capture's own JSON is already ~53MB, so two of
// them could not be held here at once (see
// nettraceParser/Diff/CaptureDiffJsonExporter.cs).

import * as vscode from 'vscode';
import * as child from 'child_process';
import * as crypto from 'crypto';
import * as fs from 'fs';
import * as path from 'path';
import * as readline from 'readline';

import { DotnetInsights } from './dotnetInsights';
import { renderNettraceLoadingHtml } from './NettraceLoadingRenderer';
import { renderNettraceDiffWebview } from './NettraceDiffRenderer';
import {
    parseProgressLine,
    NettraceProgressTracker,
    JSON_READ_RANGE,
    RENDER_RANGE,
    SWAP_RANGE
} from './NettraceProgress';

export async function showNettraceDiff(
    context: vscode.ExtensionContext,
    insights: DotnetInsights,
    baselinePath: string,
    comparisonPath: string
): Promise<void> {
    const panel = vscode.window.createWebviewPanel(
        'dotnetInsightsNettraceDiff',
        `Compare: ${path.basename(baselinePath)} ↔ ${path.basename(comparisonPath)}`,
        vscode.ViewColumn.Active,
        {
            enableScripts: true,
            retainContextWhenHidden: true,
            localResourceRoots: [vscode.Uri.joinPath(context.extensionUri, 'media')]
        }
    );

    // Same two-document approach the single-capture editor uses: a live
    // placeholder exists immediately so postMessage has somewhere to land,
    // then the whole document is replaced once the payload arrives.
    panel.webview.html = renderNettraceLoadingHtml(
        `${path.basename(baselinePath)} ↔ ${path.basename(comparisonPath)}`,
        panel.webview,
        context.extensionUri
    );

    let disposed = false;
    let spawnedProcess: child.ChildProcess | undefined;
    const tracker = new NettraceProgressTracker();

    const postProgress = (update: { percent: number; label: string }) => {
        if (disposed) {
            return;
        }

        panel.webview.postMessage({ type: 'nettraceProgress', percent: update.percent, label: update.label });
    };

    // A message posted before the placeholder document has run its own script
    // is silently dropped with no VS Code-side buffering, so the webview asks
    // for the current state once it is ready.
    const readySubscription = panel.webview.onDidReceiveMessage((message) => {
        if (message && message.type === 'nettraceLoadingReady') {
            postProgress(tracker.current);
        }
    });

    panel.onDidDispose(() => {
        disposed = true;
        readySubscription.dispose();
        spawnedProcess?.kill();
    });

    const diff = await runNettraceDiff(
        insights,
        baselinePath,
        comparisonPath,
        tracker,
        postProgress,
        (proc) => { spawnedProcess = proc; }
    );

    if (disposed) {
        return;
    }

    if (diff === null) {
        panel.webview.postMessage({
            type: 'nettraceError',
            message: 'Failed to compare the two captures. See the dotnet-insights output for details.'
        });
        return;
    }

    postProgress(tracker.recordHostStage(1, JSON_READ_RANGE, 'Reading results'));
    postProgress(tracker.recordHostStage(0, RENDER_RANGE, 'Rendering comparison'));

    // These stages are synchronous and block the extension host's own event
    // loop, so without a yield the message posted just above would not reach
    // the webview until after they finished.
    await new Promise(resolve => setImmediate(resolve));

    const html = renderNettraceDiffWebview(panel.webview, context.extensionUri, diff);

    postProgress(tracker.recordHostStage(1, RENDER_RANGE, 'Rendering comparison'));
    postProgress(tracker.recordHostStage(0, SWAP_RANGE, 'Displaying comparison'));
    await new Promise(resolve => setImmediate(resolve));

    if (!disposed) {
        panel.webview.html = html;
    }
}

function runNettraceDiff(
    insights: DotnetInsights,
    baselinePath: string,
    comparisonPath: string,
    tracker: NettraceProgressTracker,
    postProgress: (update: { percent: number; label: string }) => void,
    onProcessSpawned: (proc: child.ChildProcess) => void
): Promise<any | null> {
    return new Promise((resolve) => {
        fs.mkdirSync(insights.nettraceParserOutputPath, { recursive: true });

        const id = crypto.randomBytes(16).toString("hex");
        const jsonOutputPath = path.join(insights.nettraceParserOutputPath, `${id}-diff.json`);

        const proc = child.spawn(insights.nettraceParserPath, [
            "--diff", baselinePath, comparisonPath, "--json", jsonOutputPath
        ]);

        onProcessSpawned(proc);

        proc.stdout?.resume();

        let stderrTail = "";

        const stderrLines = readline.createInterface({ input: proc.stderr! });
        stderrLines.on('line', (line: string) => {
            const progress = parseProgressLine(line);

            if (progress !== null) {
                // The child reports 0-100 across BOTH captures (each owns half
                // - see Program.cs's BuildProfileForDiff), so the host-side
                // mapping is identical to the single-capture case.
                postProgress(tracker.recordChildPercent(progress.percent, progress.label));
                return;
            }

            if (stderrTail.length < 8192) {
                stderrTail += line + "\n";
            }
        });

        proc.on('error', (error: Error) => {
            insights.outputChannel.appendLine(`nettraceParser --diff failed to start: ${error.message}`);
            resolve(null);
        });

        proc.on('close', (exitCode: number) => {
            if (exitCode !== 0) {
                insights.outputChannel.appendLine(`nettraceParser --diff exited with ${exitCode}: ${stderrTail}`);
                resolve(null);
                return;
            }

            try {
                // No ticks sidecar and no streaming reader needed: the diff
                // payload is a few hundred KB, not tens of megabytes.
                const parsed = JSON.parse(fs.readFileSync(jsonOutputPath).toString());
                resolve(parsed);
            } catch (error) {
                insights.outputChannel.appendLine(`Failed to read diff payload: ${error}`);
                resolve(null);
            } finally {
                try {
                    fs.unlinkSync(jsonOutputPath);
                } catch {
                    // Best effort - a leftover temp file is not worth failing the view over.
                }
            }
        });
    });
}

// Resolves the two captures to compare. VS Code hands an explorer multi-select
// straight to the command, so the common path needs no prompting at all; the
// palette path falls back to two pickers.
export async function pickCapturesToDiff(
    contextUri: vscode.Uri | undefined,
    selectedUris: vscode.Uri[] | undefined
): Promise<{ baseline: string; comparison: string } | null> {
    if (selectedUris && selectedUris.length >= 2) {
        if (selectedUris.length > 2) {
            vscode.window.showWarningMessage(`Comparing the first two of ${selectedUris.length} selected captures.`);
        }

        // Explorer selection order is not guaranteed to be click order, so
        // the older file is treated as the baseline - which is what "compare
        // against before" means almost every time.
        const sorted = [...selectedUris].slice(0, 2).sort((left, right) => {
            return fs.statSync(left.fsPath).mtimeMs - fs.statSync(right.fsPath).mtimeMs;
        });

        return { baseline: sorted[0].fsPath, comparison: sorted[1].fsPath };
    }

    const baselineUri = contextUri ?? (await pickSingleCapture('Select the BASELINE capture'));
    if (!baselineUri) {
        return null;
    }

    const comparisonUri = await pickSingleCapture('Select the capture to COMPARE against the baseline');
    if (!comparisonUri) {
        return null;
    }

    return { baseline: baselineUri.fsPath, comparison: comparisonUri.fsPath };
}

async function pickSingleCapture(title: string): Promise<vscode.Uri | undefined> {
    const picked = await vscode.window.showOpenDialog({
        canSelectMany: false,
        openLabel: 'Select',
        title: title,
        filters: { 'nettrace captures': ['nettrace'] }
    });

    return picked && picked.length > 0 ? picked[0] : undefined;
}
