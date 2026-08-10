////////////////////////////////////////////////////////////////////////////////
// Module: ProgressReporterTests.cs
//
// Notes:
// Covers ProgressReporter's own gating/monotonicity/snap-to-end guarantees -
// see that file's own header comment. ProgressReporter is static/process-
// wide state (matching this codebase's existing convention for simple
// utility classes - e.g. ReadPhaseGcSuppression), so every test here resets
// it via ProgressReporter.ResetForTests() and restores the real
// Console.Error, in a finally block, so a failure partway through one test
// can't leave later tests (in this file or, since none of them redirect
// stderr, any other) observing a redirected stream or stale enabled state.
////////////////////////////////////////////////////////////////////////////////

using System;
using System.IO;

using DotnetInsights.NetTrace.Progress;

using Xunit;

namespace DotnetInsights.NetTrace.Tests {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public class ProgressReporterTests
{
    // Runs body with ProgressReporter enabled and Console.Error redirected
    // to a StringWriter, restoring both afterward regardless of outcome -
    // every test in this file goes through this so none of them can leak
    // state into the next.
    private static string[] CaptureProgressLines(Action body)
    {
        TextWriter originalError = Console.Error;
        StringWriter capturedError = new StringWriter();

        try
        {
            Console.SetError(capturedError);
            ProgressReporter.ResetForTests();
            ProgressReporter.Enable();

            body();

            string output = capturedError.ToString();
            if (output.Length == 0)
            {
                return Array.Empty<string>();
            }

            return output.Split(new[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries);
        }
        finally
        {
            ProgressReporter.ResetForTests();
            Console.SetError(originalError);
        }
    }

    [Fact]
    public void ReportFraction_WhenDisabled_WritesNothing()
    {
        TextWriter originalError = Console.Error;
        StringWriter capturedError = new StringWriter();

        try
        {
            Console.SetError(capturedError);
            ProgressReporter.ResetForTests();
            // Deliberately no Enable() call here - this is the plain
            // CLI/--dump-fields path's own contract.

            ProgressReporter.BeginPhase("Reading trace file", 0.0, 30.0);
            ProgressReporter.ReportFraction(0.5);
            ProgressReporter.CompletePhase();

            Assert.Equal(string.Empty, capturedError.ToString());
        }
        finally
        {
            ProgressReporter.ResetForTests();
            Console.SetError(originalError);
        }
    }

    [Fact]
    public void BeginPhase_AlwaysEmitsImmediatelyEvenAtTheSamePercentAsThePreviousPhaseEnded()
    {
        string[] lines = CaptureProgressLines(() =>
        {
            ProgressReporter.BeginPhase("Reading trace file", 0.0, 30.0);
            ProgressReporter.CompletePhase();
            // Next phase starts at exactly 30, the same percent the read
            // phase just snapped to on CompletePhase - contiguous stages
            // are the NORMAL case (see ProgressPlan.cs), not an edge case,
            // so this must still announce the label change.
            ProgressReporter.BeginPhase("Projecting GC events", 30.0, 40.0);
        });

        Assert.Contains(lines, line => line == "PROGRESS 30 Reading trace file");
        Assert.Contains(lines, line => line == "PROGRESS 30 Projecting GC events");
    }

    [Fact]
    public void CompletePhase_SnapsToTheExactEndPercentRegardlessOfLastReportedFraction()
    {
        string[] lines = CaptureProgressLines(() =>
        {
            ProgressReporter.BeginPhase("Exporting CPU profile", 46.0, 100.0);
            ProgressReporter.ReportFraction(0.01); // barely into the phase
            ProgressReporter.CompletePhase();
        });

        Assert.Equal("PROGRESS 100 Exporting CPU profile", lines[lines.Length - 1]);
    }

    [Fact]
    public void ReportFraction_NeverReportsAPercentLowerThanAlreadyReported()
    {
        string[] lines = CaptureProgressLines(() =>
        {
            ProgressReporter.BeginPhase("Reading trace file", 0.0, 100.0);
            ProgressReporter.ReportFraction(0.9);
            // A later, lower fraction must never move the bar backward -
            // e.g. the read phase's own known 32-bit position-tracking
            // limit (see ProgressPlan.cs's header comment) could otherwise
            // produce exactly this.
            ProgressReporter.ReportFraction(0.1);
            ProgressReporter.CompletePhase();
        });

        int previousPercent = -1;
        foreach (string line in lines)
        {
            int percent = ParsePercent(line);
            Assert.True(percent >= previousPercent, $"'{line}' reported a percent lower than a previous line");
            previousPercent = percent;
        }
    }

    [Fact]
    public void ReportFraction_ClampsFractionsOutsideZeroToOne()
    {
        string[] lines = CaptureProgressLines(() =>
        {
            ProgressReporter.BeginPhase("Reading trace file", 0.0, 50.0);
            ProgressReporter.ReportFraction(-5.0);
            ProgressReporter.ReportFraction(5.0);
        });

        foreach (string line in lines)
        {
            int percent = ParsePercent(line);
            Assert.InRange(percent, 0, 50);
        }
    }

    [Fact]
    public void Warmup_WhenDisabled_DoesNotThrowOrWrite()
    {
        TextWriter originalError = Console.Error;
        StringWriter capturedError = new StringWriter();

        try
        {
            Console.SetError(capturedError);
            ProgressReporter.ResetForTests();

            ProgressReporter.Warmup();

            Assert.Equal(string.Empty, capturedError.ToString());
        }
        finally
        {
            ProgressReporter.ResetForTests();
            Console.SetError(originalError);
        }
    }

    private static int ParsePercent(string progressLine)
    {
        // "PROGRESS <percent> <label>"
        string[] parts = progressLine.Split(' ', 3);
        Assert.Equal("PROGRESS", parts[0]);
        return int.Parse(parts[1]);
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.Tests)
