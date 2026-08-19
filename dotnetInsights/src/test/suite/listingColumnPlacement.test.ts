import * as assert from 'assert';
import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import * as vscode from 'vscode';

import { showCounterpartListing } from '../../extension';
import { columnForDisassembly } from '../../onSaveIlDasm';

// Both of these used to derive a ViewColumn from a count of, or an index into,
// visibleTextEditors - neither of which is a column. See each helper's own
// comment for what that produced.
describe('listing column placement', () => {
    var tempDir = "";

    function writeTempFile(name: string, contents: string): string {
        var filePath = path.join(tempDir, name);
        fs.writeFileSync(filePath, contents);

        return filePath;
    }

    function openTabNames(): string[] {
        var names = [] as string[];

        for (const tabGroup of vscode.window.tabGroups.all) {
            for (const tab of tabGroup.tabs) {
                names.push(tab.label);
            }
        }

        return names;
    }

    async function showListing(filePath: string, viewColumn: vscode.ViewColumn): Promise<vscode.TextEditor> {
        return await vscode.window.showTextDocument(await vscode.workspace.openTextDocument(filePath), {
            viewColumn: viewColumn,
            preview: false
        });
    }

    before(() => {
        tempDir = fs.mkdtempSync(path.join(os.tmpdir(), "dotnetInsights-column-"));
    });

    after(() => {
        fs.rmSync(tempDir, { recursive: true, force: true });
    });

    afterEach(async () => {
        await vscode.commands.executeCommand('workbench.action.closeAllEditors');
    });

    describe('showCounterpartListing', () => {
        it('opens the counterpart in the column the listing is in', async () => {
            var asmListing = writeTempFile("counterpart.asm", "; asm");
            var jitDumpListing = writeTempFile("counterpart.jitDump", "; jit dump");

            // A listing in the SECOND group is what the old index arithmetic got
            // wrong: it produced column One and moved the counterpart away.
            await showListing(writeTempFile("source.cs", "// source"), vscode.ViewColumn.One);
            var asmEditor = await showListing(asmListing, vscode.ViewColumn.Two);
            assert.strictEqual(asmEditor.viewColumn, vscode.ViewColumn.Two);

            var counterpartEditor = await showCounterpartListing(jitDumpListing, asmEditor);

            assert.strictEqual(counterpartEditor.viewColumn, vscode.ViewColumn.Two);
            assert.ok(openTabNames().indexOf("source.cs") !== -1, "the source file was closed");
        });

        it('keeps the listing it was invoked from open', async () => {
            var asmListing = writeTempFile("keepOpen.asm", "; asm");
            var jitDumpListing = writeTempFile("keepOpen.jitDump", "; jit dump");

            var asmEditor = await showListing(asmListing, vscode.ViewColumn.One);

            await showCounterpartListing(jitDumpListing, asmEditor);

            var names = openTabNames();

            assert.ok(names.indexOf("keepOpen.asm") !== -1, `the .asm listing was closed: ${names}`);
            assert.ok(names.indexOf("keepOpen.jitDump") !== -1, `the jit dump never opened: ${names}`);
        });
    });

    describe('columnForDisassembly', () => {
        it('places the disassembly in the column after the ildasm document', async () => {
            await showListing(writeTempFile("realtime.cs", "// source"), vscode.ViewColumn.One);
            var ilDasmEditor = await showListing(writeTempFile("realtime.ildasm", ".assembly x {}"), vscode.ViewColumn.Two);

            assert.strictEqual(columnForDisassembly(ilDasmEditor), vscode.ViewColumn.Three);
        });

        it('does not move as more editors are opened', async () => {
            var ilDasmEditor = await showListing(writeTempFile("stable.ildasm", ".assembly x {}"), vscode.ViewColumn.One);

            var columnWithOneEditorOpen = columnForDisassembly(ilDasmEditor);

            // The old splitIndex counted visible editors, so each of these
            // pushed the disassembly one column further right.
            await showListing(writeTempFile("extraOne.cs", "// one"), vscode.ViewColumn.Two);
            await showListing(writeTempFile("extraTwo.cs", "// two"), vscode.ViewColumn.Three);

            assert.strictEqual(columnForDisassembly(ilDasmEditor), columnWithOneEditorOpen);
            assert.strictEqual(columnWithOneEditorOpen, vscode.ViewColumn.Two);
        });

        it('falls back to Beside when there is no ildasm editor yet', () => {
            assert.strictEqual(columnForDisassembly(undefined), vscode.ViewColumn.Beside);
        });
    });
});
