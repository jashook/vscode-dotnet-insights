////////////////////////////////////////////////////////////////////////////////
// Module: LoadGenerator.cs
//
// Notes:
// Drives a managed heap toward a steady-state baseline size:
//
//   - Small (non-LOH) objects use a FIFO retained queue: most escape
//     immediately as garbage, a minority are retained toward the baseline,
//     and once the baseline is full a new retained object evicts the
//     oldest one first.
//
//   - LOH objects mock a TTL cache of large payloads (e.g. serialized API
//     responses). Each cached payload gets its own random TTL, so entries
//     expire in an order unrelated to insertion order - this is what
//     scatters free holes through the LOH address space instead of
//     freeing it front-to-back like the small-object FIFO does. TTLs are
//     kept short by default so additions/evictions churn at a high rate
//     rather than settling into a mostly-static heap. A minority of LOH
//     payloads are never cached at all (one-off, immediate garbage).
//
// Together these give gcHeapAnalyzer's FreeChunkReport something realistic
// to find: both compactable gen2 churn and genuinely fragmented,
// scattered LOH free chunks.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.GcHeapLoadGenerator {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public static class LoadGenerator
{
    public static void Run(LoadGeneratorOptions options)
    {
        long targetBytes = options.TargetBytes();
        long smallTargetBytes = (long)(targetBytes * options.SmallFraction);
        long lohTargetBytes = targetBytes - smallTargetBytes;

        Console.Error.WriteLine($"PID {Environment.ProcessId}: Server GC = {System.Runtime.GCSettings.IsServerGC}");
        Console.Error.WriteLine($"Target baseline: {FormatGb(targetBytes)} GB (small={FormatGb(smallTargetBytes)} GB, LOH={FormatGb(lohTargetBytes)} GB)");
        Console.Error.WriteLine("Press Ctrl+C to stop (or send SIGTERM).");
        Console.Error.WriteLine();

        using CancellationTokenSource stopSource = new CancellationTokenSource();

        Console.CancelKeyPress += (sender, cancelArgs) =>
        {
            cancelArgs.Cancel = true;
            stopSource.Cancel();
        };

        // Held for the lifetime of Run(): PosixSignalRegistration is only
        // rooted by holding a reference to it, and this process GCs
        // aggressively - an unreferenced registration can be collected
        // and silently unregistered mid-run, after which SIGTERM falls
        // back to the OS default (immediate termination, no clean stop).
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

        Random rng = new Random();

        Queue<byte[]> retainedSmall = new Queue<byte[]>();
        long retainedSmallBytes = 0;

        Dictionary<long, byte[]> payloadCache = new Dictionary<long, byte[]>();
        PriorityQueue<long, long> expiryQueue = new PriorityQueue<long, long>();
        long retainedLohBytes = 0;
        long nextLohKey = 0;

        long totalAllocatedBytes = 0;
        long statusIntervalMs = options.StatusIntervalSeconds * 1000L;
        long lastStatusMs = 0;

        Stopwatch stopwatch = Stopwatch.StartNew();

        while (!stopSource.IsCancellationRequested)
        {
            long nowMs = stopwatch.ElapsedMilliseconds;

            long peekedKey;
            long peekedExpiry;
            while (expiryQueue.TryPeek(out peekedKey, out peekedExpiry) && peekedExpiry <= nowMs)
            {
                expiryQueue.Dequeue();

                byte[] expiredPayload;
                if (payloadCache.Remove(peekedKey, out expiredPayload))
                {
                    retainedLohBytes -= expiredPayload.Length;
                }
            }

            bool allocateLoh = rng.NextDouble() < options.LohChance;

            if (allocateLoh)
            {
                int size = NextLohPayloadSize(rng, options);
                byte[] buffer = new byte[size];
                buffer[0] = 1;
                totalAllocatedBytes += size;

                if (rng.NextDouble() < options.LohRetainChance)
                {
                    while (retainedLohBytes + size > lohTargetBytes && expiryQueue.Count > 0)
                    {
                        long evictKey = expiryQueue.Dequeue();
                        byte[] evicted;
                        if (payloadCache.Remove(evictKey, out evicted))
                        {
                            retainedLohBytes -= evicted.Length;
                        }
                    }

                    int ttlMs = rng.Next(options.LohMinTtlMs, options.LohMaxTtlMs + 1);
                    long key = ++nextLohKey;
                    payloadCache[key] = buffer;
                    expiryQueue.Enqueue(key, nowMs + ttlMs);
                    retainedLohBytes += size;
                }

                // else: buffer is a one-off payload that is never cached, and
                // becomes garbage on the LOH immediately.
            }
            else
            {
                int size = rng.Next(options.SmallMinBytes, options.SmallMaxBytes + 1);
                byte[] buffer = new byte[size];
                buffer[0] = 1;
                totalAllocatedBytes += size;

                if (rng.NextDouble() < options.RetainChance)
                {
                    while (retainedSmallBytes + size > smallTargetBytes && retainedSmall.Count > 0)
                    {
                        byte[] evicted = retainedSmall.Dequeue();
                        retainedSmallBytes -= evicted.Length;
                    }

                    retainedSmall.Enqueue(buffer);
                    retainedSmallBytes += size;
                }

                // else: buffer is dropped here and escapes as garbage.
            }

            if (stopwatch.ElapsedMilliseconds - lastStatusMs >= statusIntervalMs)
            {
                lastStatusMs = stopwatch.ElapsedMilliseconds;
                PrintStatus(stopwatch.Elapsed, retainedSmallBytes, retainedLohBytes, payloadCache.Count, totalAllocatedBytes);
            }
        }

        Console.Error.WriteLine();
        Console.Error.WriteLine("Stopping.");
        PrintStatus(stopwatch.Elapsed, retainedSmallBytes, retainedLohBytes, payloadCache.Count, totalAllocatedBytes);

        sigTermRegistration?.Dispose();
    }

    private static int NextLohPayloadSize(Random rng, LoadGeneratorOptions options)
    {
        if (rng.NextDouble() < options.LohLargePayloadChance)
        {
            return rng.Next(options.LohLargeMinBytes, options.LohLargeMaxBytes + 1);
        }

        return rng.Next(options.LohMinBytes, options.LohTypicalMaxBytes + 1);
    }

    private static void PrintStatus(TimeSpan elapsed, long retainedSmallBytes, long retainedLohBytes, int cacheEntryCount, long totalAllocatedBytes)
    {
        long totalRetainedBytes = retainedSmallBytes + retainedLohBytes;
        long managedHeapBytes = GC.GetTotalMemory(false);
        long workingSetBytes = Process.GetCurrentProcess().WorkingSet64;

        Console.Error.WriteLine(
            $"[{elapsed:hh\\:mm\\:ss}] retained={FormatGb(totalRetainedBytes)}GB " +
            $"(small={FormatGb(retainedSmallBytes)}GB, loh={FormatGb(retainedLohBytes)}GB, cacheEntries={cacheEntryCount}) " +
            $"heap={FormatGb(managedHeapBytes)}GB workingSet={FormatGb(workingSetBytes)}GB " +
            $"allocatedLifetime={FormatGb(totalAllocatedBytes)}GB " +
            $"gen0={GC.CollectionCount(0)} gen1={GC.CollectionCount(1)} gen2={GC.CollectionCount(2)}");
    }

    private static string FormatGb(long bytes)
    {
        double gb = bytes / (1024.0 * 1024.0 * 1024.0);
        return gb.ToString("F2");
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.GcHeapLoadGenerator)
