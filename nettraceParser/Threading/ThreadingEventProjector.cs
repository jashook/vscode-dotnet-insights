////////////////////////////////////////////////////////////////////////////////
// Module: ThreadingEventProjector.cs
//
// Notes:
// Reduces the CLR's thread-pool events into the Threading view's summary.
//
// Unlike every other projector here, this one does NOT return a list of
// per-event structs. ThreadPoolWorkerThread/Wait alone is 12.2M events on the
// reference capture - 35% of the entire file, more than every GC, allocation,
// exception and contention event combined - and it carries nothing but two
// counters. Materializing it would cost hundreds of megabytes to describe a
// line on a chart, so the worker-count series is bucketed during the pass and
// the events are discarded as they are read.
//
// What the events can and cannot answer (verified against a real capture):
//   - Wait/Start/Stop carry ActiveWorkerThreadCount, so pool SIZE over time is
//     densely sampled and reliable.
//   - None of the high-volume events carry stacks (0 of 12.2M Wait events do),
//     so these events alone cannot say WHERE a pool thread was blocked.
//     ThreadingJsonExporter answers that separately by correlating CPU samples
//     against adjustment timestamps.
//   - ThreadCreating (100% stacked) and Contention/LockCreated (100% stacked)
//     are low-volume and keep their stacks, so "who created this" is
//     answerable for both.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.NetTrace.Threading {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

using DotnetInsights.NetTrace.Contention;
using DotnetInsights.NetTrace.Gc;
using DotnetInsights.NetTrace.Progress;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public sealed class ThreadPoolAdjustmentRecord
{
    public double RelativeMSec;
    public int NewWorkerThreadCount;
    public int Reason;
    public double AverageThroughput;
}

// A stacked, low-volume event worth keeping in full.
public sealed class StackedThreadingEvent
{
    public double RelativeMSec;
    public long ThreadId;
    // The object this event is about - a managed thread id for ThreadCreating,
    // a lock pointer for LockCreated. Zero when not applicable.
    public long ObjectId;
    public int StackIndex;
}

public sealed class ThreadingSummary
{
    public bool HasThreadPoolData;

    public int PeakActiveWorkerThreads;
    public int MinActiveWorkerThreads;
    public int FinalActiveWorkerThreads;
    public int PeakRetiredWorkerThreads;

    public long WorkerThreadStartCount;
    public long WorkerThreadStopCount;
    public long WorkerThreadWaitCount;

    public double MinRelativeMSec;
    public double MaxRelativeMSec;

    // Bucketed worker-count series - see this file's header for why the raw
    // events are never kept.
    public int BucketCount;
    public double BucketDurationMSec;
    public int[] MinActiveByBucket;
    public int[] MaxActiveByBucket;
    public double[] AverageActiveByBucket;
    public double[] ThroughputByBucket;

    public List<ThreadPoolAdjustmentRecord> Adjustments = new List<ThreadPoolAdjustmentRecord>();
    public List<StackedThreadingEvent> ThreadCreations = new List<StackedThreadingEvent>();
    public List<StackedThreadingEvent> LockCreations = new List<StackedThreadingEvent>();
}

public static class ThreadingEventProjector
{
    private const string ClrProviderName = "Microsoft-Windows-DotNETRuntime";

    private const int MaxTimelineBuckets = 200;

    // Both are low-volume by nature (49 and 1,239 on the reference capture),
    // but a pathological capture must not be able to blow the payload up
    // through them.
    private const int MaxStackedEvents = 500;

    public static ThreadingSummary Project(List<EventRecord> events, int pointerSize, long qpcFrequency, long referenceQpc, Action<double> onProgress = null)
    {
        ThreadingSummary summary = new ThreadingSummary();
        summary.MinActiveWorkerThreads = int.MaxValue;

        if (qpcFrequency <= 0)
        {
            return summary;
        }

        Span<EventRecord> eventsSpan = CollectionsMarshal.AsSpan(events);

        // Pass 1 finds the time span of the threading events themselves. The
        // capture's own span can't be used: the pool may only report for part
        // of it, and bucketing against a wider range would leave the chart
        // mostly empty. Two passes over the event list are far cheaper than
        // buffering 12M timestamps to discover the range afterwards.
        double minRelativeMSec = double.MaxValue;
        double maxRelativeMSec = double.MinValue;

        for (int eventIndex = 0; eventIndex < eventsSpan.Length; ++eventIndex)
        {
            ref readonly EventRecord record = ref eventsSpan[eventIndex];

            if (record.ProviderName != ClrProviderName || !IsWorkerCountEvent(record.EventId))
            {
                continue;
            }

            double relativeMSec = (record.TimeStampRelativeQPC - referenceQpc) * 1000.0 / qpcFrequency;

            if (relativeMSec < minRelativeMSec)
            {
                minRelativeMSec = relativeMSec;
            }

            if (relativeMSec > maxRelativeMSec)
            {
                maxRelativeMSec = relativeMSec;
            }
        }

        if (minRelativeMSec > maxRelativeMSec)
        {
            return summary;
        }

        summary.HasThreadPoolData = true;
        summary.MinRelativeMSec = minRelativeMSec;
        summary.MaxRelativeMSec = maxRelativeMSec;

        double totalDurationMSec = maxRelativeMSec - minRelativeMSec;
        summary.BucketCount = totalDurationMSec > 0 ? MaxTimelineBuckets : 1;
        summary.BucketDurationMSec = totalDurationMSec > 0 ? totalDurationMSec / summary.BucketCount : 1;

        summary.MinActiveByBucket = new int[summary.BucketCount];
        summary.MaxActiveByBucket = new int[summary.BucketCount];
        summary.AverageActiveByBucket = new double[summary.BucketCount];
        summary.ThroughputByBucket = new double[summary.BucketCount];

        long[] activeSumByBucket = new long[summary.BucketCount];
        long[] activeCountByBucket = new long[summary.BucketCount];
        double[] throughputSumByBucket = new double[summary.BucketCount];
        long[] throughputCountByBucket = new long[summary.BucketCount];

        for (int bucketIndex = 0; bucketIndex < summary.BucketCount; ++bucketIndex)
        {
            summary.MinActiveByBucket[bucketIndex] = int.MaxValue;
        }

        for (int eventIndex = 0; eventIndex < eventsSpan.Length; ++eventIndex)
        {
            if (onProgress != null && (eventIndex & ProgressReporter.IndexProgressMask) == 0)
            {
                onProgress((double)eventIndex / eventsSpan.Length);
            }

            ref readonly EventRecord record = ref eventsSpan[eventIndex];

            if (record.ProviderName != ClrProviderName)
            {
                continue;
            }

            int eventId = record.EventId;

            if (!IsThreadingEvent(eventId))
            {
                continue;
            }

            double relativeMSec = (record.TimeStampRelativeQPC - referenceQpc) * 1000.0 / qpcFrequency;
            PayloadReader reader = new PayloadReader(record.PayloadBuffer, record.PayloadOffset, record.PayloadLength, pointerSize);

            if (IsWorkerCountEvent(eventId))
            {
                ClrThreadPoolWorkerThread workerThread = ClrThreadPoolWorkerThread.Decode(reader);

                if (eventId == ClrThreadingEventIds.ThreadPoolWorkerThreadStart)
                {
                    ++summary.WorkerThreadStartCount;
                }
                else if (eventId == ClrThreadingEventIds.ThreadPoolWorkerThreadStop)
                {
                    ++summary.WorkerThreadStopCount;
                }
                else
                {
                    ++summary.WorkerThreadWaitCount;
                }

                int active = workerThread.ActiveWorkerThreadCount;

                if (active > summary.PeakActiveWorkerThreads)
                {
                    summary.PeakActiveWorkerThreads = active;
                }

                if (active < summary.MinActiveWorkerThreads)
                {
                    summary.MinActiveWorkerThreads = active;
                }

                if (workerThread.RetiredWorkerThreadCount > summary.PeakRetiredWorkerThreads)
                {
                    summary.PeakRetiredWorkerThreads = workerThread.RetiredWorkerThreadCount;
                }

                summary.FinalActiveWorkerThreads = active;

                int bucketIndex = BucketIndexFor(relativeMSec, minRelativeMSec, summary.BucketDurationMSec, summary.BucketCount);

                if (active < summary.MinActiveByBucket[bucketIndex])
                {
                    summary.MinActiveByBucket[bucketIndex] = active;
                }

                if (active > summary.MaxActiveByBucket[bucketIndex])
                {
                    summary.MaxActiveByBucket[bucketIndex] = active;
                }

                activeSumByBucket[bucketIndex] += active;
                ++activeCountByBucket[bucketIndex];
                continue;
            }

            if (eventId == ClrThreadingEventIds.ThreadPoolWorkerThreadAdjustmentAdjustment)
            {
                ClrThreadPoolAdjustment adjustment = ClrThreadPoolAdjustment.Decode(reader);

                ThreadPoolAdjustmentRecord record2 = new ThreadPoolAdjustmentRecord();
                record2.RelativeMSec = relativeMSec;
                record2.NewWorkerThreadCount = adjustment.NewWorkerThreadCount;
                record2.Reason = adjustment.Reason;
                record2.AverageThroughput = adjustment.AverageThroughput;
                summary.Adjustments.Add(record2);
                continue;
            }

            if (eventId == ClrThreadingEventIds.ThreadPoolWorkerThreadAdjustmentSample)
            {
                ClrThreadPoolAdjustmentSample sample = ClrThreadPoolAdjustmentSample.Decode(reader);
                int bucketIndex = BucketIndexFor(relativeMSec, minRelativeMSec, summary.BucketDurationMSec, summary.BucketCount);
                throughputSumByBucket[bucketIndex] += sample.Throughput;
                ++throughputCountByBucket[bucketIndex];
                continue;
            }

            if (eventId == ClrThreadingEventIds.ThreadCreating && summary.ThreadCreations.Count < MaxStackedEvents)
            {
                StackedThreadingEvent created = new StackedThreadingEvent();
                created.RelativeMSec = relativeMSec;
                created.ThreadId = record.ThreadId;
                created.ObjectId = reader.Length >= pointerSize ? reader.GetAddressAt(0) : 0;
                created.StackIndex = record.StackIndex;
                summary.ThreadCreations.Add(created);
                continue;
            }

            if (eventId == ClrContentionEventIds.LockCreated && summary.LockCreations.Count < MaxStackedEvents)
            {
                StackedThreadingEvent created = new StackedThreadingEvent();
                created.RelativeMSec = relativeMSec;
                created.ThreadId = record.ThreadId;
                created.ObjectId = reader.Length >= pointerSize ? reader.GetAddressAt(0) : 0;
                created.StackIndex = record.StackIndex;
                summary.LockCreations.Add(created);
            }
        }

        for (int bucketIndex = 0; bucketIndex < summary.BucketCount; ++bucketIndex)
        {
            if (activeCountByBucket[bucketIndex] > 0)
            {
                summary.AverageActiveByBucket[bucketIndex] = (double)activeSumByBucket[bucketIndex] / activeCountByBucket[bucketIndex];
            }
            else
            {
                // No sample landed here; a zero would draw a false collapse to
                // an empty pool, so the bucket is left explicitly empty.
                summary.MinActiveByBucket[bucketIndex] = 0;
            }

            if (summary.MinActiveByBucket[bucketIndex] == int.MaxValue)
            {
                summary.MinActiveByBucket[bucketIndex] = 0;
            }

            if (throughputCountByBucket[bucketIndex] > 0)
            {
                summary.ThroughputByBucket[bucketIndex] = throughputSumByBucket[bucketIndex] / throughputCountByBucket[bucketIndex];
            }
        }

        if (summary.MinActiveWorkerThreads == int.MaxValue)
        {
            summary.MinActiveWorkerThreads = 0;
        }

        return summary;
    }

    private static bool IsWorkerCountEvent(int eventId)
    {
        return eventId == ClrThreadingEventIds.ThreadPoolWorkerThreadStart
            || eventId == ClrThreadingEventIds.ThreadPoolWorkerThreadStop
            || eventId == ClrThreadingEventIds.ThreadPoolWorkerThreadWait;
    }

    private static bool IsThreadingEvent(int eventId)
    {
        return IsWorkerCountEvent(eventId)
            || eventId == ClrThreadingEventIds.ThreadPoolWorkerThreadAdjustmentAdjustment
            || eventId == ClrThreadingEventIds.ThreadPoolWorkerThreadAdjustmentSample
            || eventId == ClrThreadingEventIds.ThreadCreating
            || eventId == ClrContentionEventIds.LockCreated;
    }

    private static int BucketIndexFor(double relativeMSec, double minRelativeMSec, double bucketDurationMSec, int bucketCount)
    {
        if (bucketDurationMSec <= 0)
        {
            return 0;
        }

        int bucketIndex = (int)((relativeMSec - minRelativeMSec) / bucketDurationMSec);

        if (bucketIndex < 0)
        {
            return 0;
        }

        if (bucketIndex >= bucketCount)
        {
            return bucketCount - 1;
        }

        return bucketIndex;
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.Threading)
