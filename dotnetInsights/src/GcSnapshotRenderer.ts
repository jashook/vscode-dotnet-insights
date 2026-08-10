import * as fs from 'fs';
import * as vscode from 'vscode';

import { renderAllocationSummaryTable } from "./AllocationSummaryRenderer";
import { adaptivelyBucketTicks } from "./AllocationTicksBucketer";
import { renderContentionView } from "./ContentionRenderer";
import { renderCpuProfileView } from "./CpuProfileRenderer";
import { DotnetInsightsGcDocument } from "./DotnetInsightsGcEditor";
import { renderEventOverviewTable } from "./EventOverviewRenderer";
import { renderExceptionSummaryTable } from "./ExceptionSummaryRenderer";
import { formatHumanDateTime, renderGcDetailTable } from "./GcDetailTableRenderer";
import { computeAllocationAmountStats, computePauseTimeStats } from "./GcStatsCalculations";

// A media/ file's webview URI, with the file's own last-modified time
// appended as a ?v= query param.
//
// webview.asWebviewUri produces a byte-identical URI for a given file every
// time, and Electron's network stack caches what it serves from that URI -
// so an edited media/*.js or media/*.css can keep serving its PREVIOUS
// contents to a webview, with nothing short of a full VS Code restart
// reliably clearing it. That ambiguity ("is my fix wrong, or am I looking
// at a stale copy?") cost several confusing round-trips during drill-down
// table work. Keying the URI on mtime keeps normal caching intact for an
// unchanged file (same mtime -> same URI -> cache hit) while guaranteeing
// any actual edit produces a URI that cannot hit a stale entry. This also
// matters for shipped upgrades, where media/ changes but the URI otherwise
// wouldn't - the same stale-cache trap DependencySetup.ts's version-marker
// files already guard against for downloaded helper binaries.
export function mediaWebviewUri(webview: vscode.Webview, extensionUri: vscode.Uri, fileName: string): vscode.Uri {
    const fileUri = vscode.Uri.joinPath(extensionUri, 'media', fileName);
    const webviewUri = webview.asWebviewUri(fileUri);

    try {
        return webviewUri.with({ query: `v=${fs.statSync(fileUri.fsPath).mtimeMs}` });
    } catch (statError) {
        // Unreadable/missing file: fall back to the un-versioned URI rather
        // than failing the whole render - the <script>/<link> tag failing on
        // its own is a much clearer symptom than a blank webview.
        return webviewUri;
    }
}

// Renders the summary tiles + Chart.js graphs shared by every "static GC
// snapshot" input source (DotnetInsightsGcSnapshotEditor's .gcinfo/XML path,
// DotnetInsightsNettraceEditor's .nettrace path). gcData must already be in
// the shape { processName, gcData: [{ data: {...} }], allocationSummary? }
// - each caller is responsible for getting its own input format into that
// shape; this function doesn't care where it came from. sourceFormat gates
// nettrace-only views ("Heap Contents", and later "Profile") - it's an
// explicit parameter rather than inferred from allocationSummary's presence
// because a very short nettrace capture can legitimately have zero
// allocation ticks, which would make that inference unreliable.
export function renderGcSnapshotWebview(document: DotnetInsightsGcDocument, webview: vscode.Webview, extensionUri: vscode.Uri, gcData: any, sourceFormat: "gcinfo" | "nettrace"): string {
    const defaultHtmlReturn = /* html */`
    <!DOCTYPE html>
    <html lang="en">
    <head>
        <meta charset="UTF-8">
        <!--
        Use a content security policy to only allow loading images from https or from our extension directory,
        and only allow scripts that have a specific nonce.
        -->

        <meta http-equiv="Content-Security-Policy"
        content="default-src * vscode-resource: https: 'unsafe-inline' 'unsafe-eval';
        script-src vscode-webview-resource: https: 'unsafe-inline' 'unsafe-eval';
        style-src vscode-webview-resource: https: 'unsafe-inline';
        img-src vscode-resource: https:;
        connect-src vscode-resource: https: http:;">

        <meta name="viewport" content="width=device-width, initial-scale=1.0">
    </head>
    <body>

    </body>
    </html>`;

    // Previously also required gcData["allocations"] != null - .gcinfo's XML
    // path (DotnetInsightsGcSnapshotEditor.gcDataFromXml) never sets that
    // key at all, so every successfully-parsed .gcinfo file was hitting this
    // "corrupted" branch instead of rendering. allocationSummary (nettrace-
    // only, see AllocationJsonExporter.cs) is optional by design, gated
    // below via sourceFormat instead of required here.
    if (gcData === null || gcData["gcData"] === null) {
        vscode.window.showWarningMessage(`${document.uri.fsPath} is corrupted or a incorrect type.`);
        return defaultHtmlReturn;
    }

    // gc data has all of the allocations and gc events that occurred in the
    // window. We will now go through and calculate the interesting data we
    // want from what we were provided.

    const gcs = gcData["gcData"];

    // Visible (not just tooltip-on-hover) capture time range, so the GC
    // numbers shown below have real wall-clock context without requiring
    // interaction. See gcDataFromXml / GcJsonExporter.cs for where DateTime
    // comes from per source.
    var captureTimeRangeHtml = "";
    if (gcs.length > 0) {
        const firstDateTime = formatHumanDateTime(gcs[0]["data"]["DateTime"]);
        const lastDateTime = formatHumanDateTime(gcs[gcs.length - 1]["data"]["DateTime"]);
        captureTimeRangeHtml = `<div id="captureTimeRange">Captured: ${firstDateTime} &ndash; ${lastDateTime}</div>`;
    }

    const detailTableHtml = renderGcDetailTable(gcs);

    // Format-level (not per-file) gate: Overview/Heap Contents/Exceptions
    // are architecturally unavailable for .gcinfo/XML input (that path
    // never decodes anything beyond GC records - see GcJsonExporter.cs) -
    // those three nav buttons stay fully ABSENT for gcinfo, same as today,
    // not shown-disabled. GC itself is the only view gcinfo ever has, so it
    // stays unconditionally enabled and default-active there too,
    // unchanged. For nettrace input, all four buttons are always rendered;
    // GC/Heap Contents/Exceptions are individually `disabled` (not omitted)
    // when this particular capture has no events of that type - see
    // hasGc/hasHeapContents/hasExceptions below.
    const isNettrace = sourceFormat === "nettrace";

    // "Heap Contents" (allocation-tick-based type ranking): even for
    // nettrace input a very short capture can legitimately have zero
    // allocation ticks - hasHeapContents (format AND data) still gates
    // whether the (potentially large) ranked-table/drill-down HTML and
    // JSON below are worth building at all; the button itself is always
    // rendered for nettrace regardless, just disabled when this is false.
    const allocationSummary = gcData["allocationSummary"];
    const hasHeapContents = isNettrace && allocationSummary !== null && allocationSummary !== undefined && allocationSummary["topTypes"] !== null && allocationSummary["topTypes"] !== undefined && allocationSummary["topTypes"].length > 0;
    const allocationSummaryHtml = hasHeapContents ? renderAllocationSummaryTable(allocationSummary) : "";

    // Ticks are bucketed (only when the raw count is large enough to
    // matter - see AllocationTicksBucketer.ts) before ever being
    // stringified into the webview's HTML: a heavily-allocating capture's
    // raw tick count can be in the millions, and both this JSON.stringify
    // (plus the webview's own JSON.parse of it) and allocationStats.js's
    // per-GC-boundary summation over the raw array scale with that count -
    // left unbucketed, either can hang the page well before any chart
    // renders.
    const allocationSummaryForWebview = hasHeapContents && allocationSummary["ticks"]
        ? { ...allocationSummary, ticks: adaptivelyBucketTicks(allocationSummary["ticks"]) }
        : allocationSummary;
    const allocationSummaryJson = escapeJsonForInlineScript(hasHeapContents ? JSON.stringify(allocationSummaryForWebview) : "null");

    // "Exceptions" (CLR ExceptionThrown_V1-based type ranking) - same
    // reasoning as "Heap Contents" above (see ExceptionJsonExporter.cs).
    const exceptionSummary = gcData["exceptionSummary"];
    const hasExceptions = isNettrace && exceptionSummary !== null && exceptionSummary !== undefined && exceptionSummary["topTypes"] !== null && exceptionSummary["topTypes"] !== undefined && exceptionSummary["topTypes"].length > 0;
    const exceptionSummaryHtml = hasExceptions ? renderExceptionSummaryTable(exceptionSummary) : "";
    const exceptionSummaryJson = escapeJsonForInlineScript(hasExceptions ? JSON.stringify(exceptionSummary) : "null");

    // GC tab: enabled only when this particular capture actually has GC
    // events - a capture containing only exceptions (or, in principle,
    // only allocation ticks with no completed GC) is real and now must
    // render its GC tab as visibly present-but-disabled rather than an
    // empty/broken chart view. gcinfo format's GC tab ignores this
    // entirely (see isNettrace comment above) - always enabled there.
    const hasGc = gcs.length > 0;

    // "Overview" (total event count + a breakdown by every distinct event
    // type actually present, not just GC/allocation/exception) is
    // nettrace-only, same format-level reasoning as Heap Contents/
    // Exceptions - .gcinfo/XML input never sets eventOverview at all (see
    // GcJsonExporter.cs). Unlike those two, eventOverview is always
    // meaningful whenever it's present (every capture has *some* events),
    // so there's no data-emptiness check here - only the format gate.
    const eventOverview = gcData["eventOverview"];
    const hasOverview = isNettrace && eventOverview !== null && eventOverview !== undefined;
    const eventOverviewHtml = hasOverview ? renderEventOverviewTable(eventOverview) : "";

    // "Profile" (CPU sample-based flame graph + hot methods table) - same
    // format-level/data-emptiness gating as Heap Contents/Exceptions above
    // (see Cpu/CpuProfileJsonExporter.cs). cpuProfileJson carries the raw
    // flameTree/methodNames data media/flameGraph.js needs to build the
    // flame graph entirely client-side (see CpuProfileRenderer.ts's own
    // header comment) - hotMethods is included too so the table can be
    // resorted without a round trip, but flameGraph.js is the only reader
    // that actually needs flameTree.
    const cpuProfile = gcData["cpuProfile"];
    const hasCpuProfile = isNettrace && cpuProfile !== null && cpuProfile !== undefined && cpuProfile["totalSampleCount"] !== null && cpuProfile["totalSampleCount"] !== undefined && cpuProfile["totalSampleCount"] > 0;
    const cpuProfileHtml = hasCpuProfile ? renderCpuProfileView(cpuProfile) : "";
    const cpuProfileJson = escapeJsonForInlineScript(hasCpuProfile ? JSON.stringify(cpuProfile) : "null");

    // "Contention" (CLR Contention/Start + Stop event pairs) - same
    // format-level/data-emptiness gating as Heap Contents/Exceptions above
    // (see Contention/ContentionJsonExporter.cs).
    const contentionSummary = gcData["contentionSummary"];
    const hasContention = isNettrace && contentionSummary !== null && contentionSummary !== undefined && contentionSummary["totalContentionCount"] !== null && contentionSummary["totalContentionCount"] !== undefined && contentionSummary["totalContentionCount"] > 0;
    const contentionHtml = hasContention ? renderContentionView(contentionSummary) : "";
    const contentionSummaryJson = escapeJsonForInlineScript(hasContention ? JSON.stringify(contentionSummary) : "null");

    var totalNumbers = computePauseTimeStats(gcs);

    let gen0Numbers = computePauseTimeStats(gcs, 0);
    let gen1Numbers = computePauseTimeStats(gcs, 1);
    let gen2Numbers = computePauseTimeStats(gcs, 2);

    var allocationAmountTotal = computeAllocationAmountStats(gcs);
    var allocationAmountGen0 = computeAllocationAmountStats(gcs, 0);
    var allocationAmountGen1 = computeAllocationAmountStats(gcs, 1);
    var allocationAmountGen2 = computeAllocationAmountStats(gcs, 2);
    var allocationAmountLOH = computeAllocationAmountStats(gcs, 3);

    // dataValue is the single unit label for every row (Total/Largest/
    // Smallest/Average/Median) in every one of these tiles. Both
    // thresholds below used to convert only index [0] (Total) on the
    // second pass, while updating a *separate* label (totalTotalValue)
    // that only Total's row read - so a large enough capture could show
    // "Total: 1417.77 gb" right next to "Average: 425.75 mb" despite both
    // numbers being individually correct (425.75 MB * 3410 GCs / 1024 ==
    // 1417.77 GB), which reads as wildly inconsistent at a glance. All
    // five fields now scale together so a tile is always in one unit.
    var dataValue = "kb";

    if (allocationAmountTotal[1][0].toFixed(2).length > 8) {
        dataValue = "mb";

        allocationAmountTotal[1][0] /= 1024;
        allocationAmountTotal[1][1] /= 1024;
        allocationAmountTotal[1][2] /= 1024;
        allocationAmountTotal[1][3] /= 1024;
        allocationAmountTotal[1][4] /= 1024;

        allocationAmountGen0[1][0] /= 1024;
        allocationAmountGen0[1][1] /= 1024;
        allocationAmountGen0[1][2] /= 1024;
        allocationAmountGen0[1][3] /= 1024;
        allocationAmountGen0[1][4] /= 1024;

        allocationAmountGen1[1][0] /= 1024;
        allocationAmountGen1[1][1] /= 1024;
        allocationAmountGen1[1][2] /= 1024;
        allocationAmountGen1[1][3] /= 1024;
        allocationAmountGen1[1][4] /= 1024;

        allocationAmountGen2[1][0] /= 1024;
        allocationAmountGen2[1][1] /= 1024;
        allocationAmountGen2[1][2] /= 1024;
        allocationAmountGen2[1][3] /= 1024;
        allocationAmountGen2[1][4] /= 1024;

        allocationAmountLOH[1][0] /= 1024;
        allocationAmountLOH[1][1] /= 1024;
        allocationAmountLOH[1][2] /= 1024;
        allocationAmountLOH[1][3] /= 1024;
        allocationAmountLOH[1][4] /= 1024;
    }

    if (allocationAmountTotal[1][0].toFixed(2).length > 8) {
        dataValue = "gb";

        allocationAmountTotal[1][0] /= 1024;
        allocationAmountTotal[1][1] /= 1024;
        allocationAmountTotal[1][2] /= 1024;
        allocationAmountTotal[1][3] /= 1024;
        allocationAmountTotal[1][4] /= 1024;

        allocationAmountGen0[1][0] /= 1024;
        allocationAmountGen0[1][1] /= 1024;
        allocationAmountGen0[1][2] /= 1024;
        allocationAmountGen0[1][3] /= 1024;
        allocationAmountGen0[1][4] /= 1024;

        allocationAmountGen1[1][0] /= 1024;
        allocationAmountGen1[1][1] /= 1024;
        allocationAmountGen1[1][2] /= 1024;
        allocationAmountGen1[1][3] /= 1024;
        allocationAmountGen1[1][4] /= 1024;

        allocationAmountGen2[1][0] /= 1024;
        allocationAmountGen2[1][1] /= 1024;
        allocationAmountGen2[1][2] /= 1024;
        allocationAmountGen2[1][3] /= 1024;
        allocationAmountGen2[1][4] /= 1024;

        allocationAmountLOH[1][0] /= 1024;
        allocationAmountLOH[1][1] /= 1024;
        allocationAmountLOH[1][2] /= 1024;
        allocationAmountLOH[1][3] /= 1024;
        allocationAmountLOH[1][4] /= 1024;
    }

    // Kept as a separate name for the template below (Total's own row used
    // a distinct variable historically) but always equal to dataValue now.
    var totalTotalValue = dataValue;

    var allocTotal = allocationAmountTotal[1][0].toFixed(2);
    var allocAverage = allocationAmountTotal[1][1].toFixed(2);
    var allocMedian = allocationAmountTotal[1][2].toFixed(2);
    var allocHighest = allocationAmountTotal[1][3].toFixed(2);
    var allocLowest = allocationAmountTotal[1][4].toFixed(2);
    var allocByGc = allocationAmountTotal[0];

    var allocGen0Total = allocationAmountGen0[1][0].toFixed(2);
    var allocGen0Average = allocationAmountGen0[1][1].toFixed(2);
    var allocGen0Median = allocationAmountGen0[1][2].toFixed(2);
    var allocGen0Highest = allocationAmountGen0[1][3].toFixed(2);
    var allocGen0Lowest = allocationAmountGen0[1][4].toFixed(2);
    var allocGen0ByGc = allocationAmountGen0[0];

    var allocGen1Total = allocationAmountGen1[1][0].toFixed(2);
    var allocGen1Average = allocationAmountGen1[1][1].toFixed(2);
    var allocGen1Median = allocationAmountGen1[1][2].toFixed(2);
    var allocGen1Highest = allocationAmountGen1[1][3].toFixed(2);
    var allocGen1Lowest = allocationAmountGen1[1][4].toFixed(2);
    var allocGen1ByGc = allocationAmountGen1[0];

    var allocGen2Total = allocationAmountGen2[1][0].toFixed(2);
    var allocGen2Average = allocationAmountGen2[1][1].toFixed(2);
    var allocGen2Median = allocationAmountGen2[1][2].toFixed(2);
    var allocGen2Highest = allocationAmountGen2[1][3].toFixed(2);
    var allocGen2Lowest = allocationAmountGen2[1][4].toFixed(2);
    var allocGen2ByGc = allocationAmountGen2[0];

    var allocLOHTotal = allocationAmountLOH[1][0].toFixed(2);
    var allocLOHAverage = allocationAmountLOH[1][1].toFixed(2);
    var allocLOHMedian = allocationAmountLOH[1][2].toFixed(2);
    var allocLOHHighest = allocationAmountLOH[1][3].toFixed(2);
    var allocLOHLowest = allocationAmountLOH[1][4].toFixed(2);
    var allocLOHByGc = allocationAmountLOH[0];

    // Time in GC.

    var totalTimeInGc = totalNumbers[1][0].toFixed(2);
    var averageTimeInGc = totalNumbers[1][1].toFixed(2);
    var medianTimeInGc = totalNumbers[1][2].toFixed(2);
    var highestTimeInGc = totalNumbers[1][3].toFixed(2);
    var lowestTimeInGc = totalNumbers[1][4].toFixed(2);
    var timeinsideEachGc = totalNumbers[0];

    var gen0TotalTimeInGc = gen0Numbers[1][0].toFixed(2);
    var gen0TimesInEachGc = gen0Numbers[0];
    var gen0AverageTimeInGc = gen0Numbers[1][1].toFixed(2);
    var gen0MedianTimeInGc = gen0Numbers[1][2].toFixed(2);
    var gen0HighestTimeInGc = gen0Numbers[1][3].toFixed(2);
    var gen0LowestTimeInGc = gen0Numbers[1][4].toFixed(2);

    var gen1TotalTimeInGc = gen1Numbers[1][0].toFixed(2);
    var gen1TimesInEachGc = gen1Numbers[0];
    var gen1AverageTimeInGc = gen1Numbers[1][1].toFixed(2);
    var gen1MedianTimeInGc = gen1Numbers[1][2].toFixed(2);
    var gen1HighestTimeInGc = gen1Numbers[1][3].toFixed(2);
    var gen1LowestTimeInGc = gen1Numbers[1][4].toFixed(2);

    var gen2TotalTimeInGc = gen2Numbers[1][0].toFixed(2);
    var gen2TimesInEachGc = gen2Numbers[0];
    var gen2AverageTimeInGc = gen2Numbers[1][1].toFixed(2);
    var gen2MedianTimeInGc = gen2Numbers[1][2].toFixed(2);
    var gen2HighestTimeInGc = gen2Numbers[1][3].toFixed(2);
    var gen2LowestTimeInGc = gen2Numbers[1][4].toFixed(2);

    const nonce = getNonce();

    const mainUri = mediaWebviewUri(webview, extensionUri, 'snapshot.css');
    const styleResetUri = mediaWebviewUri(webview, extensionUri, 'reset.css');
    const styleVSCodeUri = mediaWebviewUri(webview, extensionUri, 'vscode.css');

    const scriptUri = mediaWebviewUri(webview, extensionUri, 'snapshotGcStats.js');
    const chartZoomScriptUri = mediaWebviewUri(webview, extensionUri, 'chartZoomHelper.js');
    const allocationScriptUri = mediaWebviewUri(webview, extensionUri, 'allocationStats.js');
    const drillDownScriptUri = mediaWebviewUri(webview, extensionUri, 'drillDownStats.js');
    const exceptionDrillDownScriptUri = mediaWebviewUri(webview, extensionUri, 'exceptionDrillDownStats.js');
    const cpuDrillDownScriptUri = mediaWebviewUri(webview, extensionUri, 'cpuDrillDownStats.js');
    const contentionDrillDownScriptUri = mediaWebviewUri(webview, extensionUri, 'contentionDrillDownStats.js');
    const flameGraphScriptUri = mediaWebviewUri(webview, extensionUri, 'flameGraph.js');

    const chartjs = webview.asWebviewUri(vscode.Uri.joinPath(extensionUri, 'node_modules', 'chart.js', 'dist', 'Chart.min.js'));

    var canvasData = "";
    if (gcs.length > 0) {
        canvasData += `<div class="heapChartParentMultiple"><canvas class="gcStatsChart"></canvas></div>`;
        canvasData += `<div class="allocChartParent heapChartNextLine"><canvas class="gcStatsTimeChart"></canvas></div>`;
    }

    var totalCanvasData = "";
    if (gcs.length > 0) {
        totalCanvasData += `<div class="gcStats"><canvas id="totalGcStatsOverTime"></canvas></div>`;
    }

    var pauseTimeCanvasData = "";
    if (gcs.length > 0) {
        pauseTimeCanvasData += `<div class="gcStats"><canvas id="totalGcPauseTimeOverTime"></canvas></div>`;
    }

    var fragmentationCanvasData = "";
    if (gcs.length > 0) {
        fragmentationCanvasData = `<div class="gcStats"><canvas id="gcFragmentationOverTime"></canvas></div>`;
    }

    var perHeapCanvasData = "";
    if (gcs.length > 0) {
        const gcData = gcs[0].data;

        for (var innerIndex = 0; innerIndex < gcData["Heaps"].length; ++innerIndex) {
            perHeapCanvasData += `<div class="heapChartParentMultiple"><canvas class="heapChart"></canvas></div>`;

            if (innerIndex + 1 != gcData["Heaps"].length) {
                ++innerIndex;
                perHeapCanvasData += `<div class="allocChartParent heapChartNextLine"><canvas class="heapChart"></canvas></div>`;
            }
        }
    }

    const gcCountsByGen = escapeJsonForInlineScript(JSON.stringify([gen0TimesInEachGc.length, gen1TimesInEachGc.length, gen2TimesInEachGc.length]));

    // Full per-GC data (every per-generation field included, not just what
    // the charts currently read) - kept as full fidelity on purpose so
    // future chart/tooltip work has everything available without needing to
    // widen this projection again. The old GcData class wrapper added no
    // data of its own here (timestamp/percentInGc/privateBytes/... are all
    // live-listener-view concerns, unused by this static snapshot view), so
    // it's dropped in favor of passing each GC's real data straight through.
    var chartPayload = [];
    for (var index = 0; index < gcs.length; ++index) {
        chartPayload.push({ data: gcs[index]["data"] });
    }

    var hiddenData = null;

    try {
        hiddenData = escapeJsonForInlineScript(JSON.stringify(chartPayload));
    }
    catch(e) {
        var i = 0;
    }

    var totalTimeInEachGc = [
        gen0TotalTimeInGc,
        gen1TotalTimeInGc,
        gen2TotalTimeInGc
    ];

    const totalTimeInEachGcJson = escapeJsonForInlineScript(JSON.stringify(totalTimeInEachGc));

    // Allocations

    var htmlToReturn = /* html */`
    <!DOCTYPE html>
    <html lang="en">
        <head>
            <meta charset="UTF-8">
            <!--
            Use a content security policy to only allow loading images from https or from our extension directory,
            and only allow scripts that have a specific nonce.
            -->

            <!--<meta http-equiv="Content-Security-Policy" content="default-src 'none'; img-src ${webview.cspSource}; style-src ${webview.cspSource}; script-src 'nonce-${nonce}';">-->

            <meta http-equiv="Content-Security-Policy"
            content="default-src * vscode-resource: https: 'unsafe-inline' 'unsafe-eval';
            script-src vscode-webview-resource: https: 'unsafe-inline' 'unsafe-eval';
            style-src vscode-webview-resource: https: 'unsafe-inline';
            img-src vscode-resource: https:;
            connect-src vscode-resource: https: http:;">

            <meta name="viewport" content="width=device-width, initial-scale=1.0">
            <link href="${styleResetUri}" rel="stylesheet" />
            <link href="${mainUri}" rel="stylesheet" />
            <link href="${styleVSCodeUri}" rel="stylesheet" />
        </head>
        <body>
            <script type="application/json" id="hiddenData">${hiddenData}</script>
            <script type="application/json" id="gcCountsByGen">${gcCountsByGen}</script>
            <script type="application/json" id="totalTimeInEachGcJson">${totalTimeInEachGcJson}</script>
            <script type="application/json" id="allocationSummaryJson">${allocationSummaryJson}</script>
            <script type="application/json" id="exceptionSummaryJson">${exceptionSummaryJson}</script>
            <script type="application/json" id="cpuProfileJson">${cpuProfileJson}</script>
            <script type="application/json" id="contentionSummaryJson">${contentionSummaryJson}</script>

            <!-- High-level view switcher (Overview / Profile / GC / Heap
                 Contents / Exceptions) - browser-tab style, sitting above
                 the file name so it doesn't consume horizontal width from
                 the content below the way a left-nav sidebar would. For
                 nettrace input, Overview is always the default active tab
                 (even when GC has data) and every other tab stays visible-
                 but-disabled when this particular capture has none of that
                 event type, rather than disappearing - lets a user see at
                 a glance what kinds of events this capture does/doesn't
                 have. gcinfo format only ever has the one GC tab,
                 unconditionally enabled and default-active, exactly as
                 before. -->
            <div class="viewTabBar">
                ${isNettrace ? `<button class="viewNavButton active" data-view="overview">Overview</button>` : ``}
                ${isNettrace ? `<button class="viewNavButton" data-view="profile"${hasCpuProfile ? `` : ` disabled title="No CPU samples in this capture"`}>Profile</button>` : ``}
                <button class="viewNavButton${isNettrace ? `` : ` active`}" data-view="gc"${isNettrace && !hasGc ? ` disabled title="No GC events in this capture"` : ``}>GC</button>
                ${isNettrace ? `<button class="viewNavButton" data-view="heapContents"${hasHeapContents ? `` : ` disabled title="No allocation events in this capture"`}>Heap Contents</button>` : ``}
                ${isNettrace ? `<button class="viewNavButton" data-view="exceptions"${hasExceptions ? `` : ` disabled title="No exception events in this capture"`}>Exceptions</button>` : ``}
                ${isNettrace ? `<button class="viewNavButton" data-view="contention"${hasContention ? `` : ` disabled title="No contention events in this capture"`}>Contention</button>` : ``}
            </div>

            <h2 class="divider">${gcData["processName"]}</h2>

            ${isNettrace ? `<div id="view-overview" class="viewPanel active">${eventOverviewHtml}</div>` : ``}

            <div id="view-gc" class="viewPanel${isNettrace ? `` : ` active`}">

            <input type="file" id="heapSnapshotInput" accept=".json" style="display:none">

            <div class="tabBar">
                <button class="tabButton active" data-tab="charts">Charts</button>
                <button class="tabButton" data-tab="detailed">Detailed</button>
                <button class="tabButton" id="heapSnapshotTabBtn" data-tab="heapSnapshot" style="display:none">Heap Snapshot</button>
                <button class="fieldToggleButton" id="genFieldsToggle" style="display:none">Show All Fields</button>
            </div>

            <div id="tab-charts" class="tabPanel active">
            ${captureTimeRangeHtml}

            <!-- Shared drag-to-zoom status/reset affordance for every
                 GC-over-time chart below (Pause Time, Usage Over Time,
                 Fragmentation, per-Heap) - one zoom range applies to all of
                 them at once, mirroring the Heap Contents view's own
                 allocationZoomStatus (reused here via the same CSS classes,
                 distinct ids so the two don't collide). Hidden until a zoom
                 is actually applied - see snapshotGcStats.js's
                 updateGcZoomStatusUi/renderGcCharts. -->
            <div id="gcZoomStatus" class="allocationZoomStatus" style="display:none">
                <span class="allocationZoomStatusLabel"></span>
                <button id="resetGcZoomButton" class="resetZoomButton">Reset Zoom</button>
            </div>

            <div id="timeSummary">Allocation Amount by Generation</div>

            <!-- id lets rebuildGcSummaryTiles (snapshotGcStats.js) find and
                 fully rebuild this block's innerHTML after a GC Detailed
                 table row is hidden, mirroring the exact template below with
                 recomputed numbers (a JS port of GcStatsCalculations.ts's
                 computeAllocationAmountStats, same "preserved as-is"
                 lexicographic-sort median quirk that file documents) - same
                 "recompute -> rebuild whole block" discipline
                 updateOneRankedTypesTable already uses for the Allocation
                 ranked-types table. -->
            <div class="summaryGcDiv" id="allocationAmountSummaryGcDiv">
                <div class="total">
                    <div>Total</div>
                    <div>Total<span>${allocTotal} ${totalTotalValue}</span></div>
                    <div>Largest<span>${allocHighest} ${dataValue}</span></div>
                    <div>Smallest<span>${allocLowest} ${dataValue}</span></div>
                    <div>Average<span>${allocAverage} ${dataValue}</span></div>
                    <div>Median<span>${allocMedian} ${dataValue}</span></div>
                </div>
                <div class="gen0">
                    <div>Gen 0</div>
                    <div>Total<span>${allocGen0Total} ${totalTotalValue}</span></div>
                    <div>Largest<span>${allocGen0Highest} ${dataValue}</span></div>
                    <div>Smallest<span>${allocGen0Lowest} ${dataValue}</span></div>
                    <div>Average<span>${allocGen0Average} ${dataValue}</span></div>
                    <div>Median<span>${allocGen0Median} ${dataValue}</span></div>
                </div>
                <div class="gen1">
                    <div>Gen 1</div>
                    <div>Total<span>${allocGen1Total} ${totalTotalValue}</span></div>
                    <div>Largest<span>${allocGen1Highest} ${dataValue}</span></div>
                    <div>Smallest<span>${allocGen1Lowest} ${dataValue}</span></div>
                    <div>Average<span>${allocGen1Average} ${dataValue}</span></div>
                    <div>Median<span>${allocGen1Median} ${dataValue}</span></div>
                </div>
                <div class="gen2">
                    <div>Gen 2</div>
                    <div>Total<span>${allocGen2Total} ${totalTotalValue}</span></div>
                    <div>Largest<span>${allocGen2Highest} ${dataValue}</span></div>
                    <div>Smallest<span>${allocGen2Lowest} ${dataValue}</span></div>
                    <div>Average<span>${allocGen2Average} ${dataValue}</span></div>
                    <div>Median<span>${allocGen2Median} ${dataValue}</span></div>
                </div>
                <div class="loh">
                    <div>LOH</div>
                    <div>Total<span>${allocLOHTotal} ${totalTotalValue}</span></div>
                    <div>Largest<span>${allocLOHHighest} ${dataValue}</span></div>
                    <div>Smallest<span>${allocLOHLowest} ${dataValue}</span></div>
                    <div>Average<span>${allocLOHAverage} ${dataValue}</span></div>
                    <div>Median<span>${allocLOHMedian} ${dataValue}</span></div>
                </div>
            </div>

            <div id="timeSummary">Time Spent by Generation</div>

            <!-- Same rebuild-in-place convention as
                 allocationAmountSummaryGcDiv above, driven by a JS port of
                 computePauseTimeStats instead. -->
            <div class="summaryGcDiv time" id="timeSpentSummaryGcDiv">
                <div class="total">
                    <div>Total</div>
                    <div>Count<span>${timeinsideEachGc.length}</span></div>
                    <div>Total<span>${totalTimeInGc} ms</span></div>
                    <div>Largest<span>${highestTimeInGc} ms</span></div>
                    <div>Smallest<span>${lowestTimeInGc} ms</span></div>
                    <div>Average<span>${averageTimeInGc} ms</span></div>
                    <div>Median<span>${medianTimeInGc} ms</span></div>
                </div>
                <div class="gen0">
                    <div>Gen 0</div>
                    <div>Count<span>${gen0TimesInEachGc.length}</span></div>
                    <div>Total<span>${gen0TotalTimeInGc} ms</span></div>
                    <div>Largest<span>${gen0HighestTimeInGc} ms</span></div>
                    <div>Smallest<span>${gen0LowestTimeInGc} ms</span></div>
                    <div>Average<span>${gen0AverageTimeInGc} ms</span></div>
                    <div>Median<span>${gen0MedianTimeInGc} ms</span></div>
                </div>
                <div class="gen1">
                    <div>Gen 1</div>
                    <div>Count<span>${gen1TimesInEachGc.length}</span></div>
                    <div>Total<span>${gen1TotalTimeInGc} ms</span></div>
                    <div>Largest<span>${gen1HighestTimeInGc} ms</span></div>
                    <div>Smallest<span>${gen1LowestTimeInGc} ms</span></div>
                    <div>Average<span>${gen1AverageTimeInGc} ms</span></div>
                    <div>Median<span>${gen1MedianTimeInGc} ms</span></div>
                </div>
                <div class="gen2">
                    <div>Gen 2</div>
                    <div>Count<span>${gen2TimesInEachGc.length}</span></div>
                    <div>Total<span>${gen2TotalTimeInGc} ms</span></div>
                    <div>Largest<span>${gen2HighestTimeInGc} ms</span></div>
                    <div>Smallest<span>${gen2LowestTimeInGc} ms</span></div>
                    <div>Average<span>${gen2AverageTimeInGc} ms</span></div>
                    <div>Median<span>${gen2MedianTimeInGc} ms</span></div>
                </div>
            </div>

            <div class="spacer"></div>

            <div class="gcDataContainer">
                ${canvasData}
                <!-- Load Chart.js exactly once, here, before it's needed by
                     any inline chart-building code below. It used to be
                     re-declared after every gcDataContainer div further down
                     this page (5 copies total) - since none of those tags had
                     async/defer, the browser fetched and fully executed the
                     whole library 5 times on every load, blocking HTML
                     parsing each time regardless of capture size. -->
                <script src="${chartjs}"></script>
            </div>

            <h2 class="divider">GC Pause Time by Generation</h2>
            ${captureTimeRangeHtml}

            <div class="gcDataContainer" id="pauseTimeSpacer">
                ${pauseTimeCanvasData}
            </div>

            <h2 class="divider">GC Usage Over Time</h2>
            ${captureTimeRangeHtml}

            <div class="gcDataContainer" id="nextSpacer">
                ${totalCanvasData}
            </div>

            <h2 class="divider">Heap Fragmentation Over Time</h2>
            <div class="heapSnapshotLoadRow">
                ${captureTimeRangeHtml}
                <button id="loadHeapSnapshotBtn" class="loadHeapSnapshotBtn" title="Load a gcHeapAnalyzer JSON output to see free chunk distribution, pinned types, and LOH census">Load Heap Snapshot</button>
            </div>

            <div class="gcDataContainer" id="fragmentationSpacer">
                ${fragmentationCanvasData}
            </div>

            ${hasHeapContents ? `<h2 class="divider">Top LOH Allocating Types</h2>
            <div id="lohTypesSection"></div>` : ``}

            <h2 class="divider">Per Heap GC Usage Over Time</h2>

            <div class="gcDataContainer">
                ${perHeapCanvasData}
            </div>
            </div>

            <div id="tab-detailed" class="tabPanel"></div>
            <div id="tab-heapSnapshot" class="tabPanel"></div>
            <!-- Deferred: display:none on .tabPanel only skips layout/paint,
                 not DOM construction - the browser would still have to parse
                 and build a <tr>/<td> node for every GC up front if this
                 table were inlined directly above like the other panel's
                 content. Wrapping it in a comment (the same trick
                 hiddenData/gcCountsByGen already use below) keeps it as
                 inert text until snapshotGcStats.js injects it into
                 #tab-detailed on the Detailed tab's first click. -->
            <span style="display:none" id="detailTableHtml"><!--${detailTableHtml}--></span>

                </div>
            ${hasHeapContents ? `<div id="view-heapContents" class="viewPanel"></div>
            <!-- Same lazy-inject pattern as detailTableHtml above - constructed
                 only on the "Heap Contents" nav button's first click. -->
            <span style="display:none" id="allocationSummaryHtml"><!--${allocationSummaryHtml}--></span>` : ``}
            ${hasExceptions ? `<div id="view-exceptions" class="viewPanel"></div>
            <!-- Same lazy-inject pattern as allocationSummaryHtml above -
                 constructed only on the "Exceptions" nav button's first
                 click. -->
            <span style="display:none" id="exceptionSummaryHtml"><!--${exceptionSummaryHtml}--></span>` : ``}
            ${hasContention ? `<div id="view-contention" class="viewPanel"></div>
            <!-- Same lazy-inject pattern as exceptionSummaryHtml above -
                 constructed only on the "Contention" nav button's first
                 click. -->
            <span style="display:none" id="contentionHtml"><!--${contentionHtml}--></span>` : ``}
            ${hasCpuProfile ? `<div id="view-profile" class="viewPanel"></div>
            <!-- Same lazy-inject pattern as allocationSummaryHtml above -
                 constructed only on the "Profile" nav button's first click.
                 The flame graph itself still isn't built at that point -
                 it needs cpuProfileJson (see media/flameGraph.js), not just
                 this HTML - see snapshotGcStats.js's view switcher. -->
            <span style="display:none" id="cpuProfileHtml"><!--${cpuProfileHtml}--></span>` : ``}

            <script nonce="${nonce}" src="${chartZoomScriptUri}"></script>
            <script nonce="${nonce}" src="${allocationScriptUri}"></script>
            <script nonce="${nonce}" src="${drillDownScriptUri}"></script>
            <script nonce="${nonce}" src="${exceptionDrillDownScriptUri}"></script>
            <script nonce="${nonce}" src="${cpuDrillDownScriptUri}"></script>
            <script nonce="${nonce}" src="${contentionDrillDownScriptUri}"></script>
            <script nonce="${nonce}" src="${flameGraphScriptUri}"></script>
            <script nonce="${nonce}" src="${scriptUri}"></script>
        </body>
    </html>`;

    return htmlToReturn;
}

// A `<script type="application/json">` tag's raw-text parsing only looks for
// the literal byte sequence "</script" to find its end - a "<" from embedded
// data (e.g. a "</script>" substring inside a GC Reason/Type string) would
// otherwise truncate the tag early. `<` is a valid JSON string escape
// for "<" that JSON.parse decodes transparently, so this is safe to apply to
// any JSON.stringify output before embedding it in a script tag.
export function escapeJsonForInlineScript(json: string): string {
    return json.replace(/</g, '\\u003c');
}

export function getNonce() {
    let text = '';
    const possible = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789';
    for (let i = 0; i < 32; i++) {
        text += possible.charAt(Math.floor(Math.random() * possible.length));
    }
    return text;
}
