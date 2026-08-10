////////////////////////////////////////////////////////////////////////////////
// Module: ProgressReporter.cs
//
// Notes:
// Emits "PROGRESS <percent> <phase label>" lines to stderr - the same
// channel the final "Timing: ..." line in Program.cs already uses - so the
// VS Code extension (DotnetInsightsNettraceEditor.ts) can drive a live
// progress bar while a .nettrace file is being parsed. Only ever active in
// --json mode (see Program.cs's own Enable() call site) - the plain
// human-readable CLI/--dump-fields path never calls into this class at all,
// so it is byte-for-byte unaffected by this feature.
//
// A caller reasons only in terms of ITS OWN 0.0-1.0 completion fraction
// (ReportFraction) within whatever [start, end) slice of the overall bar
// Program.cs assigned it via BeginPhase - see Progress/ProgressPlan.cs for
// how those slices are computed. This class owns turning that fraction into
// an absolute overall percent, plus every throttling/monotonicity concern,
// so call sites deep inside NettraceFile.Read or a JSON sub-writer's hot
// loop stay as simple as a single "here's my fraction" call.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.NetTrace.Progress {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Diagnostics;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public static class ProgressReporter
{
    // Shared by every per-event projector/builder loop (GcEventProjector,
    // AllocationEventProjector, ExceptionEventProjector,
    // EventOverviewBuilder, MethodSymbolTable, SampleProfileEventProjector,
    // ContentionEventProjector) and the two JSON sub-writers with their own
    // per-record hot loops (CpuProfileJsonExporter, AllocationJsonExporter)
    // as `(index & IndexProgressMask) == 0` - bounds a multi-million-
    // iteration loop to roughly 450 delegate invocations (2^24 / 2^16)
    // rather than one per iteration, which past perf work in this codebase
    // has repeatedly found and removed as a real hot-loop cost (see
    // CLAUDE.md's own CachedStackFrames history) - a single `and` plus one
    // perfectly-predicted branch per iteration otherwise.
    public const int IndexProgressMask = 0xFFFF;

    // At least this many milliseconds between two ReportFraction-driven
    // writes (BeginPhase/CompletePhase bypass this - see their own
    // comments) - bounds stderr output for a capture with millions of
    // ReportFraction calls without needing every individual call site to
    // reason about timing itself.
    private const int ThrottleMs = 100;

    private static bool enabled;
    private static string phaseLabel = string.Empty;
    private static double phaseStart;
    private static double phaseEnd;
    private static int lastReportedPercent = -1;
    private static readonly Stopwatch throttle = new Stopwatch();

    // Called once, only from Program.cs's --json mode branch - every other
    // method here is a silent no-op until this has been called, so the
    // plain CLI/--dump-fields path (which never calls Enable()) pays
    // nothing at all for this feature's existence.
    public static void Enable()
    {
        enabled = true;
    }

    // Test-only: this class is static/process-wide state (matching this
    // codebase's existing convention for simple utility classes - e.g.
    // ReadPhaseGcSuppression), so a test that calls Enable() must reset it
    // afterward or every LATER test in the same process would silently
    // start emitting PROGRESS lines too. Not called from any non-test code
    // path.
    public static void ResetForTests()
    {
        enabled = false;
        phaseLabel = string.Empty;
        phaseStart = 0.0;
        phaseEnd = 0.0;
        lastReportedPercent = -1;
        throttle.Reset();
    }

    // Pre-touches Console.Error's own lazy-initialized encoder/buffering
    // before the read phase's tightly-sized GC.TryStartNoGCRegion budget
    // (see ReadPhaseGcSuppression.cs's own header - undersizing that
    // region is measurably worse than never starting one) - without this,
    // the FIRST real progress write would be the one paying that one-time
    // allocation cost, inside the no-GC region where it's least welcome.
    public static void Warmup()
    {
        if (!enabled)
        {
            return;
        }

        Console.Error.Write(string.Empty);
        Console.Error.Flush();
    }

    // Announces a new phase and its [startPercent, endPercent) slice of
    // the overall bar. Always writes immediately (bypasses both the
    // percent-changed gate and the throttle) - a phase transition is
    // meaningful information (the label changed) even on the calls where
    // the numeric percent happens to be identical to whatever the PREVIOUS
    // phase's CompletePhase just reported (stages are contiguous by
    // construction - see ProgressPlan.cs - so this is the common case, not
    // an edge case).
    public static void BeginPhase(string label, double startPercent, double endPercent)
    {
        if (!enabled)
        {
            return;
        }

        phaseLabel = label;
        phaseStart = startPercent;
        phaseEnd = endPercent;
        Emit((int)phaseStart, force: true);
    }

    // fraction is THIS phase's own 0.0-1.0 completion, mapped here into
    // its [phaseStart, phaseEnd) slice - the one call every instrumented
    // loop (NettraceFile.Read's block loop, a projector's masked-index
    // check, a JSON sub-writer's own pass) actually needs to make.
    public static void ReportFraction(double fraction)
    {
        if (!enabled)
        {
            return;
        }

        if (fraction < 0.0)
        {
            fraction = 0.0;
        }
        else if (fraction > 1.0)
        {
            fraction = 1.0;
        }

        int percent = (int)(phaseStart + (fraction * (phaseEnd - phaseStart)));
        Emit(percent, force: false);
    }

    // Snaps to this phase's own exact end percent, absorbing whatever
    // estimate error accumulated during it - what makes a phase too small
    // to bother instrumenting internally (see ProgressPlan.cs's own
    // comment on which phases get real fraction tracking vs. just a
    // BeginPhase/CompletePhase pair) safe to leave as a pure start/end
    // jump: the jump is never more than that phase's own (small) share of
    // the bar. Bypasses the throttle - unlike an intermediate
    // ReportFraction call, a phase's true completion must never be
    // silently dropped just because the last write happened recently.
    public static void CompletePhase()
    {
        if (!enabled)
        {
            return;
        }

        Emit((int)Math.Round(phaseEnd), force: true);
    }

    private static void Emit(int percent, bool force)
    {
        // Monotonic clamp - a fraction can't ever un-happen (e.g. the read
        // phase's own known 32-bit position-tracking limit past ~2GB - see
        // ProgressPlan.cs's header comment), and the bar must never
        // visibly move backward regardless of what produced a lower value.
        if (percent < lastReportedPercent)
        {
            percent = lastReportedPercent;
        }

        bool percentChanged = percent != lastReportedPercent;
        if (!percentChanged && !force)
        {
            return;
        }

        if (!force && throttle.IsRunning && throttle.ElapsedMilliseconds < ThrottleMs)
        {
            return;
        }

        lastReportedPercent = percent;
        throttle.Restart();
        Console.Error.WriteLine($"PROGRESS {percent} {phaseLabel}");
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.Progress)

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////
