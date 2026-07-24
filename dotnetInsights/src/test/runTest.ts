import * as path from 'path';

import { runTests } from '@vscode/test-electron';

async function main() {
    try {
        // If set, this forces the VS Code binary we spawn below to run as a
        // plain Node process instead of launching the actual Electron/VS Code
        // app - it inherits from the parent shell by default, and some
        // environments (nested Electron dev setups, some CI runners) set it
        // globally for unrelated reasons. Clear it for this child process
        // regardless of what the calling shell has set.
        delete process.env.ELECTRON_RUN_AS_NODE;

        // The folder containing the Extension Manifest package.json.
        const extensionDevelopmentPath = path.resolve(__dirname, '../../');

        // The path to the compiled test suite (index.js runs Mocha inside the
        // Extension Development Host and reports pass/fail via exit code).
        const extensionTestsPath = path.resolve(__dirname, './suite/index');

        await runTests({ extensionDevelopmentPath, extensionTestsPath });
    }
    catch (err) {
        console.error('Failed to run tests');
        console.error(err);
        process.exit(1);
    }
}

main();
