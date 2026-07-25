import * as assert from 'assert';
import * as fs from 'fs';
import * as path from 'path';

import * as vscode from 'vscode';

import { renderAllocationSummaryTable } from '../../AllocationSummaryRenderer';
import { renderGcSnapshotWebview } from '../../GcSnapshotRenderer';

// Builds a minimal synthetic allocationSummary entry matching
// AllocationJsonExporter.cs's AllocationSummaryBuilder.Build output shape.
function makeAllocationSummary(topTypes: any[], overrides?: any): any {
    return Object.assign({
        totalSampledBytes: 0,
        distinctTypeCount: topTypes.length,
        totalTickCount: 0,
        topTypes: topTypes,
        ticks: []
    }, overrides);
}

function makeTypeEntry(typeName: string, totalBytes: number, tickCount: number): any {
    return {
        TypeName: typeName,
        TotalBytes: totalBytes,
        TickCount: tickCount,
        SmallCount: tickCount,
        LargeCount: 0,
        PinnedCount: 0
    };
}

// Full gcData["gcData"] entry - unlike gcDetailTableRenderer.test.ts's
// makeGc (which only needs the fields renderGcDetailTable reads),
// renderGcSnapshotWebview also runs computeAllocationAmountStats /
// computePauseTimeStats (GcStatsCalculations.ts), which read Heaps[].Generations.
function makeFullGc(id: number): any {
    const generations: any = {};
    for (let genIndex = 0; genIndex < 4; ++genIndex) {
        generations[genIndex] = {
            NewAllocation: 1024,
            SizeBefore: 1024,
            SizeAfter: 1024,
            ObjSpaceBefore: 1024,
            Fragmentation: 0,
            FreeListSpaceBefore: 0,
            FreeListSpaceAfter: 0,
            FreeObjSpaceBefore: 0,
            FreeObjSpaceAfter: 0,
            ObjSizeAfter: 1024,
            In: 0,
            Out: 0,
            SurvRate: 0,
            PinnedSurv: 0,
            NonePinnedSurv: 0,
            Id: genIndex
        };
    }

    return {
        data: {
            Id: id,
            DateTime: '2026-07-21T15:42:13.3255649-07:00',
            generation: 0,
            Type: 'AllocSmall',
            PauseDurationMSec: 1,
            PauseStartRelativeMSec: 0,
            PauseEndRelativeMSec: 1,
            Reason: 'AllocSmall',
            GenerationSize0: 1024 * 1024,
            GenerationSize1: 0,
            GenerationSize2: 0,
            GenerationSizeLOH: 0,
            GenerationSizePOH: 0,
            TotalHeapSize: 1024 * 1024,
            Gen0MinSize: 8 * 1024 * 1024,
            TotalPromotedSize0: 0,
            TotalPromotedSize1: 0,
            TotalPromotedSize2: 0,
            Heaps: [{ HeapIndex: 0, Generations: generations }]
        }
    };
}

function makeFakeWebview(): any {
    return {
        asWebviewUri: (uri: vscode.Uri) => uri,
        cspSource: ''
    };
}

function makeFakeDocument(): any {
    return { uri: vscode.Uri.file('/fake/test.gcinfo') };
}

describe('AllocationSummaryRenderer', () => {
    describe('renderAllocationSummaryTable', () => {
        it('shows a placeholder and no table when topTypes is empty', () => {
            const html = renderAllocationSummaryTable(makeAllocationSummary([]));

            assert.ok(html.includes('No allocation events to display'));
            assert.ok(!html.includes('<table>'));
        });

        it('renders summary tiles (total mb, tick count, distinct type count)', () => {
            const summary = makeAllocationSummary(
                [makeTypeEntry('System.Byte[]', 2 * 1024 * 1024, 10)],
                { totalSampledBytes: 2 * 1024 * 1024, totalTickCount: 10, distinctTypeCount: 1 }
            );

            const html = renderAllocationSummaryTable(summary);

            assert.ok(html.includes('2.00 mb'));
            assert.ok(html.includes('<span>10</span>'));
            assert.ok(html.includes('<span>1</span>'));
        });

        it('renders one row per type, labeled in mb, with % of sampled bytes', () => {
            const summary = makeAllocationSummary(
                [
                    makeTypeEntry('System.Byte[]', 3 * 1024 * 1024, 30),
                    makeTypeEntry('System.String', 1024 * 1024, 10)
                ],
                { totalSampledBytes: 4 * 1024 * 1024, totalTickCount: 40, distinctTypeCount: 2 }
            );

            const html = renderAllocationSummaryTable(summary);

            const dataRowMatches = html.match(/<tr><td>/g) || [];
            assert.strictEqual(dataRowMatches.length, 2);
            assert.ok(html.includes('<td>System.Byte[]</td><td>3.00</td><td>75.00</td><td>30</td>'));
            assert.ok(html.includes('<td>System.String</td><td>1.00</td><td>25.00</td><td>10</td>'));
        });

        it('orders the summary tiles above the chart canvas above the ranked table', () => {
            const summary = makeAllocationSummary([makeTypeEntry('System.Byte[]', 100, 1)], { totalSampledBytes: 100, totalTickCount: 1 });

            const html = renderAllocationSummaryTable(summary);

            const tilesIndex = html.indexOf('Sampled Allocations');
            const chartIndex = html.indexOf('id="allocationTimelineChart"');
            const tableIndex = html.indexOf('<table>');

            assert.ok(tilesIndex >= 0 && chartIndex >= 0 && tableIndex >= 0);
            assert.ok(tilesIndex < chartIndex);
            assert.ok(chartIndex < tableIndex);
        });

        it('preserves server-side sort order (already sorted descending by TotalBytes)', () => {
            const summary = makeAllocationSummary([
                makeTypeEntry('Biggest', 500, 5),
                makeTypeEntry('Smallest', 10, 1)
            ]);

            const html = renderAllocationSummaryTable(summary);

            assert.ok(html.indexOf('Biggest') < html.indexOf('Smallest'));
        });
    });

    describe('against real nettraceParser output', () => {
        const fixturePath = path.resolve(__dirname, '..', '..', '..', 'src', 'test', 'suite', 'fixtures', 'nettrace-gcdata.json');
        const gcData = JSON.parse(fs.readFileSync(fixturePath, 'utf8'));
        const allocationSummary = gcData['allocationSummary'];

        it('fixture has a populated allocationSummary (regenerated after adding AllocationEventProjector.cs)', () => {
            assert.ok(allocationSummary !== null && allocationSummary !== undefined);
            assert.ok(allocationSummary['topTypes'].length > 0);
        });

        // Regression guard: AllocationJsonExporter.cs used to ship a
        // pre-aggregated 100-bucket "timeline" instead of raw ticks - the
        // "Heap Contents" chart plots individual allocation events, so the
        // real per-tick RelativeMSec/AllocationAmount data has to survive
        // the export, matching totalTickCount and sorted by time.
        it('fixture has raw per-tick data (RelativeMSec/AllocationAmount), sorted ascending, matching totalTickCount', () => {
            const ticks = allocationSummary['ticks'];

            assert.strictEqual(ticks.length, allocationSummary['totalTickCount']);
            assert.ok(ticks.length > 0);
            assert.ok(typeof ticks[0]['RelativeMSec'] === 'number');
            assert.ok(typeof ticks[0]['AllocationAmount'] === 'number');

            for (let tickIndex = 1; tickIndex < ticks.length; ++tickIndex) {
                assert.ok(ticks[tickIndex]['RelativeMSec'] >= ticks[tickIndex - 1]['RelativeMSec']);
            }
        });

        // Regression guard for the "Allocated by Type Over Time" stacked
        // chart: typeTimeline's normalized types[]/bytesByType[] columns
        // must line up 1:1, "Other" must be the last column, and the whole
        // per-bucket/per-type matrix must reconcile exactly with
        // totalSampledBytes (AllocationJsonExporter.cs sums the same raw
        // events into both, so a mismatch means the bucket/column
        // assignment has a bug, not just floating-point drift).
        it('fixture has a typeTimeline whose bucket x type matrix sums to totalSampledBytes, with "Other" as the last column', () => {
            const typeTimeline = allocationSummary['typeTimeline'];

            assert.ok(typeTimeline !== null && typeTimeline !== undefined);
            assert.strictEqual(typeTimeline['types'][typeTimeline['types'].length - 1], 'Other');
            assert.ok(typeTimeline['buckets'].length > 0);

            let totalBytes = 0;
            for (const bucket of typeTimeline['buckets']) {
                assert.strictEqual(bucket['bytesByType'].length, typeTimeline['types'].length);

                for (const bytes of bucket['bytesByType']) {
                    totalBytes += bytes;
                }
            }

            assert.strictEqual(totalBytes, allocationSummary['totalSampledBytes']);
        });

        it('renders the real top-allocating-type ranking without throwing', () => {
            const html = renderAllocationSummaryTable(allocationSummary);

            assert.ok(html.includes('class="detailTable allocationTypeTable"'));
            assert.ok(html.includes('id="allocationTimelineChart"'));
            assert.strictEqual((html.match(/<tr><td>/g) || []).length, allocationSummary['topTypes'].length);
        });
    });
});

describe('GcSnapshotRenderer - view switcher and sourceFormat gating', () => {
    it('renders a .gcinfo-shaped payload with no "allocations" key (regression: this previously always hit the corrupted-file warning path)', () => {
        const gcData = {
            processName: 'test.exe',
            gcData: [makeFullGc(1)]
        };

        const html = renderGcSnapshotWebview(makeFakeDocument(), makeFakeWebview(), vscode.Uri.file('/fake/ext'), gcData, 'gcinfo');

        // The corrupted-file fallback (GcSnapshotRenderer.ts's defaultHtmlReturn)
        // has an empty <body> - processName and the view-switcher markup only
        // appear on the real render path, so their presence proves the
        // validity check let this input through instead of bailing.
        assert.ok(html.includes('test.exe'));
        assert.ok(html.includes('id="view-gc"'));
    });

    it('omits the "Heap Contents" nav button for sourceFormat "gcinfo" even if allocationSummary is present', () => {
        const gcData = {
            processName: 'test.exe',
            gcData: [makeFullGc(1)],
            allocationSummary: makeAllocationSummary([makeTypeEntry('Foo', 100, 1)], { totalSampledBytes: 100, totalTickCount: 1 })
        };

        const html = renderGcSnapshotWebview(makeFakeDocument(), makeFakeWebview(), vscode.Uri.file('/fake/ext'), gcData, 'gcinfo');

        assert.ok(!html.includes('data-view="heapContents"'));
    });

    it('omits the "Heap Contents" nav button for sourceFormat "nettrace" when allocationSummary has no topTypes', () => {
        const gcData = {
            processName: 'test.exe',
            gcData: [makeFullGc(1)],
            allocationSummary: makeAllocationSummary([])
        };

        const html = renderGcSnapshotWebview(makeFakeDocument(), makeFakeWebview(), vscode.Uri.file('/fake/ext'), gcData, 'nettrace');

        assert.ok(!html.includes('data-view="heapContents"'));
    });

    it('shows the "Heap Contents" nav button for sourceFormat "nettrace" with a populated allocationSummary', () => {
        const gcData = {
            processName: 'test.exe',
            gcData: [makeFullGc(1)],
            allocationSummary: makeAllocationSummary([makeTypeEntry('Foo', 100, 1)], { totalSampledBytes: 100, totalTickCount: 1 })
        };

        const html = renderGcSnapshotWebview(makeFakeDocument(), makeFakeWebview(), vscode.Uri.file('/fake/ext'), gcData, 'nettrace');

        assert.ok(html.includes('data-view="heapContents"'));
        assert.ok(html.includes('id="view-heapContents"'));
        assert.ok(html.includes('id="allocationSummaryHtml"'));
    });

    it('always shows the "GC" nav button as the default active view', () => {
        const gcData = { processName: 'test.exe', gcData: [makeFullGc(1)] };

        const html = renderGcSnapshotWebview(makeFakeDocument(), makeFakeWebview(), vscode.Uri.file('/fake/ext'), gcData, 'gcinfo');

        assert.ok(html.includes('<button class="viewNavButton active" data-view="gc">GC</button>'));
        assert.ok(html.includes('<div id="view-gc" class="viewPanel active">'));
    });
});
