import * as assert from 'assert';

import {
    CHILD_PROCESS_RANGE,
    JSON_READ_RANGE,
    mapChildPercentToGlobal,
    mapHostStageFractionToGlobal,
    NettraceProgressTracker,
    parseProgressLine,
    RENDER_RANGE,
    SWAP_RANGE
} from '../../NettraceProgress';

describe('NettraceProgress', () => {
    describe('parseProgressLine', () => {
        it('parses a well-formed PROGRESS line', () => {
            const parsed = parseProgressLine('PROGRESS 42 Reading trace file');

            assert.ok(parsed);
            assert.strictEqual(parsed!.percent, 42);
            assert.strictEqual(parsed!.label, 'Reading trace file');
        });

        it('returns null for the final Timing: diagnostic line', () => {
            assert.strictEqual(parseProgressLine('Timing: read=10ms (100 events) total=42ms'), null);
        });

        it('returns null for an unrelated/empty line', () => {
            assert.strictEqual(parseProgressLine(''), null);
            assert.strictEqual(parseProgressLine('some other stderr output'), null);
        });

        // A stale, pre-this-feature nettraceParser binary (see CLAUDE.md's
        // "stale-cache trap") never emits PROGRESS lines at all - this isn't
        // a special case to parse, just confirms nothing here throws on
        // arbitrary unrelated stderr content.
        it('does not throw on a line that merely starts with the word PROGRESS', () => {
            assert.strictEqual(parseProgressLine('PROGRESSIVE something'), null);
        });

        it('preserves a label containing spaces and punctuation', () => {
            const parsed = parseProgressLine('PROGRESS 100 Exporting CPU profile (final)');

            assert.strictEqual(parsed!.label, 'Exporting CPU profile (final)');
        });
    });

    describe('mapChildPercentToGlobal', () => {
        it('maps the child process range to [0, 80) by default', () => {
            assert.strictEqual(mapChildPercentToGlobal(0), CHILD_PROCESS_RANGE.start);
            assert.strictEqual(mapChildPercentToGlobal(100), CHILD_PROCESS_RANGE.end);
        });

        it('clamps out-of-range child percentages', () => {
            assert.strictEqual(mapChildPercentToGlobal(-10), CHILD_PROCESS_RANGE.start);
            assert.strictEqual(mapChildPercentToGlobal(150), CHILD_PROCESS_RANGE.end);
        });

        it('is monotonic - a higher child percent never maps to a lower global one', () => {
            let previous = -1;
            for (let childPercent = 0; childPercent <= 100; childPercent += 5) {
                const global = mapChildPercentToGlobal(childPercent);
                assert.ok(global >= previous, `${childPercent} -> ${global} should be >= previous ${previous}`);
                previous = global;
            }
        });
    });

    describe('mapHostStageFractionToGlobal', () => {
        it('maps a stage fraction into that stage\'s own range', () => {
            assert.strictEqual(mapHostStageFractionToGlobal(0, JSON_READ_RANGE), JSON_READ_RANGE.start);
            assert.strictEqual(mapHostStageFractionToGlobal(1, JSON_READ_RANGE), JSON_READ_RANGE.end);
        });

        it('clamps fractions outside [0, 1]', () => {
            assert.strictEqual(mapHostStageFractionToGlobal(-1, RENDER_RANGE), RENDER_RANGE.start);
            assert.strictEqual(mapHostStageFractionToGlobal(2, RENDER_RANGE), RENDER_RANGE.end);
        });
    });

    // The four stages - child process, JSON read, render, swap - must cover
    // the WHOLE bar with no gaps, in the order the extension host actually
    // performs them, so a real run's percent sequence is genuinely
    // continuous end to end, not just within each stage individually.
    describe('stage ranges', () => {
        it('are contiguous and cover the whole [0, 100] bar in execution order', () => {
            assert.strictEqual(CHILD_PROCESS_RANGE.start, 0);
            assert.strictEqual(CHILD_PROCESS_RANGE.end, JSON_READ_RANGE.start);
            assert.strictEqual(JSON_READ_RANGE.end, RENDER_RANGE.start);
            assert.strictEqual(RENDER_RANGE.end, SWAP_RANGE.start);
            assert.strictEqual(SWAP_RANGE.end, 100);
        });
    });

    describe('NettraceProgressTracker', () => {
        it('starts at 0 before any report', () => {
            const tracker = new NettraceProgressTracker();

            assert.strictEqual(tracker.current.percent, 0);
        });

        it('recordChildPercent maps through the child process range', () => {
            const tracker = new NettraceProgressTracker();

            const update = tracker.recordChildPercent(50, 'Reading trace file');

            assert.strictEqual(update.percent, mapChildPercentToGlobal(50));
            assert.strictEqual(update.label, 'Reading trace file');
            assert.strictEqual(tracker.current.percent, update.percent);
        });

        // Regression: mapping an already-whole child percent through the
        // [0, 80) sub-range (see CHILD_PROCESS_RANGE) is a multiplication
        // that does NOT generally land on a whole number even though its
        // input was one - e.g. 33 -> 26.4 - which showed up as a decimal
        // percent in the UI before NettraceProgressTracker.record started
        // rounding. Every whole child percent 0-100 must round-trip to a
        // whole global percent.
        it('recordChildPercent always produces a whole-number percent, for every possible child percent', () => {
            const tracker = new NettraceProgressTracker();

            for (let childPercent = 0; childPercent <= 100; ++childPercent) {
                const update = tracker.recordChildPercent(childPercent, 'Reading trace file');
                assert.strictEqual(update.percent, Math.round(update.percent), `childPercent ${childPercent} produced a non-whole global percent: ${update.percent}`);
            }
        });

        it('recordHostStage always produces a whole-number percent, for a range of fractions', () => {
            const tracker = new NettraceProgressTracker();
            tracker.recordChildPercent(100, 'Reading trace file');

            for (let step = 0; step <= 20; ++step) {
                const fraction = step / 20;
                const update = tracker.recordHostStage(fraction, RENDER_RANGE, 'Rendering');
                assert.strictEqual(update.percent, Math.round(update.percent), `fraction ${fraction} produced a non-whole global percent: ${update.percent}`);
            }
        });

        it('recordHostStage maps through the given stage range', () => {
            const tracker = new NettraceProgressTracker();
            tracker.recordChildPercent(100, 'Reading trace file');

            const update = tracker.recordHostStage(0.5, JSON_READ_RANGE, 'Reading results');

            const expected = Math.round(JSON_READ_RANGE.start + (0.5 * (JSON_READ_RANGE.end - JSON_READ_RANGE.start)));
            assert.strictEqual(update.percent, expected);
        });

        // The core guarantee: real work only ever moves the bar forward,
        // regardless of which stage or how the underlying fraction jitters -
        // mirrors ProgressReporter.cs's own monotonic clamp on the C# side,
        // re-enforced here since this tracker also bridges the child's own
        // range into the three host-only stages afterward.
        it('never reports a percent lower than it already reported, across stages', () => {
            const tracker = new NettraceProgressTracker();
            let previousPercent = -1;

            const steps = [
                () => tracker.recordChildPercent(10, 'Reading trace file'),
                () => tracker.recordChildPercent(80, 'Reading trace file'),
                () => tracker.recordChildPercent(30, 'Reading trace file'), // a real, if unlikely, backward jitter
                () => tracker.recordChildPercent(100, 'Exporting GC data'),
                () => tracker.recordHostStage(0, JSON_READ_RANGE, 'Reading results'),
                () => tracker.recordHostStage(1, JSON_READ_RANGE, 'Reading results'),
                () => tracker.recordHostStage(0, RENDER_RANGE, 'Rendering'),
                () => tracker.recordHostStage(1, RENDER_RANGE, 'Rendering'),
                () => tracker.recordHostStage(0, SWAP_RANGE, 'Finishing up'),
                () => tracker.recordHostStage(1, SWAP_RANGE, 'Done'),
            ];

            for (const step of steps) {
                const update = step();
                assert.ok(update.percent >= previousPercent, `${update.percent} should be >= previous ${previousPercent}`);
                previousPercent = update.percent;
            }

            assert.strictEqual(previousPercent, 100);
        });

        it('current reflects the most recently recorded label', () => {
            const tracker = new NettraceProgressTracker();

            tracker.recordChildPercent(20, 'Projecting GC events');
            assert.strictEqual(tracker.current.label, 'Projecting GC events');

            tracker.recordHostStage(0, RENDER_RANGE, 'Rendering');
            assert.strictEqual(tracker.current.label, 'Rendering');
        });
    });
});
