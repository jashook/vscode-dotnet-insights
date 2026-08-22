import * as vscode from 'vscode';

import { escapeJsonForInlineScript, getNonce, mediaWebviewUri } from "./GcSnapshotRenderer";
import { renderRankedTableHeader } from "./GcDetailTableRenderer";

// Renders the .gcdump (GC heap snapshot) webview: three views over one heap
// snapshot, all of them driven from nettraceParser's `--gcdump --json` output
// (see nettraceParser/GcDump/GcDumpJsonExporter.cs, which is the contract for
// every field name read here and in media/gcDumpView.js).
//
// WHY THIS IS NOT GcSnapshotRenderer. That renderer is shared by the two
// static-GC-snapshot sources (.gcinfo and .nettrace) precisely because they
// produce the SAME data shape - a series of GC events over time, rendered as
// charts and a per-GC table. A .gcdump has no time axis and no GCs at all; it
// is a single instant, and every one of its views is a ranked table over the
// object graph. Extending the shared renderer to cover it would mean branching
// almost every line of it on a source that shares none of its data, which is
// the opposite of what that sharing is for.
//
// WHAT IS SHARED, AND WHY IT HAS TO BE EXACT. Every view here is the same
// ranked-table-with-an-inline-stack-tree component the Profile, Exceptions and
// Contention views already use: renderRankedTableHeader (GcDetailTableRenderer.ts)
// for the header, media/rankedTable.js for click-to-sort and row hiding, and
// snapshot.css's .detailTable/.cpuHotMethodsTable/.callerTreeInner grid for the
// layout. That grid is positional - column 1 is the narrow row-hide column,
// column 2 is the left-aligned wrapping name column, columns 3+ are numeric -
// and this view originally emitted its own column order into it, putting the
// type name where the rules expect a number. The result was not subtly off: a
// single 567-character generic type name (ordinary in a real heap) blew the
// unwrapped first column out to several thousand pixels and pushed every
// numeric column off-screen, while the "Objects" count sat in the column sized
// to hold long names. The tree views had it worse - they built bare <table>
// elements outside any .detailTable wrapper, so they inherited no table styling
// at all and rendered center-aligned, which silently erased the indentation
// that WAS the tree.
//
// The payload is embedded as a <script type="application/json"> block, the same
// mechanism GcSnapshotRenderer.ts uses. Unlike the .nettrace path there is no
// binary sidecar to fetch: everything here is aggregated to the TYPE level in
// C# before it is written (see nettraceParser/GcDump/GcDumpAnalysis.cs), so
// the payload is a few thousand rows whether the heap held eight thousand
// objects or ten million.
export function renderGcDumpWebview(fileName: string, webview: vscode.Webview, extensionUri: vscode.Uri, gcDumpData: any, failureText: string | null = null): string {
    const nonce = getNonce();

    const styleResetUri = mediaWebviewUri(webview, extensionUri, 'reset.css');
    const styleVSCodeUri = mediaWebviewUri(webview, extensionUri, 'vscode.css');
    const styleMainUri = mediaWebviewUri(webview, extensionUri, 'main.css');
    const styleSnapshotUri = mediaWebviewUri(webview, extensionUri, 'snapshot.css');
    const styleGcDumpUri = mediaWebviewUri(webview, extensionUri, 'gcDump.css');
    const rankedTableScriptUri = mediaWebviewUri(webview, extensionUri, 'rankedTable.js');
    const scriptUri = mediaWebviewUri(webview, extensionUri, 'gcDumpView.js');

    if (gcDumpData === null || gcDumpData === undefined) {
        return renderFailureHtml(fileName, styleResetUri, styleVSCodeUri, failureText);
    }

    const payloadJson = escapeJsonForInlineScript(JSON.stringify(gcDumpData));

    const summary = gcDumpData["summary"] ?? {};
    const metadata = gcDumpData["metadata"] ?? {};

    // Column ORDER here is the contract with media/gcDumpView.js, which holds a
    // parallel array of sort keys and cell formatters and checks the two agree
    // at render time (see assertColumnContract there). The type column is
    // always first and always the name column - renderRankedTableHeader puts
    // the hide column ahead of it, which is what makes it column 2 for
    // snapshot.css's purposes.
    const censusColumns: ReadonlyArray<[string, string]> = [
        ["Type", "text"],
        ["Objects", "number"],
        ["Bytes", "number"],
        ["% of Heap", "number"],
        ["Retained Bytes", "number"],
        ["Largest Instance", "number"]
    ];

    const retainedColumns: ReadonlyArray<[string, string]> = [
        ["Type", "text"],
        ["Retained Bytes", "number"],
        ["% of Heap", "number"],
        ["Largest Instance", "number"],
        ["Objects", "number"],
        ["Own Bytes", "number"]
    ];

    const referenceColumns: ReadonlyArray<[string, string]> = [
        ["Type", "text"],
        ["References", "number"],
        ["Bytes Referenced", "number"]
    ];

    return /* html */`
    <!DOCTYPE html>
    <html lang="en">
    <head>
        <meta charset="UTF-8">

        <!-- Matches the policy GcSnapshotRenderer.ts's own document uses;
             kept identical rather than tightened here so both webviews
             behave the same way. -->
        <meta http-equiv="Content-Security-Policy"
        content="default-src * vscode-resource: https: 'unsafe-inline' 'unsafe-eval';
        script-src vscode-webview-resource: https: 'unsafe-inline' 'unsafe-eval';
        style-src vscode-webview-resource: https: 'unsafe-inline';
        img-src vscode-resource: https:;
        connect-src vscode-resource: https: http:;">

        <meta name="viewport" content="width=device-width, initial-scale=1.0">
        <link href="${styleResetUri}" rel="stylesheet" />
        <link href="${styleVSCodeUri}" rel="stylesheet" />
        <link href="${styleMainUri}" rel="stylesheet" />
        <link href="${styleSnapshotUri}" rel="stylesheet" />
        <link href="${styleGcDumpUri}" rel="stylesheet" />
        <title>${escapeHtml(fileName)}</title>
    </head>
    <body>
        <script type="application/json" id="gcDumpJson">${payloadJson}</script>

        <div class="viewTabBar">
            <button class="viewNavButton active" data-view="census">Type Census</button>
            <button class="viewNavButton" data-view="retained">Retained Size</button>
            <button class="viewNavButton" data-view="references">References</button>
        </div>

        <h2 class="divider">${escapeHtml(describeSource(fileName, metadata))}</h2>

        ${renderSummaryTiles(summary, metadata)}

        <div id="view-census" class="viewPanel active">
            <p class="gcDumpViewBlurb">
                Every type on the heap, by the bytes its own objects occupy.
                This is what a heap is made of; the Retained Size view is what
                is keeping it alive. Expand a row to see what holds instances
                of that type alive, one reference per level, all the way to a
                GC root.
            </p>
            ${renderRankedTable("census", censusColumns)}
        </div>

        <div id="view-retained" class="viewPanel">
            <p class="gcDumpViewBlurb">
                What each type <em>holds onto</em>: the bytes that would be
                freed if every instance of it became unreachable, counted once
                each. A small type high on this list is the shape a leak
                usually takes &mdash; a cache or a list whose own object is
                tiny but which retains everything it points at. Retention paths
                are traced for the heaviest types on this list, so the rows
                that expand are near the top of it.
            </p>
            ${renderRankedTable("retained", retainedColumns)}
        </div>

        <div id="view-references" class="viewPanel">
            <p class="gcDumpViewBlurb">
                The reference graph collapsed by type. Expand a row to walk
                outward (what it points at) or inward (what points at it) to
                any depth.
            </p>
            <div class="gcDumpFilterRow">
                <label class="gcDumpSelectLabel" for="referenceDirection">Direction</label>
                <select id="referenceDirection" class="gcDumpSelect">
                    <option value="outgoing">References to (outgoing)</option>
                    <option value="incoming">Referenced by (incoming)</option>
                </select>
            </div>
            ${renderRankedTable("reference", referenceColumns)}
        </div>

        <script nonce="${nonce}" src="${rankedTableScriptUri}"></script>
        <script nonce="${nonce}" src="${scriptUri}"></script>
    </body>
    </html>`;
}

// One ranked table, identical in structure across all three views and
// identical to the Profile/Exceptions/Contention tables: a filter row, a
// hide-status bar that stays hidden until something is actually hidden (the
// same .allocationZoomStatus idiom every other table on every other view uses
// for this), and a .detailTable.cpuHotMethodsTable WRAPPER around a bare
// <table>.
//
// That wrapper is load-bearing and easy to get wrong: snapshot.css's column
// rules select ".cpuHotMethodsTable > table > tbody > tr > th", so putting the
// class on the <table> itself misses every one of them and the numeric columns
// stop lining up with the trees nested inside the rows.
//
// Rows are rendered client-side (gcDumpView.js) rather than here: a heap
// carries thousands of types, the table is capped at the first few hundred, and
// filtering/sorting/hiding all re-derive that cap from the full array.
function renderRankedTable(viewId: string, columns: ReadonlyArray<[string, string]>): string {
    return `
            <div class="gcDumpFilterRow">
                <input type="text" id="${viewId}Filter" class="gcDumpFilterInput" placeholder="Filter types…">
                <span class="gcDumpFilterCount" id="${viewId}FilterCount"></span>
            </div>
            <div class="allocationZoomStatus" id="${viewId}HideStatus" style="display:none">
                <span class="allocationZoomStatusLabel" id="${viewId}HideStatusLabel"></span>
                <button class="resetZoomButton" id="${viewId}ShowAllBtn">Show all</button>
            </div>
            <div class="detailTable cpuHotMethodsTable">
                <table id="${viewId}Table">${renderRankedTableHeader(columns)}<tbody id="${viewId}TableBody"></tbody></table>
            </div>`;
}

// dotnet-gcdump does not populate ProcessName/MachineName/TimeCollected when
// it writes a dump - verified against real captures, where all three come back
// empty while the tagged CreationTool field immediately after them reads
// correctly (so this is genuinely absent data, not a misparse). The file name
// is therefore the only reliable identifier, and the process name is only used
// when something actually put one there (a PerfView-written dump does).
function describeSource(fileName: string, metadata: any): string {
    const processName = metadata["processName"];

    if (typeof processName === "string" && processName.length > 0) {
        const processId = metadata["processId"];
        return processId ? `${processName} (pid ${processId})` : processName;
    }

    return fileName;
}

function renderSummaryTiles(summary: any, metadata: any): string {
    const totalBytes = Number(summary["totalBytes"] ?? 0);
    const totalObjects = Number(summary["totalObjects"] ?? 0);
    const typeCount = Number(summary["typeCount"] ?? 0);
    const referenceCount = Number(summary["referenceCount"] ?? 0);
    const unreachableObjects = Number(summary["unreachableObjects"] ?? 0);
    const unreachableBytes = Number(summary["unreachableBytes"] ?? 0);

    let tiles = "";
    tiles += renderTile("Heap Size", formatBytes(totalBytes), formatNumber(totalBytes) + " bytes");
    tiles += renderTile("Objects", formatNumber(totalObjects), "");
    tiles += renderTile("Types", formatNumber(typeCount), "");
    tiles += renderTile("References", formatNumber(referenceCount), "");

    // Real dotnet-gcdump captures routinely contain objects nothing references
    // and no root reaches (7-9% of nodes on every file checked here, and the
    // overwhelming majority on a dump taken right after a collect). Their
    // retained sizes are necessarily 0, so this is surfaced rather than hidden
    // - a reader comparing the Retained column against the heap size deserves
    // to know how much of it was unreachable to begin with. Reported in BYTES
    // as well as objects: the two can differ enormously (98.5% of objects but
    // 99.2% of bytes on one real capture), and bytes is the figure that
    // explains a Retained column that does not add up to the heap.
    if (unreachableObjects > 0) {
        const percentObjects = totalObjects > 0 ? (unreachableObjects / totalObjects) * 100 : 0;
        const percentBytes = totalBytes > 0 ? (unreachableBytes / totalBytes) * 100 : 0;
        const sublabel = unreachableBytes > 0
            ? `${percentObjects.toFixed(1)}% of objects, ${percentBytes.toFixed(1)}% of bytes`
            : `${percentObjects.toFixed(1)}% of objects`;
        tiles += renderTile("Unrooted", formatNumber(unreachableObjects), sublabel);
    }

    // A sampled dump's counts are estimates. Saying so in a tile is the
    // difference between a reader trusting a number and a reader trusting a
    // number they should not.
    const sampledNote = metadata["isSampled"]
        ? `<div class="gcDumpSampledWarning">This dump was <strong>sampled</strong> (count &times;${Number(metadata["countMultiplier"] ?? 1).toFixed(2)}, size &times;${Number(metadata["sizeMultiplier"] ?? 1).toFixed(2)}). Counts and sizes are estimates.</div>`
        : ``;

    // Same reasoning, different missing thing: when the source could not read
    // thread stack roots (a macOS core dump - see
    // nettraceParser/CoreDump/CoreDumpHeapGraphBuilder.cs), objects held only
    // by a running frame are reported unrooted. Without this note that is
    // indistinguishable from those objects genuinely being garbage, which is
    // precisely the misreading the Unrooted tile above would otherwise invite.
    const stackRootsNote = metadata["stackRootsOmitted"]
        ? `<div class="gcDumpSampledWarning">Thread <strong>stack roots</strong> could not be read from this dump. Handles, statics and the finalizer queue are all present; anything held <em>only</em> by a running stack frame is counted as unrooted below.</div>`
        : ``;

    return `<div class="summaryTileRow">${tiles}</div>${sampledNote}${stackRootsNote}`;
}

function renderTile(label: string, value: string, sublabel: string): string {
    const sub = sublabel.length > 0 ? `<div class="summaryTileSublabel">${escapeHtml(sublabel)}</div>` : ``;
    return `<div class="summaryTile">
        <div class="summaryTileLabel">${escapeHtml(label)}</div>
        <div class="summaryTileValue">${escapeHtml(value)}</div>
        ${sub}
    </div>`;
}

// The parser's own explanation goes ON THE PAGE, not only into the output
// channel. A core dump makes this the difference between an actionable answer
// and a dead end: the most common failure is a perfectly good Linux dump opened
// on a Mac, where the fix is to convert it elsewhere - and nothing about
// "unable to read this file" suggests that, so the reader goes looking for a
// corrupt download instead.
//
// Rendered as preformatted text because the message is a small report (what the
// dump is, what this host is, what to run), and reflowing it as a paragraph
// would destroy the alignment that makes it scannable.
function renderFailureHtml(fileName: string, styleResetUri: vscode.Uri, styleVSCodeUri: vscode.Uri, failureText: string | null): string {
    const detail = failureText !== null && failureText.length > 0
        ? `<pre class="gcDumpFailureDetail">${escapeHtml(failureText)}</pre>`
        : `<p>See the &quot;Dotnet Insights&quot; output channel for details.</p>`;

    return /* html */`
    <!DOCTYPE html>
    <html lang="en">
    <head>
        <meta charset="UTF-8">
        <link href="${styleResetUri}" rel="stylesheet" />
        <link href="${styleVSCodeUri}" rel="stylesheet" />
        <style>
            .gcDumpFailureDetail {
                background-color: rgba(128, 128, 128, 0.12);
                border-left: 3px solid var(--vscode-editorError-foreground, rgba(200, 60, 60, 0.9));
                margin: 1em 0;
                overflow-x: auto;
                padding: 0.8em 1em;
                white-space: pre-wrap;
            }
        </style>
        <title>${escapeHtml(fileName)}</title>
    </head>
    <body>
        <h2>Unable to read ${escapeHtml(fileName)}</h2>
        ${detail}
    </body>
    </html>`;
}

function formatNumber(value: number): string {
    return value.toLocaleString("en-US");
}

function formatBytes(bytes: number): string {
    if (bytes < 1024) {
        return `${bytes} B`;
    }

    const units = ["KB", "MB", "GB", "TB"];
    let value = bytes / 1024;
    let unitIndex = 0;

    while (value >= 1024 && unitIndex < units.length - 1) {
        value /= 1024;
        ++unitIndex;
    }

    return `${value.toFixed(value >= 100 ? 0 : 1)} ${units[unitIndex]}`;
}

// Type names come straight out of the dumped process and are attacker-
// influenced in the sense that matters here: a type name can legitimately
// contain '<' and '>' (every generic does), so interpolating one unescaped
// into this document would break the markup on completely ordinary input.
function escapeHtml(value: string): string {
    return String(value)
        .replace(/&/g, "&amp;")
        .replace(/</g, "&lt;")
        .replace(/>/g, "&gt;")
        .replace(/"/g, "&quot;")
        .replace(/'/g, "&#39;");
}
