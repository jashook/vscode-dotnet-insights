import * as assert from 'assert';

import { renderEventOverviewTable, timeBreakdownSeverity } from '../../EventOverviewRenderer';

// The Overview's Time Breakdown tile colours a metric amber at >=5% and red at
// >=10%, and forces red whenever a metric's absolute total exceeds the
// capture's own wall-clock duration.
//
// That last rule exists because of a real reported bug: lock wait is summed
// across concurrently blocked threads, so it can exceed the capture length
// (744,412ms of wait in a 174,688ms capture) while the union-based percentage
// is only 13.3%. Left to the percentage alone that capture would have rendered
// a mild amber, badly understating 4.26 threads blocked on average.
describe('timeBreakdownSeverity', () => {
    it('is unstyled below the warn threshold', () => {
        assert.strictEqual(timeBreakdownSeverity(0, 0, 1000), 'none');
        assert.strictEqual(timeBreakdownSeverity(4.9, 49, 1000), 'none');
    });

    it('warns from 5% inclusive', () => {
        assert.strictEqual(timeBreakdownSeverity(5, 50, 1000), 'warn');
        assert.strictEqual(timeBreakdownSeverity(9.9, 99, 1000), 'warn');
    });

    it('alerts from 10% inclusive', () => {
        assert.strictEqual(timeBreakdownSeverity(10, 100, 1000), 'alert');
        assert.strictEqual(timeBreakdownSeverity(99, 990, 1000), 'alert');
    });

    // The rule that the percentage alone cannot express.
    it('alerts when the total exceeds capture duration, however low the percentage', () => {
        // The real capture's shape: 13.3% union, 4.26x summed wait.
        assert.strictEqual(timeBreakdownSeverity(13.3, 744412, 174688), 'alert');

        // Even a percentage that would otherwise be unstyled.
        assert.strictEqual(timeBreakdownSeverity(1, 1001, 1000), 'alert');
    });

    it('does not alert when the total merely equals capture duration', () => {
        // One thread blocked for the whole capture is 100% of one thread, not
        // a pile-up - the > is deliberate, not >=. The percentage rule still
        // makes this an alert on its own, so check the boundary via a
        // percentage that would not.
        assert.strictEqual(timeBreakdownSeverity(1, 1000, 1000), 'none');
    });

    it('never alerts on total when capture duration is unknown', () => {
        // captureDurationMSec of 0 means the caller had nothing real to
        // compare against; the percentage rules still apply.
        assert.strictEqual(timeBreakdownSeverity(1, 5000, 0), 'none');
        assert.strictEqual(timeBreakdownSeverity(12, 5000, 0), 'alert');
    });
});

describe('Time Breakdown tile rendering', () => {
    const eventOverview = { totalEventCount: 10, eventTypes: [] };

    function makeTimeBreakdown(overrides: any): any {
        return Object.assign({
            hasCaptureDuration: true,
            hasCpuSampleBreakdown: true,
            captureDurationMSec: 174688,
            gcPercent: 5.3,
            gcPauseMSec: 9234,
            contentionPercent: 13.3,
            contentionWaitMSec: 744412,
            averageThreadsBlocked: 4.26,
            idlePercent: 84.9,
            cpuBoundPercent: 15.1
        }, overrides);
    }

    // Counts tiles by their shared class, so a tile silently disappearing (or
    // being duplicated) fails rather than being masked by a substring match.
    function tileCount(html: string): number {
        return (html.match(/class="total timeBreakdownTile/g) || []).length;
    }

    it('renders one tile per metric group', () => {
        const html = renderEventOverviewTable(eventOverview, makeTimeBreakdown({}));

        assert.strictEqual(tileCount(html), 3, 'expected locks, GC and CPU tiles');
        assert.ok(html.includes('<div>Contending Locks</div>'), 'locks tile title missing');
        assert.ok(html.includes('<div>GC</div>'), 'gc tile title missing');
        assert.ok(html.includes('<div>CPU (est.)</div>'), 'cpu tile title missing');
    });

    it('colours the whole locks tile, not the values inside it', () => {
        const html = renderEventOverviewTable(eventOverview, makeTimeBreakdown({}));

        assert.ok(html.includes('class="total timeBreakdownTile timeBreakdownTileAlert"'), 'locks tile should be alert-coloured');

        // The values themselves stay plain - the tile carries the signal.
        assert.ok(html.includes('<span>13.3%</span>'), 'lock percentage should be an uncoloured value');
        assert.ok(html.includes('<span>744.4 s</span>'), 'lock total should be an uncoloured value');
    });

    it('gives each metric its own severity rather than one shared worst case', () => {
        const html = renderEventOverviewTable(eventOverview, makeTimeBreakdown({}));

        // Locks alert AND GC warn on the same render - the reason these are
        // separate tiles at all.
        assert.ok(html.includes('timeBreakdownTileAlert'), 'locks should be alert');
        assert.ok(html.includes('timeBreakdownTileWarn'), 'gc should be warn');
    });

    it('leaves healthy tiles unclassed so they keep the default lime', () => {
        const html = renderEventOverviewTable(eventOverview, makeTimeBreakdown({
            gcPercent: 1.0,
            gcPauseMSec: 1000,
            contentionPercent: 2.0,
            contentionWaitMSec: 2000,
            averageThreadsBlocked: 0.01
        }));

        assert.strictEqual(tileCount(html), 3, 'all three tiles should still render');
        assert.ok(!html.includes('timeBreakdownTileAlert'), 'healthy capture should have no alert tile');
        assert.ok(!html.includes('timeBreakdownTileWarn'), 'healthy capture should have no warn tile');
    });

    it('marks GC amber at 5% and red at 10%', () => {
        const amberHtml = renderEventOverviewTable(eventOverview, makeTimeBreakdown({ contentionPercent: 0, contentionWaitMSec: 0, gcPercent: 5.3, gcPauseMSec: 9234 }));
        assert.ok(amberHtml.includes('timeBreakdownTileWarn'), 'expected GC tile warn at 5.3%');
        assert.ok(!amberHtml.includes('timeBreakdownTileAlert'), 'nothing should alert here');

        const redHtml = renderEventOverviewTable(eventOverview, makeTimeBreakdown({ contentionPercent: 0, contentionWaitMSec: 0, gcPercent: 12.0, gcPauseMSec: 20000 }));
        assert.ok(redHtml.includes('timeBreakdownTileAlert'), 'expected GC tile alert at 12%');
    });

    // Independent gating: these used to be one tile suppressed entirely unless
    // every value was real, so a capture without CPU samples showed no timing
    // information at all.
    it('renders lock and GC tiles for a capture with no CPU samples', () => {
        const html = renderEventOverviewTable(eventOverview, makeTimeBreakdown({ hasCpuSampleBreakdown: false }));

        assert.strictEqual(tileCount(html), 2, 'expected only locks and GC tiles');
        assert.ok(html.includes('<div>Contending Locks</div>'), 'locks tile should still render');
        assert.ok(!html.includes('<div>CPU (est.)</div>'), 'cpu tile should be omitted');
    });

    it('renders only the CPU tile when the capture has no wall-clock duration', () => {
        const html = renderEventOverviewTable(eventOverview, makeTimeBreakdown({ hasCaptureDuration: false }));

        assert.strictEqual(tileCount(html), 1, 'expected only the cpu tile');
        assert.ok(html.includes('<div>CPU (est.)</div>'), 'cpu tile should render');
    });

    // A cached nettraceParser predating the absolute totals still renders.
    it('tolerates a payload with no absolute totals', () => {
        const html = renderEventOverviewTable(eventOverview, {
            hasCaptureDuration: true,
            hasCpuSampleBreakdown: true,
            captureDurationMSec: 1000,
            gcPercent: 1.0,
            contentionPercent: 2.0,
            idlePercent: 50,
            cpuBoundPercent: 50
        });

        assert.strictEqual(tileCount(html), 3, 'tiles should still render');
        assert.ok(!html.includes('undefined'), 'missing totals must not leak "undefined" into a tile');
        assert.ok(!html.includes('NaN'), 'missing totals must not leak "NaN" into a tile');
    });

    it('omits every tile when there is no time breakdown at all', () => {
        const html = renderEventOverviewTable(eventOverview, undefined);

        assert.strictEqual(tileCount(html), 0, 'no time breakdown means no tiles');
    });
});
