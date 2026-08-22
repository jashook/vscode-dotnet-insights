import * as assert from 'assert';
import * as fs from 'fs';
import * as path from 'path';
import * as vscode from 'vscode';

import { renderGcDumpWebview } from '../../GcDumpRenderer';

// Exercises the .gcdump renderer against a REAL nettraceParser --gcdump --json
// payload (fixtures/gcdump-analysis.json, trimmed from an actual
// dotnet-gcdump capture of an ASP.NET process), not a synthetic object.
//
// This follows the same reasoning as gcStatsCalculations.test.ts's real-fixture
// pattern, and it matters more here than usual: the renderer and
// media/gcDumpView.js read field names that are produced a whole language and
// process away, in nettraceParser/GcDump/GcDumpJsonExporter.cs. A synthetic
// fixture would be written from the same misreading of that contract as the
// code under test, and would happily pass while the real payload rendered an
// empty table. Several of the assertions below are specifically contract
// checks on that payload's shape rather than checks on the HTML.

function makeWebviewStub(): any {
    return {
        asWebviewUri: (uri: any) => uri,
        cspSource: 'vscode-resource:'
    };
}

function makeExtensionUriStub(): any {
    return vscode.Uri.file('/fake/ext');
}

function loadFixture(): any {
    // Resolved back out of out/ into src/, matching how
    // gcStatsCalculations.test.ts reaches its own fixture - the build does not
    // copy fixtures into the compiled output.
    const fixturePath = path.resolve(__dirname, '..', '..', '..', 'src', 'test', 'suite', 'fixtures', 'gcdump-analysis.json');
    return JSON.parse(fs.readFileSync(fixturePath).toString());
}

describe('GcDumpRenderer', () => {
    it('renders the three heap views', () => {
        const html = renderGcDumpWebview('heap.gcdump', makeWebviewStub(), makeExtensionUriStub(), loadFixture());

        assert.ok(html.indexOf('data-view="census"') >= 0, 'census view missing');
        assert.ok(html.indexOf('data-view="retained"') >= 0, 'retained view missing');
        assert.ok(html.indexOf('data-view="references"') >= 0, 'references view missing');

        // Retention paths were their own tab driven by a <select>; they are now
        // what a type row in the two tables above expands INTO, the same move
        // the Profile view made when its separate Drill Down tab became inline
        // caller trees. Asserted as an absence because the alternative - both
        // the tab and the inline tree - is the state this consolidation exists
        // to avoid.
        assert.ok(html.indexOf('data-view="roots"') < 0,
            'the standalone Paths to Root tab should be gone; retention paths expand inline');
        assert.ok(html.indexOf('rootTypeSelect') < 0, 'the retention-path type picker should be gone with its tab');
    });

    it('builds every table through the shared ranked-table header', () => {
        const html = renderGcDumpWebview('heap.gcdump', makeWebviewStub(), makeExtensionUriStub(), loadFixture());

        // snapshot.css's .cpuHotMethodsTable rules are positional: the hide
        // column first, the name column second (left-aligned, wrapping, and the
        // one column left auto so it absorbs leftover width), numeric columns
        // after that. Emitting the type name as column 1 - which this view used
        // to do - hands the name column's formatting to a number column and
        // leaves a 567-character generic type name unwrapped and unbounded.
        const headerCount = html.split('<tr class="tableHeader"><th class="rowHideColumn"></th>').length - 1;
        assert.strictEqual(headerCount, 3, 'all three tables should use renderRankedTableHeader');

        const firstHeader = html.substring(html.indexOf('<tr class="tableHeader">'));
        const nameCell = firstHeader.split('<th')[2];
        assert.ok(nameCell.indexOf('>Type<') >= 0, `the name column must be column 2, got: ${nameCell}`);
    });

    it('loads the shared ranked-table script the tables depend on', () => {
        const html = renderGcDumpWebview('heap.gcdump', makeWebviewStub(), makeExtensionUriStub(), loadFixture());

        // gcDumpView.js calls wireSortableTableHeaders/createRowHideController/
        // splitQualifiedName as globals from rankedTable.js. Without this tag
        // the page still renders its headers - they just do nothing when
        // clicked, which is how this view shipped with sortable-looking,
        // non-sorting columns in the first place.
        const rankedTableIndex = html.indexOf('rankedTable.js');
        const viewScriptIndex = html.indexOf('gcDumpView.js"');

        assert.ok(rankedTableIndex >= 0, 'rankedTable.js must be loaded');
        assert.ok(rankedTableIndex < viewScriptIndex, 'rankedTable.js must load before gcDumpView.js');
    });

    it('gives each table its own hide-status bar for the shared row-hide controller', () => {
        const html = renderGcDumpWebview('heap.gcdump', makeWebviewStub(), makeExtensionUriStub(), loadFixture());

        // createRowHideController finds these by id; a missing one silently
        // hides rows with nothing on screen saying so.
        for (const viewId of ['census', 'retained', 'reference']) {
            assert.ok(html.indexOf(`id="${viewId}HideStatus"`) >= 0, `${viewId} hide status bar missing`);
            assert.ok(html.indexOf(`id="${viewId}HideStatusLabel"`) >= 0, `${viewId} hide status label missing`);
            assert.ok(html.indexOf(`id="${viewId}ShowAllBtn"`) >= 0, `${viewId} show-all button missing`);
            assert.ok(html.indexOf(`id="${viewId}TableBody"`) >= 0, `${viewId} table body missing`);
        }
    });

    it('uses the wrapper table structure snapshot.css sizes columns through', () => {
        const html = renderGcDumpWebview('heap.gcdump', makeWebviewStub(), makeExtensionUriStub(), loadFixture());

        // snapshot.css selects `.cpuHotMethodsTable > table > tbody > tr > th`.
        // If the class lands on the <table> itself, every column-width rule
        // silently misses and the numeric columns stop lining up with the
        // nested trees - a purely visual break that no other assertion here
        // would catch.
        assert.ok(html.indexOf('<div class="detailTable cpuHotMethodsTable">') >= 0,
            'ranked tables must be wrapped in a .detailTable.cpuHotMethodsTable div');
        assert.ok(html.indexOf('<table class="cpuHotMethodsTable"') < 0,
            'the ranked-table class must not be on the <table> element itself');
    });

    it('embeds the payload so the webview script can read it', () => {
        const fixture = loadFixture();
        const html = renderGcDumpWebview('heap.gcdump', makeWebviewStub(), makeExtensionUriStub(), fixture);

        const openTag = '<script type="application/json" id="gcDumpJson">';
        const start = html.indexOf(openTag);
        assert.ok(start >= 0, 'payload script block missing');

        const end = html.indexOf('</script>', start);
        const embedded = JSON.parse(html.substring(start + openTag.length, end));

        assert.strictEqual(embedded.types.length, fixture.types.length);
        assert.strictEqual(embedded.typeNames.length, fixture.typeNames.length);
    });

    it('reports heap totals in the summary tiles', () => {
        const fixture = loadFixture();
        const html = renderGcDumpWebview('heap.gcdump', makeWebviewStub(), makeExtensionUriStub(), fixture);

        assert.ok(html.indexOf('Heap Size') >= 0);
        assert.ok(html.indexOf('Objects') >= 0);
        assert.ok(html.indexOf(fixture.summary.totalObjects.toLocaleString('en-US')) >= 0,
            'object count should appear in the summary tiles');
    });

    it('surfaces unrooted objects rather than hiding them', () => {
        const fixture = loadFixture();
        assert.ok(fixture.summary.unreachableObjects > 0,
            'fixture should contain unrooted objects - real dotnet-gcdump captures do');

        const html = renderGcDumpWebview('heap.gcdump', makeWebviewStub(), makeExtensionUriStub(), fixture);
        assert.ok(html.indexOf('Unrooted') >= 0, 'unrooted-object tile missing');

        // Objects and bytes can diverge enormously (98.5% of objects but 99.2%
        // of bytes on one real capture), and it is the byte figure that
        // explains a Retained column not adding up to the heap size.
        if (fixture.summary.unreachableBytes > 0) {
            assert.ok(html.indexOf('% of bytes') >= 0, 'unrooted tile should report the byte share too');
        }
    });

    it('falls back to the file name when the dump carries no process name', () => {
        const fixture = loadFixture();

        // dotnet-gcdump does not write ProcessName/MachineName/TimeCollected -
        // verified against real captures. The header has to degrade to
        // something useful rather than rendering an empty heading.
        assert.strictEqual(fixture.metadata.processName, '');

        const html = renderGcDumpWebview('my-heap.gcdump', makeWebviewStub(), makeExtensionUriStub(), fixture);
        assert.ok(html.indexOf('my-heap.gcdump') >= 0, 'file name should be used as the heading');
    });

    it('escapes generic type names instead of emitting raw angle brackets', () => {
        const fixture = loadFixture();

        // Type pool entries are interpolated into the document by the webview,
        // but the heading and any renderer-side text are not - and a generic
        // type name legitimately contains '<' and '>', so this is ordinary
        // input, not a hostile one.
        const html = renderGcDumpWebview('Dictionary<K,V>.gcdump', makeWebviewStub(), makeExtensionUriStub(), fixture);

        assert.ok(html.indexOf('Dictionary&lt;K,V&gt;.gcdump') >= 0, 'file name should be HTML-escaped');
        assert.ok(html.indexOf('<title>Dictionary<K,V>') < 0, 'raw angle brackets must not reach the document');
    });

    it('says so when the source could not read stack roots', () => {
        const fixture = loadFixture();

        // A core dump read on macOS has no stack roots (the DAC's unwind
        // crashes there), so objects held only by a running frame land in the
        // Unrooted tile. Unexplained, that reads as "these objects are
        // garbage" rather than "this tool could not see one kind of root".
        fixture.metadata.stackRootsOmitted = true;

        const html = renderGcDumpWebview('heap.dmp', makeWebviewStub(), makeExtensionUriStub(), fixture);
        assert.ok(html.indexOf('stack roots') >= 0, 'missing stack-root caveat');
    });

    it('stays quiet about stack roots when the source had them', () => {
        const html = renderGcDumpWebview('heap.gcdump', makeWebviewStub(), makeExtensionUriStub(), loadFixture());
        assert.ok(html.indexOf('could not be read from this dump') < 0,
            'a complete dump should carry no caveat');
    });

    it('renders a readable failure page instead of throwing on a null payload', () => {
        const html = renderGcDumpWebview('broken.gcdump', makeWebviewStub(), makeExtensionUriStub(), null);

        assert.ok(html.indexOf('Unable to read') >= 0);
        assert.ok(html.indexOf('broken.gcdump') >= 0);
        assert.ok(html.indexOf('output channel') >= 0, 'with no detail, point at the log');
    });

    it('puts the parser\'s own explanation on the failure page', () => {
        // The case this exists for: a valid Linux core dump opened on a Mac.
        // Nothing about "unable to read this file" suggests the fix is to
        // convert it on another host, so the reason has to be on screen.
        const failure = [
            "Could not read the managed heap in 'service-Full.dmp': Debugging a 'LINUX' crash is not supported on 'OSX'.",
            "",
            "  This dump : LINUX / X64",
            "  This host : OSX / Arm64"
        ].join("\n");

        const html = renderGcDumpWebview('service-Full.dmp', makeWebviewStub(), makeExtensionUriStub(), null, failure);

        assert.ok(html.indexOf('LINUX / X64') >= 0, 'the detail should be rendered');
        assert.ok(html.indexOf('<pre') >= 0, 'the detail is a small report, so its alignment has to survive');
    });

    it('escapes a failure message rather than letting it into the markup', () => {
        const html = renderGcDumpWebview('x.dmp', makeWebviewStub(), makeExtensionUriStub(), null, "<script>alert(1)</script>");

        assert.ok(html.indexOf('<script>alert(1)</script>') < 0, 'failure text must be escaped');
        assert.ok(html.indexOf('&lt;script&gt;') >= 0);
    });

    it('payload contract: every type reference resolves into the interned pool', () => {
        const fixture = loadFixture();
        const poolSize = fixture.typeNames.length;

        assert.strictEqual(fixture.typeModules.length, poolSize,
            'typeNames and typeModules must be parallel arrays');

        for (const row of fixture.types) {
            assert.ok(row.t >= 0 && row.t < poolSize, `census type index ${row.t} out of range`);
        }

        for (const edge of fixture.outgoingReferences) {
            assert.ok(edge.f >= 0 && edge.f < poolSize, `edge from-index ${edge.f} out of range`);
            assert.ok(edge.t >= 0 && edge.t < poolSize, `edge to-index ${edge.t} out of range`);
        }

        for (const node of fixture.rootPaths) {
            assert.ok(node.t >= 0 && node.t < poolSize, `root path type index ${node.t} out of range`);
        }
    });

    it('payload contract: the root-path trie is an acyclic parent-pointer forest', () => {
        const fixture = loadFixture();

        // gcDumpView.js inverts this into a children map and walks it with an
        // explicit stack. A forward reference or a cycle would make that walk
        // miss nodes or hang, so the shape is asserted here rather than
        // discovered in a frozen webview.
        for (let index = 0; index < fixture.rootPaths.length; ++index) {
            const parent = fixture.rootPaths[index].p;
            assert.ok(parent < index, `node ${index} must reference an earlier parent, got ${parent}`);

            if (parent >= 0) {
                assert.strictEqual(fixture.rootPaths[index].d, fixture.rootPaths[parent].d + 1,
                    `node ${index} depth should be one deeper than its parent`);
            } else {
                assert.strictEqual(fixture.rootPaths[index].d, 0, 'a parentless node must be at depth 0');
            }
        }
    });

    it('payload contract: every type named in rootPathIndexByType has a depth-0 entry', () => {
        const fixture = loadFixture();

        for (const entry of fixture.rootPathIndexByType) {
            const node = fixture.rootPaths[entry.i];
            assert.ok(node !== undefined, `rootPathIndexByType points at missing node ${entry.i}`);
            assert.strictEqual(node.d, 0, 'a type tree must start at depth 0');
            assert.strictEqual(node.t, entry.t, 'the depth-0 node must be the type it is indexed under');
        }
    });
});
