////////////////////////////////////////////////////////////////////////////////
// Module: BinaryCaptureReader.cs
//
// Notes:
// Reads the container documented in BinaryCaptureFormat.cs.
//
// The extension does NOT use this - the webview decodes the same bytes in
// JavaScript, which is the entire point of the format. This exists so the
// writer has a first-class reader to be tested against in the same language,
// and so the format has one executable specification rather than two
// hand-kept-in-sync implementations with no cross-check. The JS reader is
// then diffed against this one's output via the --json oracle.
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

public readonly struct BinaryCaptureSection
{
    public readonly BinaryCaptureSectionId SectionId;
    public readonly uint SectionVersion;
    public readonly byte[] Payload;

    public BinaryCaptureSection(BinaryCaptureSectionId sectionId, uint sectionVersion, byte[] payload)
    {
        this.SectionId = sectionId;
        this.SectionVersion = sectionVersion;
        this.Payload = payload;
    }
}

public static class BinaryCaptureReader
{
    // Returns false rather than throwing on a malformed file - callers here
    // are diagnostics and tests, and this project's own convention is to
    // return errors on the stack instead of using exceptions for recoverable
    // conditions.
    public static bool TryRead(string filePath, out List<BinaryCaptureSection> sections, out string error)
    {
        sections = null;
        error = null;

        byte[] bytes = File.ReadAllBytes(filePath);

        if (bytes.Length < BinaryCaptureFormat.HeaderBytes)
        {
            error = $"File is {bytes.Length} bytes, shorter than the {BinaryCaptureFormat.HeaderBytes}-byte header.";
            return false;
        }

        for (int magicIndex = 0; magicIndex < BinaryCaptureFormat.Magic.Length; ++magicIndex)
        {
            if (bytes[magicIndex] != BinaryCaptureFormat.Magic[magicIndex])
            {
                error = "File does not start with the expected container magic.";
                return false;
            }
        }

        uint formatVersion = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8));

        if (formatVersion != BinaryCaptureFormat.FormatVersion)
        {
            error = $"Container format version {formatVersion} is not the expected {BinaryCaptureFormat.FormatVersion}.";
            return false;
        }

        uint sectionCount = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12));
        long sectionTableOffset = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(16));

        long sectionTableEnd = sectionTableOffset + ((long)sectionCount * BinaryCaptureFormat.SectionTableEntryBytes);

        if (sectionTableOffset < BinaryCaptureFormat.HeaderBytes || sectionTableEnd > bytes.Length)
        {
            error = $"Section table at {sectionTableOffset} with {sectionCount} entries does not fit inside a {bytes.Length}-byte file.";
            return false;
        }

        List<BinaryCaptureSection> readSections = new List<BinaryCaptureSection>((int)sectionCount);

        for (uint sectionIndex = 0; sectionIndex < sectionCount; ++sectionIndex)
        {
            int entryOffset = (int)(sectionTableOffset + (sectionIndex * BinaryCaptureFormat.SectionTableEntryBytes));

            uint sectionId = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(entryOffset));
            uint sectionVersion = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(entryOffset + 4));
            long payloadOffset = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(entryOffset + 8));
            long payloadLength = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(entryOffset + 16));

            if (payloadOffset < BinaryCaptureFormat.HeaderBytes || payloadLength < 0 || payloadOffset + payloadLength > bytes.Length)
            {
                error = $"Section {sectionId} payload [{payloadOffset}, {payloadOffset + payloadLength}) lies outside a {bytes.Length}-byte file.";
                return false;
            }

            if ((payloadOffset % BinaryCaptureFormat.PayloadAlignmentBytes) != 0)
            {
                error = $"Section {sectionId} payload offset {payloadOffset} is not {BinaryCaptureFormat.PayloadAlignmentBytes}-byte aligned.";
                return false;
            }

            byte[] payload = new byte[payloadLength];
            Array.Copy(bytes, payloadOffset, payload, 0, payloadLength);

            readSections.Add(new BinaryCaptureSection((BinaryCaptureSectionId)sectionId, sectionVersion, payload));
        }

        sections = readSections;
        return true;
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.Binary)

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////
