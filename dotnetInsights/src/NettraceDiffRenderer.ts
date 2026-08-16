// Renders the two-capture comparison webview against the payload produced by
// `nettraceParser --diff` (see nettraceParser/Diff/CaptureDiffJsonExporter.cs).
//
// Deliberately NOT an extension of GcSnapshotRenderer.ts / snapshotGcStats.js.
// That pair is built around a single capture: its webview state is module-level
// singletons (gcs, allocationSummaryJson, sharedZoomRange, gcChartHandles) and
// its DOM is keyed on singleton ids (#view-*, #flameGraphContainer,
// #tab-detailed). A second dataset would collide on every one of them, so the
// diff gets its own document, its own script, and shares only the stylesheet.
//
// Two presentation rules the payload exists to support:
//   - Every row carries an absolute delta AND a per-second rate, because two
//     captures rarely span the same wall-clock time. The toggle lives in the
//     UI; the payload never picks for us.
//   - A dimension the baseline never recorded is NOT the same as one that went
//     from zero to something. `coverage` flags that case, and those tabs say
//     "not captured" instead of reporting every row as newly appeared.

import * as vscode from 'vscode';

// Direction of "worse" is per metric, not per sign: more allocated bytes, more
// GC pause, more exceptions and more lock wait are all regressions, but a CPU
// sample count that moved is neither good nor bad on its own (it tracks how
// much work the process did). Declaring it per table stops the coloring from
// asserting something the data doesn't support.
type DeltaDirection = "moreIsWorse" | "neutral";

interface DiffTableSpec {
    id: string;
    label: string;
    rowsKey: string;
    coverageKey: string | null;
    nameHeader: string;
    amountHeader: string;
    amountUnit: "bytes" | "count" | "msec";
    direction: DeltaDirection;
}

const DIFF_TABLES: DiffTableSpec[] = [
    { id: "allocations", label: "Allocations", rowsKey: "allocationTypes", coverageKey: "allocations", nameHeader: "Type", amountHeader: "Bytes", amountUnit: "bytes", direction: "moreIsWorse" },
    { id: "exceptions", label: "Exceptions", rowsKey: "exceptionTypes", coverageKey: "exceptions", nameHeader: "Exception Type", amountHeader: "Thrown", amountUnit: "count", direction: "moreIsWorse" },
    { id: "cpu", label: "CPU Methods", rowsKey: "cpuMethods", coverageKey: "cpu", nameHeader: "Method", amountHeader: "Self Samples", amountUnit: "count", direction: "neutral" },
    { id: "contention", label: "Contention", rowsKey: "contentionSites", coverageKey: "contention", nameHeader: "Contention Site", amountHeader: "Wait (ms)", amountUnit: "msec", direction: "moreIsWorse" },
    { id: "locks", label: "Locks", rowsKey: "locks", coverageKey: "contention", nameHeader: "Lock", amountHeader: "Wait (ms)", amountUnit: "msec", direction: "moreIsWorse" },
    { id: "events", label: "Event Types", rowsKey: "eventTypes", coverageKey: null, nameHeader: "Provider / Event", amountHeader: "Count", amountUnit: "count", direction: "neutral" },
];

export function renderNettraceDiffWebview(
    webview: vscode.Webview,
    extensionUri: vscode.Uri,
    diff: any
): string {
    const baseline = diff["baseline"];
    const comparison = diff["comparison"];
    const coverage = diff["coverage"] || {};

    const nonce = getNonce();
    const styleResetUri = mediaWebviewUri(webview, extensionUri, 'reset.css');
    const styleVSCodeUri = mediaWebviewUri(webview, extensionUri, 'vscode.css');
    const styleMainUri = mediaWebviewUri(webview, extensionUri, 'snapshot.css');
    const scriptUri = mediaWebviewUri(webview, extensionUri, 'nettraceDiff.js');

    const navButtons = ['summary'].concat(DIFF_TABLES.map(table => table.id))
        .map((viewId, index) => {
            const label = viewId === 'summary' ? 'Summary' : DIFF_TABLES[index - 1].label;
            return `<button class="viewNavButton${index === 0 ? ' active' : ''}" data-diffview="${viewId}">${label}</button>`;
        }).join('');

    const tablePanels = DIFF_TABLES.map(table =>
        `<div id="diffview-${table.id}" class="viewPanel">${renderDiffTablePanel(table, diff[table.rowsKey] || [], table.coverageKey ? coverage[table.coverageKey] : null)}</div>`
    ).join('');

    return `<!DOCTYPE html>
    <html lang="en">
        <head>
            <meta charset="UTF-8">
            <meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src ${webview.cspSource}; script-src 'nonce-${nonce}';">
            <meta name="viewport" content="width=device-width, initial-scale=1.0">
            <link href="${styleResetUri}" rel="stylesheet" />
            <link href="${styleMainUri}" rel="stylesheet" />
            <link href="${styleVSCodeUri}" rel="stylesheet" />
            <title>nettrace comparison</title>
        </head>
        <body>
            <div class="viewNavBar">${navButtons}</div>

            <div class="diffCaptureHeader">
                <div class="diffCaptureSide">
                    <div class="diffCaptureLabel">Baseline</div>
                    <div class="diffCaptureName">${escapeHtml(baseline["processName"])}</div>
                    <div class="diffCaptureMeta">${formatDuration(baseline["captureDurationMSec"])} · ${Number(baseline["totalEventCount"]).toLocaleString()} events</div>
                </div>
                <div class="diffCaptureArrow">→</div>
                <div class="diffCaptureSide">
                    <div class="diffCaptureLabel">Comparison</div>
                    <div class="diffCaptureName">${escapeHtml(comparison["processName"])}</div>
                    <div class="diffCaptureMeta">${formatDuration(comparison["captureDurationMSec"])} · ${Number(comparison["totalEventCount"]).toLocaleString()} events</div>
                </div>
            </div>

            <div class="diffToolbar">
                <label class="diffToolbarControl">
                    <input type="checkbox" id="diffNormalizeToggle" ${shouldDefaultToNormalized(baseline, comparison) ? 'checked' : ''}>
                    Normalize per second of capture
                </label>
                <span class="diffToolbarHint" id="diffNormalizeHint"></span>
            </div>

            <div id="diffview-summary" class="viewPanel active">${renderSummaryPanel(baseline, comparison, coverage)}</div>
            ${tablePanels}

            <script type="application/json" id="diffPayload">${escapeJsonForInlineScript(JSON.stringify(diff))}</script>
            <script nonce="${nonce}" src="${scriptUri}"></script>
        </body>
    </html>`;
}

// Normalization defaults ON whenever the two captures differ in length by more
// than 5%, because that is exactly when raw counts mislead - and defaults OFF
// for like-for-like captures, where absolute numbers are what people expect.
function shouldDefaultToNormalized(baseline: any, comparison: any): boolean {
    const baselineMSec = baseline["captureDurationMSec"];
    const comparisonMSec = comparison["captureDurationMSec"];

    if (!baselineMSec || !comparisonMSec) {
        return false;
    }

    const ratio = baselineMSec > comparisonMSec ? baselineMSec / comparisonMSec : comparisonMSec / baselineMSec;
    return ratio > 1.05;
}

function renderSummaryPanel(baseline: any, comparison: any, coverage: any): string {
    const rows: string[] = [];

    const addRow = (label: string, baseValue: number, compValue: number, unit: "bytes" | "count" | "msec" | "percent", direction: DeltaDirection) => {
        const delta = compValue - baseValue;
        const percent = baseValue !== 0 ? (delta / Math.abs(baseValue)) * 100 : null;

        rows.push(
            `<tr>` +
            `<td style="text-align:left">${escapeHtml(label)}</td>` +
            `<td style="text-align:right">${formatAmount(baseValue, unit)}</td>` +
            `<td style="text-align:right">${formatAmount(compValue, unit)}</td>` +
            `<td style="text-align:right" class="${deltaClass(delta, direction)}">${formatSignedAmount(delta, unit)}</td>` +
            `<td style="text-align:right" class="${deltaClass(delta, direction)}">${percent === null ? '<span class="deltaNew">new</span>' : formatSignedPercent(percent)}</td>` +
            `</tr>`
        );
    };

    addRow("Capture duration", baseline["captureDurationMSec"], comparison["captureDurationMSec"], "msec", "neutral");
    addRow("Total events", baseline["totalEventCount"], comparison["totalEventCount"], "count", "neutral");
    addRow("GC count", baseline["totalGcCount"], comparison["totalGcCount"], "count", "moreIsWorse");
    addRow("GC pause total", baseline["totalGcPauseMSec"], comparison["totalGcPauseMSec"], "msec", "moreIsWorse");
    addRow("Allocated bytes", baseline["totalAllocatedBytes"], comparison["totalAllocatedBytes"], "bytes", "moreIsWorse");
    addRow("Allocation ticks", baseline["totalAllocationTickCount"], comparison["totalAllocationTickCount"], "count", "moreIsWorse");
    addRow("Exceptions thrown", baseline["totalExceptionCount"], comparison["totalExceptionCount"], "count", "moreIsWorse");
    addRow("CPU samples", baseline["totalCpuSampleCount"], comparison["totalCpuSampleCount"], "count", "neutral");
    addRow("Contentions", baseline["totalContentionCount"], comparison["totalContentionCount"], "count", "moreIsWorse");
    addRow("Contention wait", baseline["totalContentionWaitMSec"], comparison["totalContentionWaitMSec"], "msec", "moreIsWorse");

    if (baseline["hasTimeBreakdown"] && comparison["hasTimeBreakdown"]) {
        addRow("% time in GC", baseline["gcPercent"], comparison["gcPercent"], "percent", "moreIsWorse");
        addRow("% time contending locks", baseline["contentionPercent"], comparison["contentionPercent"], "percent", "moreIsWorse");
        addRow("Avg threads blocked", baseline["averageThreadsBlocked"], comparison["averageThreadsBlocked"], "count", "moreIsWorse");
    }

    if (baseline["hasCpuBreakdown"] && comparison["hasCpuBreakdown"]) {
        addRow("% time CPU bound (est.)", baseline["cpuBoundPercent"], comparison["cpuBoundPercent"], "percent", "neutral");
    }

    const header = `<tr class="tableHeader">` +
        `<th style="text-align:left">Metric</th><th style="text-align:right">Baseline</th>` +
        `<th style="text-align:right">Comparison</th><th style="text-align:right">Δ</th><th style="text-align:right">Δ %</th></tr>`;

    return `${renderCoverageWarnings(coverage)}<div class="detailTable diffTable"><table>${header}${rows.join('')}</table></div>`;
}

// Surfaced prominently rather than as a footnote: a tab whose baseline has no
// data at all will otherwise read as "everything here is new", which is a
// statement about the capture's providers, not about the program.
function renderCoverageWarnings(coverage: any): string {
    const missing: string[] = [];

    for (const key of Object.keys(coverage || {})) {
        const entry = coverage[key];
        if (entry && !entry["comparable"]) {
            if (!entry["baselineHasData"] && entry["comparisonHasData"]) {
                missing.push(`${key} was not recorded in the baseline capture`);
            } else if (entry["baselineHasData"] && !entry["comparisonHasData"]) {
                missing.push(`${key} was not recorded in the comparison capture`);
            }
        }
    }

    if (missing.length === 0) {
        return "";
    }

    return `<div class="diffCoverageWarning">Not comparable: ${escapeHtml(missing.join('; '))}. ` +
        `Rows in those tabs reflect which providers were enabled, not a change in behavior.</div>`;
}

function renderDiffTablePanel(table: DiffTableSpec, rows: any[], coverageEntry: any): string {
    if (coverageEntry && !coverageEntry["comparable"]) {
        const side = coverageEntry["baselineHasData"] ? "comparison" : "baseline";
        return `<div class="diffCoverageWarning">This data was not recorded in the ${side} capture, so the two runs cannot be compared on it. ` +
            `The rows below show only what the other capture contained.</div>${renderDiffTable(table, rows)}`;
    }

    if (!rows || rows.length === 0) {
        return `<div class="detailTable"><p>No differences to display.</p></div>`;
    }

    return renderDiffTable(table, rows);
}

function renderDiffTable(table: DiffTableSpec, rows: any[]): string {
    const header = `<tr class="tableHeader">` +
        `<th style="text-align:left" data-diff-sort="name">${escapeHtml(table.nameHeader)}<span class="sortIndicator"></span></th>` +
        `<th style="text-align:right" data-diff-sort="baselineAmount">Baseline ${escapeHtml(table.amountHeader)}<span class="sortIndicator"></span></th>` +
        `<th style="text-align:right" data-diff-sort="comparisonAmount">Comparison ${escapeHtml(table.amountHeader)}<span class="sortIndicator"></span></th>` +
        `<th style="text-align:right" data-diff-sort="deltaAmount">Δ<span class="sortIndicator"> ▼</span></th>` +
        `<th style="text-align:right" data-diff-sort="percentChange">Δ %<span class="sortIndicator"></span></th>` +
        `<th style="text-align:right" data-diff-sort="deltaCount">Δ Count<span class="sortIndicator"></span></th>` +
        `</tr>`;

    // Explicit <thead>, not a bare <tr>: a header row written directly into
    // <table> is placed by the parser into an IMPLICIT tbody, which then sits
    // ahead of the explicit one. nettraceDiff.js writes rows into the table's
    // first tbody, so without this the header row is the thing it overwrites -
    // the table renders with data but no sortable headers at all.
    return `<div class="detailTable diffTable">` +
        `<table data-diff-table="${table.id}" data-diff-unit="${table.amountUnit}" data-diff-direction="${table.direction}">` +
        `<thead>${header}</thead><tbody></tbody></table></div>`;
}

function deltaClass(delta: number, direction: DeltaDirection): string {
    if (delta === 0) {
        return "";
    }

    if (direction === "neutral") {
        return "deltaNeutral";
    }

    return delta > 0 ? "deltaWorse" : "deltaBetter";
}

function formatDuration(msec: number): string {
    if (msec >= 1000) {
        return `${(msec / 1000).toFixed(1)} s`;
    }

    return `${msec.toFixed(0)} ms`;
}

function formatAmount(value: number, unit: "bytes" | "count" | "msec" | "percent"): string {
    if (unit === "bytes") {
        return formatBytes(value);
    }

    if (unit === "msec") {
        return `${value.toFixed(1)}`;
    }

    if (unit === "percent") {
        return `${value.toFixed(2)}%`;
    }

    return Math.round(value).toLocaleString();
}

function formatSignedAmount(value: number, unit: "bytes" | "count" | "msec" | "percent"): string {
    const sign = value > 0 ? "+" : (value < 0 ? "−" : "");
    return `${sign}${formatAmount(Math.abs(value), unit)}`;
}

function formatSignedPercent(percent: number): string {
    const sign = percent > 0 ? "+" : (percent < 0 ? "−" : "");
    return `${sign}${Math.abs(percent).toFixed(1)}%`;
}

function formatBytes(bytes: number): string {
    const absolute = Math.abs(bytes);

    if (absolute >= 1024 * 1024 * 1024) {
        return `${(bytes / (1024 * 1024 * 1024)).toFixed(2)} GB`;
    }

    if (absolute >= 1024 * 1024) {
        return `${(bytes / (1024 * 1024)).toFixed(2)} MB`;
    }

    if (absolute >= 1024) {
        return `${(bytes / 1024).toFixed(2)} KB`;
    }

    return `${Math.round(bytes)} B`;
}

function mediaWebviewUri(webview: vscode.Webview, extensionUri: vscode.Uri, fileName: string): vscode.Uri {
    return webview.asWebviewUri(vscode.Uri.joinPath(extensionUri, 'media', fileName));
}

function escapeHtml(value: string): string {
    return String(value)
        .replace(/&/g, "&amp;")
        .replace(/</g, "&lt;")
        .replace(/>/g, "&gt;")
        .replace(/"/g, "&quot;");
}

// Same guard GcSnapshotRenderer.ts uses: a "</script>" inside the payload would
// otherwise terminate the block early.
function escapeJsonForInlineScript(json: string): string {
    return json.replace(/</g, "\\u003c");
}

function getNonce(): string {
    let text = "";
    const possible = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
    for (let i = 0; i < 32; i++) {
        text += possible.charAt(Math.floor(Math.random() * possible.length));
    }
    return text;
}
