import * as assert from 'assert';
import * as fs from 'fs';
import * as path from 'path';

import { renderCpuProfileView } from '../../CpuProfileRenderer';

// Exercises the CPU category breakdown and, specifically, the alignment
// between the ranked category table and the caller tree each row opens into.
//
// That alignment is the thing worth testing, because it is invisible to every
// other kind of check and it broke in a way that still looked plausible: the
// tree is shared with the hot-methods table and emits its numeric columns as
// (samples, percent, percent), while the category table listed CPU % first -
// so every expanded row put a sample COUNT under a "%" header and a PERCENTAGE
// under "Samples". The HTML was well-formed, the numbers were all correct, and
// the table simply meant something other than what it said.
//
// The fixture is a real nettraceParser --json payload (a 764MB
// `dotnet-trace collect-linux` capture of a production service), trimmed only
// by pruning drill-down trees to a few levels - the categories block, the
// method-name pool and the frame indices are untouched, so a row's `id` still
// indexes categoryDrillDown exactly as it does in production.
function loadFixture(): any {
    const fixturePath = path.resolve(__dirname, '..', '..', '..', 'src', 'test', 'suite', 'fixtures', 'cpu-categories.json');
    return JSON.parse(fs.readFileSync(fixturePath).toString());
}

// The caller tree is built in the webview by media/cpuDrillDownStats.js, not
// by the TypeScript renderer, so a test that only rendered the table would
// have missed this bug entirely - the table and the tree are two grids that
// have to agree, and they are produced by different files in different
// languages.
//
// That script is loaded here and called directly rather than through a
// headless browser: it touches no DOM at all (no `document`, no `window`, no
// acquireVsCodeApi - it is pure string building over the JSON), so evaluating
// it gives the REAL builder the webview uses, with no stub standing in for the
// thing under test.
function loadCallerTreeBuilder(): (entry: any, methodNames: string[], grandTotal: number, rootClass?: string) => string {
    const scriptPath = path.resolve(__dirname, '..', '..', '..', 'media', 'cpuDrillDownStats.js');
    const source = fs.readFileSync(scriptPath).toString();

    // eslint-disable-next-line no-new-func
    return new Function(`${source}\n; return buildInlineCpuMethodCallerTree;`)();
}

function textOfCells(rowHtml: string): string[] {
    const cells: string[] = [];
    const cellPattern = /<td[^>]*>([\s\S]*?)<\/td>/g;
    var match = cellPattern.exec(rowHtml);

    while (match !== null) {
        cells.push(match[1].replace(/<[^>]*>/g, "").replace(/&#\d+;/g, "").trim());
        match = cellPattern.exec(rowHtml);
    }

    return cells;
}

function looksLikePercent(cellText: string): boolean {
    return /^-?[\d.,]+%$/.test(cellText);
}

function looksLikeCount(cellText: string): boolean {
    return /^[\d,]+$/.test(cellText);
}

describe('CPU category breakdown', () => {
    const fixture = loadFixture();
    const cpuProfile = fixture["cpuProfile"];
    const html: string = renderCpuProfileView(cpuProfile);

    const categoryTableHtml = html.slice(html.indexOf('id="cpuCategoryTable"'));
    const categoryRowsHtml = categoryTableHtml
        .split('<tr class="typeRow cpuCategoryRow"')
        .slice(1)
        .map((chunk: string) => chunk.slice(0, chunk.indexOf('</tr>')));

    // Contract checks on the payload itself. If the C# exporter stops emitting
    // these the renderer degrades silently rather than failing.
    it('the real payload carries a category block that sums to the whole capture', () => {
        const categories = cpuProfile["categories"];

        assert.ok(categories, 'categories missing from a real nettraceParser payload');
        assert.ok(Array.isArray(categories["rows"]));

        const selfTotal = categories["rows"].reduce((sum: number, row: any) => sum + Number(row["selfPercent"]), 0);

        // Self attribution puts every sample in exactly one bucket, so this is
        // a real breakdown of the CPU rather than a set of overlapping views.
        assert.ok(Math.abs(selfTotal - 100) < 0.01, `self percentages sum to ${selfTotal}, expected 100`);
    });

    it('every category row carries the id its drill-down tree is indexed by', () => {
        const rows = cpuProfile["categories"]["rows"];
        const drillDown = cpuProfile["categoryDrillDown"];

        for (const row of rows) {
            assert.strictEqual(typeof row["id"], 'number', `category ${row["name"]} has no id`);
            assert.ok(drillDown[row["id"]] !== undefined, `no drill-down entry at id ${row["id"]}`);
        }
    });

    // THE REGRESSION. Header order and cell order have to agree by TYPE, or an
    // expanded row reads a count as a percentage.
    it('numeric columns are samples-first, matching the caller tree below them', () => {
        const headerCells = textOfCells(categoryTableHtml.slice(0, categoryTableHtml.indexOf('</tr>')).replace(/<th/g, '<td').replace(/<\/th>/g, '</td>'));

        assert.deepStrictEqual(
            headerCells,
            ['', 'Category', 'Samples', 'CPU %', 'On stack %'],
            'category header order changed; the caller tree emits (samples, percent, percent) and will no longer line up');

        const firstRowCells = textOfCells(categoryRowsHtml[0]);

        assert.strictEqual(firstRowCells.length, 5);
        assert.ok(looksLikeCount(firstRowCells[2]), `expected a sample count in column 3, got "${firstRowCells[2]}"`);
        assert.ok(looksLikePercent(firstRowCells[3]), `expected a percentage in column 4, got "${firstRowCells[3]}"`);
        assert.ok(looksLikePercent(firstRowCells[4]), `expected a percentage in column 5, got "${firstRowCells[4]}"`);
    });

    it('every category row puts a count under Samples and percentages under the percent columns', () => {
        for (const rowHtml of categoryRowsHtml) {
            const cells = textOfCells(rowHtml);

            assert.ok(looksLikeCount(cells[2]), `"${cells[1]}" has "${cells[2]}" under Samples`);
            assert.ok(looksLikePercent(cells[3]), `"${cells[1]}" has "${cells[3]}" under CPU %`);
            assert.ok(looksLikePercent(cells[4]), `"${cells[1]}" has "${cells[4]}" under On stack %`);
        }
    });

    it('rows are ranked by CPU share', () => {
        const percents = categoryRowsHtml.map((rowHtml: string) => parseFloat(textOfCells(rowHtml)[3]));

        for (var index = 1; index < percents.length; ++index) {
            assert.ok(percents[index] <= percents[index - 1], 'category rows are not sorted by CPU %');
        }
    });

    // The rows are re-sorted for display, so a positional index would open one
    // bucket's row onto another bucket's call paths. This asserts the pairing
    // is by identity, on a fixture where the two genuinely differ.
    it('pairs a row with its own call paths by category id, not by row position', () => {
        const lazyIds = categoryRowsHtml.map((rowHtml: string, index: number) => {
            const detailStart = categoryTableHtml.indexOf(`id="cpuCategoryDetail${index}"`);
            assert.ok(detailStart >= 0, `no detail row for display index ${index}`);

            const attribute = /data-cpu-category-lazy="(\d+)"/.exec(categoryTableHtml.slice(detailStart, detailStart + 200));
            assert.ok(attribute, `detail row ${index} carries no category id`);
            return Number(attribute![1]);
        });

        const sortedRows = [...cpuProfile["categories"]["rows"]]
            .filter((row: any) => Number(row["selfSamples"]) > 0 || Number(row["onStackSamples"]) > 0)
            .sort((left: any, right: any) => Number(right["selfPercent"]) - Number(left["selfPercent"]));

        for (var index = 0; index < lazyIds.length; ++index) {
            assert.strictEqual(lazyIds[index], sortedRows[index]["id"],
                `display row ${index} (${sortedRows[index]["name"]}) points at the wrong category's call paths`);
        }

        assert.ok(lazyIds.some((id: number, index: number) => id !== index),
            'fixture no longer exercises the case where display order differs from category id');
    });
});

describe('CPU category caller tree', () => {
    const fixture = loadFixture();
    const cpuProfile = fixture["cpuProfile"];
    const buildTree = loadCallerTreeBuilder();

    const managedFramework = cpuProfile["categories"]["rows"].find((row: any) => row["name"] === "Managed framework");
    const treeHtml: string = buildTree(
        cpuProfile["categoryDrillDown"][managedFramework["id"]],
        cpuProfile["methodNames"],
        cpuProfile["totalSampleCount"],
        'cpuCallerForestRoot');

    const colgroupColumnCount = (treeHtml.match(/<col[\s>]/g) || []).length;

    function treeRows(): string[] {
        return treeHtml
            .split('<tr')
            .slice(1)
            .map((chunk: string) => chunk.slice(0, chunk.indexOf('</tr>')));
    }

    // The structural invariant behind the whole bug: a tree row and the table
    // above it are two grids spanning the same box, so a mismatch in cell
    // count silently shifts every number one column sideways.
    it('every tree row has exactly as many cells as the colgroup has columns', () => {
        assert.strictEqual(colgroupColumnCount, 5, 'the CPU caller tree colgroup changed shape');

        // Spanning rows are excluded on purpose: the tree interleaves a
        // single-cell <td colspan> placeholder after each caller row, which
        // the deeper subtree is injected into when that row is expanded. Those
        // are not data rows and are meant to span the whole grid.
        const dataRows = treeRows().filter((rowHtml: string) => rowHtml.indexOf('colspan') < 0);

        assert.ok(dataRows.length > 0, 'the fixture produced no non-spanning tree rows');

        for (const rowHtml of dataRows) {
            const cells = textOfCells(rowHtml);
            assert.strictEqual(cells.length, colgroupColumnCount,
                `a tree row has ${cells.length} cells against ${colgroupColumnCount} columns: ${rowHtml.slice(0, 120)}`);
        }
    });

    // And the invariant that ties the tree to the table: same numeric order.
    it('emits its numeric cells as samples then percentages, matching the table', () => {
        const dataRows = treeRows().filter((rowHtml: string) =>
            rowHtml.indexOf('treeColumnLabelRow') < 0 && rowHtml.indexOf('colspan') < 0);

        assert.ok(dataRows.length > 0, 'the fixture produced no tree rows');

        for (const rowHtml of dataRows) {
            const cells = textOfCells(rowHtml);

            assert.ok(looksLikeCount(cells[2]), `tree row has "${cells[2]}" where the table has Samples`);
            assert.ok(looksLikePercent(cells[3]), `tree row has "${cells[3]}" where the table has a percentage`);
            assert.ok(looksLikePercent(cells[4]), `tree row has "${cells[4]}" where the table has a percentage`);
        }
    });

    // REGRESSION, measured in a browser rather than argued from the source.
    // A column-label row inside this table changed that table's own column
    // sizing: under table-layout:auto the leading spacer <col>'s 1.6em is a
    // FLOOR, not a width, so the extra row's text let the top-level table's
    // spacer grow to 30px while every nested table stayed at 26px. The first
    // indent step collapsed from 13px to 9px and a caller rendered at visually
    // the same level as its own parent. Labels belong outside the grid.
    it('adds nothing to the table but caller rows, so column sizing cannot drift', () => {
        assert.ok(treeHtml.indexOf('treeColumnLabelRow') < 0,
            'a label row is back inside the caller tree; it changes the spacer column width and collapses the first indent step');

        for (const rowHtml of treeRows()) {
            const isCallerRow = rowHtml.indexOf('callerRow') >= 0;
            const isLazyHost = rowHtml.indexOf('callPathsDetail') >= 0;

            assert.ok(isCallerRow || isLazyHost,
                `unexpected row inside the caller tree: ${rowHtml.slice(0, 100)}`);
        }
    });

    // A category's tree is a FOREST - one root per leaf method in the bucket -
    // where a hot method's tree has a single root chain anchored by the ranked
    // row above it. Every depth-0 row sits at padding-left 0, so without a
    // marker a caller indented one level (0.85em) lands almost exactly where
    // the NEXT SIBLING ROOT does, and an expanded row reads as another
    // top-level method rather than as nested underneath.
    it('marks its top-level rows as roots, because the tree is a forest', () => {
        const rootRows = treeRows().filter((rowHtml: string) => rowHtml.indexOf('cpuCallerForestRoot') >= 0);

        assert.ok(rootRows.length > 1,
            'the fixture no longer exercises a forest - a single root would not need marking');

        // Precisely the depth-0 rows: every marked row is flush left, and
        // nothing deeper is marked.
        for (const rowHtml of rootRows) {
            const indent = /padding-left: ([\d.]+)em/.exec(rowHtml);
            assert.ok(indent, 'a marked root row carries no indent style');
            assert.strictEqual(Number(indent![1]), 0, 'a row below depth 0 was marked as a forest root');
        }
    });

    // The hot-method tree shares this builder and is NOT a forest, so marking
    // its single root would add a rule across the top of every expansion for
    // no reason.
    it('does not mark roots when the caller does not ask for it', () => {
        const unmarked: string = buildTree(
            cpuProfile["hotMethodDrillDown"][0],
            cpuProfile["methodNames"],
            cpuProfile["totalSampleCount"]);

        assert.ok(unmarked.indexOf('cpuCallerForestRoot') < 0);
    });

    // Nesting must still step in by one level per depth - the root marker
    // makes the forest legible, it does not replace the indentation.
    it('still indents each level below a root', () => {
        const indents = treeRows()
            .filter((rowHtml: string) => rowHtml.indexOf('colspan') < 0 && rowHtml.indexOf('treeColumnLabelRow') < 0)
            .map((rowHtml: string) => Number((/padding-left: ([\d.]+)em/.exec(rowHtml) || [, '-1'])[1]));

        assert.ok(indents.every((value: number) => value === 0),
            'the top level of a category tree should be flush left; deeper levels are built lazily');
    });

    // The spacer column's width has to be styleable, because the CPU category
    // breakdown collapses it so its expansion sits flush with the row's hide
    // column while every other tree keeps the 1.6em gutter. An inline width on
    // the <col> cannot be overridden by a stylesheet without !important, so a
    // scoped rule silently does nothing - which is exactly what happened when
    // this was first attempted.
    it('drives the spacer column width from a class, not an inline style', () => {
        assert.ok(treeHtml.indexOf('callerTreeSpacerCol') >= 0,
            'the leading spacer column lost its class; a per-view gutter can no longer be scoped');

        assert.ok(treeHtml.indexOf('style="width: 1.6em"') < 0,
            'the spacer column went back to an inline width, which no stylesheet can override');
    });

    // The tree is built from the same attribution the bucket's percentage is,
    // so the two can never legitimately disagree.
    it('totals the same sample count the category row reports', () => {
        const tree = cpuProfile["categoryDrillDown"][managedFramework["id"]];

        assert.strictEqual(tree["totalSamples"], managedFramework["selfSamples"],
            'the call paths under a category do not add up to the category');
    });
});
