////////////////////////////////////////////////////////////////////////////////
// Module: BinaryCaptureWriter.cs
//
// Notes:
// Writes the container documented in BinaryCaptureFormat.cs. Usage:
//
//     using (BinaryCaptureWriter captureWriter = BinaryCaptureWriter.Create(path))
//     {
//         captureWriter.BeginSection(BinaryCaptureSectionId.CpuSampleTimeline, 1);
//         captureWriter.WriteInt32(...);
//         captureWriter.EndSection();
//     }
//
// Payloads stream straight to the FileStream as they are written; the section
// table is appended afterwards and the two header fields that depend on it
// are patched by seeking back to offset 0 on Dispose. Nothing is buffered in
// memory, which matters because some sections (allocation ticks, CPU samples)
// run to tens of megabytes on a real capture.
//
// Scalar writes go through a small stack buffer rather than BinaryWriter so
// the endianness is explicit at every call site (BinaryPrimitives) instead of
// inherited from the host - a reader on the other side of this file is
// JavaScript reading a DataView, where the endianness argument is mandatory
// and easy to get silently wrong.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.NetTrace.Binary {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public sealed class BinaryCaptureWriter : IDisposable
{
    private struct SectionEntry
    {
        public uint SectionId;
        public uint SectionVersion;
        public long PayloadOffset;
        public long PayloadLength;
    }

    private readonly FileStream stream;
    private readonly List<SectionEntry> sections;

    private bool sectionOpen;
    private uint openSectionId;
    private uint openSectionVersion;
    private long openSectionStart;

    private BinaryCaptureWriter(FileStream stream)
    {
        this.stream = stream;
        this.sections = new List<SectionEntry>();
    }

    public static BinaryCaptureWriter Create(string filePath)
    {
        FileStream stream = new FileStream(filePath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);

        // Header is patched on Dispose once sectionCount/sectionTableOffset
        // are known - reserve its space so payloads start after it.
        stream.Write(new byte[BinaryCaptureFormat.HeaderBytes], 0, BinaryCaptureFormat.HeaderBytes);

        return new BinaryCaptureWriter(stream);
    }

    public void BeginSection(BinaryCaptureSectionId sectionId, uint sectionVersion)
    {
        if (this.sectionOpen)
        {
            throw new InvalidOperationException($"Section {this.openSectionId} is still open; call EndSection before beginning {sectionId}.");
        }

        AlignToPayloadBoundary();

        this.sectionOpen = true;
        this.openSectionId = (uint)sectionId;
        this.openSectionVersion = sectionVersion;
        this.openSectionStart = this.stream.Position;
    }

    public void EndSection()
    {
        if (!this.sectionOpen)
        {
            throw new InvalidOperationException("EndSection called with no section open.");
        }

        SectionEntry entry = new SectionEntry();
        entry.SectionId = this.openSectionId;
        entry.SectionVersion = this.openSectionVersion;
        entry.PayloadOffset = this.openSectionStart;
        entry.PayloadLength = this.stream.Position - this.openSectionStart;

        this.sections.Add(entry);
        this.sectionOpen = false;
    }

    public void WriteInt32(int value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, value);
        this.stream.Write(buffer);
    }

    public void WriteUInt32(uint value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        this.stream.Write(buffer);
    }

    public void WriteInt64(long value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(buffer, value);
        this.stream.Write(buffer);
    }

    public void WriteDouble(double value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(double)];
        BinaryPrimitives.WriteDoubleLittleEndian(buffer, value);
        this.stream.Write(buffer);
    }

    // Bulk int32 write. Goes through one pooled-size scratch buffer rather
    // than a WriteInt32 call per element: a CPU timeline's methodSelfByBucket
    // alone is methodCount * bucketCount entries, and the per-call Span
    // setup dominates at that size. The buffer is a fixed 4KB (well under
    // this project's own stackalloc ceiling, but heap-allocated once per
    // call rather than per element, and never inside the loop).
    public void WriteInt32Array(ReadOnlySpan<int> values)
    {
        const int ValuesPerChunk = 1024;

        byte[] scratch = new byte[ValuesPerChunk * sizeof(int)];
        int valueIndex = 0;

        while (valueIndex < values.Length)
        {
            int chunkLength = values.Length - valueIndex;

            if (chunkLength > ValuesPerChunk)
            {
                chunkLength = ValuesPerChunk;
            }

            for (int offsetInChunk = 0; offsetInChunk < chunkLength; ++offsetInChunk)
            {
                BinaryPrimitives.WriteInt32LittleEndian(scratch.AsSpan(offsetInChunk * sizeof(int)), values[valueIndex + offsetInChunk]);
            }

            this.stream.Write(scratch, 0, chunkLength * sizeof(int));
            valueIndex += chunkLength;
        }
    }

    private void AlignToPayloadBoundary()
    {
        int misalignment = (int)(this.stream.Position % BinaryCaptureFormat.PayloadAlignmentBytes);

        if (misalignment != 0)
        {
            int paddingBytes = BinaryCaptureFormat.PayloadAlignmentBytes - misalignment;
            Span<byte> padding = stackalloc byte[BinaryCaptureFormat.PayloadAlignmentBytes];
            padding.Clear();
            this.stream.Write(padding.Slice(0, paddingBytes));
        }
    }

    public void Dispose()
    {
        if (this.stream == null)
        {
            return;
        }

        if (this.sectionOpen)
        {
            throw new InvalidOperationException($"Section {this.openSectionId} was never closed with EndSection.");
        }

        AlignToPayloadBoundary();
        long sectionTableOffset = this.stream.Position;

        for (int sectionIndex = 0; sectionIndex < this.sections.Count; ++sectionIndex)
        {
            SectionEntry entry = this.sections[sectionIndex];
            WriteUInt32(entry.SectionId);
            WriteUInt32(entry.SectionVersion);
            WriteInt64(entry.PayloadOffset);
            WriteInt64(entry.PayloadLength);
        }

        this.stream.Seek(0, SeekOrigin.Begin);
        this.stream.Write(BinaryCaptureFormat.Magic, 0, BinaryCaptureFormat.Magic.Length);
        WriteUInt32(BinaryCaptureFormat.FormatVersion);
        WriteUInt32((uint)this.sections.Count);
        WriteInt64(sectionTableOffset);
        WriteInt64(0);

        this.stream.Dispose();
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.Binary)

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////
