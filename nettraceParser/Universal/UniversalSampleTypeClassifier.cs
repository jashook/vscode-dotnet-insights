////////////////////////////////////////////////////////////////////////////////
// Module: UniversalSampleTypeClassifier.cs
//
// Notes:
// Derives each CPU sample's ThreadSampleType (Managed vs External) for a v6
// `dotnet-trace collect-linux` capture, which carries no such field.
//
// WHY THIS IS NEEDED: the whole Threading view's parked/blocked
// classification is built on ThreadSampleType - see
// Threading/ThreadActivityProfiler.cs, whose header records that it is "the
// load-bearing signal" and that no list of library method names can substitute
// for it. On a v5 capture the CLR's own sampler supplies it in the event
// payload. A v6 capture's samples come from perf_events instead: the kernel
// samples an instruction pointer and knows nothing about managed code, so the
// field simply does not exist on this path.
//
// WHAT REPLACES IT: the same question, asked of the symbol table. External
// means "the thread was NOT executing managed code" - in a P/Invoke, in the
// runtime, or blocked in a syscall. A collect-linux capture describes its own
// address space well enough to answer that directly: the runtime publishes
// JIT'd managed methods as ProcessSymbol entries in the CLR's perf-map form,
// and everything else in a sampled stack resolves to a native symbol, a
// kernel symbol, or a native module. So a sample whose LEAF frame lands in
// managed code is Managed, and one whose leaf lands anywhere else is
// External.
//
// This is a leaf-frame test, which that same ThreadActivityProfiler header
// warns about at length - but the thing it warns against is inferring
// blocked-ness from leaf METHOD NAMES ("no list of library method names can
// ever keep up with this"), which is a guess about what a method does. This
// is a different question with a factual answer: whether the sampled
// instruction pointer was inside managed code at all. That is what
// ThreadSampleType encodes, and reading it off a real symbol table is if
// anything more direct than trusting a flag, since perf sampled the actual
// address.
//
// It is still a DERIVATION and is labelled as one all the way out to the
// webview (ThreadingSummary.SampleTypeSource), so the Threading view can say
// where its classification came from rather than presenting derived data as
// the runtime's own. It has NOT yet been validated against a v5 capture of
// the same process, which is the check that would let the parked/blocked
// tables be trusted as fully as they are on the v5 path.
//
// COST: memoized per STACK, not per sample. Stacks are deduplicated by
// content at decode time (see StackTable), so the reference capture's 1.09M
// samples share 936,389 stacks and far fewer distinct leaf addresses; the
// symbol table's own per-address cache absorbs the rest.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.NetTrace.Universal {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;

using DotnetInsights.NetTrace.Cpu;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public static class UniversalSampleTypeClassifier
{
    // Marks a stack whose answer has not been computed yet. ThreadSampleType's
    // own values start at 0 (Unknown), which is a real result here - a stack
    // with no frames, or a leaf in no known mapping - so the "not yet done"
    // state needs a value of its own.
    private const byte NotComputed = 255;

    public struct ClassificationResult
    {
        public int ManagedSampleCount;
        public int ExternalSampleCount;
        public int UnknownSampleCount;

        public bool HasAnyClassified => this.ManagedSampleCount > 0 || this.ExternalSampleCount > 0;
    }

    // Rewrites every sample's SampleType in place. A no-op for samples that
    // already carry a runtime-supplied type (a v5 capture never reaches here,
    // but this stays true if a future capture mixes both).
    public static ClassificationResult Apply(List<SampleEvent> samples, StackTable stacks, UniversalSymbolTable symbols)
    {
        ClassificationResult result = new ClassificationResult();

        if (samples == null || stacks == null || symbols == null || symbols.IsEmpty)
        {
            return result;
        }

        byte[] typeByStackIndex = new byte[stacks.Count];

        for (int stackIndex = 0; stackIndex < typeByStackIndex.Length; ++stackIndex)
        {
            typeByStackIndex[stackIndex] = NotComputed;
        }

        for (int sampleIndex = 0; sampleIndex < samples.Count; ++sampleIndex)
        {
            SampleEvent sample = samples[sampleIndex];

            if (sample.SampleType != ThreadSampleType.Unknown)
            {
                continue;
            }

            ThreadSampleType sampleType = ClassifyStack(sample.StackIndex, stacks, symbols, typeByStackIndex);

            if (sampleType == ThreadSampleType.Managed)
            {
                ++result.ManagedSampleCount;
            }
            else if (sampleType == ThreadSampleType.External)
            {
                ++result.ExternalSampleCount;
            }
            else
            {
                ++result.UnknownSampleCount;
                continue;
            }

            samples[sampleIndex] = new SampleEvent(sample.RelativeMSec, sample.ThreadId, sample.StackIndex, sampleType);
        }

        return result;
    }

    private static ThreadSampleType ClassifyStack(int stackIndex, StackTable stacks, UniversalSymbolTable symbols, byte[] typeByStackIndex)
    {
        if (stackIndex < 0 || stackIndex >= typeByStackIndex.Length)
        {
            return ThreadSampleType.Unknown;
        }

        byte memoized = typeByStackIndex[stackIndex];

        if (memoized != NotComputed)
        {
            return (ThreadSampleType)memoized;
        }

        ThreadSampleType computed = ThreadSampleType.Unknown;

        long[] frames = stacks.FramesAt(stackIndex);

        if (frames.Length > 0)
        {
            // Index 0 is the innermost/currently-executing frame - the same
            // leaf-first order every stack in this codebase carries.
            bool isManagedCode;

            if (symbols.TryClassifyManaged(frames[0], out isManagedCode))
            {
                computed = isManagedCode ? ThreadSampleType.Managed : ThreadSampleType.External;
            }
        }

        typeByStackIndex[stackIndex] = (byte)computed;
        return computed;
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.Universal)

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////
