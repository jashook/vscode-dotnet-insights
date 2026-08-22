import * as assert from 'assert';

import { isProcessDumpPath } from '../../DotnetInsightsGcDumpEditor';

// The .gcdump editor now opens two different things - a heap snapshot file and
// a process core dump - and picks the nettraceParser flag from the extension
// (see DotnetInsightsGcDumpEditor.ts). Getting that wrong is not a visible
// error: passing --gcdump a core dump fails to parse, and passing
// --gcdump-from-dump a .gcdump fails in ClrMD, both several async hops away
// from the decision.
describe('process dump detection', () => {
    it('recognizes what dotnet-dump and the runtime write', () => {
        assert.strictEqual(isProcessDumpPath('/tmp/heap.dmp'), true);
        assert.strictEqual(isProcessDumpPath('/tmp/app.core'), true);
    });

    it('leaves heap snapshot files on the .gcdump path', () => {
        assert.strictEqual(isProcessDumpPath('/tmp/heap.gcdump'), false);
        assert.strictEqual(isProcessDumpPath('/tmp/capture.nettrace'), false);
    });

    it('is case-insensitive, since Windows paths routinely are not lower case', () => {
        assert.strictEqual(isProcessDumpPath('C:\\dumps\\HEAP.DMP'), true);
        assert.strictEqual(isProcessDumpPath('C:\\dumps\\Heap.GcDump'), false);
    });

    it('matches on the extension, not on the name containing it', () => {
        // A dump directory or a file merely named after one is not a dump.
        assert.strictEqual(isProcessDumpPath('/tmp/dmp/heap.gcdump'), false);
        assert.strictEqual(isProcessDumpPath('/tmp/core-analysis.gcdump'), false);
    });
});
