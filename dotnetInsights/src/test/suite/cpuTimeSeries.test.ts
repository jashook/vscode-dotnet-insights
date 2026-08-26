import * as assert from 'assert';

import { renderCpuProfileView, formatCorePercent, formatCpuSeconds, renderCoverageLine } from '../../CpuProfileRenderer';

// Covers the CPU-time overlay on the Profile view's timeline chart.
//
// The thing being protected here is a REFUSAL. A sample count is only CPU time
// when the sampler is driven by CPU time, and only a `dotnet-trace
// collect-linux` capture that actually used perf's cpu-clock sampling is - the
// .NET runtime's own sampler walks every thread on a wall clock whether or not
// it holds a core. Multiplying those counts by anything produces a confident,
// plausible, entirely fabricated number of CPU-seconds, so the wall-clock case
// must render no cores series at all and must say why.
//
// It is not a hypothetical distinction: two real collect-linux captures of the
// same service differed on it, because naming any of
// --profile/--providers/--clrevents/--perf-events silently disables perf CPU
// sampling and falls back to the runtime sampler.

function makeCpuProfile(overrides: any): any {
    return Object.assign({
        totalSampleCount: 1000,
        hotMethods: [],
        methodNames: [],
        categories: null,
        sampleTimeline: {
            minRelativeMSec: 0,
            totalDurationMSec: 10000,
            bucketDurationMSec: 100,
            bucketCount: 100,
            samplesByBucket: new Array(100).fill(10),
            methodSelfByBucket: [],
        },
        cpuTime: {
            hasCpuTime: true,
            sampler: "perf cpu-clock",
            samplePeriodMSec: 1.0003,
            totalCpuMSec: 1091286.9,
            averageCoresBusy: 3.6421,
            processorCount: 64,
            coresBusyByBucket: new Array(100).fill(3.64),
        },
    }, overrides);
}

describe('CPU time series', () => {
    it('reports CPU seconds, cores and the machine size on a cpu-clock capture', () => {
        const html = renderCpuProfileView(makeCpuProfile({}));

        assert.ok(html.indexOf('1091.3 CPU-seconds') !== -1, 'total CPU time not stated');
        assert.ok(html.indexOf('3.64 cores') !== -1, 'average cores not stated');
        assert.ok(html.indexOf('of 64') !== -1, 'processor count not stated');
    });

    // "3.64 cores" is the kind of figure that gets quoted onward, so the fact
    // that the per-sample period was MEASURED rather than assumed has to
    // travel with it - a capture taken at a non-default rate would otherwise
    // be wrong by exactly that ratio with nothing on screen to suggest it.
    it('states that the sample period was measured, not assumed', () => {
        const html = renderCpuProfileView(makeCpuProfile({}));

        assert.ok(html.indexOf('1.000 ms') !== -1, 'sample period not shown');
        assert.ok(html.indexOf('rather than assumed') !== -1, 'the period was not disclosed as measured');
        assert.ok(html.indexOf('cpu-clock') !== -1, 'the sampler was not named');
    });

    // THE REFUSAL. No cores figure of any kind may appear on a wall-clock
    // capture, and the note must name the capture mode that would provide one.
    it('renders no cores figure at all on a wall-clock capture', () => {
        const html = renderCpuProfileView(makeCpuProfile({
            cpuTime: {
                hasCpuTime: false,
                sampler: "runtime wall-clock",
                samplePeriodMSec: 0,
                totalCpuMSec: 0,
                averageCoresBusy: 0,
                processorCount: 64,
                coresBusyByBucket: [],
            },
        }));

        assert.ok(html.indexOf('CPU-seconds') === -1, 'a CPU-seconds figure was rendered for a wall-clock capture');
        assert.ok(html.indexOf('cores busy') === -1, 'a cores figure was rendered for a wall-clock capture');
        assert.ok(html.indexOf('not CPU utilisation') !== -1, 'the chart did not disclaim being CPU utilisation');
        assert.ok(html.indexOf('collect-linux') !== -1, 'the capture mode that would provide CPU time was not named');
    });

    // An older parser build emits no cpuTime block at all. That must degrade to
    // the wall-clock wording rather than throwing or claiming CPU time.
    it('treats a missing cpuTime block as no CPU time', () => {
        const html = renderCpuProfileView(makeCpuProfile({ cpuTime: undefined }));

        assert.ok(html.indexOf('CPU-seconds') === -1);
        assert.ok(html.indexOf('not CPU utilisation') !== -1);
    });

    // Nothing to say about a chart that is not drawn.
    it('says nothing about CPU time when there is no timeline at all', () => {
        const html = renderCpuProfileView(makeCpuProfile({ sampleTimeline: null }));

        assert.ok(html.indexOf('not CPU utilisation') === -1, 'a note was rendered without a chart to annotate');
        assert.ok(html.indexOf('CPU-seconds') === -1);
    });

    // The samples line subtracts idle/hidden methods; the cores line must not,
    // because on a cpu-clock capture every sample is already on-CPU time and
    // subtracting "blocking primitive" leaves would remove real CPU work. The
    // two lines therefore mean different things on the same chart, and the
    // note is the only place that says so.
    it('explains why the two lines exclude different things', () => {
        const html = renderCpuProfileView(makeCpuProfile({}));

        assert.ok(html.indexOf('the cores line deliberately does not') !== -1, 'the exclusion difference was not explained');
    });

    // The per-row "approximate core %" column: samples x period / window,
    // expressed as a share of ONE core. 100% is one core saturated.
    it('converts samples to a share of one core', () => {
        // 1000 samples of 1ms over a 10,000ms window = 1000ms of CPU = 10%.
        assert.strictEqual(formatCorePercent(1000, 1.0, 10000), '10.00%');

        // A row that ran on more than one core at once legitimately exceeds
        // 100% and must NOT be clamped.
        assert.strictEqual(formatCorePercent(25000, 1.0, 10000), '250.00%');
    });

    // Rows costing less than 0.01% of a core would all render "0.00%" and read
    // as free. A less-than marker keeps them distinct from a true zero.
    it('distinguishes a tiny cost from no cost', () => {
        assert.strictEqual(formatCorePercent(0, 1.0, 10000), '0.00%');
        assert.strictEqual(formatCorePercent(1, 1.0, 10000000), '<0.01%');
    });

    it('returns a dash rather than a number when there is no CPU-time quantum', () => {
        assert.strictEqual(formatCorePercent(1000, 0, 10000), '\u2014');
        assert.strictEqual(formatCorePercent(1000, 1.0, 0), '\u2014');
    });

    it('adds the core column to both tables on a cpu-clock capture', () => {
        const html = renderCpuProfileView(makeCpuProfile({
            hotMethods: [{ frame: 0, selfSamples: 500, totalSamples: 900, selfPercent: 50, totalPercent: 90 }],
            methodNames: ['Some.Method'],
            categories: { totalSamples: 1000, rows: [{ id: 0, name: 'Garbage collection', description: 'd', selfSamples: 500, onStackSamples: 700, selfPercent: 50, onStackPercent: 70, topMethods: [] }] },
        }));

        assert.ok(html.indexOf('\u2248 Core %') !== -1, 'core column header missing');
        assert.ok(html.indexOf('data-has-core-percent="true"') !== -1, 'tables not marked as carrying the column');
    });

    // No quantum, no column - not a column full of dashes, and not one
    // computed from a thread-time count.
    it('omits the core column entirely on a wall-clock capture', () => {
        const html = renderCpuProfileView(makeCpuProfile({
            cpuTime: { hasCpuTime: false, sampler: 'runtime wall-clock', samplePeriodMSec: 0, totalCpuMSec: 0, averageCoresBusy: 0, processorCount: 64, coresBusyByBucket: [] },
            hotMethods: [{ frame: 0, selfSamples: 500, totalSamples: 900, selfPercent: 50, totalPercent: 90 }],
            methodNames: ['Some.Method'],
            categories: { totalSamples: 1000, rows: [{ id: 0, name: 'Garbage collection', description: 'd', selfSamples: 500, onStackSamples: 700, selfPercent: 50, onStackPercent: 70, topMethods: [] }] },
        }));

        assert.ok(html.indexOf('\u2248 Core %') === -1, 'core column rendered without a CPU-time quantum');
        assert.ok(html.indexOf('data-has-core-percent="false"') !== -1, 'tables not marked as lacking the column');
    });

    // Cumulative coverage: "the top N methods are X% of CPU", the question a
    // ranked list cannot answer about itself.
    it('accumulates self percentages down the ranking', () => {
        const hotMethods = [
            { selfSamples: 400 }, { selfSamples: 300 }, { selfSamples: 200 }, { selfSamples: 100 },
        ];

        const line = renderCoverageLine(hotMethods, 2000);

        // 400/2000 = 20%, +300 = 35%, ... but only the 10/50/200 marks are
        // reported, and none is reached here.
        assert.strictEqual(line, '', 'a coverage line was emitted with fewer methods than its first mark');
    });

    it('reports the marks it actually reaches', () => {
        const hotMethods = [];
        for (let index = 0; index < 60; ++index) {
            hotMethods.push({ selfSamples: 100 });
        }

        // 60 methods x 100 = 6000 of a 12000-sample capture.
        const line = renderCoverageLine(hotMethods, 12000);

        // top 10 = 1000/12000 = 8.3%; top 50 = 5000/12000 = 41.7%.
        assert.ok(line.indexOf('top 10 = <strong>8.3%</strong>') !== -1, 'top-10 mark wrong: ' + line);
        assert.ok(line.indexOf('top 50 = <strong>41.7%</strong>') !== -1, 'top-50 mark wrong: ' + line);
        assert.ok(line.indexOf('top 200') === -1, 'reported a mark the ranking never reached');
    });

    // A threshold looked obvious and did not survive measurement: top-10 share
    // ran 9.8% on a collect-linux capture and 95.9% on two v5 ones, because v5
    // symbolicates only managed leaves. Any verdict would describe the
    // profiler, not the program.
    it('states no flat-or-peaked verdict', () => {
        const hotMethods = [];
        for (let index = 0; index < 10; ++index) {
            hotMethods.push({ selfSamples: 1 });
        }

        const flat = renderCoverageLine(hotMethods, 100000);
        const peaked = renderCoverageLine(hotMethods, 10);

        for (const line of [flat, peaked]) {
            assert.ok(line.indexOf('flat') === -1, 'a flatness verdict was rendered');
            assert.ok(line.indexOf('no single hot spot') === -1, 'a hot-spot verdict was rendered');
        }
    });

    it('emits nothing when there is nothing to measure', () => {
        assert.strictEqual(renderCoverageLine([], 1000), '');
        assert.strictEqual(renderCoverageLine([{ selfSamples: 10 }], 0), '');
        assert.strictEqual(renderCoverageLine(null, 1000), '');
    });

    it('gives every method row a coverage cell', () => {
        const hotMethods = [];
        for (let index = 0; index < 3; ++index) {
            hotMethods.push({ frame: 0, selfSamples: 100, totalSamples: 100, selfPercent: 10, totalPercent: 10 });
        }

        const html = renderCpuProfileView(makeCpuProfile({ hotMethods: hotMethods, methodNames: ['M'] }));

        assert.ok(html.indexOf('Coverage %') !== -1, 'coverage column header missing');
        assert.strictEqual((html.match(/class="coveragePercentCell"/g) || []).length, 3, 'not one coverage cell per row');
    });

    // "Coverage %", not "Cum %": in pprof and perf "cum" means INCLUSIVE time,
    // which is this table's own Total % column. Reusing the name next to the
    // real inclusive column misread in practice.
    it('does not call the coverage column "Cum"', () => {
        const html = renderCpuProfileView(makeCpuProfile({
            hotMethods: [{ frame: 0, selfSamples: 100, totalSamples: 100, selfPercent: 10, totalPercent: 10 }],
            methodNames: ['M'],
        }));

        assert.ok(html.indexOf('Cum %') === -1, 'the ambiguous "Cum %" label is still emitted');
    });

    // Sorting BY a running total would reorder the table by a number that only
    // existed because of the previous order.
    it('marks the coverage column unsortable', () => {
        const html = renderCpuProfileView(makeCpuProfile({
            hotMethods: [{ frame: 0, selfSamples: 100, totalSamples: 100, selfPercent: 10, totalPercent: 10 }],
            methodNames: ['M'],
        }));

        assert.ok(html.indexOf('<th class="unsortableColumn"><span class="thLabel">Coverage %</span></th>') !== -1,
            'coverage column is not marked unsortable');
        assert.ok(html.indexOf('data-sort="none"') === -1, 'the sentinel leaked into the markup as a real sort type');
    });

    // Coverage % accumulates full-precision percentages, not the rounded cell
    // text: summing 200 values each rounded to 2dp drifted the running total by
    // 0.06pp, so the same table read 45.69 before a sort and 45.63 after one.
    it('carries a full-precision self percentage for the running total', () => {
        // Three equal rows: Self % is 1/3 of the SHOWN rows, a repeating
        // decimal, so a rounded stash is visible immediately.
        const html = renderCpuProfileView(makeCpuProfile({
            hotMethods: [
                { frame: 0, selfSamples: 1, totalSamples: 1, selfPercent: 0, totalPercent: 0 },
                { frame: 0, selfSamples: 1, totalSamples: 1, selfPercent: 0, totalPercent: 0 },
                { frame: 0, selfSamples: 1, totalSamples: 1, selfPercent: 0, totalPercent: 0 },
            ],
            methodNames: ['M'],
            totalSampleCount: 9,
        }));

        const match = html.match(/data-self-percent="([^"]+)"/);
        assert.ok(match, 'no full-precision self percentage on the row');
        assert.ok(match![1].indexOf('33.333') === 0,
            'self percentage was rounded before being stashed: ' + match![1]);
    });

    // CPU seconds is the same quantity as Self % and Core % - all three are
    // selfSamples times a constant - but it is the unit a finding gets written
    // down in.
    it('converts samples to absolute CPU seconds', () => {
        assert.strictEqual(formatCpuSeconds(1000, 1.0), '1.00');
        assert.strictEqual(formatCpuSeconds(16615, 1.0003), '16.62');
        assert.strictEqual(formatCpuSeconds(1, 1.0), '<0.01');
        assert.strictEqual(formatCpuSeconds(1000, 0), '\u2014');
    });

    // Self % is a share of the ROWS SHOWN, so the column sums to 100% - and
    // Total % must use the same denominator or a row can print Self % greater
    // than its own Total %, which is impossible in one basis.
    it('renders Self % against the shown rows, with Total % on the same basis', () => {
        const html = renderCpuProfileView(makeCpuProfile({
            hotMethods: [
                { frame: 0, selfSamples: 300, totalSamples: 400, selfPercent: 0, totalPercent: 0 },
                { frame: 0, selfSamples: 100, totalSamples: 150, selfPercent: 0, totalPercent: 0 },
            ],
            methodNames: ['M'],
            totalSampleCount: 100000,
        }));

        // 300 and 100 of 400 shown self samples.
        assert.ok(html.indexOf('<td>75.00</td>') !== -1, 'Self % is not a share of the shown rows: ' + html.slice(html.indexOf('<tr class="typeRow'), 900));
        assert.ok(html.indexOf('<td>25.00</td>') !== -1, 'second row Self % wrong');
        // Total % on the same basis: 400/400 and 150/400.
        assert.ok(html.indexOf('<td>100.00</td>') !== -1, 'Total % is not on the Self % basis');
        assert.ok(html.indexOf('<td>37.50</td>') !== -1, 'second row Total % wrong');
    });

    // The coverage line is the ONE place the absolute share of the capture
    // survives, now that the columns are relative. A table of the 200 coldest
    // methods would still show Coverage % ending at 100%.
    it('keeps the coverage line on the whole-capture basis', () => {
        const line = renderCoverageLine(
            Array.from({ length: 10 }, () => ({ selfSamples: 100 })),
            100000);

        // 1000 of 100,000 samples, NOT 100% of the ten rows shown.
        assert.ok(line.indexOf('top 10 = <strong>1.0%</strong>') !== -1, 'coverage line is not absolute: ' + line);
        assert.ok(line.indexOf("total</em> CPU") !== -1, 'coverage line does not say which basis it uses');
    });
});
