////////////////////////////////////////////////////////////////////////////////
// Module: CaptureProfile.cs
//
// Notes:
// The compact reduction of one capture that diffing actually needs - a few
// thousand named counters instead of the millions of events they came from.
//
// This exists because of a memory constraint, not for tidiness. A real
// capture's own --json output is already ~53MB and its parse peaks around
// 1.8GB RSS, so two captures cannot be held (or shipped to a webview) at
// once. Program.cs's --diff mode therefore parses each capture in turn,
// reduces it to one of these, and drops the whole event graph before opening
// the next file - see that file's own `file = null` discipline, which this
// depends on to keep peak memory at roughly ONE capture rather than two.
//
// Everything here is keyed by NAME rather than by any per-process identity.
// Names are the only thing two separate runs of a process share: object
// addresses, method ids, thread ids and lock pointers are all meaningless
// across captures. The lock case is the one worth stating outright, since it
// looks comparable and is not - see LockProfile.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.NetTrace.Diff {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

using DotnetInsights.NetTrace.Contention;
using DotnetInsights.NetTrace.Cpu;
using DotnetInsights.NetTrace.Exceptions;
using DotnetInsights.NetTrace.Gc;
using DotnetInsights.NetTrace.Overview;
using DotnetInsights.NetTrace.Rundown;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

// One named row of a capture's profile: a count and an amount, whose meaning
// depends on the dimension (bytes for allocations, milliseconds for
// contention, samples for CPU). Kept deliberately generic so
// CaptureDiffBuilder can join every dimension with one code path instead of
// five near-identical ones.
public sealed class NamedMetric
{
    public string Name;
    public long Count;
    public double Amount;

    public NamedMetric(string name)
    {
        this.Name = name;
    }
}

// Per-generation GC aggregates. GCs are NOT matched individually between
// captures - two runs have no correspondence between GC #18682 and any GC in
// the other file, and pretending otherwise would produce confident nonsense.
// Generation-level aggregates are the finest granularity that means the same
// thing in both captures.
public sealed class GcGenerationProfile
{
    public int Generation;
    public long Count;
    public double TotalPauseMSec;
    public double MaxPauseMSec;
    public long TotalPromotedBytes;
    // Heap size after the LAST collection of this generation, which is the
    // closest thing to a steady-state reading a capture offers.
    public long FinalHeapSizeBytes;
}

public sealed class CaptureProfile
{
    public string FilePath;
    public string ProcessName;

    // Wall-clock span of the capture, computed exactly as Program.cs's --json
    // mode computes it (min/max event QPC). Every per-second normalization in
    // the diff divides by THIS capture's own duration, never by a shared one.
    public double CaptureDurationMSec;
    public long TotalEventCount;

    public long TotalGcCount;
    public double TotalGcPauseMSec;
    public List<GcGenerationProfile> GcGenerations = new List<GcGenerationProfile>();

    public long TotalAllocationTickCount;
    public double TotalAllocatedBytes;

    public long TotalExceptionCount;
    public long TotalCpuSampleCount;

    public long TotalContentionCount;
    public double TotalContentionWaitMSec;

    // Overall time breakdown percentages (see Overview/TimeBreakdownBuilder.cs).
    public bool HasTimeBreakdown;
    public double GcPercent;
    public double ContentionPercent;
    public bool HasCpuBreakdown;
    public double IdlePercent;
    public double CpuBoundPercent;

    // Per-dimension named rows. Dictionary rather than List so the builder
    // can join by key directly.
    public Dictionary<string, NamedMetric> EventTypes = new Dictionary<string, NamedMetric>();
    public Dictionary<string, NamedMetric> AllocationTypes = new Dictionary<string, NamedMetric>();
    public Dictionary<string, NamedMetric> ExceptionTypes = new Dictionary<string, NamedMetric>();
    public Dictionary<string, NamedMetric> CpuMethods = new Dictionary<string, NamedMetric>();
    public Dictionary<string, NamedMetric> ContentionSites = new Dictionary<string, NamedMetric>();
    public Dictionary<string, NamedMetric> Locks = new Dictionary<string, NamedMetric>();

    private static NamedMetric GetOrAdd(Dictionary<string, NamedMetric> map, string name)
    {
        NamedMetric metric;

        if (!map.TryGetValue(name, out metric))
        {
            metric = new NamedMetric(name);
            map[name] = metric;
        }

        return metric;
    }

    // Symbol availability differs between captures (a method JIT-ed and
    // rundown-reported in one run may not be in the other), so every
    // unresolved frame collapses into ONE synthetic row. Without this a
    // capture with poorer symbols manufactures thousands of spurious
    // "added"/"removed" rows whose names are raw addresses that could never
    // match anything.
    public const string UnresolvedFrameName = "<unresolved>";

    private static string NormalizeFrameName(string frameName)
    {
        if (string.IsNullOrEmpty(frameName))
        {
            return UnresolvedFrameName;
        }

        if (frameName.StartsWith("<unresolved", StringComparison.Ordinal))
        {
            return UnresolvedFrameName;
        }

        return frameName;
    }

    public static CaptureProfile Build(
        string filePath,
        string processName,
        double captureDurationMSec,
        int totalEventCount,
        EventOverview eventOverview,
        List<GcEvent> gcEvents,
        List<AllocationEvent> allocationEvents,
        List<ExceptionEvent> exceptionEvents,
        List<SampleEvent> sampleEvents,
        List<ContentionEvent> contentionEvents,
        MethodSymbolTable symbolTable)
    {
        CaptureProfile profile = new CaptureProfile();
        profile.FilePath = filePath;
        profile.ProcessName = processName;
        profile.CaptureDurationMSec = captureDurationMSec;
        profile.TotalEventCount = totalEventCount;

        for (int eventTypeIndex = 0; eventTypeIndex < eventOverview.EventTypes.Count; ++eventTypeIndex)
        {
            EventTypeCount eventTypeCount = eventOverview.EventTypes[eventTypeIndex];

            // Provider-qualified: the same event id means different things
            // under different providers, so the id alone is not a key.
            NamedMetric metric = GetOrAdd(profile.EventTypes, eventTypeCount.ProviderName + "/" + eventTypeCount.DisplayName);
            metric.Count += eventTypeCount.Count;
        }

        BuildGcProfile(profile, gcEvents);
        BuildAllocationProfile(profile, allocationEvents);
        BuildExceptionProfile(profile, exceptionEvents);
        BuildCpuProfile(profile, sampleEvents, symbolTable);
        BuildContentionProfile(profile, contentionEvents, symbolTable);

        TimeBreakdown timeBreakdown = TimeBreakdownBuilder.Build(gcEvents, contentionEvents, sampleEvents, symbolTable, captureDurationMSec);
        profile.HasTimeBreakdown = timeBreakdown.HasCaptureDuration;
        profile.GcPercent = timeBreakdown.GcPercent;
        profile.ContentionPercent = timeBreakdown.ContentionPercent;
        profile.HasCpuBreakdown = timeBreakdown.HasCpuSampleBreakdown;
        profile.IdlePercent = timeBreakdown.IdlePercent;
        profile.CpuBoundPercent = timeBreakdown.CpuBoundPercent;

        return profile;
    }

    private static void BuildGcProfile(CaptureProfile profile, List<GcEvent> gcEvents)
    {
        Dictionary<int, GcGenerationProfile> byGeneration = new Dictionary<int, GcGenerationProfile>();

        for (int gcIndex = 0; gcIndex < gcEvents.Count; ++gcIndex)
        {
            GcEvent gcEvent = gcEvents[gcIndex];

            ++profile.TotalGcCount;
            profile.TotalGcPauseMSec += gcEvent.PauseDurationMSec;

            GcGenerationProfile generationProfile;

            if (!byGeneration.TryGetValue(gcEvent.Generation, out generationProfile))
            {
                generationProfile = new GcGenerationProfile();
                generationProfile.Generation = gcEvent.Generation;
                byGeneration[gcEvent.Generation] = generationProfile;
            }

            ++generationProfile.Count;
            generationProfile.TotalPauseMSec += gcEvent.PauseDurationMSec;
            generationProfile.TotalPromotedBytes += gcEvent.TotalPromotedSize0 + gcEvent.TotalPromotedSize1 + gcEvent.TotalPromotedSize2;

            if (gcEvent.PauseDurationMSec > generationProfile.MaxPauseMSec)
            {
                generationProfile.MaxPauseMSec = gcEvent.PauseDurationMSec;
            }

            // gcEvents arrive sorted by id, so the last write wins and lands
            // on the final collection of that generation.
            generationProfile.FinalHeapSizeBytes = gcEvent.TotalHeapSize;
        }

        foreach (KeyValuePair<int, GcGenerationProfile> entry in byGeneration)
        {
            profile.GcGenerations.Add(entry.Value);
        }

        profile.GcGenerations.Sort((GcGenerationProfile left, GcGenerationProfile right) => left.Generation.CompareTo(right.Generation));
    }

    private static void BuildAllocationProfile(CaptureProfile profile, List<AllocationEvent> allocationEvents)
    {
        Span<AllocationEvent> eventsSpan = CollectionsMarshal.AsSpan(allocationEvents);

        for (int eventIndex = 0; eventIndex < eventsSpan.Length; ++eventIndex)
        {
            ref readonly AllocationEvent allocationEvent = ref eventsSpan[eventIndex];

            ++profile.TotalAllocationTickCount;
            profile.TotalAllocatedBytes += allocationEvent.AllocationAmount;

            NamedMetric metric = GetOrAdd(profile.AllocationTypes, allocationEvent.TypeName ?? UnresolvedFrameName);
            ++metric.Count;
            metric.Amount += allocationEvent.AllocationAmount;
        }
    }

    private static void BuildExceptionProfile(CaptureProfile profile, List<ExceptionEvent> exceptionEvents)
    {
        Span<ExceptionEvent> eventsSpan = CollectionsMarshal.AsSpan(exceptionEvents);

        for (int eventIndex = 0; eventIndex < eventsSpan.Length; ++eventIndex)
        {
            ref readonly ExceptionEvent exceptionEvent = ref eventsSpan[eventIndex];

            ++profile.TotalExceptionCount;

            NamedMetric metric = GetOrAdd(profile.ExceptionTypes, exceptionEvent.ExceptionType ?? UnresolvedFrameName);
            ++metric.Count;
            metric.Amount += 1;
        }
    }

    private static void BuildCpuProfile(CaptureProfile profile, List<SampleEvent> sampleEvents, MethodSymbolTable symbolTable)
    {
        Span<SampleEvent> eventsSpan = CollectionsMarshal.AsSpan(sampleEvents);

        for (int eventIndex = 0; eventIndex < eventsSpan.Length; ++eventIndex)
        {
            ref readonly SampleEvent sampleEvent = ref eventsSpan[eventIndex];

            ++profile.TotalCpuSampleCount;

            if (sampleEvent.Stack.Length == 0)
            {
                continue;
            }

            // Self samples only (the leaf frame) - the same "what was actually
            // running" measure Cpu/CpuProfileJsonExporter.cs ranks by.
            string leafName = NormalizeFrameName(symbolTable.NameForId(symbolTable.ResolveId(sampleEvent.Stack[0], sampleEvent.RelativeMSec)));

            NamedMetric metric = GetOrAdd(profile.CpuMethods, leafName);
            ++metric.Count;
            metric.Amount += 1;
        }
    }

    private static void BuildContentionProfile(CaptureProfile profile, List<ContentionEvent> contentionEvents, MethodSymbolTable symbolTable)
    {
        Span<ContentionEvent> eventsSpan = CollectionsMarshal.AsSpan(contentionEvents);

        for (int eventIndex = 0; eventIndex < eventsSpan.Length; ++eventIndex)
        {
            ref readonly ContentionEvent contentionEvent = ref eventsSpan[eventIndex];

            ++profile.TotalContentionCount;
            profile.TotalContentionWaitMSec += contentionEvent.DurationMSec;

            string siteName = ResolveContentionSiteName(contentionEvent, symbolTable);

            NamedMetric siteMetric = GetOrAdd(profile.ContentionSites, siteName);
            ++siteMetric.Count;
            siteMetric.Amount += contentionEvent.DurationMSec;

            // Locks are keyed by the same NAME, never by LockId. A lock id is
            // a pointer into one process's heap: it is stable within a
            // capture and pure coincidence across two. Aggregating by name
            // also merges the several distinct locks that legitimately share
            // one (265 locks share "SslStream.DecryptData" on the reference
            // capture), which is the only comparison that means anything
            // between runs.
            if (contentionEvent.LockId != 0)
            {
                NamedMetric lockMetric = GetOrAdd(profile.Locks, siteName);
                ++lockMetric.Count;
                lockMetric.Amount += contentionEvent.DurationMSec;
            }
        }
    }

    // Mirrors Contention/ContentionJsonExporter.cs's own site attribution:
    // skip the generic runtime lock primitives every contention stack bottoms
    // out in, and attribute to the first frame below them. Duplicated rather
    // than shared because that method is private to the exporter and takes
    // its own out-parameters; if either changes they must change together.
    private static readonly string[] LockAcquisitionFramePrefixes = new string[]
    {
        "System.Threading.Monitor.",
        "System.Threading.Lock.",
        "System.Threading.LockHolder.",
        "System.Threading.SpinLock.",
        "System.Threading.ObjectHeader."
    };

    private static string ResolveContentionSiteName(in ContentionEvent contentionEvent, MethodSymbolTable symbolTable)
    {
        if (contentionEvent.Stack.Length == 0)
        {
            return UnresolvedFrameName;
        }

        for (int frameIndex = 0; frameIndex < contentionEvent.Stack.Length; ++frameIndex)
        {
            string frameName = symbolTable.NameForId(symbolTable.ResolveId(contentionEvent.Stack[frameIndex], contentionEvent.RelativeMSec));

            if (string.IsNullOrEmpty(frameName))
            {
                continue;
            }

            bool isPrimitive = false;

            for (int prefixIndex = 0; prefixIndex < LockAcquisitionFramePrefixes.Length; ++prefixIndex)
            {
                if (frameName.StartsWith(LockAcquisitionFramePrefixes[prefixIndex], StringComparison.Ordinal))
                {
                    isPrimitive = true;
                    break;
                }
            }

            if (!isPrimitive)
            {
                return NormalizeFrameName(frameName);
            }
        }

        return NormalizeFrameName(symbolTable.NameForId(symbolTable.ResolveId(contentionEvent.Stack[0], contentionEvent.RelativeMSec)));
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.Diff)
