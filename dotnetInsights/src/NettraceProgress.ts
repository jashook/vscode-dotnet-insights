// Parses nettraceParser's own "PROGRESS <percent> <label>" stderr lines (see
// nettraceParser/Progress/ProgressReporter.cs) and owns the host-side GLOBAL
// mapping from "the child process's own 0-100" down to a smaller slice of
// the bar, since real work remains in the extension host after the child
// exits: reading the JSON + ticks-binary output back
// (NettraceJsonStreamReader.ts's readNettraceJson), rendering the full
// webview HTML (GcSnapshotRenderer.ts's renderGcSnapshotWebview), and
// assigning webview.html itself. The child process never reports 100 for
// exactly this reason (see ProgressReporter.cs's own comment) - showing 100%
// followed by a multi-second freeze while those three steps run would be
// the opposite of what an accurate bar is for.
//
// Pure/stateless functions plus one small stateful tracker class (for the
// monotonic clamp and the "replay the last value to a webview that just
// signaled it's ready" handshake - see DotnetInsightsNettraceEditor.ts's own
// onDidReceiveMessage) - kept in its own file, not inline in the editor,
// specifically because it's the one piece of this feature meaningfully
// unit-testable without a real VS Code host (see
// src/test/suite/nettraceProgress.test.ts).

export interface ProgressStageRange {
    readonly start: number;
    readonly end: number;
}

export interface NettraceChildProgress {
    readonly percent: number;
    readonly label: string;
}

export interface NettraceProgressUpdate {
    readonly percent: number;
    readonly label: string;
}

const PROGRESS_LINE_PATTERN = /^PROGRESS (\d+) (.*)$/;

// Parses one line of nettraceParser's own stderr - returns null for any
// line that isn't a PROGRESS line (e.g. the final "Timing: ..." diagnostic
// line, or a stale/pre-this-feature binary's own unrelated output - see
// CLAUDE.md's "stale-cache trap"), so a caller can just skip non-matches
// without special-casing them.
export function parseProgressLine(line: string): NettraceChildProgress | null {
    const match = PROGRESS_LINE_PATTERN.exec(line);
    if (!match) {
        return null;
    }

    const percent = parseInt(match[1], 10);
    if (isNaN(percent)) {
        return null;
    }

    return { percent: percent, label: match[2] };
}

// The child process's own [0-100] maps into this host-side range; the
// remaining three ranges are the extension host's OWN post-process work,
// in the order it actually happens - see this file's own header comment.
export const CHILD_PROCESS_RANGE: ProgressStageRange = { start: 0, end: 80 };
export const JSON_READ_RANGE: ProgressStageRange = { start: 80, end: 90 };
export const RENDER_RANGE: ProgressStageRange = { start: 90, end: 97 };
export const SWAP_RANGE: ProgressStageRange = { start: 97, end: 100 };

function clamp(value: number, min: number, max: number): number {
    return Math.max(min, Math.min(max, value));
}

export function mapChildPercentToGlobal(childPercent: number): number {
    const clampedChildPercent = clamp(childPercent, 0, 100);
    const span = CHILD_PROCESS_RANGE.end - CHILD_PROCESS_RANGE.start;
    return CHILD_PROCESS_RANGE.start + ((clampedChildPercent / 100) * span);
}

export function mapHostStageFractionToGlobal(fraction: number, range: ProgressStageRange): number {
    const clampedFraction = clamp(fraction, 0, 1);
    return range.start + (clampedFraction * (range.end - range.start));
}

// Tracks the single most recently reported { percent, label } and enforces
// monotonicity across BOTH the child process's own reports (already
// monotonic on their own - see ProgressReporter.cs - but re-clamped here
// too, defensively, since this class is also the boundary between the
// child's range and the host's own three stages afterward, which must
// never regress below wherever the child left off) and the host's own
// stage transitions. Also backs the ready-handshake: a webview's own
// postMessage calls sent before its document has finished loading and run
// its own script are silently dropped (there is no VS Code-side buffering)
// - see DotnetInsightsNettraceEditor.ts's own onDidReceiveMessage, which
// replays `current` once the loading view signals it's ready.
export class NettraceProgressTracker {
    private lastPercent = 0;
    private lastLabel = "Starting…";

    public recordChildPercent(childPercent: number, label: string): NettraceProgressUpdate {
        return this.record(mapChildPercentToGlobal(childPercent), label);
    }

    public recordHostStage(fraction: number, range: ProgressStageRange, label: string): NettraceProgressUpdate {
        return this.record(mapHostStageFractionToGlobal(fraction, range), label);
    }

    public get current(): NettraceProgressUpdate {
        return { percent: this.lastPercent, label: this.lastLabel };
    }

    private record(percent: number, label: string): NettraceProgressUpdate {
        // Rounded BEFORE clamping (not after) so lastPercent - and every
        // value this tracker ever hands to the UI - is always a whole
        // number, matching ProgressReporter.cs's own C#-side int percent
        // exactly. Rounding is needed here specifically because mapping an
        // already-whole child percent through a sub-range (e.g. the child's
        // own 33 through [0, 80) - see CHILD_PROCESS_RANGE) is a
        // multiplication that doesn't generally land on a whole number
        // (33 -> 26.4) even though its INPUT was one.
        const roundedPercent = Math.round(percent);

        // Never allow the reported percent to move backward - the same
        // guarantee ProgressReporter.cs's own Emit gives on the C# side,
        // applied again here since this tracker ALSO bridges into the
        // three host-only stages after the child's own range ends.
        const clampedPercent = clamp(roundedPercent, this.lastPercent, 100);
        this.lastPercent = clampedPercent;
        this.lastLabel = label;
        return this.current;
    }
}
