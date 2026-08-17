import * as assert from 'assert';
import * as fs from 'fs';
import * as path from 'path';

import { renderThreadingView } from '../../ThreadingRenderer';

// Exercises the Threading view against a REAL nettraceParser --json payload
// (fixtures/threading-summary.json, from a 836MB capture of a production
// service), for the reason gcDumpRenderer.test.ts states: the field names this
// renderer reads are produced a whole language and process away, in
// nettraceParser/Threading/ThreadingJsonExporter.cs, and a synthetic fixture
// would be written from the same misreading of that contract as the code under
// test.
//
// It matters more than usual for the thread classification, because the thing
// being asserted is not "the HTML has a table" but "the right threads were
// called benign". That capture happens to contain, in one file, every shape
// the classifier was built for: six gRPC completion threads parked in a native
// poll whose managed leaf frame reads like running code, five Kafka consumers
// pinned to POOL threads by a synchronous consume inside an ExecuteAsync (four
// parked, one busy), the runtime's own gate and timer threads, and 34 healthy
// pool workers.
function loadFixture(): any {
    const fixturePath = path.resolve(__dirname, '..', '..', '..', 'src', 'test', 'suite', 'fixtures', 'threading-summary.json');
    return JSON.parse(fs.readFileSync(fixturePath).toString());
}

describe('ThreadingRenderer thread classification', () => {
    const fixture = loadFixture();
    const threadingSummary = fixture["threadingSummary"];
    const methodNames: string[] = fixture["threadingMethodNames"];

    // Contract checks on the payload itself, ahead of any HTML assertion. If
    // the exporter stops emitting these the renderer degrades silently to its
    // old behaviour, which is exactly the failure this feature exists to fix.
    it('the real payload carries the thread classification block', () => {
        const threadActivity = threadingSummary["threadActivity"];

        assert.ok(threadActivity, 'threadActivity is missing from a real nettraceParser payload');
        assert.strictEqual(threadActivity["hasSampleTypeData"], true);
        assert.ok(threadActivity["threads"].length > 0);
        assert.ok(threadActivity["benignThreadCount"] > 0);
    });

    // The regression the classifier was built for. These threads' leaf frame is
    // ordinary managed code, so no list of blocking-primitive names can ever
    // recognise them - they were previously scored "100% running" and shown at
    // the top of the stall table.
    it('classifies the native poll-loop threads as benignly parked', () => {
        const threads = threadingSummary["threadActivity"]["threads"];
        const pollThreads = threads.filter((thread: any) => {
            const topStacks = thread["topStacks"];
            return topStacks.length > 0 && methodNames[topStacks[0]["frames"][0]].indexOf("GrpcThreadPool.RunHandlerLoop") !== -1;
        });

        assert.ok(pollThreads.length > 0, 'the fixture should contain gRPC poll-loop threads');

        for (const pollThread of pollThreads) {
            assert.strictEqual(pollThread["isBenign"], true, `thread ${pollThread["threadId"]} should be benign`);
            assert.strictEqual(pollThread["roleName"], 'Parked');
        }
    });

    // The pair that proves the verdict has to be per thread. The same method
    // name carries threads that are excluded from the stall table and threads
    // that are ranked in it, so any filter keyed on the frame would have to be
    // wrong about one of them.
    it('reaches opposite verdicts for the same frame on different threads', () => {
        const stallCorrelation = threadingSummary["stallCorrelation"];
        const rankedFrameNames = new Set(stallCorrelation["frames"].map((frame: any) => methodNames[frame["frame"]]));
        const excludedFrameNames = new Set(stallCorrelation["benignFrames"].map((frame: any) => methodNames[frame["frame"]]));

        const inBoth = [...rankedFrameNames].filter((name: any) => excludedFrameNames.has(name));

        assert.ok(inBoth.length > 0, 'expected at least one frame ranked on one thread and excluded on another');
    });

    // The finding this reclassification surfaced, and the exact distinction
    // between a dedicated queue-drain worker (benign) and this (not): these
    // threads run a synchronous Kafka consume inside an ExecuteAsync, rooted
    // in PortableThreadPool+WorkerThread.WorkerThreadStart ->
    // ThreadPoolWorkQueue.Dispatch. They look every bit as parked as a
    // dedicated consumer and are the opposite of benign, because each one is
    // standing on a pool worker that can never be reclaimed.
    it('flags a pool worker parked in a synchronous library call, however still it sits', () => {
        const threads = threadingSummary["threadActivity"]["threads"];
        const consumerThreads = threads.filter((thread: any) => {
            const topStacks = thread["topStacks"];
            return topStacks.length > 0 && methodNames[topStacks[0]["frames"][0]].indexOf("Kafka.Consumer") !== -1;
        });

        assert.ok(consumerThreads.length > 1, 'the fixture should contain several Kafka consumer threads');

        const occupiedPoolWorkers = consumerThreads.filter((thread: any) => thread["roleName"] === 'Blocked pool worker');
        assert.ok(occupiedPoolWorkers.length > 0, 'the parked ones occupy pool workers and must be flagged');

        for (const consumerThread of consumerThreads) {
            // Every one of them is on a pool thread, so none may be called
            // benign no matter how parked it looks - unlike the dedicated
            // BlockingCollection drain workers, which cannot starve the pool.
            assert.strictEqual(consumerThread["isPoolWorker"], true);
            assert.strictEqual(consumerThread["isBenign"], false);
        }

        // ...and they are still split by their own behaviour: the one consumer
        // that does real work is not called blocked.
        assert.ok(consumerThreads.some((thread: any) => thread["roleName"] === 'Active'));
    });

    // The runtime's own gate and timer threads park by design. The timer thread
    // in particular carries TimerQueue on its stack and wakes on every tick, so
    // behaviour alone lands it on "Blocked pool worker" - the loudest label
    // this view has - on a thread doing exactly its job.
    it('never labels a runtime infrastructure thread as a blocked pool worker', () => {
        const threads = threadingSummary["threadActivity"]["threads"];
        const infrastructureThreads = threads.filter((thread: any) => thread["roleName"] === 'Runtime infrastructure');

        assert.ok(infrastructureThreads.length > 0, 'the fixture should contain gate/timer threads');

        for (const infrastructureThread of infrastructureThreads) {
            assert.strictEqual(infrastructureThread["isBenign"], true);
        }
    });

    // The join that makes this view supplemental to the Contention view rather
    // than a second copy of it: a thread whose idleness that view MATERIALLY
    // accounts for stays visible here, however parked it looks.
    //
    // Material, not merely present. Asserting "no benign thread has any
    // contention row" would pass on this fixture and still be wrong - it is
    // not the rule, and a dedicated BlockingCollection drain worker on another
    // capture carries 157 rows worth 0.0028% of its life.
    it('never calls a thread benign when contention accounts for real time on it', () => {
        const threads = threadingSummary["threadActivity"]["threads"];

        for (const thread of threads) {
            if (thread["contentionShareOfLife"] >= 0.01) {
                assert.strictEqual(thread["isBenign"], false, `thread ${thread["threadId"]} is materially blocked and must stay visible`);
            }
        }
    });

    it('ranks every actionable thread ahead of every benign one', () => {
        const threads = threadingSummary["threadActivity"]["threads"];
        var seenBenign = false;

        for (const thread of threads) {
            if (thread["isBenign"]) {
                seenBenign = true;
            } else {
                assert.strictEqual(seenBenign, false, 'an actionable thread appeared after a benign one');
            }
        }
    });

    // The stall table is the one the noise cost the most, so its exclusion is
    // checked against the payload rather than only against the HTML.
    it('keeps benignly parked threads out of the stall ranking and reports them separately', () => {
        const stallCorrelation = threadingSummary["stallCorrelation"];

        assert.ok(stallCorrelation["benignThreadSamples"] > 0);
        assert.ok(stallCorrelation["benignFrames"].length > 0);

        const rankedFrameNames = stallCorrelation["frames"].map((frame: any) => methodNames[frame["frame"]]);
        const excludedFrameNames = stallCorrelation["benignFrames"].map((frame: any) => methodNames[frame["frame"]]);

        // The frame that used to top this table.
        assert.ok(excludedFrameNames.some((name: string) => name.indexOf("GrpcThreadPool.RunHandlerLoop") !== -1));
        assert.ok(!rankedFrameNames.some((name: string) => name.indexOf("GrpcThreadPool.RunHandlerLoop") !== -1));
    });

    describe('rendering', () => {
        const html = renderThreadingView(threadingSummary, methodNames);

        it('renders the roster with a row per emitted thread', () => {
            const threads = threadingSummary["threadActivity"]["threads"];

            assert.ok(html.indexOf('id="threadRosterTable"') !== -1);

            // Two rows per thread (the summary row and its lazy detail row).
            const detailRowCount = (html.match(/data-threading-thread-lazy=/g) || []).length;
            assert.strictEqual(detailRowCount, threads.length);
        });

        it('dims benign rows rather than hiding them', () => {
            const benignCount = threadingSummary["threadActivity"]["threads"]
                .filter((thread: any) => thread["isBenign"]).length;

            assert.strictEqual((html.match(/threadingBenignRow/g) || []).length >= benignCount, true);
        });

        it('renders the excluded-samples section so the filter can be audited', () => {
            assert.ok(html.indexOf('threadingExcludedDetails') !== -1);
            assert.ok(html.indexOf('excluded from the table above') !== -1);
        });

        it('says nothing was excluded when the payload predates the classification', () => {
            const legacySummary = JSON.parse(JSON.stringify(threadingSummary));
            delete legacySummary["threadActivity"];
            delete legacySummary["stallCorrelation"]["benignFrames"];
            delete legacySummary["stallCorrelation"]["benignThreadSamples"];

            const legacyHtml = renderThreadingView(legacySummary, methodNames);

            // The distinction that matters: an older parser, not a capture
            // missing a flag - sending the reader to re-capture would waste
            // their time on something that would not help.
            assert.ok(legacyHtml.indexOf('older <b>nettraceParser</b>') !== -1);
            assert.ok(legacyHtml.indexOf('id="threadRosterTable"') === -1);
            assert.ok(legacyHtml.indexOf('threadingExcludedDetails') === -1);
        });

        it('states the reason when the capture carries no managed/native flag', () => {
            const noFlagSummary = JSON.parse(JSON.stringify(threadingSummary));
            noFlagSummary["threadActivity"]["hasSampleTypeData"] = false;

            const noFlagHtml = renderThreadingView(noFlagSummary, methodNames);

            assert.ok(noFlagHtml.indexOf('no managed/native flag') !== -1);
            // The roster still renders - it is a thread listing either way,
            // just without any parked verdict behind it.
            assert.ok(noFlagHtml.indexOf('id="threadRosterTable"') !== -1);
        });
    });
});
