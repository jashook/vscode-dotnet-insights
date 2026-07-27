import * as assert from 'assert';

import { adaptivelyBucketTicks } from '../../AllocationTicksBucketer';

function makeTicks(count: number, spanMSec: number): any[] {
    const ticks: any[] = [];
    for (let tickIndex = 0; tickIndex < count; ++tickIndex) {
        ticks.push({
            RelativeMSec: (tickIndex / count) * spanMSec,
            AllocationAmount: 1000
        });
    }
    return ticks;
}

function sumAllocationAmount(ticks: any[]): number {
    let total = 0;
    for (let tickIndex = 0; tickIndex < ticks.length; ++tickIndex) {
        total += ticks[tickIndex]["AllocationAmount"];
    }
    return total;
}

describe('AllocationTicksBucketer', () => {
    describe('adaptivelyBucketTicks', () => {
        it('passes small tick arrays through unchanged (full fidelity)', () => {
            const ticks = makeTicks(500, 60_000);

            const result = adaptivelyBucketTicks(ticks);

            assert.strictEqual(result, ticks);
        });

        it('handles null/undefined without throwing', () => {
            assert.strictEqual(adaptivelyBucketTicks(null as any), null);
            assert.strictEqual(adaptivelyBucketTicks(undefined as any), undefined);
        });

        it('handles an empty array', () => {
            const result = adaptivelyBucketTicks([]);

            assert.deepStrictEqual(result, []);
        });

        it('buckets an oversized tick array down under the cap', () => {
            const ticks = makeTicks(250_000, 300_000);

            const result = adaptivelyBucketTicks(ticks);

            assert.ok(result.length < ticks.length, `expected fewer entries than ${ticks.length}, got ${result.length}`);
            assert.ok(result.length <= 100_000, `expected at most 100,000 buckets, got ${result.length}`);
        });

        it('preserves total AllocationAmount when bucketing (no data lost, only coalesced)', () => {
            const ticks = makeTicks(250_000, 300_000);

            const result = adaptivelyBucketTicks(ticks);

            assert.strictEqual(sumAllocationAmount(result), sumAllocationAmount(ticks));
        });

        it('keeps bucketed entries sorted ascending by RelativeMSec', () => {
            const ticks = makeTicks(250_000, 300_000);

            const result = adaptivelyBucketTicks(ticks);

            for (let bucketIndex = 1; bucketIndex < result.length; ++bucketIndex) {
                assert.ok(
                    result[bucketIndex]["RelativeMSec"] > result[bucketIndex - 1]["RelativeMSec"],
                    `bucket ${bucketIndex} (${result[bucketIndex]["RelativeMSec"]}) is not after bucket ${bucketIndex - 1} (${result[bucketIndex - 1]["RelativeMSec"]})`
                );
            }
        });

        it('does not throw when every tick shares the same RelativeMSec', () => {
            const ticks: any[] = [];
            for (let tickIndex = 0; tickIndex < 150_000; ++tickIndex) {
                ticks.push({ RelativeMSec: 42, AllocationAmount: 10 });
            }

            const result = adaptivelyBucketTicks(ticks);

            assert.strictEqual(sumAllocationAmount(result), 150_000 * 10);
        });
    });
});
