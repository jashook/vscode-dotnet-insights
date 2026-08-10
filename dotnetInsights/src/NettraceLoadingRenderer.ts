import * as vscode from 'vscode';

import { escapeJsonForInlineScript, getNonce, mediaWebviewUri } from "./GcSnapshotRenderer";

// Renders the lightweight placeholder shown IMMEDIATELY when a .nettrace
// file is opened - before nettraceParser has even been spawned, let alone
// finished - so there's a live webview document able to receive postMessage
// progress updates while parsing is still in flight (see
// DotnetInsightsNettraceEditor.ts's own resolveCustomEditor, which used to
// only ever assign webview.html once, after the ENTIRE pipeline - spawn,
// parse, JSON write, JSON read-back, HTML render - had already finished).
//
// Deliberately its own tiny document (own CSP/nonce, own
// media/nettraceLoadingView.js + media/nettraceLoading.css) rather than an
// overlay injected into the real snapshot document: media/snapshotGcStats.js
// calls acquireVsCodeApi() itself, which throws if called a second time in
// the same document - swapping the whole webview.html over once parsing
// completes (the same "assign webview.html once" pattern
// GcSnapshotRenderer.ts's own caller already uses) sidesteps that entirely.
export function renderNettraceLoadingHtml(fileName: string, webview: vscode.Webview, extensionUri: vscode.Uri): string {
    const nonce = getNonce();

    const styleResetUri = mediaWebviewUri(webview, extensionUri, 'reset.css');
    const styleVSCodeUri = mediaWebviewUri(webview, extensionUri, 'vscode.css');
    const loadingStyleUri = mediaWebviewUri(webview, extensionUri, 'nettraceLoading.css');
    const scriptUri = mediaWebviewUri(webview, extensionUri, 'nettraceLoadingView.js');

    const escapedFileName = escapeJsonForInlineScript(JSON.stringify(fileName));

    return /* html */`
    <!DOCTYPE html>
    <html lang="en">
    <head>
        <meta charset="UTF-8">

        <!-- Same permissive policy GcSnapshotRenderer.ts's own real document
             uses (see that file's own comment on why the stricter, nonce-
             enforced policy is commented out there) - kept identical here
             so this placeholder behaves the same way for no reason to
             diverge. -->
        <meta http-equiv="Content-Security-Policy"
        content="default-src * vscode-resource: https: 'unsafe-inline' 'unsafe-eval';
        script-src vscode-webview-resource: https: 'unsafe-inline' 'unsafe-eval';
        style-src vscode-webview-resource: https: 'unsafe-inline';
        img-src vscode-resource: https:;
        connect-src vscode-resource: https: http:;">

        <meta name="viewport" content="width=device-width, initial-scale=1.0">
        <link href="${styleResetUri}" rel="stylesheet" />
        <link href="${styleVSCodeUri}" rel="stylesheet" />
        <link href="${loadingStyleUri}" rel="stylesheet" />
        <title>Parsing trace…</title>
    </head>
    <body>
        <div class="nettraceLoadingRoot">
            <div class="nettraceLoadingFileName" id="nettraceLoadingFileName"></div>

            <!-- Ring (a "pie chart" wedge growing to fill the circle) is
                 the ring div's own conic-gradient background, set directly
                 from JS (nettraceLoadingView.js) rather than via a CSS
                 custom property + calc() - simpler and doesn't rely on
                 <number>-in-calc() support. .nettraceLoadingPieInner is a
                 SIBLING of the ring, not nested inside it, specifically so
                 the ring's own indeterminate spin animation (see
                 nettraceLoading.css) doesn't rotate the percent text along
                 with it - a CSS transform on an element rotates its whole
                 rendered subtree, absolutely-positioned descendants
                 included, so nesting would make the text spin too. Starts
                 indeterminate (see nettraceLoadingView.js's own comment) -
                 only switches to a real percent once the first
                 nettraceProgress message actually arrives, so a stale,
                 already-downloaded nettraceParser binary that predates this
                 feature (and so never emits any PROGRESS line at all - see
                 CLAUDE.md's "stale-cache trap") still shows motion instead
                 of a ring frozen at 0%. -->
            <div class="nettraceLoadingPieWrap">
                <div class="nettraceLoadingPieRing nettraceLoadingPieIndeterminate" id="nettraceLoadingPieRing"></div>
                <div class="nettraceLoadingPieInner">
                    <span class="nettraceLoadingPiePercent" id="nettraceLoadingPercent"></span>
                </div>
            </div>

            <div class="nettraceLoadingLabel" id="nettraceLoadingLabel">Starting…</div>

            <div class="nettraceLoadingError" id="nettraceLoadingError" style="display:none"></div>
        </div>

        <script type="application/json" id="nettraceLoadingFileNameJson">${escapedFileName}</script>
        <script nonce="${nonce}" src="${scriptUri}"></script>
    </body>
    </html>`;
}
