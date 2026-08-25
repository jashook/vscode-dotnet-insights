////////////////////////////////////////////////////////////////////////////////
// Module: PerfInsightsEditor.ts
//
// Notes:
// Opens a Linux `perf record` capture as a rendered profile. Shells out to
// perfParser, which reads the perf.data and writes the SAME JSON shape
// nettraceParser writes for a .NET capture - so the view is rendered by
// dotnetInsights' own CpuProfileRenderer, copied in verbatim by
// scripts/sync-shared.js.
//
// Two behaviours here are copied deliberately from the .NET extension's
// hard-won experience rather than reinvented, both recorded in the repo's
// CLAUDE.md:
//
//   - The webview HTML is assigned TWICE: a loading placeholder synchronously,
//     so there is a live document able to receive messages at all, then the
//     real content once parsing finishes. Parsing a multi-GB capture is not
//     instant and a blank tab reads as a hang.
//
//   - The placeholder panel is never disposed and no `workbench.action.close*`
//     command is ever run. That is what closed users' entire editor groups in
//     the .NET extension (issue #99).
////////////////////////////////////////////////////////////////////////////////

import * as vscode from 'vscode';
import * as path from 'path';
import * as os from 'os';
import * as fs from 'fs';
import { spawn } from 'child_process';

import { renderCpuProfileView } from './shared/CpuProfileRenderer';
import { renderContentionView } from './shared/ContentionRenderer';
import { renderOffCpuView } from './OffCpuRenderer';

class PerfDocument implements vscode.CustomDocument {
    constructor(public readonly uri: vscode.Uri) {
    }

    dispose(): void {
    }
}

export class PerfInsightsEditorProvider implements vscode.CustomReadonlyEditorProvider<PerfDocument> {
    private static readonly viewType = 'perfInsights.edit';

    constructor(private readonly context: vscode.ExtensionContext) {
    }

    public static register(context: vscode.ExtensionContext): vscode.Disposable {
        return vscode.window.registerCustomEditorProvider(
            PerfInsightsEditorProvider.viewType,
            new PerfInsightsEditorProvider(context),
            {
                webviewOptions: { retainContextWhenHidden: true },
                supportsMultipleEditorsPerDocument: false
            });
    }

    public openCustomDocument(uri: vscode.Uri): PerfDocument {
        return new PerfDocument(uri);
    }

    public async resolveCustomEditor(document: PerfDocument, webviewPanel: vscode.WebviewPanel): Promise<void> {
        webviewPanel.webview.options = {
            enableScripts: true,
            localResourceRoots: [vscode.Uri.file(path.join(this.context.extensionPath, 'media'))]
        };

        webviewPanel.webview.html = this.renderLoading(webviewPanel.webview, document.uri.fsPath);

        try {
            const profileJson = await this.runPerfParser(document.uri.fsPath);
            webviewPanel.webview.html = this.renderProfile(webviewPanel.webview, profileJson);
        } catch (error) {
            webviewPanel.webview.html = this.renderError(document.uri.fsPath, String(error));
        }
    }

    // Resolution order matches the .NET extension's own tool-path setting:
    // an explicit user setting wins, then a local development build, then the
    // copy bundled with the extension.
    private resolvePerfParserPath(): string {
        const configured = vscode.workspace.getConfiguration('perf-insights').get<string>('perfParserPath');
        if (configured && configured.length > 0) {
            return configured;
        }

        const developmentBuild = path.join(this.context.extensionPath, '..', 'perfParser', 'bin', 'Debug', 'net10.0', 'perfParser');
        if (fs.existsSync(developmentBuild)) {
            return developmentBuild;
        }

        return path.join(this.context.extensionPath, 'perfParser', 'perfParser');
    }

    private runPerfParser(capturePath: string): Promise<any> {
        const parserPath = this.resolvePerfParserPath();
        const outputPath = path.join(os.tmpdir(), 'perfInsights-' + process.pid + '-' + Date.now() + '.json');

        const args = [capturePath, '--json', outputPath];

        const symbolPath = vscode.workspace.getConfiguration('perf-insights').get<string>('symbolPath');
        if (symbolPath && symbolPath.length > 0) {
            args.push('--symbol-path', symbolPath);
        }

        return new Promise((resolve, reject) => {
            if (!fs.existsSync(parserPath)) {
                reject(new Error('perfParser was not found at ' + parserPath + '. Set perf-insights.perfParserPath.'));
                return;
            }

            const child = spawn(parserPath, args);

            // perfParser writes its timing line and any diagnostics to stderr,
            // the same channel nettraceParser uses. Collected so a failure can
            // report what the tool actually said rather than just an exit code.
            let diagnostics = '';
            child.stderr.on('data', (chunk) => { diagnostics += chunk.toString(); });

            child.on('error', (spawnError) => { reject(spawnError); });

            child.on('close', (exitCode) => {
                if (exitCode !== 0) {
                    reject(new Error('perfParser exited with code ' + exitCode + ': ' + diagnostics.trim()));
                    return;
                }

                try {
                    const contents = fs.readFileSync(outputPath, 'utf8');
                    fs.unlinkSync(outputPath);
                    resolve(JSON.parse(contents));
                } catch (readError) {
                    reject(readError);
                }
            });
        });
    }

    private mediaUri(webview: vscode.Webview, ...parts: string[]): vscode.Uri {
        return webview.asWebviewUri(vscode.Uri.file(path.join(this.context.extensionPath, 'media', ...parts)));
    }

    private renderShell(webview: vscode.Webview, bodyHtml: string, trailingScripts: string): string {
        const nonce = createNonce();
        const styleUri = this.mediaUri(webview, 'shared', 'snapshot.css');

        return `<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="UTF-8">
    <meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src ${webview.cspSource}; script-src 'nonce-${nonce}';">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <link href="${styleUri}" rel="stylesheet">
    <style>
        .perfNav { margin: 18px 0 12px 0; display: flex; gap: 8px; flex-wrap: wrap; }
        .perfNavButton {
            background: transparent; color: inherit; opacity: 0.7;
            border: 1px solid var(--vscode-panel-border, #555); border-radius: 4px;
            padding: 5px 12px; cursor: pointer; font: inherit;
        }
        .perfNavButton:hover { opacity: 1; }
        .perfNavButton.active { opacity: 1; border-color: var(--vscode-textLink-foreground, #4daafc); }
        .viewNote { opacity: 0.75; max-width: 60em; }
        .offCpuFrame { font-family: var(--vscode-editor-font-family, monospace); padding: 1px 0 1px 24px; opacity: 0.9; }
    </style>
    <title>Perf Insights</title>
</head>
<body>
${bodyHtml}
${trailingScripts.replace(/__NONCE__/g, nonce)}
</body>
</html>`;
    }

    private renderLoading(webview: vscode.Webview, capturePath: string): string {
        return this.renderShell(webview, `
    <h2>Reading ${escapeHtml(path.basename(capturePath))}</h2>
    <p>Decoding the capture and resolving symbols&hellip;</p>`, '');
    }

    private renderError(capturePath: string, message: string): string {
        return `<!DOCTYPE html>
<html lang="en"><body>
    <h2>Could not read ${escapeHtml(path.basename(capturePath))}</h2>
    <pre>${escapeHtml(message)}</pre>
</body></html>`;
    }

    private renderProfile(webview: vscode.Webview, profileJson: any): string {
        const cpuProfile = profileJson['cpuProfile'];
        const contentionSummary = profileJson['contentionSummary'];
        const offCpu = profileJson['offCpu'];
        const captureInfo = profileJson['captureInfo'] || {};

        // Both of these are dotnetInsights' own renderers, used verbatim - the
        // JSON perfParser writes is the same JSON nettraceParser writes.
        const hasCpu = cpuProfile && cpuProfile['totalSampleCount'] > 0;
        const cpuHtml = hasCpu
            ? renderCpuProfileView(cpuProfile)
            : '<p>This capture contains no CPU samples. Record with <code>-e cpu-clock -g</code> to profile CPU time.</p>';

        const contentionHtml = contentionSummary
            ? renderContentionView(contentionSummary)
            : '<p>This capture contains no lock contention data. Record with <code>-e syscalls:sys_enter_futex -e syscalls:sys_exit_futex</code>.</p>';

        const offCpuHtml = renderOffCpuView(offCpu);

        const rankedTableUri = this.mediaUri(webview, 'shared', 'rankedTable.js');
        const cpuDrillDownUri = this.mediaUri(webview, 'shared', 'cpuDrillDownStats.js');
        const contentionDrillDownUri = this.mediaUri(webview, 'shared', 'contentionDrillDownStats.js');
        const viewUri = this.mediaUri(webview, 'perfCpuView.js');

        // Which views a capture can show depends entirely on which events were
        // recorded, so the nav is built from what is actually present rather
        // than fixed - an empty tab that never had a chance of holding
        // anything reads as a broken tool.
        const body = `
    ${renderCaptureSummary(captureInfo, profileJson['threads'])}
    <div class="perfNav">
        <button class="perfNavButton active" data-perf-view="view-cpu">CPU${hasCpu ? '' : ' (none)'}</button>
        <button class="perfNavButton" data-perf-view="view-contention">Lock contention${contentionSummary ? '' : ' (none)'}</button>
        <button class="perfNavButton" data-perf-view="view-offcpu">Off-CPU${offCpu ? '' : ' (none)'}</button>
    </div>
    <div id="view-cpu" class="perfViewPanel">${cpuHtml}</div>
    <div id="view-contention" class="perfViewPanel" style="display:none">${contentionHtml}</div>
    <div id="view-offcpu" class="perfViewPanel" style="display:none">${offCpuHtml}</div>`;

        const scripts = `
    <script nonce="__NONCE__">
        window.perfProfileData = ${JSON.stringify({
            methodNames: cpuProfile ? cpuProfile['methodNames'] : [],
            hotMethodDrillDown: cpuProfile ? cpuProfile['hotMethodDrillDown'] : [],
            totalSampleCount: cpuProfile ? cpuProfile['totalSampleCount'] : 0
        }).replace(/</g, '\\u003c')};
        window.perfContentionData = ${JSON.stringify({
            methodNames: contentionSummary ? contentionSummary['methodNames'] : [],
            siteDrillDown: contentionSummary ? contentionSummary['siteDrillDown'] : [],
            totalContentionWaitMSec: contentionSummary ? contentionSummary['totalContentionWaitMSec'] : 0
        }).replace(/</g, '\\u003c')};
    </script>
    <script nonce="__NONCE__" src="${rankedTableUri}"></script>
    <script nonce="__NONCE__" src="${cpuDrillDownUri}"></script>
    <script nonce="__NONCE__" src="${contentionDrillDownUri}"></script>
    <script nonce="__NONCE__" src="${viewUri}"></script>`;

        return this.renderShell(webview, body, scripts);
    }
}

function renderCaptureSummary(captureInfo: any, threads: any[]): string {
    const rows: string[] = [];

    const push = (label: string, value: any) => {
        if (value !== undefined && value !== null && String(value).length > 0) {
            rows.push(`<tr><td>${escapeHtml(label)}</td><td>${escapeHtml(String(value))}</td></tr>`);
        }
    };

    push('Host', captureInfo['hostName']);
    push('Architecture', captureInfo['architecture']);
    push('OS release', captureInfo['osRelease']);
    push('perf version', captureInfo['perfVersion']);
    push('Captured on', captureInfo['capturedOn']);
    push('Command line', captureInfo['commandLine']);
    // Worth stating: in perf-script mode the symbols were resolved by perf on
    // the machine that recorded, which is strictly better than anything this
    // tool can do from a different machine - and explains why kernel frames
    // are named here without any kallsyms file.
    push('Symbols', captureInfo['symbolSource']);
    push('Stacks from', captureInfo['stackSource']);
    push('Samples', Number(captureInfo['sampleCount'] || 0).toLocaleString());
    push('Distinct stacks', Number(captureInfo['distinctStackCount'] || 0).toLocaleString());
    push('Modules with symbols', captureInfo['modulesWithSymbols']);
    push('Modules without symbols', captureInfo['modulesWithoutSymbols']);

    let threadRows = '';
    if (threads && threads.length > 0) {
        for (const thread of threads.slice(0, 25)) {
            threadRows += `<tr><td>${escapeHtml(String(thread['name']))}</td>` +
                `<td>${escapeHtml(String(thread['threadId']))}</td>` +
                `<td>${Number(thread['sampleCount']).toLocaleString()}</td></tr>`;
        }
    }

    return `
    <h2>Capture</h2>
    <div class="detailTable"><table>${rows.join('')}</table></div>
    ${threadRows.length > 0 ? `<h2>Threads by sample count</h2>
    <div class="detailTable"><table>
        <tr><th>Thread</th><th>Tid</th><th>Samples</th></tr>
        ${threadRows}
    </table></div>` : ''}`;
}

function escapeHtml(value: string): string {
    return value
        .replace(/&/g, '&amp;')
        .replace(/</g, '&lt;')
        .replace(/>/g, '&gt;')
        .replace(/"/g, '&quot;');
}

function createNonce(): string {
    const alphabet = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789';
    let nonce = '';
    for (let index = 0; index < 32; ++index) {
        nonce += alphabet.charAt(Math.floor(Math.random() * alphabet.length));
    }

    return nonce;
}
