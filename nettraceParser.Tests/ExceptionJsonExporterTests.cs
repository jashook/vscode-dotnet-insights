////////////////////////////////////////////////////////////////////////////////
// Module: ExceptionJsonExporterTests.cs
//
// Notes:
// Covers the "timeline" block added to ExceptionJsonExporter.Write for the
// Exceptions view's new timeline chart - the non-obvious rules worth pinning:
// timeline is null (not a zeroed object) whenever there's no real time range
// (zero events, or every event at the same RelativeMSec), countByBucket
// counts every exception regardless of whether its type is ranked into
// topTypes, and typeSelfByBucket (parallel to topTypes) sums back to exactly
// that type's own Count - the same invariant CpuProfileJsonExporterTests
// pins for methodSelfByBucket vs. hotMethods.
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;
using System.Text.Json;

using DotnetInsights.NetTrace.Exceptions;
using DotnetInsights.NetTrace.Rundown;

using Xunit;

namespace DotnetInsights.NetTrace.Tests {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public class ExceptionJsonExporterTests
{
    private static MethodSymbolTable MakeSymbolTable()
    {
        return MethodSymbolTable.Build(new List<EventRecord>(), pointerSize: 8, qpcFrequency: 0, referenceQpc: 0);
    }

    private static ExceptionEvent MakeEvent(double relativeMSec, string exceptionType, string message = "boom")
    {
        return new ExceptionEvent(default, relativeMSec, exceptionType, message, hResult: 0, ClrExceptionFlags.None, threadId: 1, Array.Empty<long>());
    }

    private static JsonDocument WriteAndParse(List<ExceptionEvent> exceptionEvents, MethodSymbolTable symbolTable)
    {
        using System.IO.MemoryStream stream = new System.IO.MemoryStream();
        using (Utf8JsonWriter writer = new Utf8JsonWriter(stream))
        {
            ExceptionJsonExporter.Write(writer, exceptionEvents, symbolTable);
        }

        return JsonDocument.Parse(stream.ToArray());
    }

    [Fact]
    public void Write_EmptyCapture_TimelineIsNull()
    {
        JsonDocument document = WriteAndParse(new List<ExceptionEvent>(), MakeSymbolTable());
        JsonElement root = document.RootElement;

        Assert.Equal(0, root.GetProperty("totalExceptionCount").GetInt32());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("timeline").ValueKind);
    }

    [Fact]
    public void Write_AllExceptionsAtSameRelativeMSec_TimelineIsNullNotZeroedObject()
    {
        List<ExceptionEvent> events = new List<ExceptionEvent>
        {
            MakeEvent(100.0, "System.InvalidOperationException"),
            MakeEvent(100.0, "System.InvalidOperationException"),
            MakeEvent(100.0, "System.ArgumentException"),
        };

        JsonDocument document = WriteAndParse(events, MakeSymbolTable());

        // totalDurationMSec == 0 here (max - min == 0) - same "no real span
        // to bucket" case ContentionJsonExporter treats as null, not a
        // one-bucket object.
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("timeline").ValueKind);
    }

    [Fact]
    public void Write_ExceptionsSpreadOverTime_TimelineShapeMatchesEventsAndTopTypes()
    {
        List<ExceptionEvent> events = new List<ExceptionEvent>
        {
            MakeEvent(0.0, "System.InvalidOperationException"),
            MakeEvent(10.0, "System.InvalidOperationException"),
            MakeEvent(20.0, "System.ArgumentException"),
            MakeEvent(30.0, "System.ArgumentException"),
            MakeEvent(40.0, "System.ArgumentException"),
        };

        JsonDocument document = WriteAndParse(events, MakeSymbolTable());
        JsonElement root = document.RootElement;
        JsonElement timeline = root.GetProperty("timeline");

        Assert.Equal(JsonValueKind.Object, timeline.ValueKind);
        Assert.Equal(0.0, timeline.GetProperty("minRelativeMSec").GetDouble());
        Assert.Equal(40.0, timeline.GetProperty("totalDurationMSec").GetDouble());

        int bucketCount = timeline.GetProperty("bucketCount").GetInt32();
        Assert.Equal(events.Count, bucketCount);
        Assert.Equal(bucketCount, timeline.GetProperty("countByBucket").GetArrayLength());

        // Every event lands in exactly one bucket - countByBucket must sum to
        // the total regardless of which types got ranked into topTypes.
        int countByBucketSum = 0;
        foreach (JsonElement bucket in timeline.GetProperty("countByBucket").EnumerateArray())
        {
            countByBucketSum += bucket.GetInt32();
        }
        Assert.Equal(events.Count, countByBucketSum);

        int topTypesCount = root.GetProperty("topTypes").GetArrayLength();
        JsonElement typeSelfByBucket = timeline.GetProperty("typeSelfByBucket");
        Assert.Equal(topTypesCount, typeSelfByBucket.GetArrayLength());

        int typeIndex = 0;
        foreach (JsonElement typeEntry in root.GetProperty("topTypes").EnumerateArray())
        {
            int expectedCount = typeEntry.GetProperty("Count").GetInt32();

            int actualSum = 0;
            foreach (JsonElement bucket in typeSelfByBucket[typeIndex].EnumerateArray())
            {
                actualSum += bucket.GetInt32();
            }

            Assert.Equal(expectedCount, actualSum);
            ++typeIndex;
        }
    }

    [Fact]
    public void Write_HasSmallBucketCount_OneBucketPerEventNotPaddedToMax()
    {
        // Fewer events than MaxTimelineBuckets (100) - bucketCount should
        // track the real event count, same as CPU/Contention's own timeline,
        // not always jump straight to 100 mostly-empty buckets.
        List<ExceptionEvent> events = new List<ExceptionEvent>
        {
            MakeEvent(0.0, "System.Exception"),
            MakeEvent(5.0, "System.Exception"),
            MakeEvent(10.0, "System.Exception"),
        };

        JsonDocument document = WriteAndParse(events, MakeSymbolTable());
        JsonElement timeline = document.RootElement.GetProperty("timeline");

        Assert.Equal(3, timeline.GetProperty("bucketCount").GetInt32());
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.Tests)
