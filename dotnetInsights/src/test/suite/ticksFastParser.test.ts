import * as assert from 'assert';
import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';

import { parseTicksFile } from '../../TicksFastParser';

function writeTempJson(content: string): string {
    const filePath = path.join(os.tmpdir(), `ticksFastParser-test-${Date.now()}-${Math.random().toString(36).slice(2)}.json`);
    fs.writeFileSync(filePath, content);
    return filePath;
}

async function parse(content: string, highWaterMark?: number): Promise<{ ticks: Array<[number, number]>; reconstructed: any }> {
    const filePath = writeTempJson(content);
    try {
        const ticks: Array<[number, number]> = [];
        const { prefix, suffix } = await parseTicksFile(filePath, (relativeMSec, allocationAmount) => {
            ticks.push([relativeMSec, allocationAmount]);
        }, highWaterMark);

        return { ticks, reconstructed: JSON.parse(prefix + suffix) };
    }
    finally {
        fs.unlinkSync(filePath);
    }
}

describe('TicksFastParser', () => {
    describe('parseTicksFile', () => {
        it('extracts ticks and reconstructs the surrounding document with an empty ticks array', async () => {
            const content = '{"processName":"test","allocationSummary":{"totalTickCount":2,"ticks":[{"RelativeMSec":1.5,"AllocationAmount":100},{"RelativeMSec":2.5,"AllocationAmount":200}],"topTypes":[]},"gcData":[1,2,3]}';

            const { ticks, reconstructed } = await parse(content);

            assert.deepStrictEqual(ticks, [[1.5, 100], [2.5, 200]]);
            assert.strictEqual(reconstructed.processName, 'test');
            assert.deepStrictEqual(reconstructed.allocationSummary.ticks, []);
            assert.strictEqual(reconstructed.allocationSummary.totalTickCount, 2);
            assert.deepStrictEqual(reconstructed.gcData, [1, 2, 3]);
        });

        it('handles an empty ticks array', async () => {
            const content = '{"allocationSummary":{"ticks":[]}}';

            const { ticks, reconstructed } = await parse(content);

            assert.deepStrictEqual(ticks, []);
            assert.deepStrictEqual(reconstructed.allocationSummary.ticks, []);
        });

        it('handles a single tick', async () => {
            const content = '{"allocationSummary":{"ticks":[{"RelativeMSec":0,"AllocationAmount":42}]}}';

            const { ticks } = await parse(content);

            assert.deepStrictEqual(ticks, [[0, 42]]);
        });

        it('handles negative, decimal, and exponent-form numbers', async () => {
            const content = '{"allocationSummary":{"ticks":[{"RelativeMSec":-1.25,"AllocationAmount":5000000},{"RelativeMSec":1.5e2,"AllocationAmount":3}]}}';

            const { ticks } = await parse(content);

            assert.deepStrictEqual(ticks, [[-1.25, 5000000], [150, 3]]);
        });

        it('treats the whole file as prefix when no ticks marker exists', async () => {
            const content = '{"processName":"test","gcData":[]}';

            const { ticks, reconstructed } = await parse(content);

            assert.deepStrictEqual(ticks, []);
            assert.deepStrictEqual(reconstructed, { processName: 'test', gcData: [] });
        });

        it('preserves everything after the ticks array (suffix fields)', async () => {
            const content = '{"allocationSummary":{"ticks":[{"RelativeMSec":1,"AllocationAmount":2}],"typeTimeline":{"x":1},"drillDown":{}}}';

            const { reconstructed } = await parse(content);

            assert.deepStrictEqual(reconstructed.allocationSummary.typeTimeline, { x: 1 });
            assert.deepStrictEqual(reconstructed.allocationSummary.drillDown, {});
        });

        it('correctly parses every tick even when chunks are tiny enough to split the marker, keys, and numbers across many boundaries', async () => {
            const tickCount = 50;
            const tickEntries: string[] = [];
            for (let tickIndex = 0; tickIndex < tickCount; ++tickIndex) {
                tickEntries.push(`{"RelativeMSec":${tickIndex}.5,"AllocationAmount":${tickIndex * 1000}}`);
            }
            const content = `{"processName":"stress","allocationSummary":{"ticks":[${tickEntries.join(',')}]},"gcData":[]}`;

            // 5-byte chunks - far smaller than a single tick entry or even
            // the "ticks":[ marker itself, forcing every boundary-
            // straddling code path to fire many times over.
            const { ticks, reconstructed } = await parse(content, 5);

            assert.strictEqual(ticks.length, tickCount);
            for (let tickIndex = 0; tickIndex < tickCount; ++tickIndex) {
                assert.deepStrictEqual(ticks[tickIndex], [tickIndex + 0.5, tickIndex * 1000]);
            }
            assert.strictEqual(reconstructed.processName, 'stress');
            assert.deepStrictEqual(reconstructed.allocationSummary.ticks, []);
        });
    });
});
