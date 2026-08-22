////////////////////////////////////////////////////////////////////////////////
// Module: SampleProfileEventProjector.cs
//
// Notes:
// Sibling projector to Gc/AllocationEventProjector.cs, against the same
// EventRecord stream - filters for the Microsoft-DotNETCore-SampleProfiler
// provider's one real event (EventId 0, no EventName - self-describing
// metadata with zero declared fields, the same "manifest-based, empty
// EventName" shape the CLR runtime provider has - see EventRecord.cs's own
// header comment). Confirmed against a real capture
// (testApps/CpuLoadGenerator/example-cpu-sample.nettrace, captured via
// `dotnet-trace collect -p <pid>` with no --profile override - its default
// profile set includes Microsoft-DotNETCore-SampleProfiler): every decoded
// event on this provider was EventId=0, Version=0, with a non-empty
// stack already resolved (see EventBlock.cs/StackBlock.cs -
// stack resolution isn't provider-specific, so this "just worked" with no
// changes to that layer). Fires at a fixed sampling interval on EVERY managed
// thread, not only the running ones - a thread blocked in a syscall is sampled
// just the same, which is what makes a thread's sample count a proxy for its
// wall-clock time rather than for its CPU.
//
// The event's payload is a single int32, the runtime's own ThreadSampleType
// (see that enum below). It was skipped here originally - the CPU view needs
// only stack/thread/timestamp - and is decoded now because it is the one thing
// in the capture that distinguishes a thread executing managed code from one
// parked in a native call, which the stack alone cannot do.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.NetTrace.Cpu {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Runtime.InteropServices;

using DotnetInsights.NetTrace.Progress;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

// The runtime's own answer to "what was this thread executing when the
// sampler caught it", read from the event's 4-byte payload. Values match
// the CLR's SampleProfilerSampleType (ep-sample-profiler.c) and TraceEvent's
// ThreadSampleType.
//
// External is the interesting one and its name undersells it: it means the
// thread was NOT executing managed code - in a P/Invoke, in the runtime, or
// blocked in a syscall. A thread parked forever in a native poll
// (Grpc.Core.Internal.GrpcThreadPool.RunHandlerLoop,
// Confluent.Kafka.Consumer.Consume, epoll) reports External on every single
// sample while its managed leaf frame still looks like ordinary running code.
// That is the only signal in the capture that can tell those apart WITHOUT a
// per-library list of blocking methods, which is why it is plumbed through
// (see Threading/ThreadActivityProfiler.cs, the one consumer).
//
// External is emphatically NOT "blocked" on its own - 92.6% of all samples on
// a real 836MB service capture are External, because any thread doing socket
// or file work spends most of its time in native code. It only means
// something combined with the thread's other whole-capture aggregates.
public enum ThreadSampleType : byte
{
    // The capture's samples carry no type at all (payload shorter than the
    // 4 bytes this field occupies). Deliberately distinct from Managed rather
    // than folded into it: a consumer that treats "unknown" as "was running"
    // degrades to making no claim, which is what should happen.
    Unknown = 0,
    Error = 1,
    External = 2,
    Managed = 3
}

// A readonly struct, not a class - mirrors AllocationEvent's own reasoning
// (see that type's header comment): SampleProfileEventProjector.Project can
// construct many thousands of these for a real capture, all held in one
// List<SampleEvent> for the rest of the program's life.
public readonly struct SampleEvent
{
    public readonly double RelativeMSec;
    public readonly long ThreadId;
    // Index into the capture's StackTable (see StackTable.cs), copied from
    // the owning EventRecord.StackIndex - resolved at parse time, see
    // EventBlock.cs. Resolve it with StackTable.FramesAt, which returns
    // frames leaf-first (index 0 is the innermost/currently-executing frame),
    // the same order every stack in this codebase carries.
    // StackTable.EmptyStackIndex on the rare sample that couldn't be
    // stack-walked - that index resolves to an empty array, never null.
    public readonly int StackIndex;
    // Free, size-wise: the three fields above are 8+8+4 = 20 bytes in a
    // struct aligned to 8, so this lands in padding that was already being
    // paid for. Worth checking before adding a fifth - a real capture holds
    // 16.24M of these.
    public readonly ThreadSampleType SampleType;

    public SampleEvent(double relativeMSec, long threadId, int stackIndex, ThreadSampleType sampleType = ThreadSampleType.Unknown)
    {
        this.RelativeMSec = relativeMSec;
        this.ThreadId = threadId;
        this.StackIndex = stackIndex;
        this.SampleType = sampleType;
    }
}

public static class SampleProfileEventProjector
{
    private const string SampleProfilerProviderName = "Microsoft-DotNETCore-SampleProfiler";
    private const int ThreadSampleEventId = 0;

    // The v6/collect-linux equivalent. A `dotnet-trace collect-linux` capture
    // has no Microsoft-DotNETCore-SampleProfiler events at all - its CPU
    // samples come from perf_events and arrive as Universal.Events/cpu
    // (UniversalProviders.md: "cpu - Represents a CPU sample"), one event per
    // sample with the sample's weight as its payload and the same
    // already-resolved stack every other event carries.
    //
    // Matched by NAME rather than event id on purpose - UniversalProviders.md
    // guarantees stable names and explicitly does NOT guarantee ids ("There
    // are no stable event IDs, but there will be a set of stable names"). The
    // reference capture assigns it id 2; another capture need not.
    private const string UniversalEventsProviderName = "Universal.Events";
    private const string UniversalCpuEventName = "cpu";

    // Same (events, qpcFrequency, referenceQpc) shape as
    // GcEventProjector.Project/AllocationEventProjector.Project - see those
    // for why referenceQpc (NettraceHeader.SyncTimeQPC) is the correct
    // anchor. No pointerSize/referenceUtc parameters here (unlike those two)
    // - a sample carries no pointer-sized payload fields to decode, and
    // every consumer of SampleEvent only needs capture-relative time, not an
    // absolute DateTime.
    // The provider/event id this projector filters for, exposed so a caller
    // holding an EventOverview can ask it for this capture's exact sample
    // count and pass it as expectedSampleCount below.
    public static string ProviderName => SampleProfilerProviderName;
    public static int EventId => ThreadSampleEventId;

    // The v6 counterpart. There is no matching EventId property on purpose:
    // the id is assigned per capture, so it has to come from the capture's own
    // metadata (NettraceFile.V6UniversalCpuEventId) rather than from a
    // constant here.
    public static string UniversalProviderName => UniversalEventsProviderName;
    public static string UniversalCpuName => UniversalCpuEventName;

    // expectedSampleCount presizes the result list. A real capture can hold
    // 16.24M samples (3.23GB assets-registry capture), and growing there from
    // empty measured 33% of this whole phase - every doubling copies every
    // SampleEvent already added. 0 (the default) just means "unknown", and
    // behaves exactly as before.
    public static List<SampleEvent> Project(List<EventRecord> events, long qpcFrequency, long referenceQpc, Action<double> onProgress = null, int expectedSampleCount = 0)
    {
        List<SampleEvent> result = expectedSampleCount > 0 ? new List<SampleEvent>(expectedSampleCount) : new List<SampleEvent>();

        // EventRecord is a struct (~70 bytes) - events is the whole
        // capture's event list, so this is iterated as a Span over the
        // List<T>'s backing array rather than a plain `foreach`, matching
        // GcEventProjector.Project/AllocationEventProjector.Project's own
        // reasoning.
        Span<EventRecord> eventsSpan = CollectionsMarshal.AsSpan(events);
        for (int eventIndex = 0; eventIndex < eventsSpan.Length; ++eventIndex)
        {
            if (onProgress != null && (eventIndex & ProgressReporter.IndexProgressMask) == 0)
            {
                onProgress((double)eventIndex / eventsSpan.Length);
            }

            ref readonly EventRecord record = ref eventsSpan[eventIndex];

            bool isClrSample = record.ProviderName == SampleProfilerProviderName && record.EventId == ThreadSampleEventId;
            bool isUniversalSample = !isClrSample &&
                record.ProviderName == UniversalEventsProviderName &&
                record.EventName == UniversalCpuEventName;

            if (!isClrSample && !isUniversalSample)
            {
                continue;
            }

            double relativeMSec = default;
            if (qpcFrequency > 0)
            {
                long qpcDelta = record.TimeStampRelativeQPC - referenceQpc;
                relativeMSec = qpcDelta * 1000.0 / qpcFrequency;
            }

            // A Universal cpu sample carries a weight, not a ThreadSampleType
            // - the CLR's Managed/External answer does not exist on this path
            // at all, because the sampler is the kernel rather than the
            // runtime. Reported as Unknown here and derived later from whether
            // the sample's leaf frame resolves to managed or native code (see
            // Universal/UniversalSampleTypeClassifier.cs); deliberately NOT
            // guessed at here, since ThreadSampleType is load-bearing for the
            // Threading view's parked/blocked classification.
            ThreadSampleType sampleType = isClrSample ? DecodeSampleType(record) : ThreadSampleType.Unknown;

            result.Add(new SampleEvent(relativeMSec, record.ThreadId, record.StackIndex, sampleType));
        }

        return result;
    }

    // The event's whole payload is one int32 - confirmed against real
    // captures, where every SampleProfiler event has PayloadLength exactly 4
    // and only the values 1 (External) and 2 (Managed) ever appear. The
    // header comment on this projector used to say the event carried nothing
    // worth decoding, which was true only for the CPU view's needs.
    //
    // The CLR's own enum is 1-based with Error=0/External=1/Managed=2, so the
    // wire values are shifted by one into ThreadSampleType, whose 0 is
    // reserved for "the capture did not carry this at all". A payload too
    // short to hold the field (no real capture seen so far, but nothing in
    // the format prevents an older or trimmed producer) reports Unknown
    // rather than being guessed at.
    private static ThreadSampleType DecodeSampleType(in EventRecord record)
    {
        if (record.PayloadLength < sizeof(int) || record.PayloadBuffer == null)
        {
            return ThreadSampleType.Unknown;
        }

        int rawSampleType = BinaryPrimitives.ReadInt32LittleEndian(record.PayloadBuffer.AsSpan(record.PayloadOffset, sizeof(int)));

        switch (rawSampleType)
        {
            case 0:
                return ThreadSampleType.Error;

            case 1:
                return ThreadSampleType.External;

            case 2:
                return ThreadSampleType.Managed;

            default:
                return ThreadSampleType.Unknown;
        }
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.Cpu)

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////
