import * as assert from 'assert';
import * as vscode from 'vscode';

import { renderNettraceDiffWebview } from '../../NettraceDiffRenderer';

// Minimal webview/uri stubs - the renderer only needs asWebviewUri and
// cspSource, matching the pattern allocationSummaryRenderer.test.ts uses.
function makeWebviewStub(): any {
    return {
        asWebviewUri: (uri: any) => uri,
        cspSource: 'vscode-resource:'
    };
}

// A real Uri, not a stub - the renderer calls vscode.Uri.joinPath on it,
// which rejects anything without a proper path. Matches how
// allocationSummaryRenderer.test.ts passes vscode.Uri.file('/fake/ext').
function makeExtensionUriStub(): any {
    return vscode.Uri.file('/fake/ext');
}

function makeCapture(overrides: any = {}): any {
    return Object.assign({
        filePath: '/tmp/capture.nettrace',
        processName: 'capture',
        captureDurationMSec: 60000,
        totalEventCount: 1000,
        totalGcCount: 10,
        totalGcPauseMSec: 100,
        totalAllocationTickCount: 500,
        totalAllocatedBytes: 1024 * 1024,
        totalExceptionCount: 5,
        totalCpuSampleCount: 200,
        totalContentionCount: 3,
        totalContentionWaitMSec: 12,
        hasTimeBreakdown: true,
        gcPercent: 1.0,
        contentionPercent: 0.1,
        hasCpuBreakdown: true,
        idlePercent: 90,
        cpuBoundPercent: 10,
        gcGenerations: []
    }, overrides);
}

function makeRow(name: string, overrides: any = {}): any {
    return Object.assign({
        name: name,
        kind: 'matched',
        baselineCount: 1,
        comparisonCount: 2,
        baselineAmount: 100,
        comparisonAmount: 200,
        deltaCount: 1,
        deltaAmount: 100,
        baselineAmountPerSecond: 1,
        comparisonAmountPerSecond: 2,
        deltaAmountPerSecond: 1,
        percentChange: 100
    }, overrides);
}

function makeDiff(overrides: any = {}): any {
    return Object.assign({
        payloadKind: 'nettraceDiff',
        baseline: makeCapture({ processName: 'before' }),
        comparison: makeCapture({ processName: 'after' }),
        coverage: {
            gc: { baselineHasData: true, comparisonHasData: true, comparable: true },
            allocations: { baselineHasData: true, comparisonHasData: true, comparable: true },
            exceptions: { baselineHasData: true, comparisonHasData: true, comparable: true },
            cpu: { baselineHasData: true, comparisonHasData: true, comparable: true },
            contention: { baselineHasData: true, comparisonHasData: true, comparable: true }
        },
        eventTypes: [makeRow('Provider/Event')],
        allocationTypes: [makeRow('System.String')],
        exceptionTypes: [makeRow('System.InvalidOperationException')],
        cpuMethods: [makeRow('MyApp.Work')],
        contentionSites: [makeRow('MyApp.Lock')],
        locks: [makeRow('MyApp.Lock')]
    }, overrides);
}

describe('NettraceDiffRenderer', () => {
    it('renders both capture names and a nav button per dimension', () => {
        const html = renderNettraceDiffWebview(makeWebviewStub(), makeExtensionUriStub(), makeDiff());

        assert.ok(html.indexOf('before') !== -1, 'baseline process name missing');
        assert.ok(html.indexOf('after') !== -1, 'comparison process name missing');

        for (const view of ['summary', 'allocations', 'exceptions', 'cpu', 'contention', 'locks', 'events']) {
            assert.ok(html.indexOf(`data-diffview="${view}"`) !== -1, `missing nav button for ${view}`);
        }
    });

    it('each table puts its header inside a thead', () => {
        // A bare <tr> in <table> lands in an implicit tbody, which the media
        // script then overwrites when it renders rows - the headers vanish
        // and with them every sort affordance. This pins the fix.
        const html = renderNettraceDiffWebview(makeWebviewStub(), makeExtensionUriStub(), makeDiff());

        const tableCount = (html.match(/data-diff-table="/g) || []).length;
        const theadCount = (html.match(/<thead>/g) || []).length;

        assert.strictEqual(theadCount, tableCount, 'every diff table needs an explicit thead');
        assert.ok(html.indexOf('<thead><tr class="tableHeader">') !== -1, 'header row must be inside thead');
    });

    it('normalization defaults on only when the captures differ in length', () => {
        const sameLength = renderNettraceDiffWebview(makeWebviewStub(), makeExtensionUriStub(), makeDiff());
        assert.ok(sameLength.indexOf('id="diffNormalizeToggle" ') !== -1, 'toggle should be present');
        assert.ok(sameLength.indexOf('id="diffNormalizeToggle" checked') === -1, 'equal-length captures should default to absolute');

        const differentLength = renderNettraceDiffWebview(makeWebviewStub(), makeExtensionUriStub(), makeDiff({
            baseline: makeCapture({ processName: 'before', captureDurationMSec: 60000 }),
            comparison: makeCapture({ processName: 'after', captureDurationMSec: 300000 })
        }));
        assert.ok(differentLength.indexOf('id="diffNormalizeToggle" checked') !== -1, 'unequal-length captures should default to per-second');
    });

    it('a dimension missing from one capture is called out as not comparable', () => {
        // The distinction that matters: a provider that was never enabled is
        // not the same as behavior that appeared.
        const html = renderNettraceDiffWebview(makeWebviewStub(), makeExtensionUriStub(), makeDiff({
            coverage: {
                contention: { baselineHasData: false, comparisonHasData: true, comparable: false }
            }
        }));

        assert.ok(html.indexOf('Not comparable') !== -1, 'summary should warn about the missing dimension');
        assert.ok(html.indexOf('diffCoverageWarning') !== -1, 'warning element missing');
        assert.ok(html.indexOf('not a change in behavior') !== -1, 'warning should explain what it means');
    });

    it('payload is embedded for the media script and script-escaped', () => {
        const html = renderNettraceDiffWebview(makeWebviewStub(), makeExtensionUriStub(), makeDiff({
            allocationTypes: [makeRow('List<System.String>')]
        }));

        assert.ok(html.indexOf('id="diffPayload"') !== -1, 'payload block missing');
        // A raw "<" inside the JSON could terminate the script block early.
        assert.ok(html.indexOf('List\\u003cSystem.String>') !== -1, 'payload must be script-escaped');
    });
});
