////////////////////////////////////////////////////////////////////////////////
// Module: FrameIdSet.cs
//
// Notes:
// "Which frame ids does this one stack touch, once each" - a stamp-based set
// with O(1) add and, crucially, O(1) RESET between stacks.
//
// This is the third implementation of that dedup in CpuProfileJsonExporter,
// and the history is the whole justification for this one:
//   1. HashSet<int> + Clear() per stack. Clear() zeroes the entire internal
//      bucket array scaled to PEAK capacity, so one deep stack early on
//      permanently taxed every later shallow one - measured as the single
//      largest self-time cost in the whole CPU export.
//   2. A reused int[] scratch buffer, Array.Sort'd then compacted. Fixed the
//      zeroing (sorting only touches the [0, length) prefix), but pays
//      O(depth log depth) per distinct stack. Measured on a real 3.23GB
//      capture (assets-registry-service-15-aug-2026, 16.24M samples):
//      Array.Sort<int> was 16.5% of the whole "Exporting CPU profile" phase.
//   3. This: a stamp per frame id. Adding is one array read plus one write,
//      resetting is one integer increment, and nothing is ever zeroed or
//      sorted - O(depth), no comparisons at all.
//
// Indexing mirrors Cpu/IdleWaitFrameCache.cs: the symbol table's id space is
// two dense ranges, not one (see MethodSymbolTable.UnresolvedIdBase), so this
// holds one array per range rather than paying a dictionary lookup per frame.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.NetTrace.Cpu {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System;

using DotnetInsights.NetTrace.Rundown;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public sealed class FrameIdSet
{
    private const int InitialCapacity = 1024;

    // Stamp value 0 means "never added", so the first StartNewSet() moves to
    // 1 and a freshly allocated (zeroed) array reads as empty for free.
    private int[] resolvedStamps = new int[InitialCapacity];
    private int[] unresolvedStamps = new int[InitialCapacity];
    private int currentStamp;

    // Begins a new, empty set. Every id added before this call is forgotten -
    // no clearing, no allocation.
    public void StartNewSet()
    {
        if (this.currentStamp == int.MaxValue)
        {
            // Unreachable on any real capture (this would need 2^31 distinct
            // stacks; the largest measured here is 633,378), but a wrapped
            // stamp would silently report already-present ids as new, so
            // handle it rather than document it as impossible.
            this.resolvedStamps = new int[this.resolvedStamps.Length];
            this.unresolvedStamps = new int[this.unresolvedStamps.Length];
            this.currentStamp = 0;
        }

        ++this.currentStamp;
    }

    // Returns true when frameId was not already in the current set.
    public bool Add(int frameId)
    {
        bool isUnresolved = frameId >= MethodSymbolTable.UnresolvedIdBase;
        int index = isUnresolved ? frameId - MethodSymbolTable.UnresolvedIdBase : frameId;

        int[] stamps = isUnresolved ? this.unresolvedStamps : this.resolvedStamps;
        if (index >= stamps.Length)
        {
            stamps = Grow(stamps, index);
            if (isUnresolved)
            {
                this.unresolvedStamps = stamps;
            }
            else
            {
                this.resolvedStamps = stamps;
            }
        }

        if (stamps[index] == this.currentStamp)
        {
            return false;
        }

        stamps[index] = this.currentStamp;
        return true;
    }

    private static int[] Grow(int[] stamps, int requiredIndex)
    {
        int newLength = stamps.Length;
        while (newLength <= requiredIndex)
        {
            newLength *= 2;
        }

        int[] grown = new int[newLength];
        Array.Copy(stamps, grown, stamps.Length);
        return grown;
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.Cpu)

////////////////////////////////////////////////////////////////////////////////
