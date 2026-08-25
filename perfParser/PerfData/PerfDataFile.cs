////////////////////////////////////////////////////////////////////////////////
// Module: PerfDataFile.cs
//
// Notes:
// Reads a `perf record` capture: the file header, the event attributes, the
// feature sections that follow the data, and then the record stream itself.
//
// The whole file is read into one byte[] and every decoded view (a mapping's
// filename, a sample's callchain) is a span into it, matching
// nettraceParser's own NettraceFile.Read. That is a deliberate trade: a
// machine-wide capture with sched tracepoints reaches hundreds of MB in ten
// seconds (a fixture here is 710MB from a 10-second run), so this is the one
// number to revisit first if memory becomes the constraint.
//
// Errors are returned on the stack as a PerfReadResult, never thrown - a
// truncated or unsupported capture is an expected input, not an exceptional
// one, and the CLI turns it into one line of advice.
//
// Two things a reader of this format gets wrong quietly rather than loudly,
// both handled here:
//
//   - `perf record -z` compresses the record stream into PERF_RECORD_COMPRESSED
//     frames (zstd). There is nothing in .NET's BCL that decompresses zstd, so
//     this is DETECTED and reported rather than half-read: a compressed frame
//     walked as if it were a record stream produces record types and sizes
//     that are pure noise, and the walk then either overruns or invents
//     millions of records.
//
//   - The trailing sample_id block on non-sample records (present whenever
//     attr.sample_id_all is set, which perf always sets) means a COMM's
//     `comm[]` and an MMAP2's `filename[]` do NOT run to the end of their
//     record. Reading them to the end appends the raw bytes of a pid and a
//     timestamp to the string.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.Perf.PerfData {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;
using System.IO;

using DotnetInsights.Perf.Tracepoints;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public readonly struct PerfReadResult
{
    public readonly bool Success;
    public readonly string ErrorMessage;

    private PerfReadResult(bool success, string errorMessage)
    {
        this.Success = success;
        this.ErrorMessage = errorMessage;
    }

    public static PerfReadResult Ok()
    {
        return new PerfReadResult(true, null);
    }

    public static PerfReadResult Fail(string errorMessage)
    {
        return new PerfReadResult(false, errorMessage);
    }
}

// One PERF_RECORD_MMAP2: a module mapped into a process at a known address.
// This is the whole of what a perf capture says about where code lives - there
// is no symbol table in the file for anything but JIT'd code, so every native
// frame is resolved by finding its mapping here and then asking a symbol
// source about that module.
public struct PerfMapping
{
    public int ProcessId;
    public int ThreadId;
    public long StartAddress;
    public long Length;

    // The mapping's offset into the module FILE. Load-bearing for address
    // translation and routinely non-zero: a shared object's executable segment
    // is not mapped from the start of the file. See
    // nettraceParser/Symbols/NativeSymbolResolution.cs, whose header records
    // the full formula and the measurement that established it.
    public long FileOffset;
    public string FileName;

    // From the record's inline build id when `perf record --buildid-all` was
    // used, otherwise filled in afterward from the BUILD_ID feature section.
    // Empty when the capture carries neither.
    public string BuildId;

    public bool IsExecutable;
}

// Receives decoded records. An interface rather than delegates because
// PerfSample is a ref struct holding spans into the file buffer - the point of
// which is that a consumer that does not care about a given sample never
// copies it.
public interface IPerfRecordSink
{
    void OnSample(PerfEventAttr attr, ref PerfSample sample);

    void OnMapping(in PerfMapping mapping);

    void OnComm(int processId, int threadId, string comm);

    void OnFork(int processId, int parentProcessId, int threadId, int parentThreadId, ulong time);

    void OnExit(int processId, int threadId, ulong time);
}

public sealed class PerfDataFile
{
    private byte[] fileBytes;

    public readonly List<PerfEventAttr> Attrs = new List<PerfEventAttr>();

    // Sample id -> the event it belongs to. Empty for a single-event capture,
    // which needs no routing.
    public readonly Dictionary<ulong, PerfEventAttr> AttrsById = new Dictionary<ulong, PerfEventAttr>();

    // From the BUILD_ID feature section: module path -> hex build id. Merged
    // into the mappings after the record walk, because the feature section is
    // stored AFTER the data section and so is not available while walking it.
    public readonly Dictionary<string, string> BuildIdsByFileName = new Dictionary<string, string>(StringComparer.Ordinal);

    public string HostName = string.Empty;
    public string OsRelease = string.Empty;
    public string Architecture = string.Empty;
    public string PerfVersion = string.Empty;
    public string CommandLine = string.Empty;

    public long SampleRecordCount;
    public long TotalRecordCount;
    public long LostRecordCount;
    public long UnknownRecordCount;

    // Which types went unrecognised, not merely how many. A count alone
    // cannot distinguish "two harmless perf-internal records" from "every
    // record of the type carrying the data you came for".
    public readonly Dictionary<uint, long> UnknownRecordCountsByType = new Dictionary<uint, long>();

    // Records whose declared size did not match what the decode consumed.
    // Counted rather than fatal - one malformed record in a 35M-record capture
    // should not lose the other 35M - but surfaced, because a nonzero value
    // here means some layout assumption is wrong.
    public long MalformedRecordCount;

    public bool HasTracingData;

    // The ftrace format descriptions and the recording kernel's kallsyms, from
    // the TRACING_DATA section. Null unless the capture recorded at least one
    // tracepoint - a plain `perf record -e cpu-clock` has no reason to carry
    // either.
    public TraceEventFormats TraceFormats;

    public long FileSizeBytes => this.fileBytes != null ? this.fileBytes.LongLength : 0;

    public PerfEventAttr FindAttrForSample(ulong sampleId)
    {
        if (this.Attrs.Count == 1)
        {
            return this.Attrs[0];
        }

        PerfEventAttr attr;
        if (this.AttrsById.TryGetValue(sampleId, out attr))
        {
            return attr;
        }

        return null;
    }

    public PerfReadResult Read(string filePath, IPerfRecordSink sink)
    {
        if (!File.Exists(filePath))
        {
            return PerfReadResult.Fail("No such file: " + filePath);
        }

        this.fileBytes = File.ReadAllBytes(filePath);

        if (this.fileBytes.Length < PerfFormat.FileHeaderSize)
        {
            return PerfReadResult.Fail("File is too small to be a perf.data (" + this.fileBytes.Length.ToString() + " bytes).");
        }

        ReadOnlySpan<byte> whole = this.fileBytes;
        PerfSpanReader headerReader = new PerfSpanReader(whole);

        ulong magic = headerReader.ReadUInt64();
        if (magic == PerfFormat.Magic2Swapped)
        {
            return PerfReadResult.Fail("This capture was written by a big-endian machine. Cross-endian perf.data is not supported; convert it with `perf inject` on a host of the recording architecture.");
        }

        if (magic != PerfFormat.Magic2)
        {
            // The v1 container ("PERFFILE") and anything else.
            return PerfReadResult.Fail("Not a perf.data file (bad magic). Only the PERFILE2 container written by perf since 2012 is supported.");
        }

        // header.size, then attr_size.
        headerReader.Skip(8);
        long attrEntrySize = (long)headerReader.ReadUInt64();

        long attrsOffset = (long)headerReader.ReadUInt64();
        long attrsSize = (long)headerReader.ReadUInt64();
        long dataOffset = (long)headerReader.ReadUInt64();
        long dataSize = (long)headerReader.ReadUInt64();

        // event_types: unused since perf dropped it in favour of EVENT_DESC.
        headerReader.Skip(PerfFormat.FileSectionSize);

        ulong[] featureFlags = new ulong[4];
        for (int flagIndex = 0; flagIndex < featureFlags.Length; ++flagIndex)
        {
            featureFlags[flagIndex] = headerReader.ReadUInt64();
        }

        if (dataOffset < 0 || dataSize < 0 || dataOffset + dataSize > this.fileBytes.LongLength)
        {
            return PerfReadResult.Fail("Capture is truncated: its header declares a data section of " + dataSize.ToString() + " bytes at offset " + dataOffset.ToString() + ", past the end of a " + this.fileBytes.LongLength.ToString() + "-byte file.");
        }

        if (IsFeatureSet(featureFlags, PerfFormat.FeatureCompressed))
        {
            return PerfReadResult.Fail("This capture is zstd-compressed (`perf record -z`), which this reader cannot decompress. Re-record without -z, or run `perf inject -i <file> -o <uncompressed>` first.");
        }

        this.HasTracingData = IsFeatureSet(featureFlags, PerfFormat.FeatureTracingData);

        PerfReadResult attrResult = this.ReadAttrs(whole, attrsOffset, attrsSize, attrEntrySize);
        if (!attrResult.Success)
        {
            return attrResult;
        }

        // Feature sections live after the data section and are read BEFORE the
        // record walk on purpose: EVENT_DESC supplies the event names and the
        // id->attr routing that the walk needs, and BUILD_ID supplies build
        // ids for mappings recorded without --buildid-all.
        this.ReadFeatureSections(whole, dataOffset + dataSize, featureFlags);

        return this.WalkRecords(whole, dataOffset, dataSize, sink);
    }

    private static bool IsFeatureSet(ulong[] featureFlags, int featureBit)
    {
        int wordIndex = featureBit / 64;
        if (wordIndex >= featureFlags.Length)
        {
            return false;
        }

        return (featureFlags[wordIndex] & (1UL << (featureBit % 64))) != 0;
    }

    private PerfReadResult ReadAttrs(ReadOnlySpan<byte> whole, long attrsOffset, long attrsSize, long attrEntrySize)
    {
        if (attrEntrySize <= PerfFormat.FileSectionSize)
        {
            return PerfReadResult.Fail("Capture declares an implausible attribute entry size of " + attrEntrySize.ToString() + " bytes.");
        }

        if (attrsOffset < 0 || attrsSize < 0 || attrsOffset + attrsSize > whole.Length)
        {
            return PerfReadResult.Fail("Capture is truncated: its attribute section runs past the end of the file.");
        }

        int entryCount = (int)(attrsSize / attrEntrySize);
        for (int entryIndex = 0; entryIndex < entryCount; ++entryIndex)
        {
            long entryOffset = attrsOffset + (entryIndex * attrEntrySize);
            PerfSpanReader reader = new PerfSpanReader(whole.Slice((int)entryOffset, (int)attrEntrySize));

            PerfEventAttr attr = PerfEventAttr.Decode(ref reader);

            // Each entry ends with a perf_file_section naming the ids assigned
            // to this event. A single-event capture normally has none.
            reader.Position = (int)(attrEntrySize - PerfFormat.FileSectionSize);
            long idsOffset = (long)reader.ReadUInt64();
            long idsSize = (long)reader.ReadUInt64();

            if (idsSize > 0 && idsOffset >= 0 && idsOffset + idsSize <= whole.Length)
            {
                PerfSpanReader idReader = new PerfSpanReader(whole.Slice((int)idsOffset, (int)idsSize));
                while (!idReader.AtEnd)
                {
                    ulong id = idReader.ReadUInt64();
                    if (idReader.Overran)
                    {
                        break;
                    }

                    attr.Ids.Add(id);
                    this.AttrsById[id] = attr;
                }
            }

            this.Attrs.Add(attr);
        }

        if (this.Attrs.Count == 0)
        {
            return PerfReadResult.Fail("Capture declares no events at all - nothing to decode.");
        }

        return PerfReadResult.Ok();
    }

    private void ReadFeatureSections(ReadOnlySpan<byte> whole, long featureTableOffset, ulong[] featureFlags)
    {
        // One perf_file_section per set feature bit, in ascending bit order.
        List<int> presentFeatures = new List<int>();
        for (int featureBit = PerfFormat.FeatureTracingData; featureBit < PerfFormat.FeatureBitCount; ++featureBit)
        {
            if (IsFeatureSet(featureFlags, featureBit))
            {
                presentFeatures.Add(featureBit);
            }
        }

        long tableSize = (long)presentFeatures.Count * PerfFormat.FileSectionSize;
        if (featureTableOffset < 0 || featureTableOffset + tableSize > whole.Length)
        {
            // A capture truncated before its feature table still has usable
            // records; the names and build ids are a bonus, not a requirement.
            return;
        }

        PerfSpanReader tableReader = new PerfSpanReader(whole.Slice((int)featureTableOffset, (int)tableSize));

        for (int featureIndex = 0; featureIndex < presentFeatures.Count; ++featureIndex)
        {
            long sectionOffset = (long)tableReader.ReadUInt64();
            long sectionSize = (long)tableReader.ReadUInt64();

            if (sectionSize <= 0 || sectionOffset < 0 || sectionOffset + sectionSize > whole.Length)
            {
                continue;
            }

            ReadOnlySpan<byte> section = whole.Slice((int)sectionOffset, (int)sectionSize);

            switch (presentFeatures[featureIndex])
            {
                case PerfFormat.FeatureBuildId:
                    this.ReadBuildIdSection(section);
                    break;

                case PerfFormat.FeatureEventDesc:
                    this.ReadEventDescSection(section);
                    break;

                case PerfFormat.FeatureTracingData:
                {
                    string tracingError;
                    this.TraceFormats = TraceEventFormats.TryParse(section, out tracingError);
                    break;
                }

                case PerfFormat.FeatureHostname:
                    this.HostName = ReadFeatureString(section);
                    break;

                case PerfFormat.FeatureOsRelease:
                    this.OsRelease = ReadFeatureString(section);
                    break;

                case PerfFormat.FeatureArch:
                    this.Architecture = ReadFeatureString(section);
                    break;

                case PerfFormat.FeatureVersion:
                    this.PerfVersion = ReadFeatureString(section);
                    break;

                case PerfFormat.FeatureCmdline:
                    this.CommandLine = ReadCommandLineSection(section);
                    break;

                default:
                    break;
            }
        }
    }

    // Every string feature is a u32 length followed by that many bytes, the
    // length already padded (perf aligns to 64) - so the declared length is
    // not the string's length and the NUL inside it is what terminates it.
    private static string ReadFeatureString(ReadOnlySpan<byte> section)
    {
        PerfSpanReader reader = new PerfSpanReader(section);
        int length = (int)reader.ReadUInt32();
        if (length <= 0 || length > reader.Remaining)
        {
            return string.Empty;
        }

        return reader.ReadFixedString(length);
    }

    private static string ReadCommandLineSection(ReadOnlySpan<byte> section)
    {
        PerfSpanReader reader = new PerfSpanReader(section);
        uint argumentCount = reader.ReadUInt32();

        List<string> arguments = new List<string>();
        for (uint argumentIndex = 0; argumentIndex < argumentCount; ++argumentIndex)
        {
            int length = (int)reader.ReadUInt32();
            if (length <= 0 || length > reader.Remaining)
            {
                break;
            }

            arguments.Add(reader.ReadFixedString(length));
        }

        return string.Join(" ", arguments);
    }

    private void ReadBuildIdSection(ReadOnlySpan<byte> section)
    {
        PerfSpanReader reader = new PerfSpanReader(section);

        while (reader.Remaining > PerfFormat.RecordHeaderSize)
        {
            int recordStart = reader.Position;

            reader.Skip(4);
            ushort misc = reader.ReadUInt16();
            int recordSize = reader.ReadUInt16();

            if (recordSize <= PerfFormat.RecordHeaderSize || recordStart + recordSize > section.Length)
            {
                break;
            }

            // pid, then the build id field. That field is 24 bytes wide but is
            // NOT a 24-byte identifier: it is 20 bytes of SHA-1, then a `size`
            // byte, then 3 reserved bytes. Reading all 24 and trimming trailing
            // zeros yields a 21-byte id ending in 0x14 - the size byte, whose
            // value is 20 - which looks like an ordinary build id, renders as
            // 42 plausible hex characters, and matches nothing on any symbol
            // server. Observed on every module of the reference capture before
            // this was split out.
            reader.Skip(4);
            ReadOnlySpan<byte> buildIdBytes = reader.ReadBytes(20);
            int declaredSize = reader.ReadUInt8();
            reader.Skip(3);

            if ((misc & PerfFormat.MiscBuildIdSize) != 0 && declaredSize > 0 && declaredSize <= buildIdBytes.Length)
            {
                buildIdBytes = buildIdBytes.Slice(0, declaredSize);
            }

            int fileNameLength = recordStart + recordSize - reader.Position;
            string fileName = reader.ReadFixedString(fileNameLength);

            if (fileName.Length > 0)
            {
                string buildId = FormatBuildId(buildIdBytes);
                if (buildId.Length > 0)
                {
                    this.BuildIdsByFileName[fileName] = buildId;
                }
            }

            reader.Position = recordStart + recordSize;
        }
    }

    private void ReadEventDescSection(ReadOnlySpan<byte> section)
    {
        PerfSpanReader reader = new PerfSpanReader(section);

        uint eventCount = reader.ReadUInt32();
        uint attrSize = reader.ReadUInt32();

        if (attrSize == 0)
        {
            return;
        }

        for (uint eventIndex = 0; eventIndex < eventCount; ++eventIndex)
        {
            int attrStart = reader.Position;
            if (attrStart + (int)attrSize > section.Length)
            {
                return;
            }

            PerfEventAttr described = PerfEventAttr.Decode(ref reader);
            reader.Position = attrStart + (int)attrSize;

            uint idCount = reader.ReadUInt32();

            int nameLength = (int)reader.ReadUInt32();
            string eventName = string.Empty;
            if (nameLength > 0 && nameLength <= reader.Remaining)
            {
                eventName = reader.ReadFixedString(nameLength);
            }

            // Match the described event back to the attr already decoded from
            // the attrs section. Matching on the ids is exact where they
            // exist; a single-event capture assigns none, and there is exactly
            // one candidate.
            PerfEventAttr target = null;
            List<ulong> ids = new List<ulong>();

            for (uint idIndex = 0; idIndex < idCount; ++idIndex)
            {
                ulong id = reader.ReadUInt64();
                if (reader.Overran)
                {
                    break;
                }

                ids.Add(id);

                PerfEventAttr candidate;
                if (target == null && this.AttrsById.TryGetValue(id, out candidate))
                {
                    target = candidate;
                }
            }

            if (target == null && this.Attrs.Count == 1)
            {
                target = this.Attrs[0];
            }

            if (target == null)
            {
                target = this.FindAttrByTypeAndConfig(described.Type, described.Config);
            }

            if (target != null && eventName.Length > 0)
            {
                target.Name = eventName;

                // EVENT_DESC is also the only place a single-event capture's
                // ids appear at all in some perf versions; register any that
                // are not already routed.
                for (int idIndex = 0; idIndex < ids.Count; ++idIndex)
                {
                    if (!this.AttrsById.ContainsKey(ids[idIndex]))
                    {
                        this.AttrsById[ids[idIndex]] = target;
                        target.Ids.Add(ids[idIndex]);
                    }
                }
            }
        }
    }

    private PerfEventAttr FindAttrByTypeAndConfig(uint type, ulong config)
    {
        for (int attrIndex = 0; attrIndex < this.Attrs.Count; ++attrIndex)
        {
            PerfEventAttr candidate = this.Attrs[attrIndex];
            if (candidate.Type == type && candidate.Config == config)
            {
                return candidate;
            }
        }

        return null;
    }

    private PerfReadResult WalkRecords(ReadOnlySpan<byte> whole, long dataOffset, long dataSize, IPerfRecordSink sink)
    {
        // Any attr will do for the sample_id layout - perf requires every
        // event in one file to agree on it, which is what makes routing a
        // record to its event possible at all.
        PerfEventAttr referenceAttr = this.Attrs[0];
        int trailingIdSize = PerfSampleLayout.SampleIdSizeBytes(referenceAttr);

        long position = dataOffset;
        long end = dataOffset + dataSize;

        while (position + PerfFormat.RecordHeaderSize <= end)
        {
            PerfSpanReader header = new PerfSpanReader(whole.Slice((int)position, PerfFormat.RecordHeaderSize));
            uint recordType = header.ReadUInt32();
            ushort misc = header.ReadUInt16();
            int recordSize = header.ReadUInt16();

            if (recordSize < PerfFormat.RecordHeaderSize || position + recordSize > end)
            {
                // A record whose declared size runs past the section is the
                // end of anything trustworthy - stop rather than resync, since
                // a resync would be guessing at where the next record starts.
                this.MalformedRecordCount++;
                break;
            }

            ++this.TotalRecordCount;

            ReadOnlySpan<byte> body = whole.Slice((int)position + PerfFormat.RecordHeaderSize, recordSize - PerfFormat.RecordHeaderSize);

            switch (recordType)
            {
                case PerfFormat.RecordSample:
                    this.HandleSample(body, referenceAttr, sink);
                    break;

                case PerfFormat.RecordMmap2:
                    HandleMmap2(body, misc, trailingIdSize, sink);
                    break;

                case PerfFormat.RecordMmap:
                    HandleMmap(body, trailingIdSize, sink);
                    break;

                case PerfFormat.RecordComm:
                    HandleComm(body, trailingIdSize, sink);
                    break;

                case PerfFormat.RecordFork:
                    HandleForkOrExit(body, sink, true);
                    break;

                case PerfFormat.RecordExit:
                    HandleForkOrExit(body, sink, false);
                    break;

                case PerfFormat.RecordLost:
                case PerfFormat.RecordLostSamples:
                    ++this.LostRecordCount;
                    break;

                case PerfFormat.RecordCompressed:
                    return PerfReadResult.Fail("This capture contains zstd-compressed record frames (`perf record -z`). Re-record without -z, or run `perf inject` first.");

                case PerfFormat.RecordFinishedRound:
                case PerfFormat.RecordFinishedInit:
                case PerfFormat.RecordIdIndex:
                case PerfFormat.RecordThreadMap:
                case PerfFormat.RecordCpuMap:
                case PerfFormat.RecordTimeConv:
                case PerfFormat.RecordHeaderFeature:
                case PerfFormat.RecordKsymbol:
                case PerfFormat.RecordBpfEvent:
                case PerfFormat.RecordCgroup:
                case PerfFormat.RecordTextPoke:
                case PerfFormat.RecordITraceStart:
                case PerfFormat.RecordNamespaces:
                case PerfFormat.RecordEventUpdate:
                case PerfFormat.RecordStatConfig:
                case PerfFormat.RecordStat:
                case PerfFormat.RecordStatRound:
                case PerfFormat.RecordAuxtraceInfo:
                case PerfFormat.RecordAuxtraceError:
                    break;

                default:
                    ++this.UnknownRecordCount;
                    long existingForType;
                    this.UnknownRecordCountsByType.TryGetValue(recordType, out existingForType);
                    this.UnknownRecordCountsByType[recordType] = existingForType + 1;
                    break;
            }

            position += recordSize;
        }

        return PerfReadResult.Ok();
    }

    private void HandleSample(ReadOnlySpan<byte> body, PerfEventAttr referenceAttr, IPerfRecordSink sink)
    {
        PerfEventAttr attr = referenceAttr;

        if (this.Attrs.Count > 1)
        {
            ulong sampleId;
            if (PerfSampleLayout.TryReadSampleId(body, referenceAttr, out sampleId))
            {
                PerfEventAttr routed = this.FindAttrForSample(sampleId);
                if (routed != null)
                {
                    attr = routed;
                }
            }
        }

        PerfSample sample = new PerfSample();
        if (!PerfSampleLayout.TryParse(body, attr, ref sample))
        {
            ++this.MalformedRecordCount;
            return;
        }

        ++this.SampleRecordCount;
        sink.OnSample(attr, ref sample);
    }

    private static void HandleMmap2(ReadOnlySpan<byte> body, ushort misc, int trailingIdSize, IPerfRecordSink sink)
    {
        PerfSpanReader reader = new PerfSpanReader(body);

        PerfMapping mapping = new PerfMapping();
        mapping.ProcessId = reader.ReadInt32();
        mapping.ThreadId = reader.ReadInt32();
        mapping.StartAddress = reader.ReadInt64();
        mapping.Length = reader.ReadInt64();
        mapping.FileOffset = reader.ReadInt64();

        if ((misc & PerfFormat.MiscMmapBuildId) != 0)
        {
            // The build id replaces the {maj, min, ino, ino_generation}
            // identity block - same 24 bytes, completely different meaning.
            int buildIdSize = reader.ReadUInt8();
            reader.Skip(3);
            ReadOnlySpan<byte> buildIdBytes = reader.ReadBytes(20);

            if (buildIdSize > 0 && buildIdSize <= buildIdBytes.Length)
            {
                mapping.BuildId = FormatBuildId(buildIdBytes.Slice(0, buildIdSize));
            }
        }
        else
        {
            // maj, min, ino, ino_generation
            reader.Skip(24);
        }

        uint protection = reader.ReadUInt32();
        reader.Skip(4);

        // PROT_EXEC. A capture is full of mappings that can never hold a
        // sampled instruction (the heap, every data segment, every stack); a
        // non-executable mapping in the address table is a chance to resolve a
        // frame to the wrong module, never to the right one.
        mapping.IsExecutable = (protection & 0x4) != 0;

        int nameLength = reader.Remaining - trailingIdSize;
        mapping.FileName = nameLength > 0 ? reader.ReadFixedString(nameLength) : string.Empty;

        if (mapping.BuildId == null)
        {
            mapping.BuildId = string.Empty;
        }

        sink.OnMapping(in mapping);
    }

    private static void HandleMmap(ReadOnlySpan<byte> body, int trailingIdSize, IPerfRecordSink sink)
    {
        PerfSpanReader reader = new PerfSpanReader(body);

        PerfMapping mapping = new PerfMapping();
        mapping.ProcessId = reader.ReadInt32();
        mapping.ThreadId = reader.ReadInt32();
        mapping.StartAddress = reader.ReadInt64();
        mapping.Length = reader.ReadInt64();
        mapping.FileOffset = reader.ReadInt64();
        mapping.BuildId = string.Empty;

        // The pre-MMAP2 record carries no protection bits at all. Treating it
        // as executable is the safe direction: perf only synthesizes MMAP for
        // executable mappings in the first place.
        mapping.IsExecutable = true;

        int nameLength = reader.Remaining - trailingIdSize;
        mapping.FileName = nameLength > 0 ? reader.ReadFixedString(nameLength) : string.Empty;

        sink.OnMapping(in mapping);
    }

    private static void HandleComm(ReadOnlySpan<byte> body, int trailingIdSize, IPerfRecordSink sink)
    {
        PerfSpanReader reader = new PerfSpanReader(body);

        int processId = reader.ReadInt32();
        int threadId = reader.ReadInt32();

        int nameLength = reader.Remaining - trailingIdSize;
        string comm = nameLength > 0 ? reader.ReadFixedString(nameLength) : string.Empty;

        sink.OnComm(processId, threadId, comm);
    }

    private static void HandleForkOrExit(ReadOnlySpan<byte> body, IPerfRecordSink sink, bool isFork)
    {
        PerfSpanReader reader = new PerfSpanReader(body);

        int processId = reader.ReadInt32();
        int parentProcessId = reader.ReadInt32();
        int threadId = reader.ReadInt32();
        int parentThreadId = reader.ReadInt32();
        ulong time = reader.ReadUInt64();

        if (isFork)
        {
            sink.OnFork(processId, parentProcessId, threadId, parentThreadId, time);
            return;
        }

        sink.OnExit(processId, threadId, time);
    }

    // A mapping's build id, from whichever of the two places this capture put
    // it. Consult this rather than PerfMapping.BuildId: the inline form only
    // exists when the capture used `perf record --buildid-all`, and even then
    // perf 6.8 was observed writing the ids ONLY into the BUILD_ID feature
    // section - all 10 mappings of the reference capture came through the walk
    // with an empty inline id despite --buildid-all being passed. Since the
    // feature section is stored after the data section, it cannot be merged
    // during the walk, so the lookup has to happen here, afterward.
    public string BuildIdForModule(string fileName, string inlineBuildId)
    {
        if (!string.IsNullOrEmpty(inlineBuildId))
        {
            return inlineBuildId;
        }

        if (string.IsNullOrEmpty(fileName))
        {
            return string.Empty;
        }

        string buildId;
        if (this.BuildIdsByFileName.TryGetValue(fileName, out buildId))
        {
            return buildId;
        }

        return string.Empty;
    }

    private static string FormatBuildId(ReadOnlySpan<byte> buildIdBytes)
    {
        // Trailing zero bytes are padding in the 24-byte field, not part of a
        // 20-byte SHA-1. A build id rendered with them appended matches
        // nothing on any symbol server.
        int length = buildIdBytes.Length;
        while (length > 0 && buildIdBytes[length - 1] == 0)
        {
            --length;
        }

        if (length == 0)
        {
            return string.Empty;
        }

        return Convert.ToHexStringLower(buildIdBytes.Slice(0, length));
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.Perf.PerfData)

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////
