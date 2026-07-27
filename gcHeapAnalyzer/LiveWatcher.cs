////////////////////////////////////////////////////////////////////////////////
// Module: LiveWatcher.cs
//
// Notes:
// ClrMD's live-process API has no cheap way to ask "has a GC happened since
// my last look" - DataTarget.AttachToProcess's own docs say inspecting a
// running (non-suspended) process is unsupported/undefined behavior, so
// there is no lightweight polling primitive short of the full
// attach+suspend+walk cycle HeapAnalyzer already does.
//
// A single cycle can take seconds on a multi-GB heap (see the Timing: line
// HeapAnalyzer prints per cycle), so between any two cycles a GC has very
// likely already happened by the time the next capture starts - there's no
// point trying to detect that and gate the write on it. Each cycle's
// result is internally consistent (the process is suspended for the whole
// walk) even though it may be several GCs newer than the last one, so we
// just always overwrite --output with the latest capture ("patch the
// data" = replace it wholesale with the newest complete snapshot).
//
// MinIntervalSeconds is a floor on the gap between cycle starts so small
// heaps (whose walk finishes in milliseconds) don't poll needlessly fast;
// for heaps whose walk already exceeds that floor, the next cycle starts
// as soon as the previous one (and its resume) finishes.
//
// StopFilePath is a second, OS-signal-independent way to ask this to stop:
// checked at the top of every cycle and while waiting between cycles, and
// deleted once honored. This exists because a hard kill of this process
// mid-cycle can leave the ptrace-attached target stopped or terminated on
// macOS (DataTarget's Dispose/PT_DETACH never runs) - a file check gives
// operators a reliable "ask nicely first" path even in environments where
// SIGTERM delivery to an elevated process is uncertain.
//
// DurationSeconds bounds the whole run the same way dotnet-trace's
// --duration does, so a paired "N-minute GC trace + N-minute ClrMD watch"
// capture (the common use case this was built for) doesn't need an
// external sleep+touch-stop-file script to keep both halves aligned. A
// null value means "run until told to stop" (Ctrl+C/SIGTERM/stop file).
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.GcHeapAnalyzer {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public static class LiveWatcher
{
    private const int MaxConsecutiveErrors = 5;
    private const int StopFilePollIntervalMs = 500;

    public static int Watch(int pid, string outputPath, int minIntervalSeconds, string stopFilePath, int? durationSeconds)
    {
        using CancellationTokenSource stopSource = new CancellationTokenSource();

        Console.CancelKeyPress += (sender, cancelArgs) =>
        {
            cancelArgs.Cancel = true;
            stopSource.Cancel();
        };

        // Held for the lifetime of this method - see gcHeapLoadGenerator's
        // LoadGenerator.cs for why an unreferenced PosixSignalRegistration
        // can be garbage collected and silently unregistered mid-run.
        PosixSignalRegistration sigTermRegistration = null;

        try
        {
            sigTermRegistration = PosixSignalRegistration.Create(PosixSignal.SIGTERM, signalContext =>
            {
                signalContext.Cancel = true;
                stopSource.Cancel();
            });
        }
        catch (PlatformNotSupportedException)
        {
            // SIGTERM has no meaning on this platform (e.g. Windows) - Ctrl+C still works.
        }

        Console.Error.WriteLine($"Watching PID {pid}, patching {outputPath} on every cycle.");
        Console.Error.WriteLine($"Minimum {minIntervalSeconds}s between cycle starts; each cycle suspends the target for the duration of its heap walk.");

        if (durationSeconds.HasValue)
        {
            Console.Error.WriteLine($"Will stop automatically after {durationSeconds.Value}s.");
        }

        if (stopFilePath != null)
        {
            Console.Error.WriteLine($"Press Ctrl+C to stop, send SIGTERM, or create {stopFilePath}.");
        }
        else
        {
            Console.Error.WriteLine("Press Ctrl+C to stop (or send SIGTERM).");
        }

        Console.Error.WriteLine();

        int cycleCount = 0;
        int consecutiveErrors = 0;
        Stopwatch cycleStopwatch = new Stopwatch();
        Stopwatch totalStopwatch = Stopwatch.StartNew();
        bool stopFileSeen = false;
        bool durationReached = false;

        while (!stopSource.IsCancellationRequested)
        {
            if (stopFilePath != null && File.Exists(stopFilePath))
            {
                stopFileSeen = true;
                TryDeleteStopFile(stopFilePath);
                break;
            }

            if (durationSeconds.HasValue && totalStopwatch.Elapsed.TotalSeconds >= durationSeconds.Value)
            {
                durationReached = true;
                break;
            }

            cycleStopwatch.Restart();
            ++cycleCount;

            FragmentationReport report;
            string errorMessage;
            bool success = HeapAnalyzer.TryAnalyze(pid, out report, out errorMessage);

            if (!success)
            {
                ++consecutiveErrors;
                Console.Error.WriteLine($"[cycle {cycleCount}] Analysis failed: {errorMessage}");

                if (consecutiveErrors >= MaxConsecutiveErrors)
                {
                    Console.Error.WriteLine($"Stopping after {consecutiveErrors} consecutive failures - is the process still running?");
                    break;
                }
            }
            else
            {
                consecutiveErrors = 0;

                string json = ReportJsonExporter.ToJson(report);
                File.WriteAllText(outputPath, json);

                Console.Error.WriteLine(
                    $"[cycle {cycleCount}] Patched {outputPath} " +
                    $"(fragmentation={report.Summary.FragmentationPct:F2}%, " +
                    $"committed={report.Summary.TotalCommittedBytes} bytes, " +
                    $"free={report.Summary.TotalFreeBytes} bytes)");
            }

            long minIntervalMs = minIntervalSeconds * 1000L;
            long remainingMs = minIntervalMs - cycleStopwatch.ElapsedMilliseconds;

            while (remainingMs > 0 && !stopSource.IsCancellationRequested)
            {
                if (stopFilePath != null && File.Exists(stopFilePath))
                {
                    stopFileSeen = true;
                    TryDeleteStopFile(stopFilePath);
                    break;
                }

                if (durationSeconds.HasValue && totalStopwatch.Elapsed.TotalSeconds >= durationSeconds.Value)
                {
                    durationReached = true;
                    break;
                }

                int waitSliceMs = (int)Math.Min(remainingMs, StopFilePollIntervalMs);
                stopSource.Token.WaitHandle.WaitOne(waitSliceMs);
                remainingMs -= waitSliceMs;
            }

            if (stopFileSeen || durationReached)
            {
                break;
            }
        }

        Console.Error.WriteLine();

        if (stopFileSeen)
        {
            Console.Error.WriteLine($"Stop file detected - stopped after {cycleCount} cycle(s).");
        }
        else if (durationReached)
        {
            Console.Error.WriteLine($"Duration ({durationSeconds.Value}s) reached - stopped after {cycleCount} cycle(s).");
        }
        else
        {
            Console.Error.WriteLine($"Stopped after {cycleCount} cycle(s).");
        }

        sigTermRegistration?.Dispose();
        return 0;
    }

    private static void TryDeleteStopFile(string stopFilePath)
    {
        try
        {
            File.Delete(stopFilePath);
        }
        catch (IOException)
        {
            // Best-effort - a stale stop file left behind just means the
            // *next* watch run will see it and exit immediately, which is
            // a safe failure mode (not silently ignoring a stop request).
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.GcHeapAnalyzer)
