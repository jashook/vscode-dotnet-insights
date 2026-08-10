////////////////////////////////////////////////////////////////////////////////
// Module: ProgressPlan.cs
//
// Notes:
// Computes each phase's own [start, end) slice of the overall 0-100
// progress bar (see ProgressReporter.cs for how a phase then reports
// WITHIN its own slice). Deliberately NOT one global weight formula mixing
// bytes/events/output-items as if they were the same "work unit" currency -
// verified wrong: on a real 1.57GB/29,634,864-event capture
// (~/projects/Investigations/asset-delivery-api-8-aug-2026-4:10pm.nettrace),
// the read phase (bytes-driven) was only 30% of wall-clock time despite
// being ~98% of the raw byte count against the ~30M "items" (events/GCs/
// ticks/exceptions/samples) the rest of the pipeline touches - byte count
// and item count are not interchangeable costs.
//
// Instead this plans in three stages, each computed only once its own
// inputs are actually known (see Program.cs's call sites), which makes
// monotonicity structural rather than something enforced after the fact -
// each stage only ever subdivides a range the previous stage hasn't
// consumed yet:
//   Stage 0 (before NettraceFile.Read)     -> read's own [0, R) slice.
//   Stage 1 (right after Read returns)     -> the remaining [R, 100) split
//                                              into "7 projector phases
//                                              combined" vs "jsonExport".
//   Stage 2 (right before GcJsonExporter.WriteToFile) -> jsonExport's own
//                                              range split across its 5
//                                              sub-writers, using this
//                                              run's REAL counts (the one
//                                              place a per-item weight
//                                              estimate is comparing
//                                              genuinely comparable things -
//                                              5 writers in the SAME phase -
//                                              rather than different units
//                                              measured on a different
//                                              capture).
//
// All weight CONSTANTS below (not stage-2's real per-run counts) are
// measured proportions of real wall-clock time, calibrated against TWO
// differently-shaped real captures - see each constant's own comment for
// the specific numbers - via Program.cs's permanent per-sub-writer
// "Timing: ..." breakdown, specifically so recalibrating these against a
// THIRD differently-shaped capture in the future is a single CLI run, not
// scaffolding that needs re-adding.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.NetTrace.Progress {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public readonly struct ProgressRange
{
    public readonly double Start;
    public readonly double End;

    public ProgressRange(double start, double end)
    {
        this.Start = start;
        this.End = end;
    }
}

public readonly struct ExportSubWriterRanges
{
    // Named and ordered to match GcJsonExporter.WriteToFile's own real
    // call order exactly (allocationSummary, exceptionSummary, cpuProfile,
    // contentionSummary, then the gcData loop) - eventOverview's own tiny
    // inline block sits between Exception and Cpu in that call order but
    // gets no dedicated range of its own (see ProgressPlan's own class
    // comment: a phase whose share rounds to under one displayed percent
    // is indistinguishable from not tracking it at all).
    public readonly ProgressRange Allocation;
    public readonly ProgressRange Exception;
    public readonly ProgressRange Cpu;
    public readonly ProgressRange Contention;
    public readonly ProgressRange Gc;

    public ExportSubWriterRanges(ProgressRange allocation, ProgressRange exception, ProgressRange cpu, ProgressRange contention, ProgressRange gc)
    {
        this.Allocation = allocation;
        this.Exception = exception;
        this.Cpu = cpu;
        this.Contention = contention;
        this.Gc = gc;
    }
}

public static class ProgressPlan
{
    // Calibrated against TWO real, differently-shaped captures (see this
    // file's header comment for why one capture alone isn't enough to
    // trust a content-dependent split):
    //   Capture A: ~/projects/Investigations/asset-delivery-api-8-aug-2026-4:10pm.nettrace
    //     (1.57GB, 29,634,864 events, CPU-sample-heavy: 26,132,500 samples,
    //     36 GCs, 1,926,758 ticks, 31,733 exceptions, 0 contentions) -
    //     read=3317ms of a 12438ms total run.
    //   Capture B: ~/projects/Investigations/inventory-api-28-july-2026.nettrace
    //     (5,574,041 events, allocation/GC-heavy, NO CPU samples: 525 GCs,
    //     1,726,831 ticks, 29,100 exceptions, 0 samples, 0 contentions) -
    //     read=1336ms of a 4034ms total run.
    //
    // Read cost (bytes) and "everything else" cost (roughly proportional to
    // event count, for a given capture's own event-type mix) are both
    // ~linear in file size, so their RATIO is capture-SIZE-independent even
    // though absolute durations aren't - a fancier formula evaluated at the
    // only point it CAN run (before any bytes are read) would reduce to
    // exactly this same single number anyway. Measured 26.7% (A) and 33.1%
    // (B) - averaged here. Known, documented limitation: the ratio DOES
    // shift with a capture's event-type MIX (e.g. unusually large per-event
    // stacks/payloads change the bytes-per-event ratio) - not recoverable
    // from file size alone, which is exactly what these two captures'
    // real-world spread already shows.
    public const double ReadShareOfTotal = 0.30;

    // The 7 projector/builder phases combined vs. jsonExport, of whatever
    // time remains after the read phase - measured 22.4% (A: 2043ms
    // projectors / 9117ms combined-with-export) and 21.1% (B: 568ms /
    // 2694ms) - averaged. Much closer agreement between the two captures
    // than ProjectorPhaseWeights' own per-phase split below, which is
    // expected: this combined total is dominated by whichever few phases
    // are large in a given capture, while which SPECIFIC phase that is
    // varies a lot more (see that field's own comment).
    public const double ProjectorsShareOfRemainder = 0.22;

    // Relative weights among the 7 projector/builder phases, in their
    // fixed Program.cs call order (gcProject, allocationProject,
    // exceptionProject, eventOverview, symbolTable, sampleProject,
    // contentionProject) - averaged from each phase's own SHARE of the
    // combined projector total on each of the two reference captures
    // (normalizing by share, not raw ms, since the captures differ ~5x in
    // total event count):
    //   Capture A shares: 46.2%, 14.4%, 4.3%, 7.9%, 5.3%, 18.5%, 3.4%
    //   Capture B shares:  9.7%, 23.2%, 6.3%, 45.6%, 10.0%, 1.6%, 3.5%
    // These disagree with each other far more than ReadShareOfTotal or
    // ProjectorsShareOfRemainder do (e.g. eventOverview: 7.9% vs 45.6%) -
    // a genuinely noisy, capture-content-dependent split (gcProject scales
    // with GC-relevant event DENSITY, not just total event count;
    // eventOverview's own cost didn't track total event count cleanly
    // either), not just measurement noise. Averaging is still the best
    // available estimate without a third data point, but treat this
    // specific array as the least trustworthy set of constants in this
    // file - each of these 7 phases is individually small (all seven
    // combined were only ~21-22% of the non-read total on both captures),
    // so even a poorly-weighted split here moves the bar less than
    // ReadShareOfTotal or the jsonExport sub-writer weights would.
    private static readonly double[] ProjectorPhaseWeights = { 0.2792, 0.1884, 0.0533, 0.2674, 0.0767, 0.1004, 0.0348 };

    // jsonExport sub-writer per-item costs, in nanoseconds/record - REAL
    // measured values (not seeds/guesses) from the two reference captures
    // above, via the permanent per-sub-writer Timing: line breakdown (see
    // Program.cs's own comment on why it's permanent, not throwaway).
    //   Capture A: alloc=2559ms/1926758=1328ns/tick, exc=46ms/31733=1450ns,
    //     cpu=4431ms/26132500=170ns/sample, gc=17ms/36=472222ns/GC.
    //   Capture B: alloc=2040ms/1726831=1182ns/tick, exc=33ms/29100=1134ns,
    //     gc=42ms/525=80000ns/GC (no CPU samples in this capture).
    // AllocationRecordWeight/ExceptionRecordWeight are averaged directly
    // (both had large-enough item counts - tens of thousands to millions -
    // on both captures to trust the average). SampleRecordWeight has only
    // ONE real data point (capture B had zero CPU samples) - capture A's
    // 26.1M-sample count makes it a reliable single source regardless.
    // GcRecordWeight's two data points disagree by ~5.9x (472222 vs 80000)
    // - capture A's own 36-GC sample is almost certainly dominated by
    // fixed per-call/JIT-warmup overhead that capture B's 525 GCs amortize
    // away, so this is weighted 2:1 toward capture B's own larger, more
    // statistically reliable sample rather than a plain average.
    // ContentionRecordWeight has NO real data (both reference captures had
    // zero contentions) - uses ExceptionRecordWeight's own measured value
    // as the best available proxy (structurally similar: one JSON record
    // per discrete event plus its resolved stack).
    private const double GcRecordWeight = 105000.0;
    private const double AllocationRecordWeight = 1255.0;
    private const double ExceptionRecordWeight = 1292.0;
    private const double SampleRecordWeight = 170.0;
    private const double ContentionRecordWeight = 1292.0;

    public static ProgressRange PlanRead()
    {
        return new ProgressRange(0.0, ReadShareOfTotal * 100.0);
    }

    public static ProgressRange PlanProjectorsCombined()
    {
        double start = ReadShareOfTotal * 100.0;
        double remainderPercent = (1.0 - ReadShareOfTotal) * 100.0;
        return new ProgressRange(start, start + (ProjectorsShareOfRemainder * remainderPercent));
    }

    public static ProgressRange PlanJsonExport()
    {
        ProgressRange projectors = PlanProjectorsCombined();
        return new ProgressRange(projectors.End, 100.0);
    }

    // The 7 individual projector/builder phases' own ranges, in Program.cs's
    // fixed call order - see ProjectorPhaseWeights' own comment for what
    // index corresponds to which phase.
    public static ProgressRange[] PlanProjectorPhases()
    {
        ProgressRange combined = PlanProjectorsCombined();
        double totalWeight = 0.0;

        for (int weightIndex = 0; weightIndex < ProjectorPhaseWeights.Length; ++weightIndex)
        {
            totalWeight += ProjectorPhaseWeights[weightIndex];
        }

        ProgressRange[] ranges = new ProgressRange[ProjectorPhaseWeights.Length];
        double cursor = combined.Start;

        for (int phaseIndex = 0; phaseIndex < ProjectorPhaseWeights.Length; ++phaseIndex)
        {
            double width = totalWeight > 0.0
                ? (ProjectorPhaseWeights[phaseIndex] / totalWeight) * (combined.End - combined.Start)
                : (combined.End - combined.Start) / ProjectorPhaseWeights.Length;

            double nextCursor = cursor + width;
            ranges[phaseIndex] = new ProgressRange(cursor, nextCursor);
            cursor = nextCursor;
        }

        // The last range's End can drift a few ULPs short of combined.End
        // from repeated floating-point addition - snap it exactly so the
        // next stage (jsonExport) starts from the true boundary rather
        // than a value a hair below it.
        ranges[ranges.Length - 1] = new ProgressRange(ranges[ranges.Length - 1].Start, combined.End);

        return ranges;
    }

    // jsonExport's own [start, 100) range, split across its 5 sub-writers
    // by REAL counts known at this exact point in Program.cs (right before
    // GcJsonExporter.WriteToFile is called) - the one place in this whole
    // plan where weighting uses THIS run's own actual data rather than a
    // constant measured from a different capture, since these 5 writers
    // are being compared against each other within the same phase of the
    // same run, so a per-item cost estimate is meaningful in a way "bytes
    // vs events" across different units never was (see this file's own
    // header).
    public static ExportSubWriterRanges PlanJsonExportSubWriters(int gcCount, int allocationCount, int exceptionCount, int sampleCount, int contentionCount)
    {
        ProgressRange exportRange = PlanJsonExport();
        double exportWidth = exportRange.End - exportRange.Start;

        double allocationWork = allocationCount * AllocationRecordWeight;
        double exceptionWork = exceptionCount * ExceptionRecordWeight;
        double sampleWork = sampleCount * SampleRecordWeight;
        double contentionWork = contentionCount * ContentionRecordWeight;
        double gcWork = gcCount * GcRecordWeight;

        double totalWork = allocationWork + exceptionWork + sampleWork + contentionWork + gcWork;

        double cursor = exportRange.Start;
        ProgressRange allocationRange = AdvanceRange(ref cursor, allocationWork, totalWork, exportWidth);
        ProgressRange exceptionRange = AdvanceRange(ref cursor, exceptionWork, totalWork, exportWidth);
        ProgressRange cpuRange = AdvanceRange(ref cursor, sampleWork, totalWork, exportWidth);
        ProgressRange contentionRange = AdvanceRange(ref cursor, contentionWork, totalWork, exportWidth);
        ProgressRange gcRange = AdvanceRange(ref cursor, gcWork, totalWork, exportWidth);

        // Same end-snap reasoning as PlanProjectorPhases - the LAST range
        // dispatched (gcData, matching GcJsonExporter.WriteToFile's own
        // real call order) absorbs any floating-point drift.
        gcRange = new ProgressRange(gcRange.Start, exportRange.End);

        return new ExportSubWriterRanges(allocationRange, exceptionRange, cpuRange, contentionRange, gcRange);
    }

    private static ProgressRange AdvanceRange(ref double cursor, double work, double totalWork, double totalWidth)
    {
        double width = totalWork > 0.0 ? (work / totalWork) * totalWidth : 0.0;
        double start = cursor;
        cursor += width;
        return new ProgressRange(start, cursor);
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.Progress)

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////
