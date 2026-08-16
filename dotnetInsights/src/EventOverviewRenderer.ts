// Renders the "Overview" view's total-event-count tile + a ranked table of
// every distinct (provider, event type) combination actually present in the
// capture, against the gcData["eventOverview"] shape produced by
// nettraceParser/Overview/EventOverviewBuilder.cs - full transparency into
// what's in the file, not scoped to only the GC/allocation/exception subset
// this extension otherwise decodes (Rundown events, EventPipe metadata
// events, and any unrecognized CLR event all show up here too, labeled
// "EventID {n}" when this tool doesn't have a friendlier name for them - see
// EventOverviewBuilder.cs's own header comment on why that's an honest
// fallback rather than a guess).
//
// nettrace-only: gcData["eventOverview"] is absent for .gcinfo/XML input
// (see GcJsonExporter.cs) - callers should only invoke this when
// sourceFormat === "nettrace" (see GcSnapshotRenderer.ts). Unlike
// allocationSummary/exceptionSummary, eventOverview is always meaningful
// whenever it's present - every real capture has *some* events - so there's
// no "topTypes.length === 0" placeholder branch here the way those two have.
//
// timeBreakdown (gcData["timeBreakdown"], see Overview/TimeBreakdownBuilder.cs)
// is a SEPARATE top-level JSON field, not nested under eventOverview - passed
// through as its own optional parameter so a missing/absent value (older
// cached nettraceParser binary predating this feature - see the stale-cache
// trap in CLAUDE.md) degrades to simply omitting the tile rather than
// throwing. GC%/Contending Locks% render whenever hasCaptureDuration is true;
// Idle%/CPU-Bound% render only when hasCpuSampleBreakdown is ALSO true (both
// gates independent per TimeBreakdownBuilder's own contract) - all four are
// shown together as one tile only once every value in it is real, since a
// partial "2 of 4 metrics" tile would be confusing, not a graceful
// degradation.
// Severity for one Time Breakdown metric, driving the amber/red styling on
// both the percentage and its accompanying absolute time.
//
// Two independent triggers, and the second is the reason this is a shared
// function rather than an inline ternary on the percentage:
//
//   - Thresholds on the percentage: >=10% is "alert", >=5% is "warn". A tenth
//     of a process's wall clock spent paused in GC or blocked on locks is
//     worth acting on; a twentieth is worth noticing.
//
//   - totalMSec exceeding the capture's own duration forces "alert" outright,
//     regardless of the percentage. This can only happen for lock contention,
//     where the total is summed across concurrently blocked threads (GC pauses
//     cannot overlap - see TimeBreakdownBuilder.cs). It means threads were
//     piling up on locks: on a real capture, 744,412ms of summed wait against
//     a 174,688ms capture, which is only 13.3% of the wall clock but 4.26
//     threads blocked on average. The percentage alone would have read as a
//     mild amber and completely understated it.
//
// Exported for its own unit tests - the thresholds are the kind of thing that
// silently stops matching the CSS if the two drift apart.
export type TimeBreakdownSeverity = "none" | "warn" | "alert";

export function timeBreakdownSeverity(percent: number, totalMSec: number, captureDurationMSec: number): TimeBreakdownSeverity {
    if (captureDurationMSec > 0 && totalMSec > captureDurationMSec) {
        return "alert";
    }

    if (percent >= 10) {
        return "alert";
    }

    if (percent >= 5) {
        return "warn";
    }

    return "none";
}

// Severity is carried by the TILE's own background, not by colouring the
// values inside it. The values keep .total's inherited white, exactly as
// every other tile's do.
//
// This went through two worse designs first, both defeated by .total's lime
// ground: colouring the values amber/red directly (amber is nearly lime's own
// hue, so a warning read as muddy) and giving each value its own badge
// (self-contrasting, but a row of chips on a summary tile is visual noise).
// Colouring the whole tile sidesteps the ground entirely - the ground IS the
// signal - and it is legible at a glance from across the page rather than
// needing the number to be read first.
function severityTileClass(severity: TimeBreakdownSeverity): string {
    if (severity === "alert") {
        return " timeBreakdownTileAlert";
    }

    if (severity === "warn") {
        return " timeBreakdownTileWarn";
    }

    return "";
}

// Seconds past a minute of total time - "744412.0 ms" is unreadable at a
// glance and the tile is the wrong place to make someone count digits.
function formatDurationMSec(totalMSec: number): string {
    if (totalMSec >= 60000) {
        return `${(totalMSec / 1000).toFixed(1)} s`;
    }

    return `${totalMSec.toFixed(1)} ms`;
}

export function renderEventOverviewTable(eventOverview: any, timeBreakdown?: any): string {
    const totalEventCount = eventOverview["totalEventCount"];
    const eventTypes = eventOverview["eventTypes"] || [];

    // Absolute totals are optional: a cached nettraceParser predating them
    // (see this file's own header note on the stale-cache trap) still renders
    // the tiles, just without the severity that needs them.
    const captureDurationMSec = timeBreakdown ? (timeBreakdown["captureDurationMSec"] ?? 0) : 0;
    const contentionWaitMSec = timeBreakdown ? (timeBreakdown["contentionWaitMSec"] ?? 0) : 0;
    const gcPauseMSec = timeBreakdown ? (timeBreakdown["gcPauseMSec"] ?? 0) : 0;

    // Gated independently rather than all-or-nothing. When these were one
    // combined tile a partial render would have been a confusing "2 of 4
    // metrics" box, so it was suppressed entirely unless every value was real;
    // as separate tiles, showing lock/GC timing for a capture taken without
    // the SampleProfiler provider is just the right amount of information,
    // matching TimeBreakdownBuilder's own two independent gates.
    const hasTimingBreakdown = !!(timeBreakdown && timeBreakdown["hasCaptureDuration"]);
    const hasCpuBreakdown = !!(timeBreakdown && timeBreakdown["hasCpuSampleBreakdown"]);

    const lockTileClass = hasTimingBreakdown ? severityTileClass(timeBreakdownSeverity(timeBreakdown["contentionPercent"], contentionWaitMSec, captureDurationMSec)) : "";
    const gcTileClass = hasTimingBreakdown ? severityTileClass(timeBreakdownSeverity(timeBreakdown["gcPercent"], gcPauseMSec, captureDurationMSec)) : "";

    const lockTileHtml = hasTimingBreakdown ? `
            <div class="total timeBreakdownTile${lockTileClass}">
                <div>Contending Locks</div>
                <div>% of Time<span>${timeBreakdown["contentionPercent"].toFixed(1)}%</span></div>
                <div>Total Wait<span>${formatDurationMSec(contentionWaitMSec)}</span></div>
                <div>Avg Blocked<span>${(timeBreakdown["averageThreadsBlocked"] ?? 0).toFixed(2)}</span></div>
            </div>` : "";

    const gcTileHtml = hasTimingBreakdown ? `
            <div class="total timeBreakdownTile${gcTileClass}">
                <div>GC</div>
                <div>% of Time<span>${timeBreakdown["gcPercent"].toFixed(1)}%</span></div>
                <div>Total Pause<span>${formatDurationMSec(gcPauseMSec)}</span></div>
            </div>` : "";

    // No severity: idle/CPU-bound is a split of one 100%, so neither half is
    // "bad" on its own - a mostly-idle process is healthy or starved
    // depending entirely on what it was meant to be doing, which this cannot
    // know. Stays lime rather than being given a threshold that would be
    // guessing.
    const cpuTileHtml = hasCpuBreakdown ? `
            <div class="total timeBreakdownTile">
                <div>CPU (est.)</div>
                <div>Idle<span>${timeBreakdown["idlePercent"].toFixed(1)}%</span></div>
                <div>CPU Bound<span>${timeBreakdown["cpuBoundPercent"].toFixed(1)}%</span></div>
            </div>` : "";

    const timeBreakdownTileHtml = `${lockTileHtml}${gcTileHtml}${cpuTileHtml}`;

    const summaryTilesHtml = `
        <div class="summaryGcDiv">
            <div class="total">
                <div>Events</div>
                <div>Total<span>${totalEventCount.toLocaleString()}</span></div>
                <div>Distinct Types<span>${eventTypes.length}</span></div>
            </div>${timeBreakdownTileHtml}
        </div>`;

    var rows = "";
    for (var index = 0; index < eventTypes.length; ++index) {
        const eventType = eventTypes[index];

        const tdProviderName = eventType["providerName"];
        const tdDisplayName = eventType["displayName"];
        const tdCount = eventType["count"];
        const tdPercent = totalEventCount === 0 ? "0.00" : ((tdCount * 100.0) / totalEventCount).toFixed(2);

        rows += `<tr><td>${escapeHtml(tdProviderName)}</td><td>${escapeHtml(tdDisplayName)}</td><td>${tdCount.toLocaleString()}</td><td>${tdPercent}</td></tr>`;
    }

    const header = `<tr class="tableHeader"><th>Provider</th><th>Event Type</th><th>Count</th><th>% of Total</th></tr>`;
    const tableHtml = `<div class="detailTable allocationTypeTable"><table id="eventOverviewTable">${header}${rows}</table></div>`;

    return `${summaryTilesHtml}${tableHtml}`;
}

function escapeHtml(value: string): string {
    return String(value)
        .replace(/&/g, "&amp;")
        .replace(/</g, "&lt;")
        .replace(/>/g, "&gt;")
        .replace(/"/g, "&quot;");
}
