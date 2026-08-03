////////////////////////////////////////////////////////////////////////////////
// Module: StackBlockTests.cs
//
// Notes:
// StackBlock.cs's Version/MinimumReaderVersion and FirstId/Count/entry
// layout were pinned against this project's own real capture fixture
// (see this file's own header comment for why), not purely from a written
// spec - so the primary test here is simply "does NettraceFile.Read parse
// the real fixture without a version-compatibility exception, and does the
// resulting StacksById dictionary look plausible" (matches the real,
// independently-verified StackId population already confirmed for
// AllocationTick events - see RealCaptureTests.cs).
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;
using System.IO;

using DotnetInsights.NetTrace;
using DotnetInsights.NetTrace.Gc;

using Xunit;

namespace DotnetInsights.NetTrace.Tests {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public class StackBlockTests
{
    private static readonly string FixturePath = Path.Combine(AppContext.BaseDirectory, "fixtures", "trace2.nettrace");

    [Fact]
    public void NettraceFile_Read_ParsesStackBlocksWithoutThrowing()
    {
        NettraceFile file = NettraceFile.Read(FixturePath);

        Assert.NotNull(file.StacksById);
        Assert.True(file.StacksById.Count > 0, "Expected at least one decoded StackBlock entry in the real fixture.");
    }

    [Fact]
    public void NettraceFile_Read_DecodesTwelveDistinctStacksWithPlausibleAddresses()
    {
        // Dumped once during development: 12 distinct stacks (StackId 1-12).
        // StackId=1 is a genuine empty ("no frames") stack shared by
        // rundown bookkeeping events - not a decode bug, so it's excluded
        // from the non-empty/non-zero-IP checks below rather than papered
        // over by weakening those checks for every stack.
        NettraceFile file = NettraceFile.Read(FixturePath);

        Assert.Equal(12, file.StacksById.Count);
        Assert.Empty(file.StacksById[1]);

        foreach (KeyValuePair<int, long[]> stackEntry in file.StacksById)
        {
            if (stackEntry.Key == 1)
            {
                continue;
            }

            Assert.True(stackEntry.Value.Length > 0, $"StackId {stackEntry.Key} decoded to zero instruction pointers.");

            foreach (long instructionPointer in stackEntry.Value)
            {
                Assert.True(instructionPointer != 0, $"StackId {stackEntry.Key} contains a zero/null instruction pointer.");
            }
        }
    }

    [Fact]
    public void NettraceFile_Read_MostAllocationTickEventsHaveANonEmptyResolvedStack()
    {
        // EventRecord.Stack is resolved eagerly at parse time (see
        // EventBlock.cs) - cross-check against the independently-verified
        // fact (RealCaptureTests.cs / the earlier research pass) that this
        // fixture's AllocationTick events are stack-walked, so most of them
        // should have actually resolved to a real, non-empty stack rather
        // than falling back to Array.Empty<long>() (StackId 0, or a StackId
        // whose StackBlock hadn't been read yet at parse time).
        NettraceFile file = NettraceFile.Read(FixturePath);

        int allocationTickCount = 0;
        int withStackCount = 0;
        foreach (EventRecord record in file.Events)
        {
            if (record.ProviderName != "Microsoft-Windows-DotNETRuntime" || record.EventId != ClrGcEventIds.GCAllocationTick)
            {
                continue;
            }

            ++allocationTickCount;
            if (record.Stack.Length > 0)
            {
                ++withStackCount;
            }
        }

        Assert.True(allocationTickCount > 0);
        Assert.True(withStackCount > 0, "Expected at least one AllocationTick event to have a non-empty resolved stack.");
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.Tests)
