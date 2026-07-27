import * as fs from "fs";

import { adaptivelyBucketTicks } from "./AllocationTicksBucketer";

// nettraceParser's raw JSON output used to be able to exceed Node's own
// maximum string length for a heavily-allocating capture (a real 5-minute
// capture with ~12M allocation ticks produced a 696MB file, against a
// ~537M-character V8 string ceiling) - reading it via
// fs.readFileSync(...).toString() + JSON.parse(...)
// (DotnetInsightsNettraceEditor.ts's original approach) threw "Cannot
// create a string longer than 0x1fffffe8 characters", which was being
// silently swallowed and reported to the user as a generic "corrupted or
// incorrect type" error, masking the real cause.
//
// That problem no longer exists: AllocationJsonExporter.cs's WriteTicks now
// writes the allocation-tick array (the thing that was 571MB of the 696MB
// total) to a separate binary sidecar file next to the JSON instead of
// inline, dropping the JSON itself to under 100MB even for the same
// capture - comfortably under the V8 string limit again, and fast enough
// to read as a single string + JSON.parse() without a streaming parser.
// (This file previously contained a hand-rolled streaming JSON scanner,
// TicksFastParser.ts, built specifically to avoid materializing that now-
// gone giant inline array; it's no longer needed and was deleted along
// with this file's old approach.)
//
// The binary format itself is documented in AllocationJsonExporter.cs's
// WriteTicks: fixed 12-byte records (4-byte little-endian int32
// RelativeMSec in whole milliseconds, 8-byte little-endian int64
// AllocationAmount), record count and size mirrored into the JSON's
// "ticks" descriptor object so this reader never has to guess or hardcode
// them independently of the writer.
export async function readNettraceJson(jsonFilePath: string): Promise<any> {
    const mainDocument = JSON.parse(fs.readFileSync(jsonFilePath).toString());

    const allocationSummary = mainDocument?.["allocationSummary"];
    if (allocationSummary && allocationSummary["ticks"] && typeof allocationSummary["ticks"] === "object") {
        const ticksDescriptor = allocationSummary["ticks"];
        const ticksBinaryPath = ticksBinaryPathFor(jsonFilePath);
        const rawTicks = readTicksBinary(ticksBinaryPath, ticksDescriptor["recordCount"], ticksDescriptor["bytesPerRecord"]);
        allocationSummary["ticks"] = adaptivelyBucketTicks(rawTicks);
    }

    return mainDocument;
}

// Mirrors Program.cs's own Path.ChangeExtension(jsonOutputPath, ".ticks.bin")
// convention exactly - the sidecar's path is never embedded in the JSON
// itself (see WriteTicks's own comment), just derivable the same way by
// whoever already knows jsonFilePath.
export function ticksBinaryPathFor(jsonFilePath: string): string {
    const lastDot = jsonFilePath.lastIndexOf(".");
    const withoutExtension = lastDot === -1 ? jsonFilePath : jsonFilePath.substring(0, lastDot);
    return `${withoutExtension}.ticks.bin`;
}

function readTicksBinary(ticksBinaryPath: string, recordCount: number, bytesPerRecord: number): Array<{ RelativeMSec: number; AllocationAmount: number }> {
    const buffer = fs.readFileSync(ticksBinaryPath);
    const ticks: Array<{ RelativeMSec: number; AllocationAmount: number }> = new Array(recordCount);

    for (let recordIndex = 0; recordIndex < recordCount; ++recordIndex) {
        const offset = recordIndex * bytesPerRecord;
        const relativeMSec = buffer.readInt32LE(offset);
        const allocationAmount = buffer.readBigInt64LE(offset + 4);
        ticks[recordIndex] = { RelativeMSec: relativeMSec, AllocationAmount: Number(allocationAmount) };
    }

    return ticks;
}
