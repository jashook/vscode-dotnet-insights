// Renders the "Threading" view against gcData["threadingSummary"] (see
// nettraceParser/Threading/ThreadingJsonExporter.cs).
//
// What this view can and cannot say is worth stating up front, because it
// shaped the whole design. The CLR's thread-pool events are plentiful but
// bare: 0 of the 12.2M ThreadPoolWorkerThread/Wait events on the reference
// capture carry a stack, so they can report how MANY threads the pool had at
// any moment but never WHERE any of them was stuck. Only three threading
// events carry stacks at all - ThreadCreating, Contention/LockCreated, and
// AdjustmentStats (whose stack is always the same hill-climbing frame and so
// is useless).
//
// The "during pool stalls" table is what closes that gap: it comes from
// joining CPU samples to the timestamps of adjustments the runtime made
// because work stopped progressing. That is the one place this view can say
// what threads were actually doing.

import { renderSortableTableHeader } from './GcDetailTableRenderer';

function escapeHtmlForThreading(value: string): string {
    return String(value)
        .replace(/&/g, "&amp;")
        .replace(/</g, "&lt;")
        .replace(/>/g, "&gt;");
}

function formatMethodNameHtml(rawName: string): string {
    if (rawName.startsWith("<unresolved") || rawName === "<unresolved>") {
        return `<span class="unresolvedFrame">${escapeHtmlForThreading(rawName)}</span>`;
    }

    const lastDotIndex = rawName.lastIndexOf(".");
    if (lastDotIndex === -1) {
        return `<span class="methodName">${escapeHtmlForThreading(rawName)}</span>`;
    }

    return `<span class="methodTypePrefix">${escapeHtmlForThreading(rawName.slice(0, lastDotIndex + 1))}</span>` +
        `<span class="methodName">${escapeHtmlForThreading(rawName.slice(lastDotIndex + 1))}</span>`;
}

function formatMSec(valueMSec: number): string {
    if (valueMSec >= 1000) {
        return `${(valueMSec / 1000).toFixed(2)} s`;
    }

    return `${valueMSec.toFixed(1)} ms`;
}

export function renderThreadingView(threadingSummary: any, threadingMethodNames: string[]): string {
    if (!threadingSummary || !threadingSummary["hasThreadPoolData"]) {
        return `<div class="detailTable"><p>No thread pool events were recorded in this capture.</p></div>`;
    }

    const summaryTilesHtml = `
        <div class="summaryGcDiv">
            <div class="total">
                <div>Worker Threads</div>
                <div>Peak<span>${threadingSummary["peakActiveWorkerThreads"]}</span></div>
                <div>Min<span>${threadingSummary["minActiveWorkerThreads"]}</span></div>
                <div>Final<span>${threadingSummary["finalActiveWorkerThreads"]}</span></div>
            </div>
            <div class="total">
                <div>Pool Activity</div>
                <div>Adjustments<span>${Number(threadingSummary["adjustmentCount"]).toLocaleString()}</span></div>
                <div>Threads Created<span>${Number(threadingSummary["threadCreationCount"]).toLocaleString()}</span></div>
                <div>Locks Created<span>${Number(threadingSummary["lockCreationCount"]).toLocaleString()}</span></div>
            </div>
        </div>`;

    const timelineHtml = `
        <div class="cpuTimelineSection">
            <div class="threadingChartHint">Worker thread count over the capture (min/average/max per bucket).</div>
            <div class="cpuTimelineContainer"><canvas id="threadingTimeline"></canvas></div>
        </div>`;

    return `${summaryTilesHtml}${timelineHtml}` +
        `${renderStallCorrelation(threadingSummary["stallCorrelation"], threadingMethodNames)}` +
        `${renderAdjustmentReasons(threadingSummary["adjustmentReasons"])}` +
        `${renderStackedEventTable("threadCreations", "Thread Creations", threadingSummary["threadCreations"], threadingMethodNames)}` +
        `${renderStackedEventTable("lockCreations", "Lock Creations", threadingSummary["lockCreations"], threadingMethodNames)}`;
}

// The centerpiece: what threads were actually doing at the moments the pool
// decided it had to grow because work was not progressing.
function renderStallCorrelation(stallCorrelation: any, methodNames: string[]): string {
    if (!stallCorrelation) {
        return `<div class="threadingSection">
            <div class="threadingSectionTitle">During pool stalls</div>
            <div class="threadingNote">The runtime made no stall-driven thread-pool adjustments in this capture
            (or it contains no CPU samples to explain them with), so there is nothing to correlate.</div>
        </div>`;
    }

    const frames = stallCorrelation["frames"] || [];
    const totalSamples = frames.reduce((sum: number, frame: any) => sum + frame["sampleCount"], 0);

    var rows = "";
    for (var index = 0; index < frames.length; ++index) {
        const frame = frames[index];
        const percent = totalSamples > 0 ? ((frame["sampleCount"] * 100) / totalSamples) : 0;

        rows += `<tr>` +
            `<td style="text-align:left">${formatMethodNameHtml(methodNames[frame["frame"]])}</td>` +
            `<td>${Number(frame["sampleCount"]).toLocaleString()}</td>` +
            `<td>${percent.toFixed(1)}</td>` +
            `</tr>`;
    }

    const header = renderSortableTableHeader([
        ["Method", "string"],
        ["Samples", "number"],
        ["% of Blocked", "number"]
    ]);

    // The parked-worker count is shown, not hidden: a stall window that is
    // mostly parked workers means the pool had spare capacity, which changes
    // how the frames below should be read.
    return `<div class="threadingSection">
        <div class="threadingSectionTitle">During pool stalls</div>
        <div class="threadingNote">
            The thread-pool events carry no stacks, so this is built by joining CPU samples to the
            <b>${Number(stallCorrelation["stallAdjustmentCount"]).toLocaleString()}</b> adjustments the runtime made because work
            stopped progressing (±${stallCorrelation["windowHalfWidthMSec"]}ms around each).
            <b>${Number(stallCorrelation["samplesInWindows"]).toLocaleString()}</b> samples across
            <b>${Number(stallCorrelation["threadsInWindows"]).toLocaleString()}</b> threads fell in those windows;
            ${Number(stallCorrelation["parkedWorkerSamples"]).toLocaleString()} of them were idle parked workers waiting for work
            (excluded below - they mean spare capacity, not a blockage).
        </div>
        <div class="detailTable threadingTable"><table id="threadingStallTable">${header}${rows}</table></div>
    </div>`;
}

function renderAdjustmentReasons(adjustmentReasons: any[]): string {
    if (!adjustmentReasons || adjustmentReasons.length === 0) {
        return "";
    }

    var rows = "";
    for (var index = 0; index < adjustmentReasons.length; ++index) {
        const reason = adjustmentReasons[index];
        const stallClass = reason["isStallDriven"] ? ' class="threadingStallRow"' : '';

        rows += `<tr${stallClass}>` +
            `<td style="text-align:left">${escapeHtmlForThreading(reason["reasonName"])}` +
            `${reason["isStallDriven"] ? ' <span class="threadingStallBadge">stall</span>' : ''}</td>` +
            `<td>${Number(reason["count"]).toLocaleString()}</td>` +
            `</tr>`;
    }

    const header = renderSortableTableHeader([
        ["Adjustment Reason", "string"],
        ["Count", "number"]
    ]);

    return `<div class="threadingSection">
        <div class="threadingSectionTitle">Why the pool resized</div>
        <div class="threadingNote">The runtime's hill-climbing algorithm adjusts the worker count as it goes.
        Reasons marked <span class="threadingStallBadge">stall</span> mean it added threads because queued work was not
        progressing - not because more work arrived.</div>
        <div class="detailTable threadingTable"><table id="threadingReasonTable">${header}${rows}</table></div>
    </div>`;
}


// Renders a captured stack using the SAME nested caller-tree table the
// allocation/exception/CPU/contention drill-downs use, rather than a bespoke
// list - identical colgroup, identical .callerRow markup (leading spacer cell
// pairing with the colgroup's own spacer <col>), identical per-depth indent.
// A threading stack is linear and carries no counts, so the three numeric
// columns those trees use for count/% are emitted empty: the point is that a
// stack looks the same everywhere in this UI, not that this table invents
// numbers it does not have.
//
// Mirrors contentionDrillDownStats.js's CONTENTION_CALLER_TREE_COLGROUP and
// renderContentionTreeRow; the indent constants are drillDownStats.js's own
// CALLER_INDENT_EM_PER_LEVEL / CALLER_INDENT_MAX_EM.
const CALLER_TREE_COLGROUP = `<colgroup><col style="width: 1.6em"><col><col class="bytesColumn"><col class="percentColumn"><col class="percentColumn"></colgroup>`;
const CALLER_INDENT_EM_PER_LEVEL = 0.85;
const CALLER_INDENT_MAX_EM = 17;

function renderStackAsCallerTree(frames: number[], methodNames: string[]): string {
    if (!frames || frames.length === 0) {
        return `<p style="padding:8px;margin:0">No stack was captured for this event.</p>`;
    }

    var frameRows = "";
    for (var frameIndex = 0; frameIndex < frames.length; ++frameIndex) {
        const uncappedIndentEm = frameIndex * CALLER_INDENT_EM_PER_LEVEL;
        const indentEm = uncappedIndentEm < CALLER_INDENT_MAX_EM ? uncappedIndentEm : CALLER_INDENT_MAX_EM;

        frameRows += `<tr class="callerRow">` +
            `<td></td>` +
            `<td style="padding-left: ${indentEm}em">` +
            `<span class="leafMethodToggle leafMethodToggleEmpty"></span>` +
            `${formatMethodNameHtml(methodNames[frames[frameIndex]])}</td>` +
            `<td></td><td></td><td></td>` +
            `</tr>`;
    }

    return `<table class="callerTreeInner">${CALLER_TREE_COLGROUP}${frameRows}</table>`;
}

// Thread and lock creations are the only threading events that keep a usable
// stack, so each row expands to show it.
function renderStackedEventTable(idPrefix: string, title: string, stackedEvents: any[], methodNames: string[]): string {
    if (!stackedEvents || stackedEvents.length === 0) {
        return "";
    }

    var rows = "";
    for (var index = 0; index < stackedEvents.length; ++index) {
        const stackedEvent = stackedEvents[index];
        const frames = stackedEvent["frames"] || [];
        const topFrame = frames.length > 0 ? methodNames[frames[0]] : "<no stack captured>";

        rows += `<tr class="threadingStackRow" data-threading-expandable="true" data-threading-target="${idPrefix}Detail${index}">` +
            `<td style="text-align:left"><span class="leafMethodToggle">▸</span>${formatMethodNameHtml(topFrame)}</td>` +
            `<td>${formatMSec(stackedEvent["relativeMSec"])}</td>` +
            `<td>${stackedEvent["threadId"]}</td>` +
            `</tr>`;

        rows += `<tr id="${idPrefix}Detail${index}" class="callPathsDetail"><td colspan="3" class="callerTreeCell">` +
            `${renderStackAsCallerTree(frames, methodNames)}` +
            `</td></tr>`;
    }

    const header = renderSortableTableHeader([
        ["Created At", "string"],
        ["Time", "number"],
        ["Thread", "number"]
    ]);

    return `<div class="threadingSection">
        <div class="threadingSectionTitle">${escapeHtmlForThreading(title)} (${stackedEvents.length.toLocaleString()})</div>
        <div class="threadingNote">Click a row to see the full stack.</div>
        <div class="detailTable threadingTable"><table id="${idPrefix}Table">${header}${rows}</table></div>
    </div>`;
}
