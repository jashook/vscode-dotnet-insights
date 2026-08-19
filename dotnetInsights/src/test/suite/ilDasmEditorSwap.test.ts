import * as assert from 'assert';
import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import * as vscode from 'vscode';

import { DotnetInsightsTextEditorProvider, showIlDasmInPlaceOfPanel } from '../../DotnetInightsTextEditor';

// Opening a *.dll with the Dotnet Insights Editor used to close the user's
// other editors - closeActiveEditor with a single editor visible, and the whole
// editor group otherwise (issue #99). The placeholder panel is now disposed on
// its own instead, which is what these tests pin down.
describe('ildasm editor swap', () => {
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

    // Disposing a panel fires onDidDispose right away, but VS Code's tab model
    // catches up a turn later, so an assertion about tabs has to wait for it.
    async function waitForTabToClose(label: string): Promise<void> {
        for (var attempt = 0; attempt < 100 && openTabNames().indexOf(label) !== -1; ++attempt) {
            await new Promise(resolve => setTimeout(resolve, 20));
        }
    }

    function createPlaceholderPanel(viewColumn: vscode.ViewColumn): vscode.WebviewPanel {
        return vscode.window.createWebviewPanel(DotnetInsightsTextEditorProvider.viewType,
                                                "sample.dll",
                                                viewColumn,
                                                {});
    }

    // A freshly created panel has no viewColumn until it has been laid out, so
    // a test that cares which column it is in has to wait for one.
    async function createLaidOutPlaceholderPanel(viewColumn: vscode.ViewColumn): Promise<vscode.WebviewPanel> {
        var panel = createPlaceholderPanel(viewColumn);

        for (var attempt = 0; attempt < 100 && panel.viewColumn === undefined; ++attempt) {
            await new Promise(resolve => setTimeout(resolve, 20));
        }

        return panel;
    }

    before(() => {
        tempDir = fs.mkdtempSync(path.join(os.tmpdir(), "dotnetInsights-ildasm-"));
    });

    after(() => {
        fs.rmSync(tempDir, { recursive: true, force: true });
    });

    afterEach(async () => {
        await vscode.commands.executeCommand('workbench.action.closeAllEditors');
    });

    it('leaves the editors the user already had open alone', async () => {
        var otherOne = writeTempFile("otherOne.txt", "one");
        var otherTwo = writeTempFile("otherTwo.txt", "two");
        var ilDasmFile = writeTempFile("sample.ildasm", ".assembly sample {}");

        await vscode.window.showTextDocument(await vscode.workspace.openTextDocument(otherOne), { preview: false });
        await vscode.window.showTextDocument(await vscode.workspace.openTextDocument(otherTwo), { preview: false });

        await showIlDasmInPlaceOfPanel(createPlaceholderPanel(vscode.ViewColumn.Active), ilDasmFile);

        var names = openTabNames();

        assert.ok(names.indexOf("otherOne.txt") !== -1, `otherOne.txt was closed: ${names}`);
        assert.ok(names.indexOf("otherTwo.txt") !== -1, `otherTwo.txt was closed: ${names}`);
        assert.ok(names.indexOf("sample.ildasm") !== -1, `sample.ildasm never opened: ${names}`);
    });

    it('closes its own placeholder editor', async () => {
        var ilDasmFile = writeTempFile("closesOwn.ildasm", ".assembly sample {}");

        await showIlDasmInPlaceOfPanel(createPlaceholderPanel(vscode.ViewColumn.Active), ilDasmFile);
        await waitForTabToClose("sample.dll");

        var names = openTabNames();

        assert.strictEqual(names.indexOf("sample.dll"), -1, `the placeholder editor stayed open: ${names}`);
        assert.ok(names.indexOf("closesOwn.ildasm") !== -1, `closesOwn.ildasm never opened: ${names}`);
    });

    it('opens the ildasm document in the column the placeholder occupied', async () => {
        var otherOne = writeTempFile("besideOther.txt", "one");
        var ilDasmFile = writeTempFile("beside.ildasm", ".assembly sample {}");

        await vscode.window.showTextDocument(await vscode.workspace.openTextDocument(otherOne), { preview: false });

        // What the inline IL flow does: open the .dll beside what is already
        // there, so the ildasm document has to end up beside it too.
        var panel = await createLaidOutPlaceholderPanel(vscode.ViewColumn.Beside);
        assert.strictEqual(panel.viewColumn, vscode.ViewColumn.Two);

        var editor = await showIlDasmInPlaceOfPanel(panel, ilDasmFile);

        assert.strictEqual(editor.viewColumn, vscode.ViewColumn.Two);
        assert.ok(openTabNames().indexOf("besideOther.txt") !== -1, "the editor beside which the .dll opened was closed");
    });

    it('still places the document when the placeholder has no column at call time', async () => {
        var ilDasmFile = writeTempFile("noColumn.ildasm", ".assembly sample {}");

        // WebviewPanel.viewColumn is undefined until the panel has been laid
        // out, so the swap reads it after its own deferral rather than here.
        var panel = createPlaceholderPanel(vscode.ViewColumn.Active);
        assert.strictEqual(panel.viewColumn, undefined);

        var editor = await showIlDasmInPlaceOfPanel(panel, ilDasmFile);

        assert.strictEqual(editor.viewColumn, vscode.ViewColumn.One);
    });

    it('does not dispose the placeholder before resolveCustomEditor returns', async () => {
        var ilDasmFile = writeTempFile("timing.ildasm", ".assembly sample {}");

        var panel = createPlaceholderPanel(vscode.ViewColumn.Active);

        var disposed = false;
        panel.onDidDispose(() => { disposed = true; });

        var pendingSwap = showIlDasmInPlaceOfPanel(panel, ilDasmFile);

        // This is the point resolveCustomEditor hands control back to VS Code,
        // which is still setting the editor up. Disposing here failed the whole
        // open with a modal "OverlayWebview has been disposed".
        assert.strictEqual(disposed, false, "the placeholder was disposed synchronously");

        await pendingSwap;

        assert.strictEqual(disposed, true, "the placeholder was never disposed");
    });

    it('does not evict a document the user is previewing', async () => {
        var previewed = writeTempFile("previewed.txt", "one");
        var ilDasmFile = writeTempFile("noEvict.ildasm", ".assembly sample {}");

        await vscode.window.showTextDocument(await vscode.workspace.openTextDocument(previewed), { preview: true });

        await showIlDasmInPlaceOfPanel(createPlaceholderPanel(vscode.ViewColumn.Active), ilDasmFile);

        var names = openTabNames();

        assert.ok(names.indexOf("previewed.txt") !== -1, `the previewed document was evicted: ${names}`);
        assert.ok(names.indexOf("noEvict.ildasm") !== -1, `noEvict.ildasm never opened: ${names}`);
    });
});
