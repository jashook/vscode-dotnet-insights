import * as vscode from 'vscode';

import { escapeJsonForInlineScript, getNonce, mediaWebviewUri } from "./GcSnapshotRenderer";
import { renderSortableTableHeader } from "./GcDetailTableRenderer";

// Renders the .gcdump (GC heap snapshot) webview: four views over one heap
// snapshot, all of them driven from nettraceParser's `--gcdump --json` output
// (see nettraceParser/GcDump/GcDumpJsonExporter.cs, which is the contract for
// every field name read here and in media/gcDumpView.js).
//
// WHY THIS IS NOT GcSnapshotRenderer. That renderer is shared by the two
// static-GC-snapshot sources (.gcinfo and .nettrace) precisely because they
// produce the SAME data shape - a series of GC events over time, rendered as
// charts and a per-GC table. A .gcdump has no time axis and no GCs at all; it
// is a single instant, and every one of its views is a ranked table or a tree
// over the object graph. Extending the shared renderer to cover it would mean
// branching almost every line of it on a source that shares none of its data,
// which is the opposite of what that sharing is for.
//
// What IS shared, and deliberately: renderSortableTableHeader (from
// GcDetailTableRenderer.ts) so sorting behaves identically, and the
// .cpuHotMethodsTable / .callerTreeInner styling in media/snapshot.css so the
// ranked tables and nested trees line up on the same column grid - including
// its percentage-width rules, which CLAUDE.md documents at length and which
// this file's tables obey rather than re-deriving.
//
// The payload is embedded as <script type="application/json"> blocks, the same
// mechanism GcSnapshotRenderer.ts uses. Unlike the .nettrace path there is no
// binary sidecar to fetch: everything here is aggregated to the TYPE level in
// C# before it is written (see nettraceParser/GcDump/GcDumpAnalysis.cs), so
// the payload is a few thousand rows whether the heap held eight thousand
// objects or ten million.
export function renderGcDumpWebview(fileName: string, webview: vscode.Webview, extensionUri: vscode.Uri, gcDumpData: any): string {
    const nonce = getNonce();

    const styleResetUri = mediaWebviewUri(webview, extensionUri, 'reset.css');
    const styleVSCodeUri = mediaWebviewUri(webview, extensionUri, 'vscode.css');
    const styleMainUri = mediaWebviewUri(webview, extensionUri, 'main.css');
    const styleSnapshotUri = mediaWebviewUri(webview, extensionUri, 'snapshot.css');
    const styleGcDumpUri = mediaWebviewUri(webview, extensionUri, 'gcDump.css');
    const scriptUri = mediaWebviewUri(webview, extensionUri, 'gcDumpView.js');

    if (gcDumpData === null || gcDumpData === undefined) {
        return renderFailureHtml(fileName, styleResetUri, styleVSCodeUri);
    }

    const payloadJson = escapeJsonForInlineScript(JSON.stringify(gcDumpData));

    const summary = gcDumpData["summary"] ?? {};
    const metadata = gcDumpData["metadata"] ?? {};

    const censusColumns: ReadonlyArray<[string, string]> = [
        ["Type", "string"],
        ["Objects", "number"],
        ["Bytes", "number"],
        ["% of Heap", "number"],
        ["Retained Bytes", "number"],
        ["Largest Instance", "number"]
    ];

    const retainedColumns: ReadonlyArray<[string, string]> = [
        ["Type", "string"],
        ["Retained Bytes", "number"],
        ["% of Heap", "number"],
        ["Largest Instance", "number"],
        ["Objects", "number"],
        ["Own Bytes", "number"]
    ];

    const censusHeader = renderSortableTableHeader(censusColumns);
    const retainedHeader = renderSortableTableHeader(retainedColumns);

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
            <button class="viewNavButton" data-view="roots">Paths to Root</button>
            <button class="viewNavButton" data-view="references">References</button>
        </div>

        <h2 class="divider">${escapeHtml(describeSource(fileName, metadata))}</h2>

        ${renderSummaryTiles(summary, metadata)}

        <div id="view-census" class="viewPanel active">
            <p class="gcDumpViewBlurb">
                Every type on the heap, by the bytes its own objects occupy.
                This is what a heap is made of; the Retained Size view is what
                is keeping it alive.
            </p>
            <div class="gcDumpFilterRow">
                <input type="text" id="censusFilter" class="gcDumpFilterInput" placeholder="Filter types…">
                <span class="gcDumpFilterCount" id="censusFilterCount"></span>
            </div>
            <!-- Structure copied from CpuProfileRenderer.ts's own hot-methods
                 table exactly: a .detailTable.cpuHotMethodsTable WRAPPER around
                 a bare table element whose header row is a plain
                 tr.tableHeader. snapshot.css's column-width rules select
                 ".cpuHotMethodsTable > table > tbody > tr > th", so putting the
                 class on the table itself (or wrapping the header in a thead)
                 silently misses every one of them and the numeric columns stop
                 lining up. -->
            <div class="detailTable cpuHotMethodsTable">
                <table id="censusTable">${censusHeader}<tbody id="censusTableBody"></tbody></table>
            </div>
        </div>

        <div id="view-retained" class="viewPanel">
            <p class="gcDumpViewBlurb">
                What each type <em>holds onto</em>: the bytes that would be
                freed if every instance of it became unreachable, counted once
                each. A small type high on this list is the shape a leak
                usually takes &mdash; a cache or a list whose own object is
                tiny but which retains everything it points at.
            </p>
            <div class="gcDumpFilterRow">
                <input type="text" id="retainedFilter" class="gcDumpFilterInput" placeholder="Filter types…">
                <span class="gcDumpFilterCount" id="retainedFilterCount"></span>
            </div>
            <div class="detailTable cpuHotMethodsTable">
                <table id="retainedTable">${retainedHeader}<tbody id="retainedTableBody"></tbody></table>
            </div>
        </div>

        <div id="view-roots" class="viewPanel">
            <p class="gcDumpViewBlurb">
                Why objects of a type are still alive. Pick a type, then read
                downward: each level is one reference closer to a GC root.
                Branches are merged, so a chain shared by a million instances
                is one row with a count.
            </p>
            <div class="gcDumpFilterRow">
                <label class="gcDumpSelectLabel" for="rootTypeSelect">Type</label>
                <select id="rootTypeSelect" class="gcDumpSelect"></select>
            </div>
            <div id="rootPathTree" class="callerTreeInner"></div>
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
                <input type="text" id="referenceFilter" class="gcDumpFilterInput" placeholder="Filter types…">
            </div>
            <div id="referenceTree" class="callerTreeInner"></div>
        </div>

        <script nonce="${nonce}" src="${scriptUri}"></script>
    </body>
    </html>`;
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

    let tiles = "";
    tiles += renderTile("Heap Size", formatBytes(totalBytes), formatNumber(totalBytes) + " bytes");
    tiles += renderTile("Objects", formatNumber(totalObjects), "");
    tiles += renderTile("Types", formatNumber(typeCount), "");
    tiles += renderTile("References", formatNumber(referenceCount), "");

    // Real dotnet-gcdump captures routinely contain objects nothing references
    // and no root reaches (7-9% of nodes on every file checked here). Their
    // retained sizes are necessarily 0, so this is surfaced rather than hidden
    // - a reader comparing the Retained column against the heap size deserves
    // to know some of it was unreachable to begin with.
    if (unreachableObjects > 0) {
        const percentUnreachable = totalObjects > 0 ? (unreachableObjects / totalObjects) * 100 : 0;
        tiles += renderTile("Unrooted", formatNumber(unreachableObjects), percentUnreachable.toFixed(1) + "% of objects");
    }

    // A sampled dump's counts are estimates. Saying so in a tile is the
    // difference between a reader trusting a number and a reader trusting a
    // number they should not.
    const sampledNote = metadata["isSampled"]
        ? `<div class="gcDumpSampledWarning">This dump was <strong>sampled</strong> (count &times;${Number(metadata["countMultiplier"] ?? 1).toFixed(2)}, size &times;${Number(metadata["sizeMultiplier"] ?? 1).toFixed(2)}). Counts and sizes are estimates.</div>`
        : ``;

    return `<div class="summaryTileRow">${tiles}</div>${sampledNote}`;
}

function renderTile(label: string, value: string, sublabel: string): string {
    const sub = sublabel.length > 0 ? `<div class="summaryTileSublabel">${escapeHtml(sublabel)}</div>` : ``;
    return `<div class="summaryTile">
        <div class="summaryTileLabel">${escapeHtml(label)}</div>
        <div class="summaryTileValue">${escapeHtml(value)}</div>
        ${sub}
    </div>`;
}

function renderFailureHtml(fileName: string, styleResetUri: vscode.Uri, styleVSCodeUri: vscode.Uri): string {
    return /* html */`
    <!DOCTYPE html>
    <html lang="en">
    <head>
        <meta charset="UTF-8">
        <link href="${styleResetUri}" rel="stylesheet" />
        <link href="${styleVSCodeUri}" rel="stylesheet" />
        <title>${escapeHtml(fileName)}</title>
    </head>
    <body>
        <h2>Unable to read ${escapeHtml(fileName)}</h2>
        <p>This file could not be parsed as a .gcdump heap snapshot. See the
        &quot;Dotnet Insights&quot; output channel for details.</p>
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
