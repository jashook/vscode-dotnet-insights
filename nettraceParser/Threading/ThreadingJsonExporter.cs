////////////////////////////////////////////////////////////////////////////////
// Module: ThreadingJsonExporter.cs
//
// Notes:
// Writes the "threadingSummary" block the Threading view consumes.
//
// The interesting part is WriteStallCorrelation. The thread-pool events cannot
// say WHERE a pool thread was stuck - 0 of the 12.2M WorkerThread/Wait events
// on the reference capture carry a stack. But the CPU sample profiler records
// (timestamp, threadId, stack) continuously, so the two can be joined on time:
// for every adjustment the runtime made because work was NOT progressing
// (Starvation / CooperativeBlocking), take the samples around that instant and
// ask what threads were actually doing. That turns "the pool grew" into "the
// pool grew while N threads sat in Interop+Sys.Read".
//
// One correction the real data forced: the dominant leaf in every such window
// is LowLevelLifoSemaphore.WaitForSignal (2,397 of 3,256 samples in the first
// one examined). That is a pool worker PARKED waiting for work - the normal
// idle state, and the opposite of the thing being looked for. Counting it
// would bury the real culprits under it, so parked-worker frames are excluded
// and reported separately as an idle count.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.NetTrace.Threading {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text.Json;

using DotnetInsights.NetTrace.Cpu;
using DotnetInsights.NetTrace.Rundown;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public static class ThreadingJsonExporter
{
    // Half-width of the window taken around each stall-driven adjustment.
    // 25ms is wide enough to catch a meaningful number of ~100Hz-per-thread
    // samples (measured: ~3,300 samples across ~90 threads on the reference
    // capture) while staying narrow enough that the answer is about THAT
    // moment rather than the whole capture.
    private const double StallWindowHalfWidthMSec = 25.0;

    private const int MaxStallFrames = 40;
    private const int MaxStallEvents = 100;
    private const int MaxAdjustmentsEmitted = 500;
    private const int MaxStackFramesPerEvent = 40;

    // A parked pool worker waiting to be handed work. Its presence in a stall
    // window means the pool had spare capacity, not that anything was blocked.
    private static readonly string[] ParkedWorkerFramePrefixes = new string[]
    {
        "System.Threading.LowLevelLifoSemaphore.",
        "System.Threading.PortableThreadPool+WorkerThread.WorkerThreadStart"
    };

    public static void Write(Utf8JsonWriter writer, ThreadingSummary summary, List<SampleEvent> sampleEvents, MethodSymbolTable symbolTable, List<string> methodNames, Dictionary<string, int> methodNameIndexByName)
    {
        writer.WriteStartObject();

        writer.WriteBoolean("hasThreadPoolData", summary.HasThreadPoolData);

        if (!summary.HasThreadPoolData)
        {
            writer.WriteEndObject();
            return;
        }

        writer.WriteNumber("peakActiveWorkerThreads", summary.PeakActiveWorkerThreads);
        writer.WriteNumber("minActiveWorkerThreads", summary.MinActiveWorkerThreads);
        writer.WriteNumber("finalActiveWorkerThreads", summary.FinalActiveWorkerThreads);
        writer.WriteNumber("peakRetiredWorkerThreads", summary.PeakRetiredWorkerThreads);
        writer.WriteNumber("workerThreadStartCount", summary.WorkerThreadStartCount);
        writer.WriteNumber("workerThreadStopCount", summary.WorkerThreadStopCount);
        writer.WriteNumber("workerThreadWaitCount", summary.WorkerThreadWaitCount);
        writer.WriteNumber("threadCreationCount", summary.ThreadCreations.Count);
        writer.WriteNumber("lockCreationCount", summary.LockCreations.Count);
        writer.WriteNumber("adjustmentCount", summary.Adjustments.Count);

        WriteTimeline(writer, summary);
        WriteAdjustmentReasons(writer, summary);
        WriteAdjustments(writer, summary);
        WriteStallCorrelation(writer, summary, sampleEvents, symbolTable, methodNames, methodNameIndexByName);
        WriteStackedEvents(writer, "threadCreations", summary.ThreadCreations, symbolTable, methodNames, methodNameIndexByName);
        WriteStackedEvents(writer, "lockCreations", summary.LockCreations, symbolTable, methodNames, methodNameIndexByName);

        writer.WriteEndObject();
    }

    private static void WriteTimeline(Utf8JsonWriter writer, ThreadingSummary summary)
    {
        writer.WritePropertyName("timeline");
        writer.WriteStartObject();
        writer.WriteNumber("minRelativeMSec", summary.MinRelativeMSec);
        writer.WriteNumber("totalDurationMSec", summary.MaxRelativeMSec - summary.MinRelativeMSec);
        writer.WriteNumber("bucketDurationMSec", summary.BucketDurationMSec);
        writer.WriteNumber("bucketCount", summary.BucketCount);

        WriteIntArray(writer, "minActiveByBucket", summary.MinActiveByBucket);
        WriteIntArray(writer, "maxActiveByBucket", summary.MaxActiveByBucket);
        WriteDoubleArray(writer, "averageActiveByBucket", summary.AverageActiveByBucket);
        WriteDoubleArray(writer, "throughputByBucket", summary.ThroughputByBucket);

        writer.WriteEndObject();
    }

    private static void WriteAdjustmentReasons(Utf8JsonWriter writer, ThreadingSummary summary)
    {
        Dictionary<int, int> countByReason = new Dictionary<int, int>();

        for (int adjustmentIndex = 0; adjustmentIndex < summary.Adjustments.Count; ++adjustmentIndex)
        {
            int reason = summary.Adjustments[adjustmentIndex].Reason;
            countByReason.TryGetValue(reason, out int count);
            countByReason[reason] = count + 1;
        }

        List<KeyValuePair<int, int>> reasons = new List<KeyValuePair<int, int>>(countByReason);
        reasons.Sort((KeyValuePair<int, int> left, KeyValuePair<int, int> right) => right.Value.CompareTo(left.Value));

        writer.WritePropertyName("adjustmentReasons");
        writer.WriteStartArray();

        for (int reasonIndex = 0; reasonIndex < reasons.Count; ++reasonIndex)
        {
            writer.WriteStartObject();
            writer.WriteNumber("reason", reasons[reasonIndex].Key);
            writer.WriteString("reasonName", ThreadAdjustmentReason.NameFor(reasons[reasonIndex].Key));
            writer.WriteNumber("count", reasons[reasonIndex].Value);
            // Flagged rather than left for the UI to re-derive: these are the
            // adjustments made because work stopped progressing, not because
            // more work arrived.
            writer.WriteBoolean("isStallDriven", ThreadAdjustmentReason.IsStallDriven(reasons[reasonIndex].Key));
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteAdjustments(Utf8JsonWriter writer, ThreadingSummary summary)
    {
        int emitted = summary.Adjustments.Count < MaxAdjustmentsEmitted ? summary.Adjustments.Count : MaxAdjustmentsEmitted;

        writer.WritePropertyName("adjustments");
        writer.WriteStartArray();

        for (int adjustmentIndex = 0; adjustmentIndex < emitted; ++adjustmentIndex)
        {
            ThreadPoolAdjustmentRecord adjustment = summary.Adjustments[adjustmentIndex];

            writer.WriteStartObject();
            writer.WriteNumber("relativeMSec", adjustment.RelativeMSec);
            writer.WriteNumber("newWorkerThreadCount", adjustment.NewWorkerThreadCount);
            writer.WriteNumber("reason", adjustment.Reason);
            writer.WriteString("reasonName", ThreadAdjustmentReason.NameFor(adjustment.Reason));
            writer.WriteBoolean("isStallDriven", ThreadAdjustmentReason.IsStallDriven(adjustment.Reason));
            writer.WriteNumber("averageThroughput", adjustment.AverageThroughput);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    // Joins CPU samples to stall-driven adjustments on time - see this file's
    // header for why this exists at all.
    private static void WriteStallCorrelation(Utf8JsonWriter writer, ThreadingSummary summary, List<SampleEvent> sampleEvents, MethodSymbolTable symbolTable, List<string> methodNames, Dictionary<string, int> methodNameIndexByName)
    {
        List<ThreadPoolAdjustmentRecord> stallAdjustments = new List<ThreadPoolAdjustmentRecord>();

        for (int adjustmentIndex = 0; adjustmentIndex < summary.Adjustments.Count; ++adjustmentIndex)
        {
            if (ThreadAdjustmentReason.IsStallDriven(summary.Adjustments[adjustmentIndex].Reason))
            {
                stallAdjustments.Add(summary.Adjustments[adjustmentIndex]);
            }
        }

        writer.WritePropertyName("stallCorrelation");

        if (stallAdjustments.Count == 0 || sampleEvents.Count == 0)
        {
            // Null, not an empty object: "no stalls happened" and "there were
            // no CPU samples to explain them with" are both real answers, and
            // an empty table would imply the pool stalled with nothing running.
            writer.WriteNullValue();
            return;
        }

        stallAdjustments.Sort((ThreadPoolAdjustmentRecord left, ThreadPoolAdjustmentRecord right) => left.RelativeMSec.CompareTo(right.RelativeMSec));

        int windowCount = stallAdjustments.Count < MaxStallEvents ? stallAdjustments.Count : MaxStallEvents;

        Dictionary<int, int> samplesByFrameId = new Dictionary<int, int>();
        HashSet<long> threadsInWindows = new HashSet<long>();
        long totalSamplesInWindows = 0;
        long parkedWorkerSamples = 0;

        // Single ordered pass over the samples rather than one scan per
        // adjustment: 54 adjustments x 19.7M samples would be a billion
        // comparisons. Samples are already in time order (they are projected
        // in event order), so a cursor over the sorted windows is enough.
        Span<SampleEvent> samplesSpan = CollectionsMarshal.AsSpan(sampleEvents);
        int windowCursor = 0;

        for (int sampleIndex = 0; sampleIndex < samplesSpan.Length; ++sampleIndex)
        {
            ref readonly SampleEvent sampleEvent = ref samplesSpan[sampleIndex];
            double sampleTime = sampleEvent.RelativeMSec;

            // Retire windows this sample has already passed.
            while (windowCursor < windowCount && stallAdjustments[windowCursor].RelativeMSec + StallWindowHalfWidthMSec < sampleTime)
            {
                ++windowCursor;
            }

            if (windowCursor >= windowCount)
            {
                break;
            }

            // Windows can overlap (two adjustments 100ms apart share samples),
            // so membership is checked against any window still in range, not
            // just the cursor's own.
            bool inAnyWindow = false;

            for (int windowIndex = windowCursor; windowIndex < windowCount; ++windowIndex)
            {
                double windowStart = stallAdjustments[windowIndex].RelativeMSec - StallWindowHalfWidthMSec;

                if (windowStart > sampleTime)
                {
                    break;
                }

                if (sampleTime <= stallAdjustments[windowIndex].RelativeMSec + StallWindowHalfWidthMSec)
                {
                    inAnyWindow = true;
                    break;
                }
            }

            if (!inAnyWindow)
            {
                continue;
            }

            ++totalSamplesInWindows;
            threadsInWindows.Add(sampleEvent.ThreadId);

            if (sampleEvent.Stack.Length == 0)
            {
                continue;
            }

            int leafFrameId = symbolTable.ResolveId(sampleEvent.Stack[0], sampleTime);
            string leafName = symbolTable.NameForId(leafFrameId);

            if (IsParkedWorkerFrame(leafName))
            {
                ++parkedWorkerSamples;
                continue;
            }

            samplesByFrameId.TryGetValue(leafFrameId, out int frameCount);
            samplesByFrameId[leafFrameId] = frameCount + 1;
        }

        List<KeyValuePair<int, int>> rankedFrames = new List<KeyValuePair<int, int>>(samplesByFrameId);
        rankedFrames.Sort((KeyValuePair<int, int> left, KeyValuePair<int, int> right) => right.Value.CompareTo(left.Value));

        int frameCountToEmit = rankedFrames.Count < MaxStallFrames ? rankedFrames.Count : MaxStallFrames;

        writer.WriteStartObject();
        writer.WriteNumber("stallAdjustmentCount", stallAdjustments.Count);
        writer.WriteNumber("windowHalfWidthMSec", StallWindowHalfWidthMSec);
        writer.WriteNumber("samplesInWindows", totalSamplesInWindows);
        writer.WriteNumber("threadsInWindows", threadsInWindows.Count);
        // Reported rather than silently dropped: a window that is mostly
        // parked workers means the pool had spare capacity, which is itself
        // worth knowing when reading the frames below.
        writer.WriteNumber("parkedWorkerSamples", parkedWorkerSamples);

        writer.WritePropertyName("frames");
        writer.WriteStartArray();

        for (int frameIndex = 0; frameIndex < frameCountToEmit; ++frameIndex)
        {
            string frameName = symbolTable.NameForId(rankedFrames[frameIndex].Key);

            writer.WriteStartObject();
            writer.WriteNumber("frame", InternMethodName(frameName, methodNames, methodNameIndexByName));
            writer.WriteNumber("sampleCount", rankedFrames[frameIndex].Value);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static bool IsParkedWorkerFrame(string frameName)
    {
        if (string.IsNullOrEmpty(frameName))
        {
            return false;
        }

        for (int prefixIndex = 0; prefixIndex < ParkedWorkerFramePrefixes.Length; ++prefixIndex)
        {
            if (frameName.StartsWith(ParkedWorkerFramePrefixes[prefixIndex], StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static void WriteStackedEvents(Utf8JsonWriter writer, string propertyName, List<StackedThreadingEvent> stackedEvents, MethodSymbolTable symbolTable, List<string> methodNames, Dictionary<string, int> methodNameIndexByName)
    {
        writer.WritePropertyName(propertyName);
        writer.WriteStartArray();

        for (int eventIndex = 0; eventIndex < stackedEvents.Count; ++eventIndex)
        {
            StackedThreadingEvent stackedEvent = stackedEvents[eventIndex];

            writer.WriteStartObject();
            writer.WriteNumber("relativeMSec", stackedEvent.RelativeMSec);
            writer.WriteNumber("threadId", stackedEvent.ThreadId);
            writer.WriteString("objectId", "0x" + stackedEvent.ObjectId.ToString("X"));

            writer.WritePropertyName("frames");
            writer.WriteStartArray();

            int frameCount = stackedEvent.Stack.Length < MaxStackFramesPerEvent ? stackedEvent.Stack.Length : MaxStackFramesPerEvent;

            for (int frameIndex = 0; frameIndex < frameCount; ++frameIndex)
            {
                string frameName = symbolTable.NameForId(symbolTable.ResolveId(stackedEvent.Stack[frameIndex], stackedEvent.RelativeMSec));
                writer.WriteNumberValue(InternMethodName(frameName, methodNames, methodNameIndexByName));
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static int InternMethodName(string frameName, List<string> methodNames, Dictionary<string, int> methodNameIndexByName)
    {
        string safeName = frameName ?? "<unresolved>";
        int frameNameIndex;

        if (!methodNameIndexByName.TryGetValue(safeName, out frameNameIndex))
        {
            frameNameIndex = methodNames.Count;
            methodNames.Add(safeName);
            methodNameIndexByName[safeName] = frameNameIndex;
        }

        return frameNameIndex;
    }

    private static void WriteIntArray(Utf8JsonWriter writer, string propertyName, int[] values)
    {
        writer.WritePropertyName(propertyName);
        writer.WriteStartArray();

        for (int valueIndex = 0; valueIndex < values.Length; ++valueIndex)
        {
            writer.WriteNumberValue(values[valueIndex]);
        }

        writer.WriteEndArray();
    }

    private static void WriteDoubleArray(Utf8JsonWriter writer, string propertyName, double[] values)
    {
        writer.WritePropertyName(propertyName);
        writer.WriteStartArray();

        for (int valueIndex = 0; valueIndex < values.Length; ++valueIndex)
        {
            writer.WriteNumberValue(values[valueIndex]);
        }

        writer.WriteEndArray();
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.Threading)
