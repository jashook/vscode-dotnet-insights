////////////////////////////////////////////////////////////////////////////////
// Module: TestStacks.cs
//
// Notes:
// Test-side companion to StackTable: builds one up as a test declares stacks,
// and hands back the index an event carries.
//
// Events refer to their stack by index rather than holding the decoded long[]
// (see StackTable.cs for the measured reason), so a test that used to write
// `stack: new long[] { 0x1000 }` inline now has to put those frames somewhere
// the code under test can resolve them from. This keeps that to one extra
// local per test and one extra argument, rather than every test hand-rolling
// its own table.
////////////////////////////////////////////////////////////////////////////////

using DotnetInsights.NetTrace;

namespace DotnetInsights.NetTrace.Tests {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

internal sealed class TestStacks
{
    public StackTable Table { get; } = new StackTable();

    // Registers one stack and returns its index. No frames means "this event
    // wasn't stack-walked", which is StackTable's own reserved empty index
    // rather than a new entry.
    //
    // Deduplicating (StackTable.GetOrAdd) means two calls with the same frames
    // return the SAME index, which is what the real parser does too - a test
    // that needs two distinct stacks has to declare two distinct frame lists.
    public int Index(params long[] frames)
    {
        if (frames == null || frames.Length == 0)
        {
            return StackTable.EmptyStackIndex;
        }

        return this.Table.GetOrAdd(frames);
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.Tests)

////////////////////////////////////////////////////////////////////////////////
