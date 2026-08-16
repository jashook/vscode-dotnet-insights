////////////////////////////////////////////////////////////////////////////////
// Module: BinaryCaptureTests.cs
//
// Notes:
// Coverage for the binary container (Binary/BinaryCaptureFormat.cs) that is
// replacing --json as the payload the VS Code extension consumes.
//
// Two distinct jobs here, and they fail for different reasons:
//
//   1. CONTAINER round-trip - header, section table, alignment, and the
//      writer's own "table goes last so the section count need not be known
//      up front" trick. These would break on a format change.
//
//   2. ORACLE DIFF - the encoded CpuSampleTimeline section is compared field
//      for field against the "sampleTimeline" object the JSON writer produces
//      from THE SAME run. This is the check that actually matters during the
//      migration: every section is being moved off JSON one at a time, and
//      --json is deliberately kept alive (see Program.cs) to be the reference
//      each new section is validated against. A binary encoder that silently
//      disagrees with the JSON it replaces is the entire failure mode this
//      migration has to avoid, and it is invisible to any test that only
//      checks the binary against itself.
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

using DotnetInsights.NetTrace.Binary;
using DotnetInsights.NetTrace.Cpu;
using DotnetInsights.NetTrace.Rundown;

using Xunit;

namespace DotnetInsights.NetTrace.Tests {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public class BinaryCaptureTests
{
    // One stack table for the whole class so the static Make* helpers
    // below can register stacks too - see TestStacks.cs.
    private static readonly TestStacks stacks = new TestStacks();

    private static string NewTempPath()
    {
        return Path.Combine(Path.GetTempPath(), $"nettraceParser-bin-{Guid.NewGuid():N}.bin");
    }

    [Fact]
    public void Writer_RoundTripsSections_WithAlignedPayloadsAndTrailingTable()
    {
        string path = NewTempPath();

        try
        {
            using (BinaryCaptureWriter captureWriter = BinaryCaptureWriter.Create(path))
            {
                // Deliberately an ODD payload length so the next section has
                // to be pushed to the next 8-byte boundary - the padding path
                // is otherwise never exercised.
                captureWriter.BeginSection(BinaryCaptureSectionId.CpuSampleTimeline, 1);
                captureWriter.WriteUInt32(0xAABBCCDDu);
                captureWriter.EndSection();

                captureWriter.BeginSection((BinaryCaptureSectionId)9999, 7);
                captureWriter.WriteDouble(1.5);
                captureWriter.WriteInt32Array(new int[] { 1, -2, 3 });
                captureWriter.EndSection();
            }

            List<BinaryCaptureSection> sections;
            string error;
            Assert.True(BinaryCaptureReader.TryRead(path, out sections, out error), error);
            Assert.Equal(2, sections.Count);

            Assert.Equal(BinaryCaptureSectionId.CpuSampleTimeline, sections[0].SectionId);
            Assert.Equal(1u, sections[0].SectionVersion);
            Assert.Equal(0xAABBCCDDu, BinaryPrimitives.ReadUInt32LittleEndian(sections[0].Payload));

            Assert.Equal((BinaryCaptureSectionId)9999, sections[1].SectionId);
            Assert.Equal(7u, sections[1].SectionVersion);
            Assert.Equal(1.5, BinaryPrimitives.ReadDoubleLittleEndian(sections[1].Payload));
            Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(sections[1].Payload.AsSpan(8)));
            Assert.Equal(-2, BinaryPrimitives.ReadInt32LittleEndian(sections[1].Payload.AsSpan(12)));
            Assert.Equal(3, BinaryPrimitives.ReadInt32LittleEndian(sections[1].Payload.AsSpan(16)));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Reader_RejectsAFileThatIsNotAContainer()
    {
        string path = NewTempPath();

        try
        {
            File.WriteAllText(path, "this is definitely not a container");

            List<BinaryCaptureSection> sections;
            string error;
            Assert.False(BinaryCaptureReader.TryRead(path, out sections, out error));
            Assert.NotNull(error);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static MethodSymbolTable MakeSymbolTable()
    {
        byte[] payload = new PayloadBuilder()
            .WriteAddress(1, 8)
            .WriteAddress(2, 8)
            .WriteAddress(0x1000, 8)
            .WriteInt32(0x100)
            .WriteInt32(0x06000001)
            .WriteInt32(0)
            .WriteUnicodeString("")
            .WriteUnicodeString("MethodA")
            .WriteUnicodeString("sig")
            .ToArray();

        List<EventRecord> events = new List<EventRecord>
        {
            new EventRecord("Microsoft-Windows-DotNETRuntimeRundown", eventName: null, ClrRundownEventIds.MethodDCStartVerbose, version: 1, timeStampRelativeQpc: 0, threadId: 0, stackIndex: StackTable.EmptyStackIndex, fields: null, payload, payloadOffset: 0, payload.Length),
        };

        return MethodSymbolTable.Build(events, pointerSize: 8, qpcFrequency: 0, referenceQpc: 0);
    }

    // The check that matters during the migration: the binary section and the
    // JSON it is replacing must describe the same timeline, from one run.
    [Fact]
    public void CpuSampleTimeline_BinaryEncodingMatchesTheJsonOracle()
    {
        int stack = stacks.Index(0x1010);
        List<SampleEvent> sampleEvents = new List<SampleEvent>();

        // Spread across a range wide enough to land in several distinct
        // buckets, so a bucketing disagreement between the two encoders
        // actually shows up rather than collapsing into one bucket.
        for (int sampleIndex = 0; sampleIndex < 250; ++sampleIndex)
        {
            sampleEvents.Add(new SampleEvent(sampleIndex * 4.0, threadId: 1, stack));
        }

        CpuProfileJsonExporter.SampleTimeline timeline;
        JsonDocument document;

        using (MemoryStream stream = new MemoryStream())
        {
            using (Utf8JsonWriter writer = new Utf8JsonWriter(stream))
            {
                timeline = CpuProfileJsonExporter.Write(writer, sampleEvents, stacks.Table, MakeSymbolTable());
            }

            document = JsonDocument.Parse(stream.ToArray());
        }

        Assert.NotNull(timeline);

        JsonElement jsonTimeline = document.RootElement.GetProperty("sampleTimeline");

        string path = NewTempPath();

        try
        {
            using (BinaryCaptureWriter captureWriter = BinaryCaptureWriter.Create(path))
            {
                CpuBinarySections.WriteSampleTimeline(captureWriter, timeline);
            }

            List<BinaryCaptureSection> sections;
            string error;
            Assert.True(BinaryCaptureReader.TryRead(path, out sections, out error), error);
            Assert.Single(sections);
            Assert.Equal(BinaryCaptureSectionId.CpuSampleTimeline, sections[0].SectionId);
            Assert.Equal(CpuBinarySections.SampleTimelineVersion, sections[0].SectionVersion);

            ReadOnlySpan<byte> payload = sections[0].Payload;

            Assert.Equal(jsonTimeline.GetProperty("minRelativeMSec").GetDouble(), BinaryPrimitives.ReadDoubleLittleEndian(payload));
            Assert.Equal(jsonTimeline.GetProperty("totalDurationMSec").GetDouble(), BinaryPrimitives.ReadDoubleLittleEndian(payload.Slice(8)));
            Assert.Equal(jsonTimeline.GetProperty("bucketDurationMSec").GetDouble(), BinaryPrimitives.ReadDoubleLittleEndian(payload.Slice(16)));

            int bucketCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(24));
            int methodCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(28));

            Assert.Equal(jsonTimeline.GetProperty("bucketCount").GetInt32(), bucketCount);

            JsonElement jsonSamplesByBucket = jsonTimeline.GetProperty("samplesByBucket");
            Assert.Equal(jsonSamplesByBucket.GetArrayLength(), bucketCount);

            for (int bucketIndex = 0; bucketIndex < bucketCount; ++bucketIndex)
            {
                int binaryValue = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(32 + (bucketIndex * sizeof(int))));
                Assert.Equal(jsonSamplesByBucket[bucketIndex].GetInt32(), binaryValue);
            }

            JsonElement jsonMethodSelfByBucket = jsonTimeline.GetProperty("methodSelfByBucket");
            Assert.Equal(jsonMethodSelfByBucket.GetArrayLength(), methodCount);

            // The binary form flattens the JSON's array-of-arrays into one
            // row-major block - this is where a row-stride mistake would show.
            int methodSelfBase = 32 + (bucketCount * sizeof(int));

            for (int methodIndex = 0; methodIndex < methodCount; ++methodIndex)
            {
                JsonElement jsonRow = jsonMethodSelfByBucket[methodIndex];
                Assert.Equal(jsonRow.GetArrayLength(), bucketCount);

                for (int bucketIndex = 0; bucketIndex < bucketCount; ++bucketIndex)
                {
                    int flatIndex = (methodIndex * bucketCount) + bucketIndex;
                    int binaryValue = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(methodSelfBase + (flatIndex * sizeof(int))));
                    Assert.Equal(jsonRow[bucketIndex].GetInt32(), binaryValue);
                }
            }

            // Guards the guard: a timeline where every bucket was zero would
            // satisfy every comparison above without proving anything.
            long totalSamplesInBinary = 0;
            for (int bucketIndex = 0; bucketIndex < bucketCount; ++bucketIndex)
            {
                totalSamplesInBinary += BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(32 + (bucketIndex * sizeof(int))));
            }

            Assert.Equal(sampleEvents.Count, totalSamplesInBinary);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.Tests)

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////
