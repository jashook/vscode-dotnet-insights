////////////////////////////////////////////////////////////////////////////////
// Module: ReadPhaseGcSuppressionTests.cs
//
// Notes:
// Covers ReadPhaseGcSuppression.ComputeBudgetBytes's sizing rules - see that
// file's own header comment for the measured evidence behind each constant.
// The case that actually matters most here is the "declines rather than
// undersizing" one: a NoGCRegion whose budget runs out mid-read is measurably
// WORSE than never starting one (3743ms/[5,4,4] vs a 3561ms/[4,3,3] baseline
// on a real capture), so every path that can't confidently cover the whole
// read must return 0 rather than a smaller-but-nonzero budget.
////////////////////////////////////////////////////////////////////////////////

using DotnetInsights.NetTrace;

using Xunit;

namespace DotnetInsights.NetTrace.Tests {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public class ReadPhaseGcSuppressionTests
{
    private const long Megabyte = 1024L * 1024;
    private const long Gigabyte = 1024L * 1024 * 1024;

    // Plenty of headroom - isolates the rules under test from the
    // affordability clamp.
    private const long AmpleMemory = 64L * Gigabyte;

    [Fact]
    public void ComputeBudgetBytes_DeclinesForSmallInputsWhereSuppressionIsNotWorthIt()
    {
        Assert.Equal(0, ReadPhaseGcSuppression.ComputeBudgetBytes(9 * Megabyte, AmpleMemory));
        Assert.Equal(0, ReadPhaseGcSuppression.ComputeBudgetBytes(63 * Megabyte, AmpleMemory));
    }

    [Fact]
    public void ComputeBudgetBytes_RequestsAboveTheMeasuredMinimumForRealCaptureSizes()
    {
        // The two real captures this was measured against: a 737MB capture
        // needed >=512MB (0.69x) and a 1115MB capture needed >=1024MB
        // (0.92x) for the region to survive the whole read. Both must come
        // back with a budget comfortably above their own observed minimum.
        long budgetFor737Mb = ReadPhaseGcSuppression.ComputeBudgetBytes(737 * Megabyte, AmpleMemory);
        Assert.True(budgetFor737Mb > 512 * Megabyte, $"737MB capture should request more than its 512MB observed minimum, got {budgetFor737Mb / Megabyte}MB");

        long budgetFor1115Mb = ReadPhaseGcSuppression.ComputeBudgetBytes(1115 * Megabyte, AmpleMemory);
        Assert.True(budgetFor1115Mb > 1024 * Megabyte, $"1115MB capture should request more than its 1024MB observed minimum, got {budgetFor1115Mb / Megabyte}MB");
    }

    [Fact]
    public void ComputeBudgetBytes_ScalesWithInputSize()
    {
        long smallerBudget = ReadPhaseGcSuppression.ComputeBudgetBytes(200 * Megabyte, AmpleMemory);
        long largerBudget = ReadPhaseGcSuppression.ComputeBudgetBytes(800 * Megabyte, AmpleMemory);

        Assert.True(largerBudget > smallerBudget);
    }

    // The important one - see this file's header comment. A machine that
    // can't back the full budget must get NO region at all, never a
    // partial one.
    [Fact]
    public void ComputeBudgetBytes_DeclinesEntirelyRatherThanUndersizingWhenMemoryIsTight()
    {
        // A 1GB capture on a machine reporting only 1GB available: half of
        // that (the affordability cap) is far below what this read needs.
        long budget = ReadPhaseGcSuppression.ComputeBudgetBytes(1024 * Megabyte, 1024 * Megabyte);

        Assert.Equal(0, budget);
    }

    [Fact]
    public void ComputeBudgetBytes_DeclinesForInputsTooLargeToBudgetSensibly()
    {
        Assert.Equal(0, ReadPhaseGcSuppression.ComputeBudgetBytes(16 * Gigabyte, 256L * Gigabyte));
    }

    [Fact]
    public void ComputeBudgetBytes_SkipsTheAffordabilityCheckWhenAvailableMemoryIsUnknown()
    {
        // 0 means "unknown" (GC.GetGCMemoryInfo can report it) - should fall
        // back to the size-based rules rather than declining outright.
        long budget = ReadPhaseGcSuppression.ComputeBudgetBytes(737 * Megabyte, totalAvailableMemoryBytes: 0);

        Assert.True(budget > 512 * Megabyte);
    }

    [Fact]
    public void TryStart_DoesNothingForADeclinedBudget()
    {
        Assert.False(ReadPhaseGcSuppression.TryStart(0));
        Assert.False(ReadPhaseGcSuppression.TryStart(-1));
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.Tests)
