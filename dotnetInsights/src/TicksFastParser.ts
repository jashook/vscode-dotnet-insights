import * as fs from 'fs';

// Hand-rolled scanner for the "ticks" array specifically, exploiting its
// exact, deterministic shape as written by AllocationJsonExporter.cs's
// WriteTicks: a JSON array of objects, each with EXACTLY two numeric
// fields in a fixed order - {"RelativeMSec":<number>,"AllocationAmount":<number>}
// - with no extra whitespace (Utf8JsonWriter's default, non-indented
// output). Profiling NettraceJsonStreamReader.ts's original stream-json-
// based approach against a real 696MB/11.9M-tick capture showed >55% of
// wall time going to stream-json's general-purpose per-token SAX dispatch
// machinery, doubled by needing two full passes over the file (one to
// skip past the ticks array while assembling everything else, one to
// actually read it) - switching that same two-pass shape to
// parseFile()+pipe()/drain() (stream-json's own documented "fast path")
// only bought ~5%, because the dominant cost is tokenizing ~72M JSON
// tokens per pass through a general-purpose recursive tokenizer, not
// which driver API sits on top of it.
//
// This scanner processes the whole file in a SINGLE pass instead: copy
// everything before/after the ticks array verbatim (cheap - orders of
// magnitude smaller than the array itself in a real capture, safe to
// JSON.parse normally), and hand-scan the array's numbers directly via a
// tight character loop with no generic tokenizer, no per-token object
// allocation, and no recursive-descent call stack.
//
// This is intentionally NOT a general JSON parser - it only understands
// this one exact shape, which is safe because nettraceParser is the only
// producer of this file and its format is under our own control. If
// AllocationJsonExporter.cs's WriteTicks output shape ever changes (field
// order, added whitespace, additional fields per tick), this scanner must
// change with it - there is no schema validation here beyond what the
// state machine requires to make progress, by design (that's the whole
// point of skipping general-purpose tokenization).

const TicksMarker = '"ticks":[';

export type TickCallback = (relativeMSec: number, allocationAmount: number) => void;

export interface ParsedTicksFile {
    // Everything in the file up to and including the ticks array's
    // opening "[") - JSON.parse(prefix + suffix) reconstructs the whole
    // document with an empty ticks array, since suffix begins with the
    // matching "]".
    prefix: string;
    suffix: string;
}

// Parses one JSON number starting at text[startIndex] (optional leading
// '-', digits, optional '.' + digits, optional exponent) - a superset of
// what WriteNumber(string, long)/WriteNumber(string, double) ever
// actually emit, but cheap to check exhaustively rather than assume the
// narrower common case. Returns the index just past the number, which
// equals text.length if the number's end wasn't reached within this
// string (ambiguous at a chunk boundary - callers must treat that as
// "not enough data yet", not as a real terminator).
function scanNumberEnd(text: string, startIndex: number): number {
    let index = startIndex;
    const length = text.length;

    if (text[index] === '-') {
        ++index;
    }

    while (index < length && text[index] >= '0' && text[index] <= '9') {
        ++index;
    }

    if (text[index] === '.') {
        ++index;
        while (index < length && text[index] >= '0' && text[index] <= '9') {
            ++index;
        }
    }

    if (text[index] === 'e' || text[index] === 'E') {
        ++index;
        if (text[index] === '+' || text[index] === '-') {
            ++index;
        }
        while (index < length && text[index] >= '0' && text[index] <= '9') {
            ++index;
        }
    }

    return index;
}

const RelativeMSecKey = '"RelativeMSec":';
const AllocationAmountKey = '"AllocationAmount":';

// Consumes as many complete {"RelativeMSec":X,"AllocationAmount":Y}
// entries (each optionally preceded by a ',') as `text` contains starting
// at index 0, calling onTick for each. Returns the unconsumed remainder:
// either an incomplete trailing entry (to prepend to the next chunk) or
// the ']' that closes the array once no more entries follow.
function scanTicks(text: string, onTick: TickCallback): string {
    let index = 0;
    const length = text.length;

    for (;;) {
        if (index >= length) {
            return '';
        }

        if (text[index] === ']') {
            return text.slice(index);
        }

        if (text[index] === ',') {
            ++index;
            continue;
        }

        const entryStart = index;

        if (text[index] !== '{') {
            return text.slice(entryStart);
        }
        ++index;

        if (!text.startsWith(RelativeMSecKey, index)) {
            return text.slice(entryStart);
        }
        index += RelativeMSecKey.length;

        const relativeMSecEnd = scanNumberEnd(text, index);
        if (relativeMSecEnd >= length) {
            // Could be a real end-of-number or a chunk boundary mid-digit -
            // ambiguous without more data, so treat it as incomplete.
            return text.slice(entryStart);
        }
        const relativeMSec = Number(text.slice(index, relativeMSecEnd));
        index = relativeMSecEnd;

        if (text[index] !== ',') {
            return text.slice(entryStart);
        }
        ++index;

        if (!text.startsWith(AllocationAmountKey, index)) {
            return text.slice(entryStart);
        }
        index += AllocationAmountKey.length;

        const allocationAmountEnd = scanNumberEnd(text, index);
        if (allocationAmountEnd >= length) {
            return text.slice(entryStart);
        }
        const allocationAmount = Number(text.slice(index, allocationAmountEnd));
        index = allocationAmountEnd;

        if (text[index] !== '}') {
            return text.slice(entryStart);
        }
        ++index;

        onTick(relativeMSec, allocationAmount);
    }
}

// highWaterMark defaults to 4MB - comfortably larger than any single tick
// entry (~40-50 bytes) or the marker itself, so the "not enough data,
// buffer and retry" paths below converge in at most a couple of chunks
// even in pathological cases. Exposed as a parameter purely so tests can
// force tiny chunks and deterministically exercise every boundary-
// straddling path (marker split across chunks, a tick's key or number
// split across chunks, etc.) without needing multi-megabyte fixtures.
export async function parseTicksFile(jsonFilePath: string, onTick: TickCallback, highWaterMark: number = 4 * 1024 * 1024): Promise<ParsedTicksFile> {
    return new Promise((resolve, reject) => {
        const readStream = fs.createReadStream(jsonFilePath, { encoding: 'utf8', highWaterMark });

        let phase: 'beforeTicks' | 'inTicks' | 'afterTicks' = 'beforeTicks';
        const prefixParts: string[] = [];
        const suffixParts: string[] = [];
        let pending = '';

        readStream.on('data', (chunkUnknown) => {
            let chunk = pending + (chunkUnknown as string);
            pending = '';

            if (phase === 'beforeTicks') {
                const markerIndex = chunk.indexOf(TicksMarker);
                if (markerIndex === -1) {
                    // The marker itself could straddle this chunk boundary -
                    // keep enough of the tail to catch it next time.
                    const safeLength = Math.max(0, chunk.length - TicksMarker.length);
                    prefixParts.push(chunk.slice(0, safeLength));
                    pending = chunk.slice(safeLength);
                    return;
                }

                prefixParts.push(chunk.slice(0, markerIndex + TicksMarker.length));
                phase = 'inTicks';
                chunk = chunk.slice(markerIndex + TicksMarker.length);
            }

            if (phase === 'inTicks') {
                const remainder = scanTicks(chunk, onTick);

                if (remainder.length > 0 && remainder[0] === ']') {
                    phase = 'afterTicks';
                    suffixParts.push(remainder);
                } else {
                    pending = remainder;
                }
            } else if (phase === 'afterTicks') {
                suffixParts.push(chunk);
            }
        });

        readStream.on('end', () => {
            if (phase === 'beforeTicks') {
                // No "ticks" marker ever found in the whole file - treat
                // everything as prefix. JSON.parse(prefix + "") then just
                // parses the document as-is (no ticks field to restore).
                prefixParts.push(pending);
            } else if (phase === 'inTicks') {
                if (pending.length === 0) {
                    // Nothing left to reconcile - the array's closing ']'
                    // must already be accounted for (see the 'data'
                    // handler), so this only happens for a genuinely empty
                    // remainder at end-of-stream.
                } else if (pending[0] === ']') {
                    suffixParts.push(pending);
                } else {
                    reject(new Error(`Unexpected trailing content in ticks array: ${JSON.stringify(pending.slice(0, 100))}`));
                    return;
                }
            }

            resolve({ prefix: prefixParts.join(''), suffix: suffixParts.join('') });
        });

        readStream.on('error', reject);
    });
}
