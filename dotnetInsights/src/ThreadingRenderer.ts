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
//
// And what closes the OTHER gap - the one that made this view misleading
// rather than merely incomplete - is the thread roster, from
// threadingSummary["threadActivity"]. Joining samples to stall windows
// answers "what stack was this thread in", which on a real service is mostly
// answered by threads that are parked on purpose: native poll loops, message
// consumers, watchers, the runtime's own gate and timer threads. They look
// blocked in every window ever taken, so every table here used to be topped
// by them. The roster classifies each thread over the WHOLE capture (see
// nettraceParser/Threading/ThreadActivityProfiler.cs) and every table below
// reads that classification, so what is left is the part of the picture that
// actually changed when the pool stalled.

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
            ${renderThreadRosterTile(threadingSummary["threadActivity"])}
        </div>`;

    // Same zoom-status/reset-button idiom every other timeline on this page
    // uses (cpuTimelineZoomStatus, contentionTimelineZoomStatus): hidden until
    // a drag actually zooms, and it names Backspace so the keyboard/gesture
    // path is discoverable rather than folklore.
    const timelineHtml = `
        <div class="cpuTimelineSection">
            <div class="threadingChartHint">Worker thread count over the capture (min/average/max per bucket). Drag horizontally to zoom.</div>
            <div class="allocationZoomStatus" id="threadingTimelineZoomStatus" style="display:none">
                <span class="allocationZoomStatusLabel" id="threadingTimelineZoomLabel"></span>
                <button class="resetZoomButton" id="threadingTimelineResetZoomBtn">Reset Zoom (Backspace)</button>
            </div>
            <div class="cpuTimelineContainer"><canvas id="threadingTimeline"></canvas></div>
        </div>`;

    return `${summaryTilesHtml}${timelineHtml}` +
        `${renderThreadRoster(threadingSummary["threadActivity"], threadingMethodNames)}` +
        `${renderStallCorrelation(threadingSummary["stallCorrelation"], threadingSummary["threadActivity"], threadingMethodNames)}` +
        `${renderAdjustmentReasons(threadingSummary["adjustmentReasons"])}` +
        `${renderAdjustmentsTable(threadingSummary)}` +
        `${renderStackedEventTable("threadCreations", "Thread Creations", threadingSummary["threadCreations"], threadingMethodNames, true)}` +
        `${renderStackedEventTable("lockCreations", "Lock Creations", threadingSummary["lockCreations"], threadingMethodNames, false)}`;
}

function formatPercent(fraction: number): string {
    const percent = Number(fraction) * 100;

    if (percent > 0 && percent < 0.1) {
        // "0.0%" for a thread that ran 60 managed samples out of 222,240 is
        // technically right and reads as "never", which is a different claim
        // than the one being made.
        return "&lt;0.1%";
    }

    return `${percent.toFixed(1)}%`;
}

// The summary tile, so the headline number is visible without scrolling to
// the roster: how much of this capture's thread activity the reader can skip.
function renderThreadRosterTile(threadActivity: any): string {
    if (!threadActivity || !threadActivity["hasSampleTypeData"]) {
        return "";
    }

    const threadCount = Number(threadActivity["threadCount"]);
    const benignCount = Number(threadActivity["benignThreadCount"]);
    const sampleCount = Number(threadActivity["sampleCount"]);
    const benignSampleCount = Number(threadActivity["benignSampleCount"]);
    const benignSamplePercent = sampleCount > 0 ? (benignSampleCount * 100) / sampleCount : 0;

    return `<div class="total">
                <div>Threads</div>
                <div>Total<span>${threadCount.toLocaleString()}</span></div>
                <div>Worth Reading<span>${(threadCount - benignCount).toLocaleString()}</span></div>
                <div>Benignly Parked<span>${benignCount.toLocaleString()} (${benignSamplePercent.toFixed(0)}% of samples)</span></div>
            </div>`;
}

// One row per thread, classified over the whole capture. This is the
// supplemental information every other table on this page now leans on, and
// it is deliberately the first table: deciding which threads to ignore is
// what makes the rest of the page readable.
//
// Every input to a classification is shown next to its verdict, not just the
// verdict. A label with no numbers behind it gets ignored the first time it
// looks wrong, and this one WILL look wrong on some thread somebody knows
// well - at which point the fix is for them to see managedFraction and
// dominant-stack share and judge for themselves.
function renderThreadRoster(threadActivity: any, methodNames: string[]): string {
    if (!threadActivity) {
        return "";
    }

    const threads = threadActivity["threads"] || [];

    if (threads.length === 0) {
        return "";
    }

    // The classification's own precondition, stated rather than silently
    // degraded: without the sample profiler's managed/native flag there is no
    // way to tell a thread parked in a native call from one running native
    // code, so nothing is called parked (see ThreadActivityProfiler.
    // ClassifyRole) and this table is just a thread listing.
    const noSampleTypeNote = threadActivity["hasSampleTypeData"]
        ? ""
        : `<div class="threadingNote threadingNoSampleTypeNote">This capture's CPU samples carry no managed/native flag, so no thread
           can be shown to be parked and every thread below is listed as active. Captures taken with
           <b>Microsoft-DotNETCore-SampleProfiler</b> enabled (the <b>dotnet-trace collect</b> default) carry it.</div>`;

    // A dotnet-trace collect-linux capture is sampled by perf_events, which
    // knows nothing about managed code, so its samples carry no
    // ThreadSampleType at all - the flag is DERIVED from whether each sample's
    // leaf frame resolved to managed code. That derivation is reasonable but
    // the thresholds this table's parked/blocked classification uses were
    // calibrated against the runtime's own signal, so the difference is
    // labelled rather than left to look identical to a collect capture.
    const derivedSampleTypeNote = threadActivity["sampleTypeSource"] === "derived"
        ? `<div class="threadingNote threadingDerivedSampleTypeNote">This capture was taken with <b>dotnet-trace collect-linux</b>, whose
           samples come from <b>perf_events</b> and carry no managed/native flag. The classification below derives one from whether each
           sample's innermost frame resolved to managed code. That is a close stand-in for the runtime's own signal, but the parked
           and blocked thresholds were tuned against captures that carry it, so treat these roles as indicative rather than exact.</div>`
        : "";

    var rows = "";
    for (var index = 0; index < threads.length; ++index) {
        const thread = threads[index];
        const topStacks = thread["topStacks"] || [];
        const topFrames = topStacks.length > 0 ? (topStacks[0]["frames"] || []) : [];
        const topLeaf = topFrames.length > 0 ? methodNames[topFrames[0]] : "&lt;no stack captured&gt;";
        const isBenign = thread["isBenign"] === true;

        const contentionCell = Number(thread["contentionCount"]) > 0
            ? `${Number(thread["contentionCount"]).toLocaleString()} / ${formatMSec(Number(thread["contentionWaitMSec"]))}`
            : `<span class="threadingNoSnapshot">none</span>`;

        rows += `<tr class="threadingStackRow${isBenign ? ' threadingBenignRow' : ''}" ` +
            `data-threading-expandable="true" data-threading-target="threadRosterDetail${index}">` +
            `<td style="text-align:left"><span class="leafMethodToggle">&#9656;</span>` +
            `<span class="threadingRoleBadge threadingRoleBadge${Number(thread["role"])}" title="${escapeHtmlForThreading(String(thread["explanation"] || ""))}">` +
            `${escapeHtmlForThreading(String(thread["roleName"]))}</span></td>` +
            `<td>${Number(thread["threadId"]).toLocaleString()}</td>` +
            `<td style="text-align:left">${formatMethodNameHtml(topLeaf)}</td>` +
            `<td>${formatPercent(thread["managedFraction"])}</td>` +
            `<td title="Share of this thread's samples in its top three stacks together - the concentration the parked test keys on. Its single top stack alone is ${formatPercent(thread["dominantStackShare"])}.">` +
            `${formatPercent(thread["topStacksShare"] !== undefined ? thread["topStacksShare"] : thread["dominantStackShare"])}</td>` +
            `<td>${formatMSec(Number(thread["longestContinuousIdleMSec"]))}</td>` +
            `<td>${Number(thread["wakeCount"]).toLocaleString()}</td>` +
            `<td style="text-align:left">${contentionCell}</td>` +
            `</tr>`;

        // Lazy, like the adjustment drill-downs below and for the same
        // reason: 300 rows x 3 stacks x up to 40 frames is tens of thousands
        // of table rows built into the initial HTML for a table whose rows are
        // mostly never opened. Built on first expand by
        // buildThreadRosterDetailHtml in snapshotGcStats.js.
        rows += `<tr id="threadRosterDetail${index}" class="callPathsDetail" data-threading-thread-lazy="${index}">` +
            `<td colspan="8" class="callerTreeCell"></td></tr>`;
    }

    const header = renderSortableTableHeader([
        ["Role", "string"],
        ["Thread", "number"],
        // Named for what it is rather than "Method": for a parked thread this
        // is the call it is parked IN, which is the single most useful cell in
        // the row.
        ["Sitting In", "string"],
        // "Managed", not "CPU": the runtime's flag says the thread was running
        // managed code, which is not the same as it being on a core, and
        // labelling it CPU% would be a measurement this view does not have.
        ["Managed %", "number"],
        // "Same Loop", not "Same Stack": the figure is the top THREE stacks
        // together, because a parked worker is a small loop with two or three
        // park sites rather than one frozen stack (see TopStacksShare).
        ["Same Loop %", "number"],
        ["Longest Park", "number"],
        ["Wakes", "number"],
        ["Contention (n / blocked)", "number"]
    ]);

    const expandControlsHtml = `<div class="drillDownExpandControls">` +
        `<button class="drillDownExpandControlButton" type="button" data-threading-expand-target="threadRosterTable" data-threading-expand="true">Expand All</button>` +
        `<button class="drillDownExpandControlButton" type="button" data-threading-expand-target="threadRosterTable" data-threading-expand="false">Collapse All</button>` +
        `</div>`;

    const totalCount = Number(threadActivity["threadCount"]);
    const truncatedNote = totalCount > threads.length
        ? ` The first <b>${threads.length.toLocaleString()}</b> of <b>${totalCount.toLocaleString()}</b> threads are shown - the list is
           ordered most-actionable-first, so what is cut is the benign end.`
        : "";

    return `<div class="threadingSection">
        <div class="threadingSectionTitle">Threads (<span class="threadingSectionCount">${threads.length.toLocaleString()}</span>)</div>
        <div class="threadingNote">
            What each thread did across the <b>whole capture</b>, so the ones that only ever look blocked can be told from
            the ones that are. A long-running poller, a message consumer or the runtime's own timer thread sits in one
            unchanging blocking call from start to finish - it appears in every stall window taken and explains none of
            them. Rows marked benign are dimmed and sorted last; the tables below already exclude them.
            ${truncatedNote}
        </div>
        ${renderThreadRoleLegend(threadActivity["roles"])}
        ${noSampleTypeNote}
        ${derivedSampleTypeNote}
        ${ZOOM_AGGREGATE_NOTE_HTML}
        ${expandControlsHtml}
        <div class="detailTable threadingTable"><table id="threadRosterTable">${header}${rows}</table></div>
    </div>`;
}

function renderThreadRoleLegend(roles: any[]): string {
    if (!roles || roles.length === 0) {
        return "";
    }

    var chips = "";
    for (var index = 0; index < roles.length; ++index) {
        const role = roles[index];

        chips += `<span class="threadingRoleChip${role["isBenign"] ? " threadingRoleChipBenign" : ""}">` +
            `<span class="threadingRoleBadge threadingRoleBadge${Number(role["role"])}">${escapeHtmlForThreading(String(role["roleName"]))}</span>` +
            `<span class="threadingRoleChipCount">${Number(role["threadCount"]).toLocaleString()}</span></span>`;
    }

    return `<div class="threadingRoleLegend">${chips}</div>`;
}

// The centerpiece: what threads were actually doing at the moments the pool
// decided it had to grow because work was not progressing.
function renderStallCorrelation(stallCorrelation: any, threadActivity: any, methodNames: string[]): string {
    if (!stallCorrelation) {
        return `<div class="threadingSection">
            <div class="threadingSectionTitle">During pool stalls</div>
            <div class="threadingNote">The runtime made no stall-driven thread-pool adjustments in this capture
            (or it contains no CPU samples to explain them with), so there is nothing to correlate.</div>
        </div>`;
    }

    const frames = stallCorrelation["frames"] || [];
    const benignFrames = stallCorrelation["benignFrames"] || [];
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

    const benignSamples = Number(stallCorrelation["benignThreadSamples"] || 0);
    const benignThreads = Number(stallCorrelation["benignThreadsInWindows"] || 0);
    // Three states, not two, and they get three different sentences. A payload
    // with no threadActivity block at all came from a nettraceParser build
    // that predates the classification (see CLAUDE.md's stale-cache trap) -
    // telling that reader their CAPTURE is missing a flag would send them to
    // re-capture something that would not help.
    const hasThreadActivity = threadActivity !== undefined && threadActivity !== null;
    const hasClassification = hasThreadActivity && threadActivity["hasSampleTypeData"];

    // Everything the table set aside, ranked the same way and shown under its
    // own heading. This is the audit trail for the exclusion: without it a
    // reader who expected to see a frame here has no way to tell "the filter
    // dropped it" from "it never happened", and a filter that cannot be
    // checked is one nobody trusts the first time it surprises them.
    var benignRows = "";
    for (var benignIndex = 0; benignIndex < benignFrames.length; ++benignIndex) {
        const benignFrame = benignFrames[benignIndex];
        const benignPercent = benignSamples > 0 ? ((benignFrame["sampleCount"] * 100) / benignSamples) : 0;

        benignRows += `<tr class="threadingBenignRow">` +
            `<td style="text-align:left">${formatMethodNameHtml(methodNames[benignFrame["frame"]])}</td>` +
            `<td>${Number(benignFrame["sampleCount"]).toLocaleString()}</td>` +
            `<td>${benignPercent.toFixed(1)}</td>` +
            `</tr>`;
    }

    const excludedSectionHtml = benignRows === ""
        ? ""
        : `<details class="threadingExcludedDetails">
            <summary>Show the ${Number(benignSamples).toLocaleString()} samples excluded from the table above
            (${benignThreads.toLocaleString()} benignly parked ${benignThreads === 1 ? "thread" : "threads"})</summary>
            <div class="threadingNote">These frames are real, and they are excluded anyway: they belong to threads that sit in the
            same call for the entire capture whether or not the pool is stalling, so their presence in a stall window carries no
            information about the stall. A frame can legitimately appear in <b>both</b> tables - the same
            <b>Consume</b> or <b>Read</b> call on a thread that does real work is a finding, and on a thread that does
            nothing else is not, which is exactly why the exclusion is per thread and not per method name.</div>
            <div class="detailTable threadingTable"><table id="threadingBenignStallTable">${header}${benignRows}</table></div>
        </details>`;

    // What the numbers mean is stated before the table rather than after it,
    // because the parked/benign split is the difference between this table
    // being useful and it being a list of the process's idle threads.
    var exclusionNoteHtml: string;

    if (hasClassification) {
        exclusionNoteHtml = `Samples from <b>${benignThreads.toLocaleString()}</b> benignly parked threads
           (<b>${Number(benignSamples).toLocaleString()}</b> samples) are excluded below - see the Threads table above for what
           each of them was and why.`;
    } else if (hasThreadActivity) {
        exclusionNoteHtml = `This capture's samples carry no managed/native flag, so parked threads could not be identified and
           nothing has been excluded below beyond the pool's own idle workers.`;
    } else {
        exclusionNoteHtml = `This data was produced by an older <b>nettraceParser</b> that did not classify threads, so nothing
           has been excluded below beyond the pool's own idle workers.`;
    }

    return `<div class="threadingSection">
        <div class="threadingSectionTitle">During pool stalls</div>
        <div class="threadingNote">
            The thread-pool events carry no stacks, so this is built by joining CPU samples to the
            <b>${Number(stallCorrelation["stallAdjustmentCount"]).toLocaleString()}</b> adjustments the runtime made because work
            stopped progressing (the ${stallCorrelation["lookbackMSec"]}ms <b>before</b> each - never after, since samples
            from after an adjustment show the pool it produced rather than the one that forced it).
            <b>${Number(stallCorrelation["samplesInWindows"]).toLocaleString()}</b> samples across
            <b>${Number(stallCorrelation["threadsInWindows"]).toLocaleString()}</b> threads fell in those windows;
            ${Number(stallCorrelation["parkedWorkerSamples"]).toLocaleString()} of them were idle pool workers waiting for work.
            ${exclusionNoteHtml}
        </div>
        ${ZOOM_AGGREGATE_NOTE_HTML}
        <div class="detailTable threadingTable"><table id="threadingStallTable">${header}${rows}</table></div>
        ${excludedSectionHtml}
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
        ${ZOOM_AGGREGATE_NOTE_HTML}
        <div class="detailTable threadingTable"><table id="threadingReasonTable">${header}${rows}</table></div>
    </div>`;
}

// Shown only while the timeline is zoomed. These two tables are aggregated in
// nettraceParser over the WHOLE capture (the payload carries totals, not the
// per-event rows they were summed from), so unlike the thread/lock creation
// tables they cannot be narrowed to a time window here. Saying so is the point:
// a zoomed view where some tables silently follow the zoom and others silently
// do not is worse than one that admits which is which.
const ZOOM_AGGREGATE_NOTE_HTML =
    `<div class="threadingZoomAggregateNote" style="display:none">Whole-capture totals - this table is aggregated by the parser and does not follow the timeline zoom.</div>`;


// Every individual decision the pool made, each drillable into what every
// thread was doing at that instant.
//
// The drill-down content is NOT rendered here. 500 adjustments x ~18 distinct
// stacks each would be ~9,000 caller trees built into the initial HTML for a
// view whose rows are mostly never opened; it is built on first expand in
// snapshotGcStats.js instead, the same lazy pattern the Contention and
// Exceptions drill-downs already use (data-contention-lazy / the
// typeDrillDown lazy rows).
function renderAdjustmentsTable(threadingSummary: any): string {
    const adjustments = threadingSummary["adjustments"];

    if (!adjustments || adjustments.length === 0) {
        return "";
    }

    var rows = "";
    for (var index = 0; index < adjustments.length; ++index) {
        const adjustment = adjustments[index];
        const isStallDriven = adjustment["isStallDriven"];
        const delta = adjustment["workerThreadDelta"];
        const snapshot = adjustment["threadSnapshot"];

        // A row with no snapshot still expands - it explains that no CPU
        // samples landed in its window, which is a different thing from an
        // empty stack list.
        const threadsSampledCell = snapshot
            ? `${Number(snapshot["threadsSampled"]).toLocaleString()}`
            : `<span class="threadingNoSnapshot">none</span>`;

        rows += `<tr class="threadingStackRow${isStallDriven ? ' threadingStallRow' : ''}" ` +
            `data-threading-expandable="true" ` +
            `data-threading-target="poolAdjustmentsDetail${index}" ` +
            `data-threading-msec="${adjustment["relativeMSec"]}">` +
            `<td style="text-align:left"><span class="leafMethodToggle">&#9656;</span>` +
            `${escapeHtmlForThreading(adjustment["reasonName"])}` +
            `${isStallDriven ? ' <span class="threadingStallBadge">stall</span>' : ''}</td>` +
            `<td>${formatMSec(adjustment["relativeMSec"])}</td>` +
            `<td>${formatWorkerDelta(delta)}</td>` +
            `<td>${adjustment["newWorkerThreadCount"]}</td>` +
            `<td>${threadsSampledCell}</td>` +
            `</tr>`;

        rows += `<tr id="poolAdjustmentsDetail${index}" class="callPathsDetail" ` +
            `data-threading-adjustment-lazy="${index}" data-threading-msec="${adjustment["relativeMSec"]}">` +
            `<td colspan="5" class="callerTreeCell"></td></tr>`;
    }

    const header = renderSortableTableHeader([
        ["Reason", "string"],
        ["Time", "number"],
        ["Change", "number"],
        // "Target", not "Workers After": NewWorkerThreadCount is the count
        // hill climbing decided to aim for, and it visibly oscillates (64 <->
        // 84 on consecutive Climbing move adjustments in a real capture)
        // rather than tracking the live pool size, which the timeline above
        // already shows from the Wait/Start/Stop events.
        ["Target Workers", "number"],
        ["Threads Sampled", "number"]
    ]);

    const expandControlsHtml = `<div class="drillDownExpandControls">` +
        `<button class="drillDownExpandControlButton" type="button" data-threading-expand-target="poolAdjustmentsTable" data-threading-expand="true">Expand All</button>` +
        `<button class="drillDownExpandControlButton" type="button" data-threading-expand-target="poolAdjustmentsTable" data-threading-expand="false">Collapse All</button>` +
        `</div>`;

    const totalCount = Number(threadingSummary["adjustmentCount"]);
    const truncatedNote = totalCount > adjustments.length
        ? ` The first <b>${adjustments.length.toLocaleString()}</b> of <b>${totalCount.toLocaleString()}</b> adjustments are shown.`
        : "";

    return `<div class="threadingSection">
        <div class="threadingSectionTitle">Pool Adjustments
            (<span class="threadingSectionCount" id="poolAdjustmentsCount" data-threading-total="${adjustments.length}">${adjustments.length.toLocaleString()}</span>)</div>
        <div class="threadingNote">Every resize decision the runtime's hill-climbing algorithm made, and why.
        Open one to see what <b>every thread</b> was doing going into that decision.
        Threads sitting in the same stack are grouped, since the count is the finding.${truncatedNote}</div>
        ${SNAPSHOT_PROVENANCE_NOTE_HTML}
        ${expandControlsHtml}
        <div class="detailTable threadingTable"><table id="poolAdjustmentsTable">${header}${rows}</table></div>
    </div>`;
}

// Why this thread was created, built from two different grades of evidence and
// careful not to blur them.
//
// Whether the POOL created the thread is a fact, read off the creation stack
// (a frame under PortableThreadPool). The reason is weaker: ThreadCreating
// carries none, and the runtime logs its hill-climbing decision as a separate
// event with no correlation id, so the nearest preceding decision is context,
// not proven cause - and the cell says how long before it happened so that can
// be judged rather than assumed.
//
// An application thread is not an error or a gap in the data: plenty of
// threads are started by library code (gRPC, Kafka) and never involve the pool.
function renderCreationCauseCell(stackedEvent: any): string {
    const isPoolWorker = stackedEvent["isPoolWorker"] === true;

    if (!isPoolWorker) {
        return `<td style="text-align:left"><span class="threadingNoSnapshot" ` +
            `title="The creation stack has no thread-pool frame, so this thread was started by application or library code rather than by the pool.">application thread</span></td>`;
    }

    const causeIndex = stackedEvent["causeAdjustmentIndex"];

    if (causeIndex === undefined || causeIndex < 0) {
        return `<td style="text-align:left">pool worker ` +
            `<span class="threadingNoSnapshot" title="A pool worker (its stack runs through PortableThreadPool), but no pool adjustment was logged close enough before it to say which decision it followed.">(no nearby decision)</span></td>`;
    }

    const reasonName = escapeHtmlForThreading(stackedEvent["causeReasonName"]);
    const badge = stackedEvent["causeIsStallDriven"] ? ' <span class="threadingStallBadge">stall</span>' : '';
    const delayMSec = Number(stackedEvent["causeDelayMSec"]);

    return `<td style="text-align:left" title="Pool worker. The runtime's most recent pool decision before this thread was created was &quot;${escapeHtmlForThreading(stackedEvent["causeReasonName"])}&quot;, ${formatMSec(delayMSec)} earlier. The events carry no correlation id, so this is the nearest decision in time, not a proven cause.">` +
        `${reasonName}${badge} <span class="threadingCauseDelay">${formatMSec(delayMSec)} earlier</span></td>`;
}

// The provenance of every stack in this view, stated up front rather than left
// for the reader to infer from a table that looks like a stack dump.
//
// It is not a stack dump. The thread-pool events carry no stacks at all, so
// these come from the CPU sample profiler, and each thread's stack is the last
// sample taken BEFORE the decision. The backward-only direction matters enough
// to say out loud: a sample from after the adjustment would show the state the
// decision produced (the new thread already running, the queued work already
// picked up) rather than the state that caused it.
const SNAPSHOT_PROVENANCE_NOTE_HTML =
    `<div class="threadingProvenanceNote">
        <b>How these stacks are found:</b> thread-pool events carry no stacks, so each thread's stack is its
        <b>last CPU sample before</b> the adjustment - never after it, since a later sample would show the state
        the decision produced rather than the state that caused it. Each drill-down says how stale its oldest
        stack is. A thread with no sample in that window does not appear, so this is the sampled picture going
        into the decision, not a guaranteed-complete thread dump.
    </div>`;

// Signed, because the direction is the point: "+1" is the pool growing, which
// is what a stall-driven adjustment does.
function formatWorkerDelta(delta: number): string {
    if (delta > 0) {
        return `<span class="threadingDeltaUp">+${delta}</span>`;
    }

    if (delta < 0) {
        return `<span class="threadingDeltaDown">${delta}</span>`;
    }

    return `<span class="threadingDeltaFlat">0</span>`;
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
// Two columns, not the drill-downs' five. Those three extra columns exist to
// carry count/%-of-parent/%-of-total, which a linear stack does not have -
// and .callerTreeInner is table-layout:fixed, so emitting them empty is not
// free: measured at 1398px table width they consumed 928px (66%) and left the
// frame column just 398px, squeezing method names into a narrow strip with
// two-thirds of the row blank to its right. Dropping them hands that width to
// the frames. Everything that carries the SHARED look - the .callerTreeInner
// table, the .callerRow rows, the leading spacer cell/col, the per-depth
// indent - is unchanged.
const CALLER_TREE_COLGROUP = `<colgroup><col style="width: 1.6em"><col></colgroup>`;
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
            `</tr>`;
    }

    return `<table class="callerTreeInner">${CALLER_TREE_COLGROUP}${frameRows}</table>`;
}

// Thread and lock creations are the only threading events that keep a usable
// stack, so each row expands to show it.
function renderStackedEventTable(idPrefix: string, title: string, stackedEvents: any[], methodNames: string[], showCauseColumn: boolean): string {
    if (!stackedEvents || stackedEvents.length === 0) {
        return "";
    }

    const columnCount = showCauseColumn ? 4 : 3;

    var rows = "";
    for (var index = 0; index < stackedEvents.length; ++index) {
        const stackedEvent = stackedEvents[index];
        const frames = stackedEvent["frames"] || [];
        const topFrame = frames.length > 0 ? methodNames[frames[0]] : "<no stack captured>";
        const causeCell = showCauseColumn ? renderCreationCauseCell(stackedEvent) : "";

        // data-threading-msec on BOTH halves of the pair, not just the summary
        // row: the timeline's zoom filter hides rows outside the zoomed window,
        // and an already-expanded detail row left behind would render its stack
        // under a row that is no longer there.
        const relativeMSec = stackedEvent["relativeMSec"];

        rows += `<tr class="threadingStackRow" data-threading-expandable="true" data-threading-target="${idPrefix}Detail${index}" data-threading-msec="${relativeMSec}">` +
            `<td style="text-align:left"><span class="leafMethodToggle">▸</span>${formatMethodNameHtml(topFrame)}</td>` +
            `${causeCell}` +
            `<td>${formatMSec(relativeMSec)}</td>` +
            `<td>${stackedEvent["threadId"]}</td>` +
            `</tr>`;

        rows += `<tr id="${idPrefix}Detail${index}" class="callPathsDetail" data-threading-msec="${relativeMSec}"><td colspan="${columnCount}" class="callerTreeCell">` +
            `${renderStackAsCallerTree(frames, methodNames)}` +
            `</td></tr>`;
    }

    const columns: Array<[string, string]> = [["Created At", "string"]];

    if (showCauseColumn) {
        columns.push(["Why", "string"]);
    }

    columns.push(["Time", "number"]);
    columns.push(["Thread", "number"]);

    const header = renderSortableTableHeader(columns);

    // Same .drillDownExpandControls/.drillDownExpandControlButton pair, in the
    // same place (above the table, not inside a row), that CPU Methods and the
    // Heap Contents/Exceptions drill-downs use. Scoped per table via
    // data-threading-expand-target rather than a per-table class, because
    // both tables in this view are structurally identical and only differ by
    // id - one delegated handler serves both (see wireThreadingTab).
    const expandControlsHtml = `<div class="drillDownExpandControls">` +
        `<button class="drillDownExpandControlButton" type="button" data-threading-expand-target="${idPrefix}Table" data-threading-expand="true">Expand All</button>` +
        `<button class="drillDownExpandControlButton" type="button" data-threading-expand-target="${idPrefix}Table" data-threading-expand="false">Collapse All</button>` +
        `</div>`;

    // The count is a live element, not baked text: zooming the timeline filters
    // these rows, and a header still claiming the full capture's count while
    // showing a subset is exactly the kind of quiet lie that makes a filtered
    // view untrustworthy.
    return `<div class="threadingSection">
        <div class="threadingSectionTitle">${escapeHtmlForThreading(title)}
            (<span class="threadingSectionCount" id="${idPrefix}Count" data-threading-total="${stackedEvents.length}">${stackedEvents.length.toLocaleString()}</span>)</div>
        <div class="threadingNote">Click a row to see the full stack.</div>
        ${expandControlsHtml}
        <div class="detailTable threadingTable"><table id="${idPrefix}Table">${header}${rows}</table></div>
    </div>`;
}
