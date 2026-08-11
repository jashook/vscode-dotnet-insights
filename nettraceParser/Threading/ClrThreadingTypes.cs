////////////////////////////////////////////////////////////////////////////////
// Module: ClrThreadingTypes.cs
//
// Notes:
// EventIds and payload decoders for the CLR provider's thread-pool events -
// hardcoded from TraceEvent's own ClrTraceEventParser.cs, same convention as
// Gc/ClrGcTypes.cs and Contention/ClrContentionTypes.cs (read as reference,
// not taken as a dependency). These events are manifest-based and carry no
// self-describing metadata, so offsets cannot be discovered at runtime.
//
// Every layout below was confirmed by hand-decoding raw payload bytes from a
// real capture rather than trusting the manifest alone:
//   id 50/51/57 (WorkerThreadStart/Stop/Wait) len=10: ActiveWorkerThreadCount
//       (UInt32 @0), RetiredWorkerThreadCount (UInt32 @4), ClrInstanceID @8.
//       A first Wait event decoded ActiveWorkerThreadCount = 0x40 = 64, which
//       matches the pool size the same capture's samples show.
//   id 54 (AdjustmentSample) len=10: Throughput (Double @0), ClrInstanceID @8.
//   id 55 (AdjustmentAdjustment) len=18: AverageThroughput (Double @0),
//       NewWorkerThreadCount (UInt32 @8), Reason (UInt32 @12), ClrInstanceID
//       @16 - 8+4+4+2 = 18, exactly the observed length.
//   id 70/71 (ThreadCreating/ThreadRunning) len=10: ID (pointer @0),
//       ClrInstanceID.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.NetTrace.Threading {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using DotnetInsights.NetTrace.Gc;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public static class ClrThreadingEventIds
{
    public const int ThreadPoolWorkerThreadStart = 50;
    public const int ThreadPoolWorkerThreadStop = 51;
    public const int ThreadPoolWorkerThreadAdjustmentSample = 54;
    public const int ThreadPoolWorkerThreadAdjustmentAdjustment = 55;
    public const int ThreadPoolWorkerThreadAdjustmentStats = 56;
    public const int ThreadPoolWorkerThreadWait = 57;
    public const int ThreadCreating = 70;
    public const int ThreadRunning = 71;
}

// Why the runtime's hill-climbing algorithm changed the worker count.
// Numeric values are the manifest's, not this enum's declaration order.
//
// Starvation is the one that matters diagnostically: it means the pool saw
// queued work making no progress and injected a thread to break the stall.
// A capture full of Starvation adjustments is the signature of blocking calls
// occupying pool threads - which is exactly what the Contention view's
// "worker threads blocked" ranking is for, from the other direction.
public static class ThreadAdjustmentReason
{
    public const int Warmup = 0;
    public const int Initializing = 1;
    public const int RandomMove = 2;
    public const int ClimbingMove = 3;
    public const int ChangePoint = 4;
    public const int Stabilizing = 5;
    public const int Starvation = 6;
    public const int ThreadTimedOut = 7;
    public const int CooperativeBlocking = 8;

    public static string NameFor(int reason)
    {
        switch (reason)
        {
            case Warmup: return "Warmup";
            case Initializing: return "Initializing";
            case RandomMove: return "Random move";
            case ClimbingMove: return "Climbing move";
            case ChangePoint: return "Change point";
            case Stabilizing: return "Stabilizing";
            case Starvation: return "Starvation";
            case ThreadTimedOut: return "Thread timed out";
            case CooperativeBlocking: return "Cooperative blocking";
            default: return "Reason " + reason;
        }
    }

    // Starvation and CooperativeBlocking both mean the pool had to add
    // threads because existing ones were stuck rather than because more work
    // arrived - the distinction the Threading view highlights.
    public static bool IsStallDriven(int reason)
    {
        return reason == Starvation || reason == CooperativeBlocking;
    }
}

// Shared by WorkerThreadStart/Stop/Wait - all three carry the same counts,
// which is what makes the (very frequent) Wait event a dense sampling of the
// live pool size.
public readonly struct ClrThreadPoolWorkerThread
{
    public readonly int ActiveWorkerThreadCount;
    public readonly int RetiredWorkerThreadCount;

    private ClrThreadPoolWorkerThread(int activeWorkerThreadCount, int retiredWorkerThreadCount)
    {
        this.ActiveWorkerThreadCount = activeWorkerThreadCount;
        this.RetiredWorkerThreadCount = retiredWorkerThreadCount;
    }

    public static ClrThreadPoolWorkerThread Decode(PayloadReader reader)
    {
        if (reader.Length < 8)
        {
            return new ClrThreadPoolWorkerThread(0, 0);
        }

        return new ClrThreadPoolWorkerThread(reader.GetInt32At(0), reader.GetInt32At(4));
    }
}

public readonly struct ClrThreadPoolAdjustment
{
    public readonly double AverageThroughput;
    public readonly int NewWorkerThreadCount;
    public readonly int Reason;

    private ClrThreadPoolAdjustment(double averageThroughput, int newWorkerThreadCount, int reason)
    {
        this.AverageThroughput = averageThroughput;
        this.NewWorkerThreadCount = newWorkerThreadCount;
        this.Reason = reason;
    }

    public static ClrThreadPoolAdjustment Decode(PayloadReader reader)
    {
        if (reader.Length < 16)
        {
            return new ClrThreadPoolAdjustment(0, 0, -1);
        }

        double averageThroughput = System.BitConverter.Int64BitsToDouble(reader.GetInt64At(0));
        return new ClrThreadPoolAdjustment(averageThroughput, reader.GetInt32At(8), reader.GetInt32At(12));
    }
}

public readonly struct ClrThreadPoolAdjustmentSample
{
    public readonly double Throughput;

    private ClrThreadPoolAdjustmentSample(double throughput)
    {
        this.Throughput = throughput;
    }

    public static ClrThreadPoolAdjustmentSample Decode(PayloadReader reader)
    {
        if (reader.Length < 8)
        {
            return new ClrThreadPoolAdjustmentSample(0);
        }

        return new ClrThreadPoolAdjustmentSample(System.BitConverter.Int64BitsToDouble(reader.GetInt64At(0)));
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.Threading)
