import * as assert from 'assert';
import * as fs from 'fs';
import * as path from 'path';

import { formatHumanDateTime, renderGcDetailTable } from '../../GcDetailTableRenderer';

// Builds a minimal synthetic gcData["gcData"] entry with just the fields
// renderGcDetailTable reads.
function makeGc(id: number, pauseDurationMSec: number, overrides?: any): any {
    return {
        data: Object.assign({
            Id: id,
            DateTime: '2026-07-21T15:42:13.3255649-07:00',
            PauseStartRelativeMSec: id * 1000,
            generation: 0,
            Type: 'AllocSmall',
            PauseDurationMSec: pauseDurationMSec,
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
            TotalPromotedSize2: 0
        }, overrides)
    };
}

describe('GcDetailTableRenderer', () => {
    describe('formatHumanDateTime', () => {
        it('formats an absolute ISO timestamp as "DD-Mon-YYYY hh:mm:ss AM/PM TZ"', () => {
            const formatted = formatHumanDateTime('2026-07-21T15:42:13.3255649-07:00');

            // Regex rather than an exact string - the timezone abbreviation
            // (PDT/PST/etc) depends on the machine running the test.
            assert.ok(/^21-Jul-2026 \d{2}:42:13 (AM|PM) [A-Z]+$/.test(formatted), `unexpected format: ${formatted}`);
        });

        it('passes through .gcinfo/XML elapsed-time strings unchanged (no absolute date to format)', () => {
            assert.strictEqual(formatHumanDateTime('+00:01:23.456'), '+00:01:23.456');
        });

        it('returns an empty string for undefined/null', () => {
            assert.strictEqual(formatHumanDateTime(undefined), '');
            assert.strictEqual(formatHumanDateTime(null), '');
        });

        it('falls back to the raw string for an unparseable date', () => {
            assert.strictEqual(formatHumanDateTime('not-a-date'), 'not-a-date');
        });
    });

    describe('renderGcDetailTable', () => {
        it('shows a placeholder and no table for an empty GC list', () => {
            const html = renderGcDetailTable([]);

            assert.ok(html.includes('No GC events to display'));
            assert.ok(!html.includes('<table>'));
        });

        it('wraps the table in a .detailTable class, not an id (multiple tables share the Detailed panel)', () => {
            const html = renderGcDetailTable([makeGc(1, 1)]);

            assert.ok(html.includes('<div class="detailTable">'));
            assert.ok(!html.includes('id="detailTable"'));
        });

        it('labels every size column in mb, not kb - the underlying divisor is 1024*1024', () => {
            const html = renderGcDetailTable([makeGc(1, 1)]);

            assert.strictEqual((html.match(/\(mb\)/g) || []).length, 10);
            assert.strictEqual((html.match(/\(kb\)/g) || []).length, 0);
        });

        // DateTime is emitted raw (data-raw attribute, empty cell text) here
        // rather than pre-formatted - formatting happens client-side in
        // snapshotGcStats.js, once, when the Detailed tab is first opened,
        // instead of on every extension-host render (see the comment above
        // tdDateTimeRaw in GcDetailTableRenderer.ts).
        it('includes a DateTime column carrying the raw value in a data-raw attribute, not pre-formatted text', () => {
            const html = renderGcDetailTable([makeGc(1, 1)]);

            assert.ok(html.includes('<td class="gcDateTimeCell" data-raw="2026-07-21T15:42:13.3255649-07:00"></td>'));
        });

        // Column sorting (snapshotGcStats.js's setupDetailTableSortHandlers)
        // reads each <th>'s data-sort attribute to decide how to compare
        // that column's cells - pinned here so a future column reorder/
        // rename can't silently drop or mislabel one.
        it('marks every header with the data-sort type its column needs (number/date/text)', () => {
            const html = renderGcDetailTable([makeGc(1, 1)]);

            assert.ok(html.includes('<th data-sort="number"><span class="thLabel">GC Number</span>'));
            assert.ok(html.includes('<th data-sort="date"><span class="thLabel">DateTime</span>'));
            assert.ok(html.includes('<th data-sort="number"><span class="thLabel">Collection Generation</span>'));
            assert.ok(html.includes('<th data-sort="text"><span class="thLabel">Type</span>'));
            assert.ok(html.includes('<th data-sort="number"><span class="thLabel">Pause Time (mSec)</span>'));
            assert.ok(html.includes('<th data-sort="text"><span class="thLabel">Reason</span>'));

            // Every header - regardless of sort type - gets an (initially
            // empty) sort-direction indicator span the click handler fills in.
            assert.strictEqual((html.match(/<span class="sortIndicator"><\/span>/g) || []).length, 16);
        });

        it('renders GenerationSizePOH as a real value, not the old NYI placeholder', () => {
            const html = renderGcDetailTable([makeGc(1, 1, { GenerationSizePOH: 2 * 1024 * 1024 })]);

            assert.ok(!html.includes('NYI'));
            assert.ok(html.includes('<td>2.00</td>'));
        });

        // Pause-time severity color-coding - each threshold's own class,
        // matching DotnetInsightsGcEditor.ts's live-view table this was
        // extracted from. Pinned here so a future refactor can't silently
        // drop it again.
        it('color-codes rows by pause-time severity threshold', () => {
            const gcs = [
                makeGc(1, 250),  // > 200ms
                makeGc(2, 150),  // > 100ms
                makeGc(3, 75),   // > 50ms
                makeGc(4, 30),   // > 20ms
                makeGc(5, 15),   // > 10ms
                makeGc(6, 5)     // <= 10ms - no severity class
            ];

            const html = renderGcDetailTable(gcs);

            assert.ok(html.includes('<tr class="expensiveGc" data-elapsed-msec="1000"><td>1</td>'));
            assert.ok(html.includes('<tr class="warnGc" data-elapsed-msec="2000"><td>2</td>'));
            assert.ok(html.includes('<tr class="interstingGc" data-elapsed-msec="3000"><td>3</td>'));
            assert.ok(html.includes('<tr class="somewhatInterestingGc" data-elapsed-msec="4000"><td>4</td>'));
            assert.ok(html.includes('<tr class="notSomewhatInterestingGc" data-elapsed-msec="5000"><td>5</td>'));
            assert.ok(html.includes('<tr data-elapsed-msec="6000"><td>6</td>'));
        });
    });

    describe('against real nettraceParser output', () => {
        const fixturePath = path.resolve(__dirname, '..', '..', '..', 'src', 'test', 'suite', 'fixtures', 'nettrace-gcdata.json');
        const gcData = JSON.parse(fs.readFileSync(fixturePath, 'utf8'));
        const gcs = gcData['gcData'];

        it('renders one row per GC', () => {
            const html = renderGcDetailTable(gcs);

            const dataRowMatches = html.match(/<tr(?: class="[a-zA-Z]*")? data-elapsed-msec="[^"]*"><td>\d+<\/td>/g) || [];
            assert.strictEqual(dataRowMatches.length, 140);
        });

        it('renders no severity-colored rows for this capture (max pause ~1.2ms, under every threshold)', () => {
            const html = renderGcDetailTable(gcs);

            assert.strictEqual((html.match(/class="expensiveGc"/g) || []).length, 0);
            assert.strictEqual((html.match(/class="warnGc"/g) || []).length, 0);
            assert.strictEqual((html.match(/class="interstingGc"/g) || []).length, 0);
        });

        // Regression guard for a real bug: GcEventProjector.cs appends heaps
        // in wire-arrival order, not physical heap order, so GcJsonExporter.cs
        // must sort by each heap's own HeapIndex before serializing - without
        // that, array position (what every per-heap consumer, client-side and
        // otherwise, keys off of) doesn't actually match the heap number.
        // Invisible on this single-heap fixture; caught instead by asserting
        // the invariant the sort is supposed to guarantee (HeapIndex[i] === i)
        // rather than depending on a multi-heap fixture being available.
        it('reports each GC\'s Heaps array pre-sorted by HeapIndex (HeapIndex[i] === i)', () => {
            for (const gc of gcs) {
                const heaps = gc['data']['Heaps'];
                for (let heapIndex = 0; heapIndex < heaps.length; ++heapIndex) {
                    assert.strictEqual(heaps[heapIndex]['HeapIndex'], heapIndex, `GC #${gc['data']['Id']} heap at position ${heapIndex}`);
                }
            }
        });
    });
});
