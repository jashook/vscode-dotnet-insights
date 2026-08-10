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

// Builds a minimal synthetic exceptionSummary entry matching
// ExceptionJsonExporter.cs's Write output shape.
function makeExceptionSummary(topTypes: any[], overrides?: any): any {
    const totalExceptionCount = topTypes.reduce((sum, entry) => sum + entry.Count, 0);
    return Object.assign({
        totalExceptionCount: totalExceptionCount,
        distinctTypeCount: topTypes.length,
        topTypes: topTypes,
        typeDrillDown: topTypes.map(() => ({ count: 0, distinctStackCount: 0, totalChildCount: 0, children: [] })),
        methodNames: []
    }, overrides);
}

function makeExceptionTypeEntry(typeName: string, count: number): any {
    return { TypeName: typeName, Count: count, PercentOfTotal: 0, SampleMessage: 'test message' };
}

// Builds a minimal synthetic cpuProfile entry matching
// Cpu/CpuProfileJsonExporter.cs's Write output shape.
function makeCpuProfile(totalSampleCount: number, overrides?: any): any {
    return Object.assign({
        totalSampleCount: totalSampleCount,
        hotMethods: [],
        flameTree: { frame: -1, totalSamples: 0, totalChildCount: 0, children: [] },
        methodNames: []
    }, overrides);
}

// Builds a minimal synthetic eventOverview entry matching
// EventOverviewBuilder.cs's Build output shape (as written by
// GcJsonExporter.cs).
function makeEventOverview(eventTypes: any[]): any {
    return {
        totalEventCount: eventTypes.reduce((sum, entry) => sum + entry.count, 0),
        eventTypes: eventTypes
    };
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
            // Distinct Types tile carries an id (allocationDistinctTypesTile-<scope>)
            // so a row-hide toggle can rewrite it - see
            // updateOneRankedTypesTable in snapshotGcStats.js.
            assert.ok(html.includes('<span id="allocationDistinctTypesTile-all">1</span>'));
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

            // Rows are clickable (see snapshotGcStats.js's
            // onTypeDrillDownClick) - class="typeRow" data-type-index="N"
            // data-scope="...", not a bare <tr>. Matches the opening tag
            // loosely (not attribute-by-attribute) so it doesn't need
            // updating every time a new data-* attribute is added.
            const dataRowMatches = html.match(/<tr class="typeRow"[^>]*>/g) || [];
            assert.strictEqual(dataRowMatches.length, 2);
            // <td>...rowHideBtn...</td> now precedes the Type Name cell -
            // matched loosely (like the <tr> above) rather than pinning the
            // button's own markup here too.
            assert.ok(/<td class="rowHideColumn"><button class="rowHideBtn"[^<]*<\/button><\/td><td>System\.Byte\[\]<\/td><td>3\.00<\/td><td>75\.00<\/td><td class="ticksOnlyColumn">30<\/td>/.test(html));
            assert.ok(/<td class="rowHideColumn"><button class="rowHideBtn"[^<]*<\/button><\/td><td>System\.String<\/td><td>1\.00<\/td><td>25\.00<\/td><td class="ticksOnlyColumn">10<\/td>/.test(html));
        });

        // The allocation-rate line chart (raw ticks, no per-type/kind
        // breakdown) is shared/unfiltered and sits above the per-scope
        // tiles/type-timeline-chart/table block (see
        // AllocationSummaryRenderer.ts's renderTypeBreakdownPanel) - it
        // doesn't change when the LOH-only toggle is used, so it isn't part
        // of that per-scope block.
        it('orders the rate chart above the summary tiles above the type-timeline chart above the ranked table', () => {
            const summary = makeAllocationSummary([makeTypeEntry('System.Byte[]', 100, 1)], { totalSampledBytes: 100, totalTickCount: 1 });

            const html = renderAllocationSummaryTable(summary);

            const rateChartIndex = html.indexOf('id="allocationTimelineChart"');
            const tilesIndex = html.indexOf('Sampled Allocations');
            const typeChartIndex = html.indexOf('id="allocationTypeTimelineChart-all"');
            const tableIndex = html.indexOf('<table id="allocationTypeTable-all">');

            assert.ok(rateChartIndex >= 0 && tilesIndex >= 0 && typeChartIndex >= 0 && tableIndex >= 0);
            assert.ok(rateChartIndex < tilesIndex);
            assert.ok(tilesIndex < typeChartIndex);
            assert.ok(typeChartIndex < tableIndex);
        });

        it('preserves server-side sort order (already sorted descending by TotalBytes)', () => {
            const summary = makeAllocationSummary([
                makeTypeEntry('Biggest', 500, 5),
                makeTypeEntry('Smallest', 10, 1)
            ]);

            const html = renderAllocationSummaryTable(summary);

            assert.ok(html.indexOf('Biggest') < html.indexOf('Smallest'));
        });

        it('always renders the Charts inner tab, active by default, with its panel containing the tiles/charts/table', () => {
            const summary = makeAllocationSummary([makeTypeEntry('System.Byte[]', 100, 1)], { totalSampledBytes: 100, totalTickCount: 1 });

            const html = renderAllocationSummaryTable(summary);

            assert.ok(html.includes('<button class="heapContentsTabButton active" data-heaptab="charts">Charts</button>'));
            assert.ok(html.includes('<div id="heapContents-tab-charts" class="heapContentsTabPanel active">'));
            assert.ok(html.includes('Sampled Allocations'));
        });

        it('omits the Drill Down tab button/panel and the Back button when allocationSummary has no drillDown data', () => {
            const summary = makeAllocationSummary([makeTypeEntry('System.Byte[]', 100, 1)], { totalSampledBytes: 100, totalTickCount: 1 });

            const html = renderAllocationSummaryTable(summary);

            assert.ok(!html.includes('drillDownTabButton'));
            assert.ok(!html.includes('backToChartsButton'));
            assert.ok(!html.includes('heapContents-tab-drilldown'));
        });

        it('omits the Drill Down tab when drillDown.cells is present but empty', () => {
            const summary = makeAllocationSummary([makeTypeEntry('System.Byte[]', 100, 1)], {
                totalSampledBytes: 100,
                totalTickCount: 1,
                drillDown: { cells: {} }
            });

            const html = renderAllocationSummaryTable(summary);

            assert.ok(!html.includes('drillDownTabButton'));
        });

        it('shows the (initially hidden) Drill Down tab button, panel, and Back button when drillDown.cells is non-empty', () => {
            const summary = makeAllocationSummary([makeTypeEntry('System.Byte[]', 100, 1)], {
                totalSampledBytes: 100,
                totalTickCount: 1,
                drillDown: { cells: { '0:0': { totalBytes: 100, totalTickCount: 1, distinctStackCount: 1, stacks: [{ frames: ['Foo.Bar'], tickCount: 1, totalBytes: 100 }] } } }
            });

            const html = renderAllocationSummaryTable(summary);

            assert.ok(html.includes('<button class="heapContentsTabButton" id="drillDownTabButton" data-heaptab="drilldown" style="display:none">Drill Down</button>'));
            assert.ok(html.includes('<div id="heapContents-tab-drilldown" class="heapContentsTabPanel"></div>'));
            assert.ok(html.includes('id="backToChartsButton"'));
        });

        it('omits the All Types/LOH Only toggle when allocationSummary.loh is absent or empty', () => {
            const summary = makeAllocationSummary([makeTypeEntry('System.Byte[]', 100, 1)], { totalSampledBytes: 100, totalTickCount: 1 });

            const html = renderAllocationSummaryTable(summary);

            assert.ok(!html.includes('allocationViewToggle'));
            assert.ok(!html.includes('allocationTypeTimelineChart-loh'));

            const withEmptyLoh = renderAllocationSummaryTable(Object.assign({}, summary, { loh: { topTypes: [] } }));
            assert.ok(!withEmptyLoh.includes('allocationViewToggle'));
        });

        it('shows the All Types/LOH Only toggle and both panels when allocationSummary.loh has data', () => {
            const summary = makeAllocationSummary([makeTypeEntry('System.Byte[]', 100, 1)], { totalSampledBytes: 100, totalTickCount: 1 });
            summary.loh = makeAllocationSummary([makeTypeEntry('System.Byte[]', 80, 1)], { totalSampledBytes: 80, totalTickCount: 1 });

            const html = renderAllocationSummaryTable(summary);

            assert.ok(html.includes('<button class="allocationViewButton active" data-allocview="all">All Types</button>'));
            assert.ok(html.includes('<button class="allocationViewButton" data-allocview="loh">LOH Only</button>'));

            assert.ok(html.includes('<div id="allocView-all" class="allocationViewPanel active">'));
            assert.ok(html.includes('<div id="allocView-loh" class="allocationViewPanel">'));
            assert.ok(html.includes('id="allocationTypeTimelineChart-all"'));
            assert.ok(html.includes('id="allocationTypeTimelineChart-loh"'));

            // One row per scope, each tagged so snapshotGcStats.js's click
            // delegation resolves against the right summary object.
            assert.ok(html.includes('<tr class="typeRow" data-type-index="0" data-scope="all">'));
            assert.ok(html.includes('<tr class="typeRow" data-type-index="0" data-scope="loh">'));
        });

        it('shows the Drill Down tab when only the loh scope (not the all scope) has drillable data', () => {
            const summary = makeAllocationSummary([makeTypeEntry('System.Byte[]', 100, 1)], { totalSampledBytes: 100, totalTickCount: 1 });
            summary.loh = makeAllocationSummary([makeTypeEntry('System.Byte[]', 80, 1)], {
                totalSampledBytes: 80,
                totalTickCount: 1,
                drillDown: { cells: { '0:0': { totalBytes: 80, totalTickCount: 1, distinctStackCount: 1, stacks: [{ frames: ['Foo.Bar'], tickCount: 1, totalBytes: 80 }] } } }
            });

            const html = renderAllocationSummaryTable(summary);

            assert.ok(html.includes('id="drillDownTabButton"'));
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
            assert.strictEqual((html.match(/<tr class="typeRow"[^>]*>/g) || []).length, allocationSummary['topTypes'].length);
        });

        it('fixture has a populated drillDown with real resolved method names, not every frame unresolved', () => {
            const drillDown = allocationSummary['drillDown'];
            const methodNames = allocationSummary['methodNames'];

            assert.ok(drillDown !== null && drillDown !== undefined);
            const cellKeys = Object.keys(drillDown['cells']);
            assert.ok(cellKeys.length > 0);

            let foundRealFrame = false;
            for (const cellKey of cellKeys) {
                for (const stackEntry of drillDown['cells'][cellKey]['stacks']) {
                    for (const frameIndex of stackEntry['frames']) {
                        const frame = methodNames[frameIndex];
                        if (!frame.startsWith('<unresolved') && frame !== '<no stack captured>') {
                            foundRealFrame = true;
                        }
                    }
                }
            }

            assert.ok(foundRealFrame, 'Expected at least one real resolved frame across all drillDown cells.');
        });

        it('renders the Drill Down tab for the real fixture (which has populated drillDown data)', () => {
            const html = renderAllocationSummaryTable(allocationSummary);

            assert.ok(html.includes('drillDownTabButton'));
            assert.ok(html.includes('id="heapContents-tab-drilldown"'));
        });

        it('fixture has a populated typeDrillDown (whole-capture, not scoped to one bucket) with real resolved method names', () => {
            const typeDrillDown = allocationSummary['typeDrillDown'];
            const methodNames = allocationSummary['methodNames'];

            assert.ok(typeDrillDown !== null && typeDrillDown !== undefined);
            // Parallel array to topTypes - see AllocationJsonExporter.cs's
            // WriteTypeDrillDown.
            assert.strictEqual(typeDrillDown.length, allocationSummary['topTypes'].length);

            let foundRealFrame = false;
            for (const typeEntry of typeDrillDown) {
                for (const stackEntry of typeEntry['stacks']) {
                    for (const frameIndex of stackEntry['frames']) {
                        const frame = methodNames[frameIndex];
                        if (!frame.startsWith('<unresolved') && frame !== '<no stack captured>') {
                            foundRealFrame = true;
                        }
                    }
                }
            }

            assert.ok(foundRealFrame, 'Expected at least one real resolved frame across all typeDrillDown entries.');
        });

        // Regression guard for the truncation bug: totalBytes must reflect
        // EVERY distinct call stack the C# side aggregated for a type, not
        // just the (possibly capped) ones listed in "stacks" - otherwise
        // the Drill Down view's percentages silently disagree with the
        // ranked table row / chart bar they were opened from. Below the
        // cap distinctStackCount === stacks.length, so this also holds
        // trivially there; it's the >cap case this guards against.
        it('every typeDrillDown entry\'s totalBytes/totalTickCount match the corresponding topTypes row exactly', () => {
            const typeDrillDown = allocationSummary['typeDrillDown'];
            const topTypes = allocationSummary['topTypes'];

            for (let typeIndex = 0; typeIndex < topTypes.length; ++typeIndex) {
                const typeEntry = typeDrillDown[typeIndex];
                assert.strictEqual(typeEntry['totalBytes'], topTypes[typeIndex]['TotalBytes'], `typeDrillDown[${typeIndex}].totalBytes should match topTypes[${typeIndex}].TotalBytes`);
                assert.strictEqual(typeEntry['totalTickCount'], topTypes[typeIndex]['TickCount'], `typeDrillDown[${typeIndex}].totalTickCount should match topTypes[${typeIndex}].TickCount`);
                assert.ok(typeEntry['distinctStackCount'] >= typeEntry['stacks'].length);
            }
        });

        it('every rendered type row links to a non-empty typeDrillDown entry at the same index', () => {
            const html = renderAllocationSummaryTable(allocationSummary);
            const typeDrillDown = allocationSummary['typeDrillDown'];

            const rowIndexMatches = [...html.matchAll(/data-type-index="(\d+)"/g)];
            assert.strictEqual(rowIndexMatches.length, allocationSummary['topTypes'].length);

            for (const match of rowIndexMatches) {
                const typeIndex = parseInt(match[1], 10);
                assert.ok(typeDrillDown[typeIndex] && typeDrillDown[typeIndex]['stacks'].length > 0, `typeDrillDown[${typeIndex}] should be non-empty`);
            }
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

    it('shows the "Heap Contents" nav button as disabled for sourceFormat "nettrace" when allocationSummary has no topTypes', () => {
        const gcData = {
            processName: 'test.exe',
            gcData: [makeFullGc(1)],
            allocationSummary: makeAllocationSummary([])
        };

        const html = renderGcSnapshotWebview(makeFakeDocument(), makeFakeWebview(), vscode.Uri.file('/fake/ext'), gcData, 'nettrace');

        // Present (not omitted, unlike the gcinfo case above), but disabled -
        // per the "visible but unclickable" symmetric design (see
        // GcSnapshotRenderer.ts's viewTabBar comment).
        assert.ok(html.includes('data-view="heapContents"'));
        assert.ok(/data-view="heapContents"[^>]*\bdisabled\b/.test(html));
    });

    it('shows the "Heap Contents" nav button enabled for sourceFormat "nettrace" with a populated allocationSummary', () => {
        const gcData = {
            processName: 'test.exe',
            gcData: [makeFullGc(1)],
            allocationSummary: makeAllocationSummary([makeTypeEntry('Foo', 100, 1)], { totalSampledBytes: 100, totalTickCount: 1 })
        };

        const html = renderGcSnapshotWebview(makeFakeDocument(), makeFakeWebview(), vscode.Uri.file('/fake/ext'), gcData, 'nettrace');

        assert.ok(html.includes('data-view="heapContents"'));
        assert.ok(!/data-view="heapContents"[^>]*\bdisabled\b/.test(html));
        assert.ok(html.includes('id="view-heapContents"'));
        assert.ok(html.includes('id="allocationSummaryHtml"'));
    });

    it('disables the "Exceptions" nav button for sourceFormat "nettrace" when exceptionSummary has no topTypes', () => {
        const gcData = {
            processName: 'test.exe',
            gcData: [makeFullGc(1)],
            exceptionSummary: makeExceptionSummary([])
        };

        const html = renderGcSnapshotWebview(makeFakeDocument(), makeFakeWebview(), vscode.Uri.file('/fake/ext'), gcData, 'nettrace');

        assert.ok(html.includes('data-view="exceptions"'));
        assert.ok(/data-view="exceptions"[^>]*\bdisabled\b/.test(html));
    });

    it('enables the "Exceptions" nav button for sourceFormat "nettrace" with a populated exceptionSummary', () => {
        const gcData = {
            processName: 'test.exe',
            gcData: [makeFullGc(1)],
            exceptionSummary: makeExceptionSummary([makeExceptionTypeEntry('System.Exception', 3)])
        };

        const html = renderGcSnapshotWebview(makeFakeDocument(), makeFakeWebview(), vscode.Uri.file('/fake/ext'), gcData, 'nettrace');

        assert.ok(!/data-view="exceptions"[^>]*\bdisabled\b/.test(html));
    });

    it('disables the "Profile" nav button for sourceFormat "nettrace" when cpuProfile has zero samples', () => {
        const gcData = {
            processName: 'test.exe',
            gcData: [makeFullGc(1)],
            cpuProfile: makeCpuProfile(0)
        };

        const html = renderGcSnapshotWebview(makeFakeDocument(), makeFakeWebview(), vscode.Uri.file('/fake/ext'), gcData, 'nettrace');

        assert.ok(html.includes('data-view="profile"'));
        assert.ok(/data-view="profile"[^>]*\bdisabled\b/.test(html));
    });

    it('enables the "Profile" nav button for sourceFormat "nettrace" with a populated cpuProfile', () => {
        const gcData = {
            processName: 'test.exe',
            gcData: [makeFullGc(1)],
            cpuProfile: makeCpuProfile(3, { hotMethods: [{ frame: 0, selfSamples: 3, totalSamples: 3 }], methodNames: ['Program.Main'] })
        };

        const html = renderGcSnapshotWebview(makeFakeDocument(), makeFakeWebview(), vscode.Uri.file('/fake/ext'), gcData, 'nettrace');

        assert.ok(!/data-view="profile"[^>]*\bdisabled\b/.test(html));
        assert.ok(html.includes('Program.Main'));
    });

    it('omits the "Profile" nav button for sourceFormat "gcinfo"', () => {
        const gcData = { processName: 'test.exe', gcData: [makeFullGc(1)] };

        const html = renderGcSnapshotWebview(makeFakeDocument(), makeFakeWebview(), vscode.Uri.file('/fake/ext'), gcData, 'gcinfo');

        assert.ok(!html.includes('data-view="profile"'));
    });

    it('disables the "GC" nav button for sourceFormat "nettrace" when the capture has zero GCs, and defaults to Overview', () => {
        const gcData = {
            processName: 'test.exe',
            gcData: [],
            eventOverview: makeEventOverview([{ providerName: 'Microsoft-Windows-DotNETRuntime', displayName: 'ExceptionThrown', eventId: 80, count: 5 }])
        };

        const html = renderGcSnapshotWebview(makeFakeDocument(), makeFakeWebview(), vscode.Uri.file('/fake/ext'), gcData, 'nettrace');

        assert.ok(/data-view="gc"[^>]*\bdisabled\b/.test(html));
        assert.ok(html.includes('<button class="viewNavButton active" data-view="overview">Overview</button>'));
        assert.ok(html.includes('<div id="view-overview" class="viewPanel active">'));
        assert.ok(!html.includes('<div id="view-gc" class="viewPanel active">'));
    });

    it('enables the "GC" nav button for sourceFormat "nettrace" when the capture has GCs, but Overview still defaults active', () => {
        const gcData = {
            processName: 'test.exe',
            gcData: [makeFullGc(1)],
            eventOverview: makeEventOverview([{ providerName: 'Microsoft-Windows-DotNETRuntime', displayName: 'GCStart', eventId: 1, count: 1 }])
        };

        const html = renderGcSnapshotWebview(makeFakeDocument(), makeFakeWebview(), vscode.Uri.file('/fake/ext'), gcData, 'nettrace');

        assert.ok(!/data-view="gc"[^>]*\bdisabled\b/.test(html));
        // "Always default" - even though GC has real data, Overview is
        // still the tab the user lands on for nettrace input.
        assert.ok(html.includes('<button class="viewNavButton active" data-view="overview">Overview</button>'));
        assert.ok(!html.includes('<div id="view-gc" class="viewPanel active">'));
    });

    it('always shows the "GC" nav button as the default active view for sourceFormat "gcinfo", with no Overview tab at all', () => {
        const gcData = { processName: 'test.exe', gcData: [makeFullGc(1)] };

        const html = renderGcSnapshotWebview(makeFakeDocument(), makeFakeWebview(), vscode.Uri.file('/fake/ext'), gcData, 'gcinfo');

        assert.ok(html.includes('<button class="viewNavButton active" data-view="gc">GC</button>'));
        assert.ok(html.includes('<div id="view-gc" class="viewPanel active">'));
        assert.ok(!html.includes('data-view="overview"'));
        assert.ok(!html.includes('id="view-overview"'));
    });

    it('renders the Overview tab with total event count and a per-type breakdown', () => {
        const gcData = {
            processName: 'test.exe',
            gcData: [makeFullGc(1)],
            eventOverview: makeEventOverview([
                { providerName: 'Microsoft-Windows-DotNETRuntime', displayName: 'GCStart', eventId: 1, count: 7 },
                { providerName: 'Microsoft-Windows-DotNETRuntime', displayName: 'EventID 999', eventId: 999, count: 3 }
            ])
        };

        const html = renderGcSnapshotWebview(makeFakeDocument(), makeFakeWebview(), vscode.Uri.file('/fake/ext'), gcData, 'nettrace');

        assert.ok(html.includes('GCStart'));
        assert.ok(html.includes('EventID 999'));
        assert.ok(html.includes('<span>10</span>'));
    });
});
