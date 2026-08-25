////////////////////////////////////////////////////////////////////////////////
// Module: PerfSampleLayout.cs
//
// Notes:
// Decodes a PERF_RECORD_SAMPLE body. This is the single most
// misdecode-prone piece of the format and the reason it gets its own file.
//
// A sample carries NO field list. Its body is the concatenation of whichever
// fields the event's `sample_type` mask selected, written in ONE fixed order -
// the order the kernel's perf_output_sample() emits them, encoded below. That
// order is NOT the numeric order of the PERF_SAMPLE_* bit values, and the two
// disagree in a way that decodes silently rather than loudly: PERF_SAMPLE_ID
// is bit 6 and PERF_SAMPLE_CPU is bit 7, but CPU is written AFTER STREAM_ID
// (bit 9), while PERF_SAMPLE_IDENTIFIER is the highest of the four id bits
// (16) and is written FIRST of everything. Walking the bits in numeric order
// therefore yields a body that still parses - plausible addresses, plausible
// timestamps, a callchain of plausible depth - and is wrong in every field.
//
// Three of the fields are variable-length and must be sized from the ATTR, not
// from the sample: PERF_SAMPLE_REGS_USER stores one u64 per set bit of
// attr.sample_regs_user, PERF_SAMPLE_REGS_INTR likewise from
// attr.sample_regs_intr, and PERF_SAMPLE_READ's size depends on
// attr.read_format. Nothing in this reader consumes any of the three, and all
// three are still stepped over exactly, because a field this reader DOES care
// about can follow them.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.Perf.PerfData {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

// A ref struct: the callchain and raw payload are views INTO the file buffer,
// never copies. A real capture holds millions of samples and every one of them
// would otherwise allocate a long[] before anything has decided whether the
// sample is even interesting.
public ref struct PerfSample
{
    public ulong Id;
    public ulong InstructionPointer;
    public uint ProcessId;
    public uint ThreadId;
    public ulong Time;
    public uint Cpu;
    public ulong Period;

    // Leaf-first, exactly as the kernel writes it - entry 0 is the innermost
    // frame. Still contains PERF_CONTEXT_* markers; see
    // PerfFormat.IsContextMarker.
    public ReadOnlySpan<ulong> Callchain;

    // Tracepoint payload for a PERF_SAMPLE_RAW event (lock:contention_begin
    // and friends). Empty for a plain sampling event.
    public ReadOnlySpan<byte> Raw;

    // The user-space register file at the moment of the sample, present only
    // in a `--call-graph dwarf` capture. Stored as one u64 per SET BIT of
    // attr.sample_regs_user, in ascending bit order - so a register's position
    // here depends on the mask, not on its own number. Use
    // PerfSampleLayout.TryGetRegister rather than indexing this directly.
    public ReadOnlySpan<ulong> UserRegisters;

    // A verbatim copy of the top of the user stack, taken at the sample.
    // The unwinder walks THIS, not the live process - there is no live process
    // by the time a capture is read. Bounded by the size perf was asked to
    // copy (8KB by default), which is why a deep stack can run off the end and
    // has to stop rather than read whatever follows.
    public ReadOnlySpan<byte> UserStack;

    // The stack pointer's value when the copy was taken. The copied bytes
    // correspond to addresses [StackBase, StackBase + UserStack.Length), and
    // without this the copy is a bag of bytes with no idea where it came from.
    public ulong StackBase;

    public bool HasCallchain;
    public bool HasRaw;
    public bool HasTime;
    public bool HasUserRegisters;
    public bool HasUserStack;
}

public static class PerfSampleLayout
{
    // Bytes the trailing sample_id block occupies on a NON-sample record when
    // attr.sample_id_all is set. Those records end with this block, so their
    // own variable-length trailing member (a COMM's `comm[]`, an MMAP2's
    // `filename[]`) stops this many bytes short of the record end - reading it
    // to the true end swallows the block into the string.
    public static int SampleIdSizeBytes(PerfEventAttr attr)
    {
        if (attr == null || !attr.SampleIdAll)
        {
            return 0;
        }

        int sizeBytes = 0;
        if ((attr.SampleType & PerfFormat.SampleTid) != 0)
        {
            sizeBytes += 8;
        }

        if ((attr.SampleType & PerfFormat.SampleTime) != 0)
        {
            sizeBytes += 8;
        }

        if ((attr.SampleType & PerfFormat.SampleId) != 0)
        {
            sizeBytes += 8;
        }

        if ((attr.SampleType & PerfFormat.SampleStreamId) != 0)
        {
            sizeBytes += 8;
        }

        if ((attr.SampleType & PerfFormat.SampleCpu) != 0)
        {
            sizeBytes += 8;
        }

        if ((attr.SampleType & PerfFormat.SampleIdentifier) != 0)
        {
            sizeBytes += 8;
        }

        return sizeBytes;
    }

    // The event id a SAMPLE belongs to, read without decoding the rest of it.
    // Only needed when a file carries more than one event.
    //
    // PERF_SAMPLE_IDENTIFIER exists precisely for this: perf sets it whenever
    // it records multiple events, and it is written FIRST, so the id is
    // readable at a fixed offset with no knowledge of the other fields. The
    // PERF_SAMPLE_ID fallback is not equivalent - its position depends on
    // every field before it - which is why perf itself requires all events in
    // a file to agree on that prefix.
    public static bool TryReadSampleId(ReadOnlySpan<byte> body, PerfEventAttr referenceAttr, out ulong id)
    {
        id = 0;
        if (referenceAttr == null)
        {
            return false;
        }

        PerfSpanReader reader = new PerfSpanReader(body);
        if ((referenceAttr.SampleType & PerfFormat.SampleIdentifier) != 0)
        {
            id = reader.ReadUInt64();
            return !reader.Overran;
        }

        if ((referenceAttr.SampleType & PerfFormat.SampleId) == 0)
        {
            return false;
        }

        if ((referenceAttr.SampleType & PerfFormat.SampleIp) != 0)
        {
            reader.Skip(8);
        }

        if ((referenceAttr.SampleType & PerfFormat.SampleTid) != 0)
        {
            reader.Skip(8);
        }

        if ((referenceAttr.SampleType & PerfFormat.SampleTime) != 0)
        {
            reader.Skip(8);
        }

        if ((referenceAttr.SampleType & PerfFormat.SampleAddr) != 0)
        {
            reader.Skip(8);
        }

        id = reader.ReadUInt64();
        return !reader.Overran;
    }

    // The id a NON-sample record belongs to. Same block as above but written
    // at the END of the record, so it is located by walking back from the
    // record's end rather than forward from its start.
    public static bool TryReadRecordId(ReadOnlySpan<byte> body, PerfEventAttr referenceAttr, out ulong id)
    {
        id = 0;
        if (referenceAttr == null || !referenceAttr.SampleIdAll)
        {
            return false;
        }

        if ((referenceAttr.SampleType & PerfFormat.SampleIdentifier) == 0)
        {
            return false;
        }

        int idSize = SampleIdSizeBytes(referenceAttr);
        if (idSize == 0 || body.Length < idSize)
        {
            return false;
        }

        // IDENTIFIER is the last member of the trailing block.
        PerfSpanReader reader = new PerfSpanReader(body);
        reader.Position = body.Length - 8;
        id = reader.ReadUInt64();
        return !reader.Overran;
    }

    public static bool TryParse(ReadOnlySpan<byte> body, PerfEventAttr attr, ref PerfSample sample)
    {
        if (attr == null)
        {
            return false;
        }

        ulong sampleType = attr.SampleType;
        PerfSpanReader reader = new PerfSpanReader(body);

        if ((sampleType & PerfFormat.SampleIdentifier) != 0)
        {
            sample.Id = reader.ReadUInt64();
        }

        if ((sampleType & PerfFormat.SampleIp) != 0)
        {
            sample.InstructionPointer = reader.ReadUInt64();
        }

        if ((sampleType & PerfFormat.SampleTid) != 0)
        {
            sample.ProcessId = reader.ReadUInt32();
            sample.ThreadId = reader.ReadUInt32();
        }

        if ((sampleType & PerfFormat.SampleTime) != 0)
        {
            sample.Time = reader.ReadUInt64();
            sample.HasTime = true;
        }

        if ((sampleType & PerfFormat.SampleAddr) != 0)
        {
            reader.Skip(8);
        }

        if ((sampleType & PerfFormat.SampleId) != 0)
        {
            ulong plainId = reader.ReadUInt64();
            if ((sampleType & PerfFormat.SampleIdentifier) == 0)
            {
                sample.Id = plainId;
            }
        }

        if ((sampleType & PerfFormat.SampleStreamId) != 0)
        {
            reader.Skip(8);
        }

        if ((sampleType & PerfFormat.SampleCpu) != 0)
        {
            sample.Cpu = reader.ReadUInt32();
            // The `res` half of the pair. Always zero, always present.
            reader.Skip(4);
        }

        if ((sampleType & PerfFormat.SamplePeriod) != 0)
        {
            sample.Period = reader.ReadUInt64();
        }

        if ((sampleType & PerfFormat.SampleRead) != 0)
        {
            SkipReadBlock(ref reader, attr);
        }

        if ((sampleType & PerfFormat.SampleCallchain) != 0)
        {
            ulong frameCount = reader.ReadUInt64();

            // Bounded against the record rather than trusted: the count comes
            // from the file, and attr.sample_max_stack caps it at 127 by
            // default but nothing guarantees a well-formed value in a
            // truncated capture.
            int available = reader.Remaining / 8;
            if (frameCount > (ulong)available)
            {
                return false;
            }

            sample.Callchain = reader.ReadUInt64Array((int)frameCount);
            sample.HasCallchain = true;
        }

        if ((sampleType & PerfFormat.SampleRaw) != 0)
        {
            int rawSize = (int)reader.ReadUInt32();
            if (rawSize < 0 || rawSize > reader.Remaining)
            {
                return false;
            }

            sample.Raw = reader.ReadBytes(rawSize);
            sample.HasRaw = true;
        }

        // Nothing past this point is consumed today. The fields are still
        // walked in order rather than abandoned, so that adding a consumer for
        // any of them is a local change rather than a re-derivation of the
        // whole layout.
        if ((sampleType & PerfFormat.SampleBranchStack) != 0)
        {
            ulong branchCount = reader.ReadUInt64();
            if ((attr.BranchSampleType & (1UL << 20)) != 0)
            {
                // PERF_SAMPLE_BRANCH_HW_INDEX adds one u64 before the entries.
                reader.Skip(8);
            }

            long branchBytes = (long)branchCount * 24;
            if (branchBytes < 0 || branchBytes > reader.Remaining)
            {
                return false;
            }

            reader.Skip((int)branchBytes);
        }

        if ((sampleType & PerfFormat.SampleRegsUser) != 0)
        {
            // abi is PERF_SAMPLE_REGS_ABI_NONE(0)/_32(1)/_64(2). Zero means no
            // registers follow at all - which happens for a sample taken in
            // kernel context, where there is no user register file to report.
            ulong abi = reader.ReadUInt64();
            if (abi != 0)
            {
                sample.UserRegisters = reader.ReadUInt64Array(attr.RegsUserCount);
                sample.HasUserRegisters = sample.UserRegisters.Length > 0;
            }
        }

        if ((sampleType & PerfFormat.SampleStackUser) != 0)
        {
            ulong stackSize = reader.ReadUInt64();
            if (stackSize > 0)
            {
                if (stackSize > (ulong)reader.Remaining)
                {
                    return false;
                }

                ReadOnlySpan<byte> copiedStack = reader.ReadBytes((int)stackSize);

                // dyn_size, present only when the stack itself was, and NOT
                // redundant with the size above: the first size is how much
                // perf was configured to copy, the second is how much was
                // actually valid. The kernel copies the requested amount and
                // then reports how much of it really came from the stack - the
                // remainder is whatever the buffer already held. Unwinding into
                // it produces frames from a previous sample's stack, which look
                // entirely plausible.
                ulong dynamicSize = reader.ReadUInt64();

                if (dynamicSize > 0 && dynamicSize <= stackSize)
                {
                    sample.UserStack = copiedStack.Slice(0, (int)dynamicSize);
                    sample.HasUserStack = true;
                }
            }
        }

        return !reader.Overran;
    }

    private static void SkipReadBlock(ref PerfSpanReader reader, PerfEventAttr attr)
    {
        int headerWords = 0;
        if ((attr.ReadFormat & PerfFormat.ReadFormatTotalTimeEnabled) != 0)
        {
            ++headerWords;
        }

        if ((attr.ReadFormat & PerfFormat.ReadFormatTotalTimeRunning) != 0)
        {
            ++headerWords;
        }

        int perValueWords = 1;
        if ((attr.ReadFormat & PerfFormat.ReadFormatId) != 0)
        {
            ++perValueWords;
        }

        if ((attr.ReadFormat & PerfFormat.ReadFormatLost) != 0)
        {
            ++perValueWords;
        }

        if ((attr.ReadFormat & PerfFormat.ReadFormatGroup) != 0)
        {
            // The member count is in the stream, not in the attr - a group's
            // size is not knowable from one event's description.
            ulong memberCount = reader.ReadUInt64();
            reader.Skip(headerWords * 8);

            long memberBytes = (long)memberCount * perValueWords * 8;
            if (memberBytes < 0 || memberBytes > reader.Remaining)
            {
                reader.Skip(reader.Remaining);
                return;
            }

            reader.Skip((int)memberBytes);
            return;
        }

        reader.Skip((headerWords + perValueWords) * 8);
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.Perf.PerfData)

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////
