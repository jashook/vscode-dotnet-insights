////////////////////////////////////////////////////////////////////////////////
// Module: MethodSymbolTable.cs
//
// Notes:
// Resolves a raw instruction pointer (from Blocks/StackBlock.cs's decoded
// stacks) to a method display name, built from every
// MethodDCStartVerbose record's [MethodStartAddress, MethodStartAddress +
// MethodSize) range (see ClrMethodRundown.cs). Rundown only covers managed
// methods the process actually JIT'd - an IP outside every known range is
// expected (native/runtime-internal frames) and resolves to a placeholder
// rather than throwing.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.NetTrace.Rundown {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System.Collections.Generic;

using DotnetInsights.NetTrace.Gc;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public class MethodSymbolTable
{
    private class MethodRange
    {
        public long StartAddress;
        public long EndAddress;
        public string DisplayName;
    }

    private const string ClrRundownProviderName = "Microsoft-Windows-DotNETRuntimeRundown";

    private readonly List<MethodRange> sortedRanges;

    private MethodSymbolTable(List<MethodRange> sortedRanges)
    {
        this.sortedRanges = sortedRanges;
    }

    public static MethodSymbolTable Build(IEnumerable<EventRecord> events, int pointerSize)
    {
        List<MethodRange> ranges = new List<MethodRange>();

        foreach (EventRecord record in events)
        {
            if (record.ProviderName != ClrRundownProviderName || record.EventId != ClrRundownEventIds.MethodDCStartVerbose)
            {
                continue;
            }

            PayloadReader reader = new PayloadReader(record.PayloadBytes, pointerSize);
            ClrMethodRecord method = ClrMethodRecord.Decode(reader);

            if (method == null || method.MethodSize <= 0)
            {
                continue;
            }

            MethodRange range = new MethodRange();
            range.StartAddress = method.MethodStartAddress;
            range.EndAddress = method.MethodStartAddress + method.MethodSize;
            range.DisplayName = method.DisplayName;
            ranges.Add(range);
        }

        ranges.Sort(CompareByStartAddress);

        return new MethodSymbolTable(ranges);
    }

    public string Resolve(long instructionPointer)
    {
        int lowIndex = 0;
        int highIndex = this.sortedRanges.Count - 1;

        while (lowIndex <= highIndex)
        {
            int midIndex = lowIndex + ((highIndex - lowIndex) / 2);
            MethodRange candidate = this.sortedRanges[midIndex];

            if (instructionPointer < candidate.StartAddress)
            {
                highIndex = midIndex - 1;
            }
            else if (instructionPointer >= candidate.EndAddress)
            {
                lowIndex = midIndex + 1;
            }
            else
            {
                return candidate.DisplayName;
            }
        }

        return $"<unresolved 0x{instructionPointer:X}>";
    }

    private static int CompareByStartAddress(MethodRange left, MethodRange right)
    {
        return left.StartAddress.CompareTo(right.StartAddress);
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.Rundown)

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////
