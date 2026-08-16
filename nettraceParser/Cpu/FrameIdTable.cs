////////////////////////////////////////////////////////////////////////////////
// Module: FrameIdTable.cs
//
// Notes:
// A map from frame id to TValue backed by plain arrays instead of a
// Dictionary, for the frame-id-keyed lookups that happen once per SAMPLE
// (16.24M times on a real 3.23GB capture) rather than once per distinct
// method. Dictionary<Int32, __Canon>.FindValue was 13.4% of the whole
// "Exporting CPU profile" phase measured that way - all of it hashing and
// probing for keys that are already small dense integers minted by
// MethodSymbolTable itself.
//
// Same two-range indexing as Cpu/IdleWaitFrameCache.cs and Cpu/FrameIdSet.cs
// (see MethodSymbolTable.UnresolvedIdBase). Keys tracks insertion order so
// callers that used to enumerate the dictionary still can.
//
// Absence is represented by default(TValue): null for a class. A caller
// storing a value type where 0 is meaningful (see WriteTimeline's rank
// lookup) should store "value + 1" and treat 0 as absent, rather than this
// type carrying a second presence array for every caller's sake.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.NetTrace.Cpu {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;

using DotnetInsights.NetTrace.Rundown;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public sealed class FrameIdTable<TValue>
{
    private const int InitialCapacity = 1024;

    private TValue[] resolvedValues = new TValue[InitialCapacity];
    private TValue[] unresolvedValues = new TValue[InitialCapacity];

    // Every frame id that has had Set called on it, in insertion order.
    private readonly List<int> keys = new List<int>();

    public List<int> Keys => this.keys;

    public int Count => this.keys.Count;

    public TValue Get(int frameId)
    {
        bool isUnresolved = frameId >= MethodSymbolTable.UnresolvedIdBase;
        int index = isUnresolved ? frameId - MethodSymbolTable.UnresolvedIdBase : frameId;
        TValue[] values = isUnresolved ? this.unresolvedValues : this.resolvedValues;

        if (index >= values.Length)
        {
            return default;
        }

        return values[index];
    }

    // Records frameId in Keys the first time it's set. Callers only ever set
    // a given id once (they Get first, and only Set when it came back absent),
    // so this doesn't re-scan Keys to check.
    public void Set(int frameId, TValue value)
    {
        bool isUnresolved = frameId >= MethodSymbolTable.UnresolvedIdBase;
        int index = isUnresolved ? frameId - MethodSymbolTable.UnresolvedIdBase : frameId;

        TValue[] values = isUnresolved ? this.unresolvedValues : this.resolvedValues;
        if (index >= values.Length)
        {
            values = Grow(values, index);
            if (isUnresolved)
            {
                this.unresolvedValues = values;
            }
            else
            {
                this.resolvedValues = values;
            }
        }

        values[index] = value;
        this.keys.Add(frameId);
    }

    private static TValue[] Grow(TValue[] values, int requiredIndex)
    {
        int newLength = values.Length;
        while (newLength <= requiredIndex)
        {
            newLength *= 2;
        }

        TValue[] grown = new TValue[newLength];
        Array.Copy(values, grown, values.Length);
        return grown;
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.Cpu)

////////////////////////////////////////////////////////////////////////////////
