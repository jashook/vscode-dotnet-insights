////////////////////////////////////////////////////////////////////////////////
// Module: OffCpuRenderer.ts
//
// Notes:
// Renders the off-CPU view. Unlike the CPU and contention views, this one has
// no counterpart in dotnetInsights and is not shared: off-CPU time is derived
// from Linux scheduler tracepoints and the .NET tool has no equivalent input.
//
// It answers the question a CPU profile structurally cannot. `perf record -e
// cpu-clock` samples threads that are ON a cpu, so a thread blocked for the
// whole capture produces zero samples and is invisible in the CPU view - here
// it is the top row.
//
// Reason is shown as its own column and never summed away. "Preempted" means
// the thread was runnable and the CPU was taken from it, which is a machine
// sizing problem; "Blocked / sleeping" means it was waiting on something, which
// is a program problem; "Idle kernel thread" is neither and is only present at
// all because a machine-wide capture records every thread on the box. One
// "not running" number would hide which of the three you have.
//
// Markup follows the shared ranked-table contract exactly - a `.detailTable
// .cpuHotMethodsTable` div wrapping a bare table, a leading hide column, the
// name column second and numerics after - so snapshot.css's grid rules apply
// unchanged. See the repo CLAUDE.md's "The ranked table is ONE component".
////////////////////////////////////////////////////////////////////////////////

import { renderRankedTableHeader } from './shared/GcDetailTableRenderer';

function escapeHtml(value: string): string {
    return String(value)
        .replace(/&/g, '&amp;')
        .replace(/</g, '&lt;')
        .replace(/>/g, '&gt;')
        .replace(/"/g, '&quot;');
}

function formatMilliseconds(value: number): string {
    return value.toLocaleString(undefined, { maximumFractionDigits: 1 });
}

export function renderOffCpuView(offCpu: any): string {
    if (!offCpu) {
        return `<p>This capture contains no scheduling data. Record with <code>-e sched:sched_switch</code> to see why threads were not running.</p>`;
    }

    const totalMSec = offCpu['totalMSec'] || 0;

    let reasonRows = '';
    for (const entry of offCpu['byReason'] || []) {
        const share = totalMSec > 0 ? (entry['mSec'] * 100.0) / totalMSec : 0;
        reasonRows += `<tr><td>${escapeHtml(entry['reason'])}</td>` +
            `<td>${formatMilliseconds(entry['mSec'])}</td>` +
            `<td>${share.toFixed(2)}</td></tr>`;
    }

    let threadRows = '';
    for (const thread of (offCpu['threads'] || []).slice(0, 100)) {
        threadRows += `<tr><td>${escapeHtml(thread['name'])}</td>` +
            `<td>${thread['threadId']}</td>` +
            `<td>${formatMilliseconds(thread['offCpuMSec'])}</td></tr>`;
    }

    // Each stack expands into its own frames, using the same lazy-expand
    // discipline and the same row classes as the shared ranked tables.
    let stackRows = '';
    const stacks = offCpu['stacks'] || [];
    for (let index = 0; index < stacks.length && index < 200; ++index) {
        const stack = stacks[index];
        const frames: string[] = stack['frames'] || [];
        const leaf = frames.length > 0 ? frames[0] : '(no stack)';
        const share = totalMSec > 0 ? (stack['mSec'] * 100.0) / totalMSec : 0;

        let frameHtml = '';
        for (const frame of frames) {
            frameHtml += `<div class="offCpuFrame">${escapeHtml(frame)}</div>`;
        }

        stackRows += `<tr class="typeRow offCpuStackRow" data-offcpu-target="offCpuDetail${index}">` +
            `<td class="rowHideColumn"><button class="rowHideBtn" type="button" title="Hide this row">&#10005;</button></td>` +
            `<td><span class="leafMethodToggle">&#9656;</span>${escapeHtml(leaf)}</td>` +
            `<td>${formatMilliseconds(stack['mSec'])}</td>` +
            `<td>${share.toFixed(2)}</td>` +
            `<td>${Number(stack['count']).toLocaleString()}</td>` +
            `</tr>` +
            `<tr id="offCpuDetail${index}" class="callPathsDetail">` +
            `<td colspan="5" class="callerTreeCell">${frameHtml}</td></tr>`;
    }

    const reasonHeader = renderRankedTableHeader([
        ["Reason", "text"],
        ["Time (ms)", "number"],
        ["% of off-CPU", "number"],
    ]).replace(/<th[^>]*class="rowHideColumn"[^>]*><\/th>/, '');

    const stackHeader = renderRankedTableHeader([
        ["Blocked in", "text"],
        ["Time (ms)", "number"],
        ["% of off-CPU", "number"],
        ["Intervals", "number"],
    ]);

    return `
    <h2>Off-CPU time</h2>
    <p class="viewNote">Time threads spent <em>not</em> running, from <code>sched:sched_switch</code>.
       A thread blocked for the whole capture produces no CPU samples at all, so it is invisible in the CPU view and appears here instead.
       Total ${formatMilliseconds(totalMSec)} ms over ${Number(offCpu['intervalCount']).toLocaleString()} intervals.</p>

    <h3>By reason</h3>
    <div class="detailTable"><table>${reasonRows}</table></div>

    <h3>Where threads blocked</h3>
    <div class="detailTable cpuHotMethodsTable"><table id="offCpuStackTable">${stackHeader}${stackRows}</table></div>

    <h3>By thread</h3>
    <div class="detailTable"><table>
        <tr><th>Thread</th><th>Tid</th><th>Off-CPU (ms)</th></tr>
        ${threadRows}
    </table></div>`;
}
