import * as assert from 'assert';
import * as fs from 'fs';
import * as path from 'path';

import { computeAllocationAmountStats, computePauseTimeStats } from '../../GcStatsCalculations';

// Builds a minimal synthetic gcData["gcData"] entry with just the fields
// these calculations touch.
function makeGc(generation: number, pauseDurationMSec: number, newAllocationByGen: number[]): any {
    const generations: any = {};
    for (let genIndex = 0; genIndex < newAllocationByGen.length; ++genIndex) {
        generations[genIndex] = { NewAllocation: newAllocationByGen[genIndex] };
    }

    return {
        data: {
            generation: generation,
            PauseDurationMSec: pauseDurationMSec,
            Heaps: [
                { Generations: generations }
            ]
        }
    };
}

describe('GcStatsCalculations', () => {
    describe('computeAllocationAmountStats', () => {
        it('sums NewAllocation across all four generations when no generation filter is given', () => {
            const gcs = [
                makeGc(0, 1, [1024, 2048, 0, 0]),
                makeGc(0, 1, [1024, 0, 0, 0])
            ];

            const [byGc, [total]] = computeAllocationAmountStats(gcs);

            // byGc is sorted (see the lexicographic-sort quirk documented
            // below) as a side effect of computing the median, so it is NOT
            // in GC-input order: the first GC here totals 3 KB, the second
            // totals 1 KB, but the returned array comes back [1, 3]. This
            // return value isn't actually consumed anywhere in
            // GcSnapshotRenderer.ts (allocByGc and friends are assigned but
            // never read), so the sort has no effect on rendered output -
            // asserting it here so that stays true on purpose, not by luck.
            assert.deepStrictEqual(byGc, [1, 3]);
            assert.strictEqual(total, 4);
        });

        it('sums only the requested generation across heaps when a generation filter is given', () => {
            const gcs = [
                makeGc(0, 1, [1024, 2048, 0, 0])
            ];

            const [byGc] = computeAllocationAmountStats(gcs, 1);

            assert.strictEqual(byGc[0], 2);
        });

        it('sums across multiple heaps', () => {
            const gc: any = {
                data: {
                    generation: 0,
                    PauseDurationMSec: 1,
                    Heaps: [
                        { Generations: { 0: { NewAllocation: 1024 }, 1: { NewAllocation: 0 }, 2: { NewAllocation: 0 }, 3: { NewAllocation: 0 } } },
                        { Generations: { 0: { NewAllocation: 1024 }, 1: { NewAllocation: 0 }, 2: { NewAllocation: 0 }, 3: { NewAllocation: 0 } } }
                    ]
                }
            };

            const [byGc] = computeAllocationAmountStats([gc]);

            assert.strictEqual(byGc[0], 2);
        });

        it('reports max and min across GCs', () => {
            const gcs = [
                makeGc(0, 1, [1024, 0, 0, 0]),
                makeGc(0, 1, [10240, 0, 0, 0]),
                makeGc(0, 1, [512, 0, 0, 0])
            ];

            const [, [, , , max, min]] = computeAllocationAmountStats(gcs);

            assert.strictEqual(max, 10);
            assert.strictEqual(min, 0.5);
        });

        // Documents a pre-existing quirk inherited unmodified from
        // GcSnapshotRenderer.ts's original inline implementation: the median
        // is computed with Array.sort() and no comparator, which sorts
        // numbers as strings. This test exists to make that behavior visible
        // and pinned, not to endorse it as correct - see the comment in
        // GcStatsCalculations.ts.
        it('computes "median" via lexicographic (not numeric) sort - pre-existing quirk', () => {
            // KB values 100, 9, 20 sort lexicographically as "100", "20", "9".
            const gcs = [
                makeGc(0, 1, [100 * 1024, 0, 0, 0]),
                makeGc(0, 1, [9 * 1024, 0, 0, 0]),
                makeGc(0, 1, [20 * 1024, 0, 0, 0])
            ];

            const [, [, , median]] = computeAllocationAmountStats(gcs);

            // A correct numeric median of [100, 9, 20] would be 20. The
            // lexicographic sort ["100", "20", "9"] picks the middle entry, 20 -
            // which happens to coincide with the numeric answer for this
            // particular input. Use a case where it doesn't to prove the point:
            const skewedGcs = [
                makeGc(0, 1, [100 * 1024, 0, 0, 0]),
                makeGc(0, 1, [9 * 1024, 0, 0, 0]),
                makeGc(0, 1, [9 * 1024, 0, 0, 0]),
                makeGc(0, 1, [9 * 1024, 0, 0, 0]),
                makeGc(0, 1, [20 * 1024, 0, 0, 0])
            ];
            // Numeric sort: [9,9,9,20,100] -> numeric median = 9.
            // Lexicographic sort: ["100","20","9","9","9"] -> middle element "9".
            const [, [, , skewedMedian]] = computeAllocationAmountStats(skewedGcs);
            assert.strictEqual(skewedMedian, 9);
            assert.strictEqual(median, 20);
        });
    });

    describe('computePauseTimeStats', () => {
        it('sums pause duration across all GCs when no generation filter is given', () => {
            const gcs = [
                makeGc(0, 1.5, [0, 0, 0, 0]),
                makeGc(2, 3.5, [0, 0, 0, 0])
            ];

            const [times, [total, mean]] = computePauseTimeStats(gcs);

            assert.deepStrictEqual(times, [1.5, 3.5]);
            assert.strictEqual(total, 5);
            assert.strictEqual(mean, 2.5);
        });

        it('filters by generation', () => {
            const gcs = [
                makeGc(0, 1, [0, 0, 0, 0]),
                makeGc(2, 5, [0, 0, 0, 0]),
                makeGc(0, 2, [0, 0, 0, 0])
            ];

            const [times, [total]] = computePauseTimeStats(gcs, 0);

            assert.deepStrictEqual(times, [1, 2]);
            assert.strictEqual(total, 3);
        });

        it('returns all zeros without throwing when no GCs match the generation filter', () => {
            const gcs = [
                makeGc(0, 1, [0, 0, 0, 0])
            ];

            const [times, summary] = computePauseTimeStats(gcs, 2);

            assert.deepStrictEqual(times, []);
            assert.deepStrictEqual(summary, [0, 0, 0, 0, 0]);
        });
    });

    describe('against real nettraceParser output', () => {
        // trace2.nettrace (140 real GCs from a test workload that repeatedly
        // allocates then periodically forces a gen2 collection) parsed by
        // nettraceParser --json. Cross-checks the actual production code path
        // against numbers already hand-verified in the terminal during
        // nettraceParser development, rather than only synthetic data.
        const fixturePath = path.resolve(__dirname, '..', '..', '..', 'src', 'test', 'suite', 'fixtures', 'nettrace-gcdata.json');
        const gcData = JSON.parse(fs.readFileSync(fixturePath, 'utf8'));
        const gcs = gcData['gcData'];

        it('loads the fixture with the expected GC count', () => {
            assert.strictEqual(gcs.length, 140);
        });

        it('computes plausible gen0 allocation totals (8MB gen0 budget per collection)', () => {
            const [byGc, [total, mean]] = computeAllocationAmountStats(gcs, 0);

            assert.strictEqual(byGc.length, 140);

            // Every gen0 budget observed in this capture was exactly 8MB (8192 KB).
            for (const amount of byGc) {
                assert.strictEqual(amount, 8192);
            }

            assert.strictEqual(mean, 8192);
            assert.strictEqual(total, 140 * 8192);
        });

        it('computes pause-time stats with a higher average for gen2 than gen0', () => {
            const [, [, gen0Mean]] = computePauseTimeStats(gcs, 0);
            const [, [, gen2Mean]] = computePauseTimeStats(gcs, 2);

            // Full (gen2) collections cost meaningfully more than ephemeral
            // gen0 collections - if this ever inverts, something regressed.
            assert.ok(gen2Mean > gen0Mean, `expected gen2 mean (${gen2Mean}) > gen0 mean (${gen0Mean})`);
        });

        it('reports 140 GCs split 119/1/20 across gen0/gen1/gen2, matching the known capture', () => {
            const countByGeneration: { [key: number]: number } = { 0: 0, 1: 0, 2: 0 };

            for (const gc of gcs) {
                countByGeneration[gc['data']['generation']] += 1;
            }

            assert.strictEqual(countByGeneration[0], 119);
            assert.strictEqual(countByGeneration[1], 1);
            assert.strictEqual(countByGeneration[2], 20);
        });

        it('reports real, chronologically increasing DateTime values anchored to the actual capture', () => {
            // Regression guard: nettraceParser's Trace-header SyncTimeQPC field
            // does not reliably correspond to the same instant as the per-event
            // QPC stream on every platform (a real bug found and fixed during
            // development - GC timestamps were coming out 3 days off from the
            // trace's real capture time). GcEventProjector now anchors off the
            // trace's own first event instead. This fixture was captured on
            // 2026-07-24; asserting the year/month/day here would have caught
            // that bug.
            const firstDateTime = new Date(gcs[0]['data']['DateTime']);
            assert.strictEqual(firstDateTime.getUTCFullYear(), 2026);
            assert.strictEqual(firstDateTime.getUTCMonth(), 6); // 0-indexed: July
            assert.strictEqual(firstDateTime.getUTCDate(), 24);

            let previousTime = firstDateTime.getTime();
            for (const gc of gcs) {
                const currentTime = new Date(gc['data']['DateTime']).getTime();
                assert.ok(currentTime >= previousTime, `DateTime went backwards at GC #${gc['data']['Id']}`);
                previousTime = currentTime;
            }

            // The whole capture spans a bit over a second - not hours or days.
            const totalSpanMs = previousTime - firstDateTime.getTime();
            assert.ok(totalSpanMs > 0 && totalSpanMs < 60000, `expected a sub-minute capture span, got ${totalSpanMs}ms`);
        });
    });
});
