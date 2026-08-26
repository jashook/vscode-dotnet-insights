// Renders the "Contention" view's summary tiles, optional timeline chart, and
// ranked lock-contention sites table against the gcData["contentionSummary"]
// shape produced by nettraceParser/Contention/ContentionJsonExporter.cs.
//
// nettrace-only: gcData["contentionSummary"] is absent for .gcinfo/XML input.
// Callers should only invoke this when both sourceFormat === "nettrace" and
// contentionSummary.totalContentionCount > 0 (see GcSnapshotRenderer.ts).
//
// Each ranked site row expands inline to show its caller tree (same
// lazy-expand pattern as the CPU Methods table), populated lazily by
// buildInlineContentionSiteCallerTree in contentionDrillDownStats.js when a
// row is first expanded.

import { renderRankedTableHeader } from './GcDetailTableRenderer';

function escapeHtmlForContention(value: string): string {
    return String(value)
        .replace(/&/g, "&amp;")
        .replace(/</g, "&lt;")
        .replace(/>/g, "&gt;");
}

function formatSiteNameHtml(rawName: string): string {
    if (rawName === "<no stack captured>" || rawName.startsWith("<unresolved")) {
        return `<span class="unresolvedFrame">${escapeHtmlForContention(rawName)}</span>`;
    }

    const lastDotIndex = rawName.lastIndexOf(".");
    if (lastDotIndex === -1) {
        return `<span class="methodName">${escapeHtmlForContention(rawName)}</span>`;
    }

    const typePrefix = rawName.slice(0, lastDotIndex + 1);
    const methodName = rawName.slice(lastDotIndex + 1);
    return `<span class="methodTypePrefix">${escapeHtmlForContention(typePrefix)}</span><span class="methodName">${escapeHtmlForContention(methodName)}</span>`;
}

export function renderContentionView(contentionSummary: any): string {
    const totalCount = contentionSummary["totalContentionCount"];
    const totalWaitMSec = contentionSummary["totalContentionWaitMSec"];

    if (!totalCount) {
        return `<div class="detailTable"><p>No contention events to display.</p></div>`;
    }

    const topSites = contentionSummary["topSites"] || [];
    const hasTimeline = !!(contentionSummary["timeline"]);

    const avgWaitMSec = totalCount > 0 ? (totalWaitMSec / totalCount) : 0;

    // Ids on the three derived tiles let rebuildContentionSitesTable
    // (snapshotGcStats.js) rewrite them in place after a row is hidden -
    // Total Events itself never changes (hiding doesn't remove an event
    // from the capture), only Total/Avg Wait, which are recomputed against
    // the remaining visible sites.
    const summaryTilesHtml = `
        <div class="summaryGcDiv">
            <div class="total">
                <div>Lock Contention</div>
                <div>Total Events<span>${totalCount.toLocaleString()}</span></div>
                <div>Total Wait (ms)<span id="contentionTotalWaitTile">${totalWaitMSec.toFixed(1)}</span></div>
                <div>Avg Wait (ms)<span id="contentionAvgWaitTile">${avgWaitMSec.toFixed(3)}</span></div>
            </div>
        </div>`;

    const timelineHtml = hasTimeline ? `
        <div class="cpuTimelineSection">
            <div class="allocationZoomStatus" id="contentionTimelineZoomStatus" style="display:none">
                <span class="allocationZoomStatusLabel" id="contentionTimelineZoomLabel"></span>
                <button class="resetZoomButton" id="contentionTimelineResetZoomBtn">Reset Zoom (Backspace)</button>
            </div>
            <div class="cpuTimelineContainer"><canvas id="contentionTimeline"></canvas></div>
        </div>` : ``;

    // Hidden until at least one row is hidden (rebuildContentionSitesTable
    // in snapshotGcStats.js) - same allocationZoomStatus idiom as every
    // other hide-status bar on this page.
    const sitesHideStatusHtml = `
        <div class="allocationZoomStatus" id="contentionSitesHideStatus" style="display:none">
            <span class="allocationZoomStatusLabel" id="contentionSitesHideStatusLabel"></span>
            <button class="resetZoomButton" id="contentionSitesShowAllBtn">Show all</button>
        </div>`;

    const sitesTableHtml = renderTopSitesTable(contentionSummary);

    // Two tabs, mirroring the Profile view's own Flame Graph/Methods bar
    // (same heapContentsTabBar/heapContentsTabPanel classes, so the existing
    // tab CSS and switching idiom apply unchanged). The Lock Timeline tab is
    // omitted entirely - not rendered empty - when the capture carries no
    // lock identity at all, which is the case for any pre-.NET-9 runtime
    // emitting V1 ContentionStart payloads (see ClrContentionStart.Decode).
    const lockTimeline = contentionSummary["lockTimeline"];
    const hasLockTimeline = !!(lockTimeline && lockTimeline["locks"] && lockTimeline["locks"].length > 0);

    const overview = contentionSummary["overview"];
    const hasOverview = !!overview;

    const sitesPanelInner = `${summaryTilesHtml}${timelineHtml}${sitesHideStatusHtml}${sitesTableHtml}`;

    if (!hasLockTimeline && !hasOverview) {
        return sitesPanelInner;
    }

    // Overview leads, and is default-active: it is the only tab that answers
    // "is locking a problem in this capture at all" before asking the reader
    // to pick a row. Same ordering rationale as the GC view putting Charts
    // ahead of Detailed.
    const tabBarHtml = `
        <div class="heapContentsTabBar">
            ${hasOverview ? `<button class="heapContentsTabButton active" data-contentiontab="overview">Overview</button>` : ``}
            <button class="heapContentsTabButton${hasOverview ? `` : ` active`}" data-contentiontab="sites">Sites</button>
            ${hasLockTimeline ? `<button class="heapContentsTabButton" data-contentiontab="locktimeline">Lock Timeline</button>` : ``}
        </div>`;

    const overviewPanelHtml = hasOverview
        ? `<div id="contention-tab-overview" class="heapContentsTabPanel active">${renderContentionOverviewPanel(overview)}</div>`
        : ``;
    const sitesPanelHtml = `<div id="contention-tab-sites" class="heapContentsTabPanel${hasOverview ? `` : ` active`}">${sitesPanelInner}</div>`;
    const lockTimelinePanelHtml = hasLockTimeline
        ? `<div id="contention-tab-locktimeline" class="heapContentsTabPanel">${renderLockTimelinePanel(lockTimeline)}</div>`
        : ``;

    return `${tabBarHtml}${overviewPanelHtml}${sitesPanelHtml}${lockTimelinePanelHtml}`;
}

// Overview tab: the lock-side counterpart to the GC view's Charts tab.
//
// Every number here is emitted by
// nettraceParser/Contention/ContentionOverviewBuilder.cs and rendered
// verbatim - this function computes nothing beyond formatting, for the same
// reason InsightsRenderer.ts does not: the CLI and the webview must not be
// able to disagree about a capture.
//
// The three charts are drawn by media/contentionOverview.js (Chart.js 2.x,
// like every other chart in this webview) on ONE shared bucket grid, which is
// the CPU sample timeline's own grid whenever the capture has samples - see
// that builder's header on why resampling was rejected.
//
// TWO DIFFERENT BLOCKED-TIME NUMBERS ARE SHOWN SIDE BY SIDE and the labels
// have to keep them apart, because collapsing them is a mistake this codebase
// has already shipped once (Overview/TimeBreakdownBuilder.cs rendered
// "Contending Locks 426.1%"): "Blocked Wall Clock" is a union and is bounded
// by the capture, "Total Wait" is summed across threads and is not.
export function renderContentionOverviewPanel(overview: any): string {
    const percentiles = overview["waitPercentiles"] || {};
    const contentionCount = overview["contentionCount"] || 0;

    const percentileTilesHtml = `
        <div class="total">
            <div>Wait Duration</div>
            <div>Median (p50)<span>${formatMSec(percentiles["p50"])}</span></div>
            <div>p90<span>${formatMSec(percentiles["p90"])}</span></div>
            <div>p99<span>${formatMSec(percentiles["p99"])}</span></div>
            <div>p99.9<span>${formatMSec(percentiles["p999"])}</span></div>
            <div>Max<span>${formatMSec(percentiles["max"])}</span></div>
            <div>Mean<span>${formatMSec(overview["meanWaitMSec"])}</span></div>
        </div>`;

    const blockedTilesHtml = `
        <div class="gen0">
            <div>Time Blocked</div>
            <div title="Wall-clock time during which at least one thread was blocked on a lock. A union across threads, so it is bounded by the capture.">Blocked Wall Clock<span>${formatMSec(overview["blockedWallClockMSec"])}</span></div>
            <div title="Blocked wall clock as a share of the whole capture.">% of Capture<span>${overview["hasCaptureDuration"] ? formatPercent(overview["blockedWallClockPercent"]) : "n/a"}</span></div>
            <div title="Summed across every blocked thread, so this is not a percentage of anything and can exceed the capture duration.">Total Wait (summed)<span>${formatMSec(overview["totalWaitMSec"])}</span></div>
            <div title="Summed wait divided by capture duration - how many threads were blocked at a typical instant.">Avg Threads Blocked<span>${formatNumber(overview["averageThreadsBlocked"], 2)}</span></div>
            <div title="The most threads observed blocked on locks at the same instant.">Peak Threads Blocked<span>${(overview["peakThreadsBlocked"] || 0).toLocaleString()}</span></div>
            <div>Contentions<span>${contentionCount.toLocaleString()}</span></div>
        </div>`;

    // Only a CPU-time-sampled capture can show this at all - see the CPU
    // definition table's own note. On every other capture the tile is omitted
    // rather than filled with a thread-time figure wearing a CPU label.
    const cpuTimeTileHtml = overview["hasCpuTime"] ? `
        <div class="total">
            <div>CPU Time</div>
            <div title="Total CPU consumed across the capture: samples x the sampling period recovered from this capture.">Total CPU<span>${formatMSec(overview["totalCpuMSec"])}</span></div>
            <div title="Total CPU time divided by wall-clock time - how many cores were busy on average.">Avg Cores Busy<span>${formatNumber(overview["averageCoresBusy"], 2)}</span></div>
            <div title="Milliseconds of CPU time one sample stands for, measured from this capture's own inter-sample gaps rather than assumed.">Per Sample<span>${formatNumber(overview["samplePeriodMSec"], 3)} ms</span></div>
        </div>` : ``;

    const worstWaitHtml = `
        <div class="gen1">
            <div>Worst Single Wait</div>
            <div>Duration<span>${formatMSec(percentiles["max"])}</span></div>
            <div>Started At<span>${formatElapsedForOverview(percentiles["maxStartMSec"])}</span></div>
            <div>Blocked Thread<span>${percentiles["maxThreadId"] ? percentiles["maxThreadId"].toLocaleString() : "unknown"}</span></div>
            <div>Peak Blocked At<span>${formatElapsedForOverview(overview["peakThreadsBlockedAtMSec"])}</span></div>
        </div>`;

    const tilesHtml = `<div class="summaryGcDiv">${percentileTilesHtml}${blockedTilesHtml}${worstWaitHtml}${cpuTimeTileHtml}</div>`;

    const hasCpu = !!overview["hasCpuSamples"];
    const correlation = overview["correlation"];

    // The correlation line states its own measurement, its n, and what it is
    // NOT, per the same house rule the insight rules follow: a reader has to
    // be able to disagree with the statistic rather than only with the
    // conclusion drawn from it.
    const correlationHtml = renderCorrelationNote(hasCpu, correlation, overview);

    // The CPU definition is a CONTROL, not a constant. On a wall-clock capture
    // there is no single right answer (see renderCorrelationNote), so the view
    // makes the choice visible and switchable instead of burying it.
    const cpuSeriesOptions = (overview["cpuSeries"] || []).map((series: any, index: number) =>
        `<option value="${escapeHtmlForContention(series["id"])}"${index === defaultCpuSeriesIndex(overview) ? ` selected` : ``}>${escapeHtmlForContention(series["label"])}</option>`).join("");

    const cpuChartHtml = hasCpu ? `
        <div id="contentionOverviewCpuSection">
            <div class="sectionHeading">Lock Wait vs CPU</div>
            <div class="lockTimelineNote">
                GC pause is drawn alongside so a spike that belongs to a collection is not read as a locking one.
                ${overview["samplingSemantics"] === "cpuTime"
                    ? `These samples are real CPU time (perf <code>cpu-clock</code>), so this reads as CPU utilisation.`
                    : `<strong>These samples are not CPU utilisation.</strong> .NET's profiler samples every thread on a wall clock, so "CPU" here is a judgement call &mdash; switch definitions below and see whether your conclusion survives.`}
            </div>
            <div class="lockTimelineToolbar">
                <label class="lockTimelineControl">CPU definition
                    <select id="contentionOverviewCpuSeriesSelect">${cpuSeriesOptions}</select>
                </label>
                <span class="lockTimelineHint" id="contentionOverviewCpuSeriesHint"></span>
            </div>
            <div class="cpuTimelineContainer"><canvas id="contentionOverviewCpuChart"></canvas></div>
            ${correlationHtml}
        </div>` : `
        <div id="contentionOverviewCpuSection">
            <div class="sectionHeading">Lock Wait vs CPU</div>
            <div class="lockTimelineNote">
                This capture has no CPU samples, so there is nothing to correlate lock wait against.
                Re-capture with the CPU sampling provider enabled to use this chart.
            </div>
        </div>`;

    return `
        <div class="lockTimelineNote">
            Every chart below shares one bucket grid${overview["gridSource"] === "cpu" ? " taken directly from the CPU sample timeline, so the CPU overlay needs no resampling" : " derived from the contention events themselves"}
            &mdash; ${overview["bucketCount"].toLocaleString()} buckets of ${formatMSec(overview["bucketDurationMSec"])} each.
        </div>
        ${tilesHtml}
        <div class="sectionHeading">Time Blocked Over Time</div>
        <div class="lockTimelineNote">
            A wait spanning several buckets is counted in each of them in proportion, not banked at the bucket it started in &mdash;
            a four-second stall did not happen in one instant.
            The shaded area is wall clock with at least one thread blocked; the line is how many threads were blocked on average.
        </div>
        <div class="cpuTimelineContainer"><canvas id="contentionOverviewBlockedChart"></canvas></div>

        <div class="sectionHeading">Wait Duration Over Time (p50 / p99 / max)</div>
        <div class="lockTimelineNote">
            Percentiles of the individual waits that <em>started</em> in each bucket &mdash; the opposite attribution from the chart above, on purpose.
            Buckets with no contention are gaps rather than zeroes.
            The scale is logarithmic by default because p50 and p99 routinely differ by three orders of magnitude.
            <label class="lockTimelineControl" style="margin-left: 8px">
                <input type="checkbox" id="contentionOverviewLinearScale"> Linear scale
            </label>
        </div>
        <div class="cpuTimelineContainer"><canvas id="contentionOverviewPercentileChart"></canvas></div>

        ${cpuChartHtml}`;
}

// Says what was measured, over how many points, and what it does not
// establish. A bare "r = 0.82" invites exactly the causal reading this note
// exists to withhold.
//
// THE HEADLINE NUMBER IS WITHHELD ENTIRELY WHEN THE DEFINITIONS DISAGREE, and
// that is the most important thing this function does. A .NET v5 capture has
// no native stacks, so it cannot separate "in a syscall" from "in a native
// compute loop" - both are just External. On a real 3.0GB production capture
// 79% of the samples the default definition counts as CPU sit in that
// ambiguous bucket, and the resulting coefficient runs from -0.598 to +0.248
// depending on which defensible definition is used. Picking one and printing
// it would answer a question the capture does not settle.
function renderCorrelationNote(hasCpu: boolean, correlation: any, overview: any): string {
    if (!hasCpu) {
        return ``;
    }

    if (!correlation) {
        return `
        <div class="lockTimelineNote">
            No correlation reported: one of the two series is flat across the capture, so the coefficient is undefined rather than zero.
        </div>`;
    }

    const agreement = correlation["agreement"];
    const bucketCount = correlation["bucketCount"];
    const cpuSeries = (overview["cpuSeries"] || []).filter((series: any) => series["hasCorrelation"]);

    const rowsHtml = cpuSeries.map((series: any) => {
        const lagText = series["bestLagBuckets"] === 0
            ? "none"
            : `${series["bestLagBuckets"] > 0 ? "+" : "\u2212"}${formatMSec(Math.abs(series["bestLagMSec"]))}`;

        return `<tr>` +
            `<td>${escapeHtmlForContention(series["label"])}</td>` +
            `<td>${series["totalSamples"].toLocaleString()}</td>` +
            `<td>${formatNumber(series["coefficient"], 3)}</td>` +
            `<td>${lagText}</td>` +
            `<td>${formatNumber(series["bestLagCoefficient"], 3)}</td>` +
            `<td class="cpuSeriesBias">${escapeHtmlForContention(series["bias"])}</td>` +
            `</tr>`;
    }).join("");

    // Deliberately NOT .detailTable/.cpuHotMethodsTable. Those carry a column
    // CONTRACT - column 1 is the row-hide gutter, column 2 the wrapping name
    // column, 3+ numeric and right-aligned - and this table's first column is
    // a name. Borrowing the class is exactly how the .gcdump census table
    // ended up pushing its numeric columns off-screen (see CLAUDE.md). It is a
    // small static table with no sorting and no expansion, so it gets its own
    // minimal styling instead.
    const tableHtml = `
        <table class="cpuSeriesTable">
            <thead>
                <tr><th>CPU definition</th><th>Samples</th><th>r</th><th>Best lag</th><th>r at that lag</th><th>Known bias</th></tr>
            </thead>
            <tbody>${rowsHtml}</tbody>
        </table>`;

    const isWallClock = overview["samplingSemantics"] !== "cpuTime";

    // The root cause, stated once, in the place someone reads when the answer
    // looks strange. This is not a caveat about precision - it is the reason
    // the numbers above can point in opposite directions.
    const samplingNote = isWallClock ? `
            <p>
                <strong>Why there is more than one number.</strong>
                This capture's samples come from .NET's own sample profiler, which is <em>wall-clock per thread</em>: it samples every thread on an interval
                whether or not that thread held a core. Narrowing those samples down to "was doing work" therefore requires a judgement call, and the capture
                carries no native stacks to settle it &mdash; the runtime reports a thread in a syscall and a thread inside a native compression loop
                identically, as <code>External</code>. Each row above resolves that differently, which is why they can disagree.
                A <code>dotnet-trace collect-linux</code> capture does not have this problem: it samples on <code>cpu-clock</code>, which only fires on a thread
                that actually holds a core, so there every sample is CPU time and the top row is the answer.
            </p>` : `
            <p>
                <strong>These samples are real CPU time.</strong>
                This capture is perf-sampled on <code>cpu-clock</code>, which only fires on a thread that actually holds a core, so
                <em>All samples</em> is a genuine CPU-utilisation series rather than a thread-state count. The narrower rows are subsets of it.
            </p>`;

    let verdictHtml: string;

    if (agreement === "disagree") {
        verdictHtml = `
            <p>
                <strong>The definitions disagree, so no single coefficient is reported.</strong>
                Across ${bucketCount.toLocaleString()} buckets, r ranges from <strong>${formatNumber(correlation["minCoefficient"], 3)}</strong>
                to <strong>${formatNumber(correlation["maxCoefficient"], 3)}</strong> &mdash; opposite signs &mdash; depending only on which rows above you
                treat as CPU. This capture does not settle whether lock stalls coincide with CPU spikes; read the chart and pick the definition you can defend
                for the workload, rather than taking a number from here.
            </p>`;
    } else if (agreement === "inconclusive") {
        verdictHtml = `
            <p>
                <strong>Every definition agrees, and none shows a meaningful relationship.</strong>
                Across ${bucketCount.toLocaleString()} buckets, r stays between ${formatNumber(correlation["minCoefficient"], 3)} and
                ${formatNumber(correlation["maxCoefficient"], 3)}, all below the 0.3 magnitude this view treats as meaningful.
                Lock stalls and CPU are not tracking each other here on any reading.
            </p>`;
    } else if (agreement === "single") {
        verdictHtml = `
            <p>
                Only one CPU definition could be measured on this capture, so there is nothing to cross-check it against and no claim of robustness is made.
                r = ${formatNumber(correlation["maxCoefficient"], 3)} across ${bucketCount.toLocaleString()} buckets.
            </p>`;
    } else {
        const sharedSign = correlation["maxCoefficient"] > 0 ? "positive" : "negative";
        verdictHtml = `
            <p>
                <strong>Every definition agrees on the sign (${sharedSign}).</strong>
                Across ${bucketCount.toLocaleString()} buckets, r ranges from ${formatNumber(correlation["minCoefficient"], 3)} to
                ${formatNumber(correlation["maxCoefficient"], 3)}, and at least one reaches the 0.3 magnitude this view treats as meaningful.
                ${sharedSign === "negative"
                    ? `A negative relationship is the expected shape when threads blocked on a lock are simply not running: blocking and CPU trade off rather than one driving the other.`
                    : `Lock blocking and CPU rise together here. That is consistent with stalls coinciding with CPU spikes, but see the caveat below before reading it as cause.`}
            </p>`;
    }

    return `
        <div class="lockTimelineNote correlationNote">
            ${verdictHtml}
            ${tableHtml}
            ${samplingNote}
            <p>
                Whatever the rows say, these are <strong>descriptive statistics, not significance tests, and not evidence of causation</strong>: both series are
                strongly autocorrelated (a convoy and a CPU spike each span many buckets), which inflates r well beyond what ${bucketCount.toLocaleString()}
                independent observations would justify. A lag scan also maximises r by construction and will always return some best shift.
                The charts are the evidence; these numbers are an index into them.
            </p>
        </div>`;
}

// Which CPU definition the chart opens on.
//
// On a perf-sampled capture that is "all samples", because there every sample
// really is CPU time. On a wall-clock capture it is the narrowest UNAMBIGUOUS
// definition available (managed and not parked) rather than the broadest:
// where the reading is a judgement call, the default should be the one whose
// every sample can be defended, and the reader can widen it deliberately.
function defaultCpuSeriesIndex(overview: any): number {
    const cpuSeries = overview["cpuSeries"] || [];
    const preferredId = overview["samplingSemantics"] === "cpuTime" ? "allSamples" : "managedRunning";

    for (let seriesIndex = 0; seriesIndex < cpuSeries.length; ++seriesIndex) {
        if (cpuSeries[seriesIndex]["id"] === preferredId) {
            return seriesIndex;
        }
    }

    // A capture with no ThreadSampleType has neither managed series; fall back
    // to the blocking-primitive filter, which is always present.
    for (let seriesIndex = 0; seriesIndex < cpuSeries.length; ++seriesIndex) {
        if (cpuSeries[seriesIndex]["id"] === "notBlockingPrimitive") {
            return seriesIndex;
        }
    }

    return 0;
}

function formatNumber(value: any, digits: number): string {
    if (typeof value !== "number" || !isFinite(value)) {
        return "n/a";
    }

    return value.toFixed(digits);
}

function formatPercent(value: any): string {
    if (typeof value !== "number" || !isFinite(value)) {
        return "n/a";
    }

    return value.toFixed(2) + "%";
}

// Durations here span nanoseconds to minutes within one capture, so a single
// fixed precision either loses the short waits or renders the long ones as an
// unreadable run of digits.
function formatMSec(value: any): string {
    if (typeof value !== "number" || !isFinite(value)) {
        return "n/a";
    }

    if (value === 0) {
        return "0 ms";
    }

    if (value < 1) {
        return value.toFixed(3) + " ms";
    }

    if (value < 1000) {
        return value.toFixed(2) + " ms";
    }

    return (value / 1000).toFixed(2) + " s";
}

function formatElapsedForOverview(value: any): string {
    if (typeof value !== "number" || !isFinite(value)) {
        return "n/a";
    }

    const totalSeconds = value / 1000;
    const minutes = Math.floor(totalSeconds / 60);
    const seconds = totalSeconds - (minutes * 60);

    return minutes > 0
        ? `${minutes}m ${seconds.toFixed(1)}s`
        : `${seconds.toFixed(2)}s`;
}

// Lock Timeline tab: a Gantt-style track per lock (y) against capture time
// (x), each bar an observed ownership window colored by the owning thread.
//
// The canvas itself is drawn entirely by media/lockTimeline.js rather than
// Chart.js - this codebase is pinned to Chart.js 2.x (see CLAUDE.md), which
// has no floating/range bar type at all (arbitrary [start,end] bars only
// arrived in Chart.js 3), and a hand-drawn canvas also handles the ~9k
// segments a real capture produces far faster than a chart library's own
// per-element model would.
//
// The explanatory note is deliberately part of the UI, not a comment: these
// bars are inferred from contention events, which the CLR only emits when a
// lock is actually contended, so a gap means "nobody was blocked here", NOT
// "the lock was free". Without saying so the view reads as a complete
// ownership history, which it cannot be.
function renderLockTimelinePanel(lockTimeline: any): string {
    const totalDistinctLockCount = lockTimeline["totalDistinctLockCount"];

    // The lock list and the thread dropdown are both populated by
    // media/lockTimeline.js rather than server-rendered here: the lock list
    // has to rebuild whenever the Top-N selector changes, and the thread
    // list is derived from the segments themselves. Server-rendering either
    // would mean emitting one markup blob and then immediately replacing it.
    return `
        <div class="lockTimelineNote">
            Each bar is a window where a thread held a lock while another thread was blocked on it.
            Because the runtime only reports contended locks, a gap means no thread was blocked - not that the lock was free.
            This capture had ${totalDistinctLockCount.toLocaleString()} contended ${totalDistinctLockCount === 1 ? "lock" : "locks"}; locks are ranked by total wait time.
            <span id="lockOutlierNote" class="lockOutlierNote"></span>
        </div>
        <div class="lockTimelineToolbar">
            <label class="lockTimelineControl">Show
                <select id="lockTopNSelect">
                    <option value="10">Top 10</option>
                    <option value="25">Top 25</option>
                    <option value="40" selected>Top 40</option>
                    <option value="100">Top 100</option>
                    <option value="250">Top 250</option>
                    <option value="all">All (${totalDistinctLockCount.toLocaleString()})</option>
                </select>
            </label>
            <label class="lockTimelineControl">Rank by
                <select id="lockRankMetricSelect">
                    <option value="wait" selected>Total wait</option>
                    <option value="contentions">Contentions</option>
                    <option value="threads">Contending threads</option>
                    <option value="workerthreads">Worker threads blocked</option>
                    <option value="maxwait">Longest single wait</option>
                </select>
            </label>
            <label class="lockTimelineControl">Stalls
                <select id="lockLongestWaitSelect">
                    <option value="">Jump to longest…</option>
                </select>
            </label>
            <button id="lockTimelineResetZoomBtn" class="resetZoomButton" style="display:none">Reset Zoom</button>
            <span id="lockTimelineZoomLabel" class="lockTimelineZoomLabel"></span>
            <span class="lockTimelineHint">Drag to zoom · double-click to reset · click a lock name for its stacks</span>
        </div>
        <div class="lockTimelineLayout">
            <div class="lockTimelineChartArea">
                <div id="lockTimelineContainer" class="lockTimelineContainer">
                    <canvas id="lockTimelineCanvas"></canvas>
                </div>
                <div id="lockTimelineTooltip" class="lockTimelineTooltip" style="display:none"></div>
            </div>
            <div class="lockSidebar">
                <div class="lockFilterPanel">
                    <div class="lockFilterHeader">
                        <span id="lockFilterHeaderLabel">Locks</span>
                        <span class="lockFilterButtons">
                            <button id="lockFilterAllBtn" class="resetZoomButton">All</button>
                            <button id="lockFilterNoneBtn" class="resetZoomButton">None</button>
                        </span>
                    </div>
                    <div id="lockFilterList" class="lockFilterList"></div>
                </div>
                <div class="lockFilterPanel">
                    <div class="lockFilterHeader">
                        <span id="threadFilterHeaderLabel">Threads</span>
                        <span class="lockFilterButtons">
                            <button id="threadFilterAllBtn" class="resetZoomButton">All</button>
                            <button id="threadFilterNoneBtn" class="resetZoomButton">None</button>
                            <button id="threadFilterWorkerBtn" class="resetZoomButton" title="Select only .NET worker threads">Worker</button>
                        </span>
                    </div>
                    <input id="threadFilterSearch" class="threadFilterSearch" type="text" placeholder="Find thread id…">
                    <div id="threadFilterList" class="lockFilterList"></div>
                </div>
            </div>
        </div>
        <div class="lockTableSection">
            <div id="lockTableContainer" class="detailTable lockTable"></div>
        </div>
        <div id="lockContextMenu" class="lockContextMenu" style="display:none">
            <div class="lockContextMenuTitle" id="lockContextMenuTitle"></div>
            <button class="lockContextMenuItem" data-lock-menu="only">Show only this lock</button>
            <button class="lockContextMenuItem" data-lock-menu="hide">Hide this lock</button>
            <button class="lockContextMenuItem" data-lock-menu="onlyThreads">Show only this lock's threads</button>
            <div class="lockContextMenuSeparator"></div>
            <button class="lockContextMenuItem" data-lock-menu="stacks">Show contended stacks</button>
            <button class="lockContextMenuItem" data-lock-menu="reset">Reset all filters</button>
        </div>
        <div id="lockStackPanel" class="lockStackPanel" style="display:none">
            <div class="lockStackHeader">
                <span id="lockStackTitle"></span>
                <button id="lockStackCloseBtn" class="resetZoomButton">Close</button>
            </div>
            <div id="lockStackBody" class="lockStackBody"></div>
        </div>`;
}

// Ranked sites table: each row shows the contention site (leaf frame),
// contention count, total wait, average wait, and % of total wait. Each row
// is expandable inline to show the full caller tree, lazily populated by
// buildInlineContentionSiteCallerTree (contentionDrillDownStats.js).
function renderTopSitesTable(contentionSummary: any): string {
    const topSites = contentionSummary["topSites"];

    if (!topSites || topSites.length === 0) {
        return `<div class="detailTable"><p>No ranked sites to display.</p></div>`;
    }

    var rows = "";
    for (var index = 0; index < topSites.length; ++index) {
        const site = topSites[index];
        const siteName = site["SiteName"] || "";
        const contentionCount = site["ContentionCount"];
        const totalWaitMSec = site["TotalWaitMSec"];
        const averageWaitMSec = site["AverageWaitMSec"];
        const percentOfTotal = site["PercentOfTotalWait"];

        rows += `<tr class="typeRow contentionSiteRow" ` +
            `data-contention-site-index="${index}" ` +
            `data-contention-expandable="true" ` +
            `data-contention-target="contentionSiteDetail${index}">` +
            `<td class="rowHideColumn"><button class="rowHideBtn" type="button" title="Hide this row">&#10005;</button></td>` +
            `<td><span class="leafMethodToggle">&#9656;</span>${formatSiteNameHtml(siteName)}</td>` +
            `<td>${contentionCount.toLocaleString()}</td>` +
            `<td>${totalWaitMSec.toFixed(3)}</td>` +
            `<td>${averageWaitMSec.toFixed(3)}</td>` +
            `<td>${percentOfTotal.toFixed(2)}</td>` +
            `</tr>` +
            `<tr id="contentionSiteDetail${index}" class="callPathsDetail" data-contention-lazy="${index}">` +
            `<td colspan="6" class="callerTreeCell"></td>` +
            `</tr>`;
    }

    const columns: ReadonlyArray<[string, string]> = [
        ["Lock Acquisition Site", "text"],
        ["Count", "number"],
        ["Total Wait (ms)", "number"],
        ["Avg Wait (ms)", "number"],
        ["% of Wait", "number"],
    ];

    // renderRankedTableHeader is renderSortableTableHeader plus the hide
    // button's own bare, unsortable leading <th> - see that function.
    const headerWithHideColumn = renderRankedTableHeader(columns);

    return `<div class="detailTable cpuHotMethodsTable"><table id="contentionSitesTable">${headerWithHideColumn}${rows}</table></div>`;
}
