////////////////////////////////////////////////////////////////////////////////
// Module: IdleWaitFrameCache.cs
//
// Notes:
// Memoizes CpuIdleWaitClassifier's own answer per resolved frame id, for
// callers that classify one leaf frame per CPU SAMPLE. That classifier is a
// linear walk of 11 string prefixes plus 7 exact-name compares (see its own
// header comment for why it deliberately stays a literal list), which is
// cheap once and ruinous 16 million times: on a real 3.23GB capture
// (assets-registry-service-15-aug-2026, 16.24M samples) it plus the
// SpanHelpers.SequenceEqual underneath it accounted for ~20% of the whole
// "Exporting exception summary" phase, purely re-deciding the same few
// thousand distinct methods over and over.
//
// The number of DISTINCT leaf methods in a capture is small (2,675 on the
// 1.57GB capture this codebase's earlier CPU-profiling work measured), so the
// cache is bounded by the symbol table's own id space, not by sample count.
// That id space is two dense ranges rather than one (see
// MethodSymbolTable.UnresolvedIdBase), which is why this holds two arrays and
// not a dictionary - an int-indexed array read is the whole point.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.NetTrace.Cpu {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System;

using DotnetInsights.NetTrace.Rundown;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public sealed class IdleWaitFrameCache
{
    // Tri-state per id: a bool[] can't tell "false" from "not asked yet", and
    // a nullable bool[] would box nothing but still cost 2 bytes/entry with a
    // worse access pattern.
    private const byte StateUnknown = 0;
    private const byte StateIdleWait = 1;
    private const byte StateNotIdleWait = 2;

    private const int InitialCapacity = 1024;

    private readonly MethodSymbolTable symbolTable;

    // Indexed by frame id directly for real (resolved) ids, and by
    // (id - MethodSymbolTable.UnresolvedIdBase) for the unresolved-address
    // ids. Both grow on demand - the symbol table mints ids lazily, so
    // neither final count is known up front.
    private byte[] resolvedStates = new byte[InitialCapacity];
    private byte[] unresolvedStates = new byte[InitialCapacity];

    public IdleWaitFrameCache(MethodSymbolTable symbolTable)
    {
        this.symbolTable = symbolTable;
    }

    // frameId must have come from this cache's own symbolTable (same
    // requirement MethodSymbolTable.NameForId already documents).
    public bool IsIdleWaitFrame(int frameId)
    {
        bool isUnresolved = frameId >= MethodSymbolTable.UnresolvedIdBase;
        int index = isUnresolved ? frameId - MethodSymbolTable.UnresolvedIdBase : frameId;

        byte[] states = isUnresolved ? this.unresolvedStates : this.resolvedStates;
        if (index >= states.Length)
        {
            states = Grow(states, index);
            if (isUnresolved)
            {
                this.unresolvedStates = states;
            }
            else
            {
                this.resolvedStates = states;
            }
        }

        byte state = states[index];
        if (state == StateUnknown)
        {
            state = CpuIdleWaitClassifier.IsKnownIdleWaitLeafMethodName(this.symbolTable.NameForId(frameId))
                ? StateIdleWait
                : StateNotIdleWait;
            states[index] = state;
        }

        return state == StateIdleWait;
    }

    private static byte[] Grow(byte[] states, int requiredIndex)
    {
        int newLength = states.Length;
        while (newLength <= requiredIndex)
        {
            newLength *= 2;
        }

        byte[] grown = new byte[newLength];
        Array.Copy(states, grown, states.Length);
        return grown;
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.Cpu)

////////////////////////////////////////////////////////////////////////////////
