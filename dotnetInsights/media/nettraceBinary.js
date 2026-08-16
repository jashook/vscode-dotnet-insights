// Decodes nettraceParser's binary container INSIDE the webview.
//
// This is the read half of nettraceParser/Binary/BinaryCaptureFormat.cs -
// keep the two in sync; that file is the format's specification and carries
// the layout diagram. BinaryCaptureReader.cs is the C# implementation of this
// same decode, kept as the format's executable spec and cross-checked against
// the --json oracle in BinaryCaptureTests.cs.
//
// WHY the webview does this rather than the extension host: the JSON pipeline
// this replaces serialized every payload three times before anything drew -
// nettraceParser wrote JSON, the host JSON.parsed the whole file, the host
// JSON.stringified nine sections back into <script type="application/json">
// blocks, and this document JSON.parsed all nine again. Measured on a real
// 3.01GB capture (54.4MB of JSON): 177ms parse + 102ms stringify + 132ms
// reparse = 435ms, all of it synchronous on the extension host's event loop,
// which is exactly the window where the UI appears frozen. Fetching the bytes
// here skips all of it.
//
// Everything below reads straight out of the fetched ArrayBuffer. Sections are
// 8-byte aligned by the writer specifically so typed-array views can be
// created over the buffer with no intermediate copy.

var NettraceBinary = (function () {
    // "DNIBIN\0\0"
    var MAGIC = [0x44, 0x4E, 0x49, 0x42, 0x49, 0x4E, 0x00, 0x00];
    var FORMAT_VERSION = 1;
    var HEADER_BYTES = 32;
    var SECTION_TABLE_ENTRY_BYTES = 24;

    var SECTION_CPU_SAMPLE_TIMELINE = 1;

    // Reads the container's header + section table. Returns
    // { sections: Map<sectionId, {version, offset, length}>, buffer } or null
    // if this is not a container we understand - callers fall back to the JSON
    // payload in that case rather than failing the whole view, which is what
    // keeps a stale, pre-binary nettraceParser binary (see CLAUDE.md's
    // "stale-cache trap") from producing a blank panel.
    function parseContainer(buffer) {
        if (buffer.byteLength < HEADER_BYTES) {
            return null;
        }

        var bytes = new Uint8Array(buffer);

        for (var magicIndex = 0; magicIndex < MAGIC.length; ++magicIndex) {
            if (bytes[magicIndex] !== MAGIC[magicIndex]) {
                return null;
            }
        }

        var view = new DataView(buffer);

        // Every multi-byte read passes `true` for littleEndian. The writer
        // (BinaryCaptureWriter.cs) is explicit about this on its side too -
        // DataView defaults to BIG endian, so omitting the argument here would
        // be a silent, total misread rather than an error.
        if (view.getUint32(8, true) !== FORMAT_VERSION) {
            return null;
        }

        var sectionCount = view.getUint32(12, true);
        // Offsets are int64 in the format. Read as two 32-bit halves and
        // recombine: these are file offsets bounded by the container's own
        // size, so they stay far inside Number's exact-integer range, and this
        // avoids requiring BigInt support for what is always a small value.
        var sectionTableOffset = readInt64AsNumber(view, 16);

        if (sectionTableOffset < HEADER_BYTES) {
            return null;
        }

        if (sectionTableOffset + (sectionCount * SECTION_TABLE_ENTRY_BYTES) > buffer.byteLength) {
            return null;
        }

        var sections = new Map();

        for (var sectionIndex = 0; sectionIndex < sectionCount; ++sectionIndex) {
            var entryOffset = sectionTableOffset + (sectionIndex * SECTION_TABLE_ENTRY_BYTES);

            var sectionId = view.getUint32(entryOffset, true);
            var sectionVersion = view.getUint32(entryOffset + 4, true);
            var payloadOffset = readInt64AsNumber(view, entryOffset + 8);
            var payloadLength = readInt64AsNumber(view, entryOffset + 16);

            if (payloadOffset < HEADER_BYTES || payloadLength < 0 || payloadOffset + payloadLength > buffer.byteLength) {
                return null;
            }

            sections.set(sectionId, { version: sectionVersion, offset: payloadOffset, length: payloadLength });
        }

        return { sections: sections, buffer: buffer };
    }

    function readInt64AsNumber(view, byteOffset) {
        var low = view.getUint32(byteOffset, true);
        var high = view.getUint32(byteOffset + 4, true);
        return (high * 4294967296) + low;
    }

    // Decodes CpuSampleTimeline (version 1) into the SAME shape the JSON
    // "sampleTimeline" object had, so media/snapshotGcStats.js's existing
    // timeline rendering needs no changes:
    //   { minRelativeMSec, totalDurationMSec, bucketDurationMSec, bucketCount,
    //     samplesByBucket, methodSelfByBucket }
    //
    // samplesByBucket and each methodSelfByBucket row are Int32Array VIEWS over
    // the fetched buffer, not copies and not plain Arrays. Every consumer only
    // indexes and iterates them, which typed arrays support identically, so
    // nothing here has to materialize ~20K numbers as boxed JS values the way
    // JSON.parse did.
    function decodeSampleTimeline(container) {
        var section = container.sections.get(SECTION_CPU_SAMPLE_TIMELINE);

        if (!section || section.version !== 1) {
            return null;
        }

        var view = new DataView(container.buffer, section.offset, section.length);

        var minRelativeMSec = view.getFloat64(0, true);
        var totalDurationMSec = view.getFloat64(8, true);
        var bucketDurationMSec = view.getFloat64(16, true);
        var bucketCount = view.getUint32(24, true);
        var methodCount = view.getUint32(28, true);

        var samplesOffset = section.offset + 32;
        var samplesByBucket = new Int32Array(container.buffer, samplesOffset, bucketCount);

        // One flat row-major block in the file (see CpuBinarySections.cs), split
        // back into per-method rows here purely to match the JSON shape. These
        // are subarray views, so this costs a handful of small view objects
        // rather than copying methodCount * bucketCount values.
        var methodSelfBase = samplesOffset + (bucketCount * 4);
        var methodSelfByBucket = new Array(methodCount);

        for (var methodIndex = 0; methodIndex < methodCount; ++methodIndex) {
            methodSelfByBucket[methodIndex] = new Int32Array(container.buffer, methodSelfBase + (methodIndex * bucketCount * 4), bucketCount);
        }

        return {
            minRelativeMSec: minRelativeMSec,
            totalDurationMSec: totalDurationMSec,
            bucketDurationMSec: bucketDurationMSec,
            bucketCount: bucketCount,
            samplesByBucket: samplesByBucket,
            methodSelfByBucket: methodSelfByBucket
        };
    }

    // Fetches and decodes the container named by the embedded URI. Resolves to
    // null (never rejects) when there is no container, it cannot be fetched, or
    // it is not a format this build understands - callers keep using whatever
    // the JSON payload already gave them, so a decode problem degrades to the
    // old behaviour instead of an empty panel.
    function load(uri) {
        if (!uri) {
            return Promise.resolve(null);
        }

        return fetch(uri)
            .then(function (response) {
                if (!response.ok) {
                    return null;
                }

                return response.arrayBuffer();
            })
            .then(function (buffer) {
                if (!buffer) {
                    return null;
                }

                return parseContainer(buffer);
            })
            .catch(function () {
                return null;
            });
    }

    return {
        load: load,
        decodeSampleTimeline: decodeSampleTimeline
    };
})();
