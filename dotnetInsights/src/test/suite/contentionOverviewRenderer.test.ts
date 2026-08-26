import * as assert from 'assert';
import * as fs from 'fs';
import * as path from 'path';

import { renderContentionOverviewPanel } from '../../ContentionRenderer';

// Exercises the Contention view's Overview tab against a REAL
// nettraceParser --json payload (fixtures/contention-overview.json, the
// "overview" block from a 3.0GB capture of a production service with 37,964
// contention events), for the reason threadingRenderer.test.ts states: the
// field names this renderer reads are produced a whole language and process
// away, in nettraceParser/Contention/ContentionOverviewBuilder.cs, and a
// synthetic fixture would be written from the same misreading of that
// contract as the code under test.
//
// That capture is a useful one to pin against: its median wait is 0.054ms and
// its p99 is 203ms - a four-thousand-fold spread - which is exactly the shape
// that makes a mean useless and this tab worth having.
//
// The correlation note gets the most attention here because it is the only
// part of the view that renders a JUDGEMENT rather than a number, and it is
// judging the very hypothesis someone opens this tab to test. Its failure mode
// is not a broken page, it is a confidently-worded sentence pointing the
// reader at a conclusion the data does not support.

// Resolved back into src/, not __dirname: tsc does not copy fixtures into the
// compiled output - same path gcDumpRenderer.test.ts and
// threadingRenderer.test.ts already take, for the same reason.
const fixturePath = path.resolve(__dirname, '..', '..', '..', 'src', 'test', 'suite', 'fixtures', 'contention-overview.json');
const realOverview = JSON.parse(fs.readFileSync(fixturePath, 'utf8'));

// A minimal overview with every key the renderer touches, so a test can vary
// one thing without the rest going undefined.
function makeOverview(overrides: any): any {
    return Object.assign({
        contentionCount: 100,
        totalWaitMSec: 1000,
        meanWaitMSec: 10,
        waitPercentiles: { p50: 1, p75: 2, p90: 5, p95: 8, p99: 50, p999: 90, max: 100, maxStartMSec: 1234, maxThreadId: 77 },
        blockedWallClockMSec: 500,
        hasCaptureDuration: true,
        captureDurationMSec: 10000,
        blockedWallClockPercent: 5,
        averageThreadsBlocked: 0.1,
        peakThreadsBlocked: 4,
        peakThreadsBlockedAtMSec: 2000,
        minRelativeMSec: 0,
        bucketDurationMSec: 100,
        bucketCount: 100,
        gridSource: "cpu",
        hasCpuSamples: true,
        hasSampleTypeData: true,
        samplingSemantics: "wallClock",
        hasGcPauses: true,
        cpuSeries: [
            { id: "allSamples", label: "All samples", definition: "d", bias: "overcounts", totalSamples: 1000, hasCorrelation: true, coefficient: 0.1, bestLagCoefficient: 0.1, bestLagBuckets: 0, bestLagMSec: 0, samplesByBucket: [] },
            { id: "managedRunning", label: "Managed and not parked", definition: "d", bias: "undercounts", totalSamples: 100, hasCorrelation: true, coefficient: 0.12, bestLagCoefficient: 0.12, bestLagBuckets: 0, bestLagMSec: 0, samplesByBucket: [] },
        ],
        correlation: { agreement: "inconclusive", minCoefficient: 0.1, maxCoefficient: 0.12, bucketCount: 100 },
    }, overrides);
}

describe('Contention Overview renderer', () => {
    it('renders every percentile tile against a real capture', () => {
        const html = renderContentionOverviewPanel(realOverview);

        assert.ok(html.indexOf('Median (p50)') !== -1, 'p50 tile missing');
        assert.ok(html.indexOf('p90') !== -1, 'p90 tile missing');
        assert.ok(html.indexOf('p99.9') !== -1, 'p99.9 tile missing');

        // 0.054ms must not render as "0.05 ms" or "0 ms" - a sub-millisecond
        // median is the normal case, and rounding it away makes the whole
        // percentile spread look like noise.
        assert.ok(html.indexOf('0.054 ms') !== -1, 'sub-millisecond median lost its precision: ' + html.slice(0, 400));
    });

    it('keeps the union and the summed wait visibly distinct', () => {
        const html = renderContentionOverviewPanel(realOverview);

        // These two are 23.17s and 744.41s for the same capture. Showing
        // either alone, or labelling them the same way, is how
        // TimeBreakdownBuilder once shipped "Contending Locks 426.1%".
        assert.ok(html.indexOf('Blocked Wall Clock') !== -1, 'union figure missing');
        assert.ok(html.indexOf('Total Wait (summed)') !== -1, 'summed figure missing or not marked as summed');
        assert.ok(html.indexOf('Avg Threads Blocked') !== -1, 'concurrency figure missing');
    });

    it('renders all three chart canvases on the shared grid', () => {
        const html = renderContentionOverviewPanel(realOverview);

        assert.ok(html.indexOf('id="contentionOverviewBlockedChart"') !== -1);
        assert.ok(html.indexOf('id="contentionOverviewPercentileChart"') !== -1);
        assert.ok(html.indexOf('id="contentionOverviewCpuChart"') !== -1);
    });

    it('states the bucket grid and where it came from', () => {
        const html = renderContentionOverviewPanel(realOverview);

        assert.ok(html.indexOf('100 buckets') !== -1, 'bucket count not stated');
        assert.ok(html.indexOf('taken directly from the CPU sample timeline') !== -1, 'grid provenance not stated');
    });

    it('says so, and draws no chart, when the capture has no CPU samples', () => {
        const html = renderContentionOverviewPanel(makeOverview({ hasCpuSamples: false, correlation: null }));

        assert.ok(html.indexOf('id="contentionOverviewCpuChart"') === -1, 'CPU canvas rendered with no CPU data');
        assert.ok(html.indexOf('no CPU samples') !== -1, 'absence of CPU data not explained');
    });

    // The real capture's four CPU definitions produce r from -0.598 to +0.248 -
    // OPPOSITE SIGNS - because a v5 capture has no native stacks and so cannot
    // separate "parked in a syscall" from "running native code". Reporting any
    // one of them as "the" correlation would answer a question the capture does
    // not settle, which is the single most damaging thing this panel could do.
    it('withholds a headline coefficient when the definitions disagree', () => {
        const html = renderContentionOverviewPanel(realOverview);

        assert.ok(html.indexOf('The definitions disagree, so no single coefficient is reported') !== -1, 'a disagreement was not reported as such');
        assert.ok(html.indexOf('-0.598') !== -1 && html.indexOf('0.248') !== -1, 'the range of coefficients was not shown');
        assert.ok(html.indexOf('opposite signs') !== -1, 'the sign conflict was not named');
        assert.ok(html.indexOf('Pearson r = ') === -1, 'a single headline coefficient was rendered despite disagreement');
    });

    it('shows every candidate definition with its own r and its known bias', () => {
        const html = renderContentionOverviewPanel(realOverview);

        for (const label of ['All samples', 'Not parked in a known primitive', 'Executing managed code', 'Managed and not parked']) {
            assert.ok(html.indexOf(label) !== -1, 'missing CPU definition row: ' + label);
        }

        assert.ok(html.indexOf('overcounts') !== -1, 'a definition did not disclose its bias');
        assert.ok(html.indexOf('undercounts') !== -1, 'a definition did not disclose its bias');
    });

    // The root cause, not just the symptom. Someone looking at contradictory
    // numbers needs to be told why, and what capture mode does not have the
    // problem.
    it('explains that wall-clock sampling and missing native stacks are the cause', () => {
        const html = renderContentionOverviewPanel(realOverview);

        assert.ok(html.indexOf('wall-clock per thread') !== -1, 'the sampling semantics were not explained');
        assert.ok(html.indexOf('no native stacks') !== -1, 'the missing-native-stacks cause was not named');
        assert.ok(html.indexOf('collect-linux') !== -1, 'the capture mode that avoids this was not named');
    });

    // A perf-sampled capture genuinely does measure CPU time, so it must NOT
    // carry the wall-clock warning - that would train readers to ignore it.
    it('states that a perf-sampled capture really is CPU time', () => {
        const html = renderContentionOverviewPanel(makeOverview({
            samplingSemantics: "cpuTime",
            correlation: { agreement: "agree", minCoefficient: 0.7, maxCoefficient: 0.8, bucketCount: 100 },
        }));

        assert.ok(html.indexOf('These samples are real CPU time') !== -1, 'a cpu-time capture was not identified as such');
        assert.ok(html.indexOf('wall-clock per thread') === -1, 'a cpu-time capture carried the wall-clock caveat');
    });

    it('names a shared negative sign when every definition agrees on it', () => {
        const html = renderContentionOverviewPanel(makeOverview({
            correlation: { agreement: "agree", minCoefficient: -0.8, maxCoefficient: -0.5, bucketCount: 100 },
        }));

        assert.ok(html.indexOf('Every definition agrees on the sign (negative)') !== -1);
        assert.ok(html.indexOf('blocking and CPU trade off') !== -1, 'the physical explanation for a negative r was dropped');
    });

    it('reports a weak but consistent result as inconclusive', () => {
        const html = renderContentionOverviewPanel(makeOverview({
            correlation: { agreement: "inconclusive", minCoefficient: 0.003, maxCoefficient: 0.207, bucketCount: 100 },
        }));

        assert.ok(html.indexOf('none shows a meaningful relationship') !== -1);
        assert.ok(html.indexOf('below the 0.3 magnitude') !== -1, 'the threshold was not stated');
    });

    it('keeps the not-a-significance-test caveat whatever the verdict', () => {
        for (const agreement of ['disagree', 'agree', 'inconclusive', 'single']) {
            const html = renderContentionOverviewPanel(makeOverview({
                correlation: { agreement: agreement, minCoefficient: -0.4, maxCoefficient: 0.6, bucketCount: 100 },
            }));

            assert.ok(html.indexOf('not significance tests, and not evidence of causation') !== -1, 'caveat dropped for verdict: ' + agreement);
        }
    });

    // Zero variance means r is undefined, not zero. Rendering "r = 0.000"
    // would read as "measured, no relationship".
    it('distinguishes an unmeasurable correlation from a zero one', () => {
        const html = renderContentionOverviewPanel(makeOverview({ correlation: null }));

        assert.ok(html.indexOf('undefined rather than zero') !== -1, 'an unmeasurable correlation was not distinguished');
    });

    // The chart must let the reader change the definition, since the view
    // itself refuses to pick one on their behalf.
    it('offers the CPU definition as a control, defaulting to the unambiguous one', () => {
        const html = renderContentionOverviewPanel(realOverview);

        assert.ok(html.indexOf('id="contentionOverviewCpuSeriesSelect"') !== -1, 'no CPU definition selector');
        assert.ok(html.indexOf('value="managedRunning" selected') !== -1, 'wall-clock capture did not default to the unambiguous definition');
    });

    it('defaults a perf-sampled capture to all samples', () => {
        const html = renderContentionOverviewPanel(makeOverview({ samplingSemantics: "cpuTime" }));

        assert.ok(html.indexOf('value="allSamples" selected') !== -1, 'cpu-time capture did not default to all samples');
    });

    it('renders n/a rather than a number when the capture has no duration', () => {
        const html = renderContentionOverviewPanel(makeOverview({ hasCaptureDuration: false, blockedWallClockPercent: 0 }));

        assert.ok(html.indexOf('% of Capture<span>n/a</span>') !== -1, 'a percentage of an unknown duration was rendered as a number');
    });

    it('names the worst wait its own thread and start time', () => {
        const html = renderContentionOverviewPanel(realOverview);

        assert.ok(html.indexOf('Worst Single Wait') !== -1);
        assert.ok(html.indexOf('129,056') !== -1, 'blocked thread id missing');
    });

    it('says unknown, not thread 0, when the worst wait has no thread', () => {
        const overview = makeOverview({});
        overview["waitPercentiles"] = Object.assign({}, overview["waitPercentiles"], { maxThreadId: 0 });

        const html = renderContentionOverviewPanel(overview);

        assert.ok(html.indexOf('Blocked Thread<span>unknown</span>') !== -1, 'thread 0 rendered as a real thread id');
    });
});
