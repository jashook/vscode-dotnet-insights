////////////////////////////////////////////////////////////////////////////////
// Module: Program.cs
//
// Notes:
// Standalone CLI mirroring nettraceParser/Program.cs's own shape, so the two
// tools can be run side by side by hand against any .nettrace file:
//   nettraceParser.GroundTruth <file.nettrace> [--json <output.json>]
// With no --json, prints one summary line per completed GC (TraceEvent's
// numbers, not nettraceParser's) to stdout. Real automated coverage is the
// diff test in nettraceParser.Tests/GroundTruthDiffTests.cs, which calls
// TraceEventGcReader.Read directly rather than shelling out to this CLI -
// this Program.cs exists for ad hoc manual comparison, e.g. pointing it at
// an investigation capture that isn't checked into the repo.
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

using DotnetInsights.NetTrace.GroundTruth;

if (args.Length < 1)
{
    Console.WriteLine("Usage: nettraceParser.GroundTruth <file.nettrace> [--json <output.json>]");
    return;
}

string filePath = args[0];

List<GcTruthRecord> records = TraceEventGcReader.Read(filePath);

int jsonArgIndex = Array.IndexOf(args, "--json");
if (jsonArgIndex >= 0 && jsonArgIndex + 1 < args.Length)
{
    string jsonOutputPath = args[jsonArgIndex + 1];

    using (FileStream stream = new FileStream(jsonOutputPath, FileMode.Create, FileAccess.Write))
    using (Utf8JsonWriter writer = new Utf8JsonWriter(stream))
    {
        writer.WriteStartArray();

        foreach (GcTruthRecord record in records)
        {
            writer.WriteStartObject();
            writer.WriteNumber("number", record.Number);
            writer.WriteNumber("generation", record.Generation);
            writer.WriteNumber("reason", record.Reason);
            writer.WriteNumber("type", record.Type);
            writer.WriteNumber("pauseDurationMSec", record.PauseDurationMSec);
            writer.WriteNumber("pauseStartRelativeMSec", record.PauseStartRelativeMSec);
            writer.WriteNumber("totalHeapSize", record.TotalHeapSize);
            writer.WriteNumber("totalPromoted", record.TotalPromoted);
            writer.WriteNumber("generationSize0", record.GenerationSize0);
            writer.WriteNumber("generationSize1", record.GenerationSize1);
            writer.WriteNumber("generationSize2", record.GenerationSize2);
            writer.WriteNumber("generationSize3", record.GenerationSize3);
            writer.WriteNumber("generationSize4", record.GenerationSize4);
            writer.WriteNumber("totalPromotedSize0", record.TotalPromotedSize0);
            writer.WriteNumber("totalPromotedSize1", record.TotalPromotedSize1);
            writer.WriteNumber("totalPromotedSize2", record.TotalPromotedSize2);
            writer.WriteNumber("totalPromotedSize3", record.TotalPromotedSize3);
            writer.WriteNumber("totalPromotedSize4", record.TotalPromotedSize4);
            writer.WriteNumber("pinnedObjectCount", record.PinnedObjectCount);
            writer.WriteNumber("numHeaps", record.NumHeaps);
            writer.WriteNumber("finalYoungestDesired", record.FinalYoungestDesired);
            writer.WriteNumber("globalMechanisms", record.GlobalMechanisms);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    Console.Error.WriteLine($"Wrote {records.Count} ground-truth GC records to {jsonOutputPath}");
    return;
}

Console.WriteLine("== TraceEvent ground-truth GC summary ==");
Console.WriteLine($"Completed GCs: {records.Count}");

foreach (GcTruthRecord record in records)
{
    Console.WriteLine($"  GC #{record.Number} gen{record.Generation} reason={record.Reason} pause={record.PauseDurationMSec:F2}ms numHeaps={record.NumHeaps} totalHeapSize={record.TotalHeapSize} totalPromoted={record.TotalPromoted}");
}
