////////////////////////////////////////////////////////////////////////////////
// Module: PerfFormat.cs
//
// Notes:
// Constants for the `perf.data` container written by `perf record`. The format
// is documented in the kernel tree at
// tools/perf/Documentation/perf.data-file-format.txt, and the record bodies
// are the kernel's own perf_event.h structures - so unlike a versioned
// application format, the authority here is a header file and the layout is
// whatever the running kernel emitted. Two consequences shape this reader:
//
//   - `perf_event_attr` GROWS between kernel versions and carries its own
//     `size` field for exactly that reason. Never sizeof-assume it; read
//     `size`, decode the prefix this reader understands, and skip the rest.
//     The reference captures here report size 136.
//
//   - A PERF_RECORD_SAMPLE has NO self-describing layout. Its body is a
//     concatenation of whichever fields the event's own `sample_type` bitmask
//     selected, in one fixed canonical order (the order the kernel's
//     perf_output_sample() writes them). Reading them in any other order
//     decodes garbage that still looks like plausible addresses and
//     timestamps, so PerfSampleLayout derives the order from sample_type
//     rather than hardcoding one capture's shape.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.Perf.PerfData {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public static class PerfFormat
{
    // "PERFILE2" as a little-endian u64. The older "PERFFILE" (v1) container
    // is not supported - perf has written v2 since 2012 - and a big-endian
    // writer stores the same bytes reversed, which is how a cross-endian
    // capture is detected rather than silently misread.
    public const ulong Magic2 = 0x32454C4946524550UL;
    public const ulong Magic2Swapped = 0x50455246494C4532UL;

    // sizeof(struct perf_file_header). Fixed, unlike perf_event_attr.
    public const int FileHeaderSize = 104;

    // struct perf_file_section { u64 offset; u64 size; }
    public const int FileSectionSize = 16;

    // struct perf_event_header { u32 type; u16 misc; u16 size; }
    public const int RecordHeaderSize = 8;

    // The prefix of perf_event_attr this reader decodes. Everything past it is
    // skipped using the attr's own declared size.
    //
    // 104 is not an arbitrary "enough" - it is exactly far enough to reach
    // `sample_regs_intr`, and every field up to there is needed to SIZE a
    // sample body rather than merely to describe the event: PERF_SAMPLE_REGS_USER
    // stores popcount(sample_regs_user) registers (offset 80),
    // PERF_SAMPLE_STACK_USER is bounded by sample_stack_user (88), and
    // PERF_SAMPLE_REGS_INTR by sample_regs_intr (96). Stopping short of any of
    // them makes every field AFTER it in the sample decode at the wrong
    // offset.
    public const int AttrDecodedPrefixSize = 104;

    ////////////////////////////////////////////////////////////////////////////
    // Record types. 1-63 are the kernel's own (perf_event_type); 64+ are
    // synthesized by perf itself in user space (perf_user_event_type) and
    // never come out of the ring buffer.
    ////////////////////////////////////////////////////////////////////////////

    public const uint RecordMmap = 1;
    public const uint RecordLost = 2;
    public const uint RecordComm = 3;
    public const uint RecordExit = 4;
    public const uint RecordThrottle = 5;
    public const uint RecordUnthrottle = 6;
    public const uint RecordFork = 7;
    public const uint RecordRead = 8;
    public const uint RecordSample = 9;
    public const uint RecordMmap2 = 10;
    public const uint RecordAux = 11;
    public const uint RecordITraceStart = 12;
    public const uint RecordLostSamples = 13;
    public const uint RecordSwitch = 14;
    public const uint RecordSwitchCpuWide = 15;
    public const uint RecordNamespaces = 16;
    public const uint RecordKsymbol = 17;
    public const uint RecordBpfEvent = 18;
    public const uint RecordCgroup = 19;
    public const uint RecordTextPoke = 20;
    public const uint RecordAuxOutputHwId = 21;

    public const uint RecordUserTypeStart = 64;
    public const uint RecordHeaderAttr = 64;
    public const uint RecordHeaderEventType = 65;
    public const uint RecordHeaderTracingData = 66;
    public const uint RecordHeaderBuildId = 67;
    public const uint RecordFinishedRound = 68;
    public const uint RecordIdIndex = 69;
    public const uint RecordAuxtraceInfo = 70;
    public const uint RecordAuxtrace = 71;
    public const uint RecordAuxtraceError = 72;
    public const uint RecordThreadMap = 73;
    public const uint RecordCpuMap = 74;
    public const uint RecordStatConfig = 75;
    public const uint RecordStat = 76;
    public const uint RecordStatRound = 77;
    public const uint RecordEventUpdate = 78;
    public const uint RecordTimeConv = 79;
    public const uint RecordHeaderFeature = 80;
    public const uint RecordCompressed = 81;
    public const uint RecordFinishedInit = 82;

    ////////////////////////////////////////////////////////////////////////////
    // perf_event_header.misc
    ////////////////////////////////////////////////////////////////////////////

    public const ushort MiscCpuModeMask = 7;
    public const ushort MiscCpuModeUnknown = 0;
    public const ushort MiscCpuModeKernel = 1;
    public const ushort MiscCpuModeUser = 2;
    public const ushort MiscCpuModeHypervisor = 3;
    public const ushort MiscCpuModeGuestKernel = 4;
    public const ushort MiscCpuModeGuestUser = 5;

    // On a PERF_RECORD_MMAP2 this bit replaces the {maj, min, ino,
    // ino_generation} identity block with an inline 20-byte ELF build id.
    // `perf record --buildid-all` (what the fixture captures use) sets it.
    // Reading the wrong branch does not fail - it decodes a device number as
    // a build id and vice versa - so the bit must be honoured, not assumed.
    public const ushort MiscMmapBuildId = 1 << 14;

    // On a build_id_event in the BUILD_ID feature section, says the `size`
    // byte that follows the 20 id bytes is meaningful. See
    // PerfDataFile.ReadBuildIdSection - the id field is NOT a flat 24 bytes,
    // and reading it as one appends the size byte to every id.
    public const ushort MiscBuildIdSize = 1 << 15;

    ////////////////////////////////////////////////////////////////////////////
    // perf_event_attr.sample_type. The ORDER of these fields inside a sample
    // body is fixed by the kernel and is NOT the numeric order of these bits -
    // see PerfSampleLayout, which encodes the real order.
    ////////////////////////////////////////////////////////////////////////////

    public const ulong SampleIp = 1UL << 0;
    public const ulong SampleTid = 1UL << 1;
    public const ulong SampleTime = 1UL << 2;
    public const ulong SampleAddr = 1UL << 3;
    public const ulong SampleRead = 1UL << 4;
    public const ulong SampleCallchain = 1UL << 5;
    public const ulong SampleId = 1UL << 6;
    public const ulong SampleCpu = 1UL << 7;
    public const ulong SamplePeriod = 1UL << 8;
    public const ulong SampleStreamId = 1UL << 9;
    public const ulong SampleRaw = 1UL << 10;
    public const ulong SampleBranchStack = 1UL << 11;
    public const ulong SampleRegsUser = 1UL << 12;
    public const ulong SampleStackUser = 1UL << 13;
    public const ulong SampleWeight = 1UL << 14;
    public const ulong SampleDataSrc = 1UL << 15;
    public const ulong SampleIdentifier = 1UL << 16;
    public const ulong SampleTransaction = 1UL << 17;
    public const ulong SampleRegsIntr = 1UL << 18;
    public const ulong SamplePhysAddr = 1UL << 19;
    public const ulong SampleAux = 1UL << 20;
    public const ulong SampleCgroup = 1UL << 21;
    public const ulong SampleDataPageSize = 1UL << 22;
    public const ulong SampleCodePageSize = 1UL << 23;
    public const ulong SampleWeightStruct = 1UL << 24;

    ////////////////////////////////////////////////////////////////////////////
    // perf_event_attr.read_format, needed only to size a PERF_SAMPLE_READ
    // block so the fields after it land at the right offset.
    ////////////////////////////////////////////////////////////////////////////

    public const ulong ReadFormatTotalTimeEnabled = 1UL << 0;
    public const ulong ReadFormatTotalTimeRunning = 1UL << 1;
    public const ulong ReadFormatId = 1UL << 2;
    public const ulong ReadFormatGroup = 1UL << 3;
    public const ulong ReadFormatLost = 1UL << 4;

    ////////////////////////////////////////////////////////////////////////////
    // perf_event_attr.type
    ////////////////////////////////////////////////////////////////////////////

    public const uint AttrTypeHardware = 0;
    public const uint AttrTypeSoftware = 1;
    public const uint AttrTypeTracepoint = 2;
    public const uint AttrTypeHwCache = 3;
    public const uint AttrTypeRaw = 4;
    public const uint AttrTypeBreakpoint = 5;

    ////////////////////////////////////////////////////////////////////////////
    // Feature sections, stored after the data section as one perf_file_section
    // per set bit, in ascending bit order.
    ////////////////////////////////////////////////////////////////////////////

    public const int FeatureBitCount = 256;

    public const int FeatureTracingData = 1;
    public const int FeatureBuildId = 2;
    public const int FeatureHostname = 3;
    public const int FeatureOsRelease = 4;
    public const int FeatureVersion = 5;
    public const int FeatureArch = 6;
    public const int FeatureNrCpus = 7;
    public const int FeatureCpuDesc = 8;
    public const int FeatureCpuId = 9;
    public const int FeatureTotalMem = 10;
    public const int FeatureCmdline = 11;
    public const int FeatureEventDesc = 12;
    public const int FeatureCpuTopology = 13;
    public const int FeatureNumaTopology = 14;
    public const int FeatureBranchStack = 15;
    public const int FeaturePmuMappings = 16;
    public const int FeatureGroupDesc = 17;
    public const int FeatureAuxtrace = 18;
    public const int FeatureStat = 19;
    public const int FeatureCache = 20;
    public const int FeatureSampleTime = 21;
    public const int FeatureMemTopology = 22;
    public const int FeatureClockId = 23;
    public const int FeatureDirFormat = 24;
    public const int FeatureBpfProgInfo = 25;
    public const int FeatureBpfBtf = 26;
    public const int FeatureCompressed = 27;
    public const int FeatureCpuPmuCaps = 28;
    public const int FeatureClockData = 29;
    public const int FeatureHybridTopology = 30;
    public const int FeaturePmuCaps = 31;

    // A callchain entry that is one of these is a MARKER, not an address - the
    // kernel uses them to delimit the kernel/user portions of a chain, and
    // they are the reason a naive reader ends up with frames at 0xFFFFFFFFFFFFFF80
    // ranked in its hot-method list. Values are PERF_CONTEXT_* from
    // perf_event.h: (u64)-1 through (u64)-128 are reserved as context marks,
    // so the test is a RANGE, not a set of equality checks.
    public const ulong ContextMarkerMinimum = unchecked((ulong)-4095L);

    public const ulong ContextHypervisor = unchecked((ulong)-32L);
    public const ulong ContextKernel = unchecked((ulong)-128L);
    public const ulong ContextUser = unchecked((ulong)-512L);
    public const ulong ContextGuest = unchecked((ulong)-2048L);
    public const ulong ContextGuestKernel = unchecked((ulong)-2176L);
    public const ulong ContextGuestUser = unchecked((ulong)-2560L);

    public static bool IsContextMarker(ulong callchainEntry)
    {
        return callchainEntry >= ContextMarkerMinimum;
    }

    public static string NameForRecordType(uint recordType)
    {
        switch (recordType)
        {
            case RecordMmap: return "MMAP";
            case RecordLost: return "LOST";
            case RecordComm: return "COMM";
            case RecordExit: return "EXIT";
            case RecordThrottle: return "THROTTLE";
            case RecordUnthrottle: return "UNTHROTTLE";
            case RecordFork: return "FORK";
            case RecordRead: return "READ";
            case RecordSample: return "SAMPLE";
            case RecordMmap2: return "MMAP2";
            case RecordAux: return "AUX";
            case RecordITraceStart: return "ITRACE_START";
            case RecordLostSamples: return "LOST_SAMPLES";
            case RecordSwitch: return "SWITCH";
            case RecordSwitchCpuWide: return "SWITCH_CPU_WIDE";
            case RecordNamespaces: return "NAMESPACES";
            case RecordKsymbol: return "KSYMBOL";
            case RecordBpfEvent: return "BPF_EVENT";
            case RecordCgroup: return "CGROUP";
            case RecordTextPoke: return "TEXT_POKE";
            case RecordAuxOutputHwId: return "AUX_OUTPUT_HW_ID";
            case RecordHeaderAttr: return "HEADER_ATTR";
            case RecordHeaderTracingData: return "HEADER_TRACING_DATA";
            case RecordHeaderBuildId: return "HEADER_BUILD_ID";
            case RecordFinishedRound: return "FINISHED_ROUND";
            case RecordIdIndex: return "ID_INDEX";
            case RecordThreadMap: return "THREAD_MAP";
            case RecordCpuMap: return "CPU_MAP";
            case RecordTimeConv: return "TIME_CONV";
            case RecordHeaderFeature: return "HEADER_FEATURE";
            case RecordCompressed: return "COMPRESSED";
            case RecordFinishedInit: return "FINISHED_INIT";
            default: return "Unknown(" + recordType.ToString() + ")";
        }
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.Perf.PerfData)

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////
