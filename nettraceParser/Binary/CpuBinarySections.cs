////////////////////////////////////////////////////////////////////////////////
// Module: CpuBinarySections.cs
//
// Notes:
// Binary encoders for the CPU profile's sections of the container documented
// in BinaryCaptureFormat.cs. Each writer here is paired with a decoder in the
// webview (media/), and with a diff test that checks it against the --json
// output for the same run (--json stays available precisely to be that
// oracle - see Program.cs).
//
// CpuSampleTimeline, version 1:
//
//     0   float64  minRelativeMSec
//     8   float64  totalDurationMSec
//     16  float64  bucketDurationMSec
//     24  uint32   bucketCount
//     28  uint32   methodCount
//     32  int32[bucketCount]               samplesByBucket
//     ..  int32[methodCount * bucketCount] methodSelfByBucket, method-major
//
// methodSelfByBucket is flattened to a single row-major block rather than
// kept as an array of arrays: the JSON form nests one array per ranked
// method, which costs a separate JS array object per method on the consuming
// side, and the consumer (media/snapshotGcStats.js's CPU timeline) only ever
// walks it as methodIndex * bucketCount + bucketIndex anyway. One flat
// Int32Array view over the payload serves it with no per-row allocation.
//
// The three leading float64s come first, and the counts after them, so every
// field lands naturally aligned for a DataView read and the two int32 blocks
// start on a 4-byte boundary - a reader can build Int32Array views straight
// over the payload rather than copying it.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.NetTrace.Binary {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System;

using DotnetInsights.NetTrace.Cpu;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public static class CpuBinarySections
{
    public const uint SampleTimelineVersion = 1;

    public static void WriteSampleTimeline(BinaryCaptureWriter captureWriter, CpuProfileJsonExporter.SampleTimeline timeline)
    {
        captureWriter.BeginSection(BinaryCaptureSectionId.CpuSampleTimeline, SampleTimelineVersion);

        captureWriter.WriteDouble(timeline.MinRelativeMSec);
        captureWriter.WriteDouble(timeline.TotalDurationMSec);
        captureWriter.WriteDouble(timeline.BucketDurationMSec);
        captureWriter.WriteUInt32((uint)timeline.BucketCount);
        captureWriter.WriteUInt32((uint)timeline.MethodSelfByBucket.Length);

        captureWriter.WriteInt32Array(timeline.SamplesByBucket.AsSpan(0, timeline.BucketCount));

        for (int methodIndex = 0; methodIndex < timeline.MethodSelfByBucket.Length; ++methodIndex)
        {
            captureWriter.WriteInt32Array(timeline.MethodSelfByBucket[methodIndex].AsSpan(0, timeline.BucketCount));
        }

        captureWriter.EndSection();
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.Binary)

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////
