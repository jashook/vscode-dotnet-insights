import * as assert from 'assert';
import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import * as vscode from 'vscode';

import { showGeneratedDocument } from '../../extension';

// The min opts / tier 1 / jit dump commands each generate a listing under a
// fresh random filename and show it. Opening those in preview mode reused one
// tab, so generating the second listing closed the first - measured, not
// assumed: showTextDocument(doc, 1) twice leaves exactly one tab behind.
describe('generated listing tabs', () => {
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

    before(() => {
        tempDir = fs.mkdtempSync(path.join(os.tmpdir(), "dotnetInsights-listing-"));
    });

    after(() => {
        fs.rmSync(tempDir, { recursive: true, force: true });
    });

    afterEach(async () => {
        await vscode.commands.executeCommand('workbench.action.closeAllEditors');
    });

    it('keeps an earlier listing open when a second one is generated', async () => {
        var minOptsListing = writeTempFile("minOpts.asm", "; min opts");
        var tierOneListing = writeTempFile("tierOne.asm", "; tier 1");

        await showGeneratedDocument(minOptsListing);
        await showGeneratedDocument(tierOneListing);

        var names = openTabNames();

        assert.ok(names.indexOf("minOpts.asm") !== -1, `the first listing was closed: ${names}`);
        assert.ok(names.indexOf("tierOne.asm") !== -1, `the second listing never opened: ${names}`);
    });

    it('does not evict a document the user is previewing', async () => {
        var previewed = writeTempFile("previewed.txt", "one");
        var listing = writeTempFile("listing.asm", "; asm");

        await vscode.window.showTextDocument(await vscode.workspace.openTextDocument(previewed), { preview: true });

        await showGeneratedDocument(listing);

        var names = openTabNames();

        assert.ok(names.indexOf("previewed.txt") !== -1, `the previewed document was evicted: ${names}`);
        assert.ok(names.indexOf("listing.asm") !== -1, `listing.asm never opened: ${names}`);
    });
});
