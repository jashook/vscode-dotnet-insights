////////////////////////////////////////////////////////////////////////////////
// Module: PerfEventAttr.cs
//
// Notes:
// One decoded `struct perf_event_attr` plus the sample ids the file maps onto
// it. This is the only thing that makes a PERF_RECORD_SAMPLE decodable - the
// sample body carries no field list of its own, so `SampleType` here IS the
// schema for every sample belonging to this event (see PerfSampleLayout).
//
// The struct grows between kernel releases and carries its own `Size`, so this
// decodes a fixed prefix (PerfFormat.AttrDecodedPrefixSize) and skips the
// remainder using that declared size rather than a compiled-in sizeof. A
// capture from a newer kernel therefore reads correctly instead of
// misaligning every field after the first unknown one.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.Perf.PerfData {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;
using System.Numerics;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public sealed class PerfEventAttr
{
    public uint Type;
    public uint Size;
    public ulong Config;
    public ulong SamplePeriodOrFrequency;
    public ulong SampleType;
    public ulong ReadFormat;

    // The packed bitfield word at offset 40. Individual flags are exposed as
    // properties below rather than decoded into fields, because only three of
    // the ~38 bits in it are ever consulted.
    public ulong Flags;

    public ulong BranchSampleType;
    public ulong SampleRegsUser;
    public uint SampleStackUser;
    public ulong SampleRegsIntr;

    // Every `id` value the file assigns to this event. A multi-event capture
    // routes each sample to its attr through these; a single-event capture
    // usually assigns none at all, because there is nothing to disambiguate.
    public readonly List<ulong> Ids = new List<ulong>();

    // From the EVENT_DESC feature section when present ("cpu-clock",
    // "lock:contention_begin", ...). Empty when the capture predates that
    // section, in which case callers fall back to Type/Config.
    public string Name = string.Empty;

    // Bit 18 of the flags word. When set, EVERY non-sample record
    // (MMAP2/COMM/FORK/...) carries a trailing sample_id block, which changes
    // where those records end - so this is not cosmetic, it is required to
    // walk them at all.
    public bool SampleIdAll => (this.Flags & (1UL << 18)) != 0;

    public bool Freq => (this.Flags & (1UL << 10)) != 0;

    public bool UseClockId => (this.Flags & (1UL << 25)) != 0;

    public bool IsTracepoint => this.Type == PerfFormat.AttrTypeTracepoint;

    // Number of u64 registers a PERF_SAMPLE_REGS_USER block carries when its
    // abi word is non-zero: one per set bit of the mask the event was opened
    // with.
    public int RegsUserCount => BitOperations.PopCount(this.SampleRegsUser);

    public int RegsIntrCount => BitOperations.PopCount(this.SampleRegsIntr);

    // Where a given architectural register sits inside a sample's register
    // array. Registers are stored one u64 per SET BIT of sample_regs_user in
    // ascending bit order, so a register's slot is the number of set bits
    // BELOW its own - it moves when the mask changes, and a capture recorded
    // with a different mask puts the stack pointer somewhere else entirely.
    // Returns -1 when this capture did not record that register.
    public int RegisterSlot(int registerNumber)
    {
        if (registerNumber < 0 || registerNumber > 63)
        {
            return -1;
        }

        ulong bit = 1UL << registerNumber;
        if ((this.SampleRegsUser & bit) == 0)
        {
            return -1;
        }

        return BitOperations.PopCount(this.SampleRegsUser & (bit - 1));
    }

    public static PerfEventAttr Decode(ref PerfSpanReader reader)
    {
        int startPosition = reader.Position;

        PerfEventAttr attr = new PerfEventAttr();
        attr.Type = reader.ReadUInt32();
        attr.Size = reader.ReadUInt32();
        attr.Config = reader.ReadUInt64();
        attr.SamplePeriodOrFrequency = reader.ReadUInt64();
        attr.SampleType = reader.ReadUInt64();
        attr.ReadFormat = reader.ReadUInt64();
        attr.Flags = reader.ReadUInt64();

        // wakeup_events/wakeup_watermark, bp_type
        reader.Skip(8);

        // config1, config2
        reader.Skip(16);

        attr.BranchSampleType = reader.ReadUInt64();
        attr.SampleRegsUser = reader.ReadUInt64();
        attr.SampleStackUser = reader.ReadUInt32();

        // clockid
        reader.Skip(4);

        attr.SampleRegsIntr = reader.ReadUInt64();

        // A capture written by an OLDER kernel can declare a Size smaller than
        // the prefix decoded above. Everything past its real end read as zero
        // from this reader's bounds check, which is the correct value for a
        // field that kernel did not have - but the cursor must still be placed
        // by the declared size, not by how far the decode walked.
        int declaredSize = (int)attr.Size;
        if (declaredSize < PerfFormat.AttrDecodedPrefixSize)
        {
            declaredSize = PerfFormat.AttrDecodedPrefixSize;
        }

        reader.Position = startPosition + declaredSize;
        return attr;
    }

    // Bytes a PERF_SAMPLE_READ block occupies, which has to be stepped over
    // even though nothing here consumes it - the fields after it in the sample
    // body land at the wrong offset otherwise.
    public int ReadBlockSizeBytes(int groupMemberCount)
    {
        int perValueWords = 1;
        if ((this.ReadFormat & PerfFormat.ReadFormatId) != 0)
        {
            ++perValueWords;
        }

        if ((this.ReadFormat & PerfFormat.ReadFormatLost) != 0)
        {
            ++perValueWords;
        }

        int headerWords = 0;
        if ((this.ReadFormat & PerfFormat.ReadFormatTotalTimeEnabled) != 0)
        {
            ++headerWords;
        }

        if ((this.ReadFormat & PerfFormat.ReadFormatTotalTimeRunning) != 0)
        {
            ++headerWords;
        }

        if ((this.ReadFormat & PerfFormat.ReadFormatGroup) != 0)
        {
            // u64 nr, then the shared time words, then one entry per member.
            return (1 + headerWords + (groupMemberCount * perValueWords)) * 8;
        }

        return (headerWords + perValueWords) * 8;
    }

    public string DescribeSampleType()
    {
        List<string> parts = new List<string>();
        AppendIfSet(parts, this.SampleType, PerfFormat.SampleIdentifier, "IDENTIFIER");
        AppendIfSet(parts, this.SampleType, PerfFormat.SampleIp, "IP");
        AppendIfSet(parts, this.SampleType, PerfFormat.SampleTid, "TID");
        AppendIfSet(parts, this.SampleType, PerfFormat.SampleTime, "TIME");
        AppendIfSet(parts, this.SampleType, PerfFormat.SampleAddr, "ADDR");
        AppendIfSet(parts, this.SampleType, PerfFormat.SampleId, "ID");
        AppendIfSet(parts, this.SampleType, PerfFormat.SampleStreamId, "STREAM_ID");
        AppendIfSet(parts, this.SampleType, PerfFormat.SampleCpu, "CPU");
        AppendIfSet(parts, this.SampleType, PerfFormat.SamplePeriod, "PERIOD");
        AppendIfSet(parts, this.SampleType, PerfFormat.SampleRead, "READ");
        AppendIfSet(parts, this.SampleType, PerfFormat.SampleCallchain, "CALLCHAIN");
        AppendIfSet(parts, this.SampleType, PerfFormat.SampleRaw, "RAW");
        AppendIfSet(parts, this.SampleType, PerfFormat.SampleBranchStack, "BRANCH_STACK");
        AppendIfSet(parts, this.SampleType, PerfFormat.SampleRegsUser, "REGS_USER");
        AppendIfSet(parts, this.SampleType, PerfFormat.SampleStackUser, "STACK_USER");
        AppendIfSet(parts, this.SampleType, PerfFormat.SampleWeight, "WEIGHT");
        AppendIfSet(parts, this.SampleType, PerfFormat.SampleDataSrc, "DATA_SRC");
        AppendIfSet(parts, this.SampleType, PerfFormat.SampleTransaction, "TRANSACTION");
        AppendIfSet(parts, this.SampleType, PerfFormat.SampleRegsIntr, "REGS_INTR");
        AppendIfSet(parts, this.SampleType, PerfFormat.SamplePhysAddr, "PHYS_ADDR");
        AppendIfSet(parts, this.SampleType, PerfFormat.SampleAux, "AUX");
        AppendIfSet(parts, this.SampleType, PerfFormat.SampleCgroup, "CGROUP");
        AppendIfSet(parts, this.SampleType, PerfFormat.SampleDataPageSize, "DATA_PAGE_SIZE");
        AppendIfSet(parts, this.SampleType, PerfFormat.SampleCodePageSize, "CODE_PAGE_SIZE");
        AppendIfSet(parts, this.SampleType, PerfFormat.SampleWeightStruct, "WEIGHT_STRUCT");
        return string.Join("|", parts);
    }

    private static void AppendIfSet(List<string> parts, ulong sampleType, ulong bit, string name)
    {
        if ((sampleType & bit) != 0)
        {
            parts.Add(name);
        }
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.Perf.PerfData)

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////
