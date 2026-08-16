////////////////////////////////////////////////////////////////////////////////
// Module: BinaryCaptureFormat.cs
//
// Notes:
// Wire format shared by BinaryCaptureWriter (C#) and the extension's own
// reader. This is the container that replaces nettraceParser's --json output
// as the path the VS Code extension consumes.
//
// WHY a binary container at all. The JSON pipeline serializes the same data
// three times before anything renders:
//
//   1. nettraceParser writes a JSON file (54MB on a real 3.01GB capture),
//   2. the extension host JSON.parses that whole file into an object graph,
//   3. the host JSON.stringifies nine separate sections of that graph back
//      into <script type="application/json"> blocks in the webview HTML
//      (GcSnapshotRenderer.ts), and
//   4. the webview JSON.parses all nine again (media/snapshotGcStats.js).
//
// Steps 2-4 are pure overhead - the host never inspects most of what it
// parses, it only re-emits it - and they run synchronously on the extension
// host's event loop, which is exactly why the progress bar has to reserve
// [80, 100] for host-side stages that cannot report finer progress (see
// NettraceProgress.ts). With this container the host passes a URI, and the
// webview fetches and decodes the bytes itself.
//
// LAYOUT. All integers little-endian (matching the existing ticks sidecar,
// and every platform this ships on - see pack.py's RIDs). Offsets are
// absolute from the start of the file.
//
//   Header, 32 bytes at offset 0:
//     0   byte[8]  magic, "DNIBIN\0\0"
//     8   uint32   formatVersion
//     12  uint32   sectionCount
//     16  uint64   sectionTableOffset
//     24  uint64   reserved (0)
//
//   Section table, sectionCount entries of 24 bytes at sectionTableOffset:
//     0   uint32   sectionId        (BinaryCaptureSectionId)
//     4   uint32   sectionVersion
//     8   uint64   payloadOffset
//     16  uint64   payloadLength
//
//   Payloads, each aligned to PayloadAlignmentBytes.
//
// The section table lives at the END rather than immediately after the
// header so the writer never has to know the section count (or reserve a
// fixed maximum) before it starts writing payloads - it streams payloads
// straight out, then appends the table and patches the two header fields.
//
// Payload alignment is 8 bytes so a reader can hand a section straight to a
// Float64Array/Int32Array view without copying it to a fresh, aligned
// buffer first - the whole point of the format.
//
// VERSIONING. formatVersion covers the container (header + table) only.
// Each section carries its OWN version so one payload's layout can change
// without invalidating the rest, which matters because sections are being
// migrated off JSON one at a time rather than in a single cut-over.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.NetTrace.Binary {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public static class BinaryCaptureFormat
{
    // "DNIBIN\0\0" - dotnet-insights binary. Deliberately 8 bytes so the
    // uint32s that follow land naturally aligned.
    public static readonly byte[] Magic = new byte[] { (byte)'D', (byte)'N', (byte)'I', (byte)'B', (byte)'I', (byte)'N', 0, 0 };

    public const uint FormatVersion = 1;

    public const int HeaderBytes = 32;
    public const int SectionTableEntryBytes = 24;

    // See this file's header comment: 8 so a reader can create a typed-array
    // view directly over a section rather than copying it into an aligned
    // buffer.
    public const int PayloadAlignmentBytes = 8;
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

// Stable numeric ids - never renumber an existing member, since a published
// binary carries these values. Gaps are grouped by producing subsystem so a
// new section slots in next to its siblings without disturbing anything.
public enum BinaryCaptureSectionId : uint
{
    // 1-19: CPU profile (Cpu/CpuProfileJsonExporter.cs).
    CpuSampleTimeline = 1,
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.Binary)

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////
