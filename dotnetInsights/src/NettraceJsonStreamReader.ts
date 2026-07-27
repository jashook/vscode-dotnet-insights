import { adaptivelyBucketTicks } from "./AllocationTicksBucketer";
import { parseTicksFile } from "./TicksFastParser";

// nettraceParser's raw JSON output can exceed Node's own maximum string
// length for a heavily-allocating capture (a real 5-minute capture with
// ~12M allocation ticks produced a 696MB file, against a ~537M-character
// V8 string ceiling) - reading it via fs.readFileSync(...).toString() +
// JSON.parse(...) (DotnetInsightsNettraceEditor.ts's previous approach)
// throws "Cannot create a string longer than 0x1fffffe8 characters",
// which was being silently swallowed and reported to the user as a
// generic "corrupted or incorrect type" error, masking the real cause.
//
// Two earlier approaches were tried and measured against a real
// 696MB/11.9M-tick capture before landing on TicksFastParser.ts's
// hand-rolled scanner:
//   1. stream-json's "idiomatic" chain([createReadStream, parser(), ...])
//      + .on('data', ...), two passes (ignore the ticks array while
//      assembling everything else, then read it) - ~54s, >55% of it in
//      stream-chain's generic per-token dispatch machinery.
//   2. stream-json's own documented "fast path", parseFile()+pipe()/
//      drain() with the work as an in-pipe stage instead of an external
//      event listener - ~52s, a marginal ~5% improvement. Profiling
//      showed the same dispatch machinery still dominant either way: the
//      real cost is tokenizing ~72M JSON tokens per pass through a
//      general-purpose recursive tokenizer, not which driver API sits on
//      top of it, and two passes doubles that.
// TicksFastParser.ts replaces both with a single pass that never
// generically tokenizes the ticks array at all - see its own header
// comment for why that's safe here (nettraceParser is the only producer
// of this exact, fixed shape).
export async function readNettraceJson(jsonFilePath: string): Promise<any> {
    const rawTicks: Array<{ RelativeMSec: number; AllocationAmount: number }> = [];

    const { prefix, suffix } = await parseTicksFile(jsonFilePath, (relativeMSec, allocationAmount) => {
        rawTicks.push({ RelativeMSec: relativeMSec, AllocationAmount: allocationAmount });
    });

    // prefix ends with the ticks array's opening "[" and suffix begins
    // with its closing "]", so concatenating them directly reconstructs
    // valid JSON with an empty ticks array - collecting all raw ticks
    // into a plain array first and only then bucketing (rather than
    // bucketing inline while scanning) is deliberate: simple array
    // pushes are cheap regardless of count, and this reuses
    // AllocationTicksBucketer.ts's already-tested adaptive bucketing
    // instead of a third reimplementation of the same logic.
    const mainDocument = JSON.parse(prefix + suffix);

    if (mainDocument?.["allocationSummary"]) {
        mainDocument["allocationSummary"]["ticks"] = adaptivelyBucketTicks(rawTicks);
    }

    return mainDocument;
}
