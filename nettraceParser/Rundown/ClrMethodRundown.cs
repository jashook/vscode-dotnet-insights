////////////////////////////////////////////////////////////////////////////////
// Module: ClrMethodRundown.cs
//
// Notes:
// Decoder for the Microsoft-Windows-DotNETRuntimeRundown provider's
// MethodDCStartVerbose event (EventId 144 - the dominant rundown event by
// far, 800+ records in every real capture available here) - fired once per
// currently-loaded managed method at trace-end rundown, giving a
// MethodStartAddress/MethodSize/name triple for every method the process
// actually used. This is the "IP -> method name" half of stack
// resolution (see Rundown/MethodSymbolTable.cs for the other half).
//
// Like every CLR-provider event (Gc/ClrGcTypes.cs's own header comment),
// this is manifest-based, not self-describing - EventName is empty on
// EventRecord for these, so identify the event by EventId, never EventName.
//
// Layout below was pinned by dumping raw payload bytes from three real
// MethodDCStartVerbose records in nettraceParser.Tests/fixtures/trace2.nettrace
// and correlating: MethodID/ModuleID look like real heap pointers (same
// numeric neighborhood as MethodStartAddress and the raw stack IPs decoded
// by Blocks/StackBlock.cs), MethodToken decoded to a genuine MethodDef
// token (0x06xxxxxx - the MethodDef metadata table's token prefix), and the
// three trailing UTF-16 strings decoded to real, readable BCL method names
// (e.g. "System.Buffers.SearchValues"/"TryGetSingleRange"/the method's
// generic signature) - not assumed from a written spec.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.NetTrace.Rundown {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using DotnetInsights.NetTrace.Gc;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public static class ClrRundownEventIds
{
    public const int MethodDCStartVerbose = 144;
}

// Regular Microsoft-Windows-DotNETRuntime provider (not Rundown) - fired
// live, during tracing, whenever a method is actually JIT'd/loaded or
// unloaded, each carrying its own timestamp. This is what makes a
// time-aware MethodSymbolTable possible at all: MethodDCStartVerbose alone
// (rundown, fired once at trace-end) only says a method was loaded *by* the
// end of the capture, never *when* - not enough to tell whether a given
// stack frame's address still belonged to that method at the time that
// particular frame was actually captured, if the address was reused by a
// different method in between (collectible/dynamic methods being the
// common case - see MethodSymbolTable.cs). EventID values confirmed against
// microsoft/perfview's ClrTraceEventParser.cs (MethodTaskGuid, opcodes 37/38).
public static class ClrMethodEventIds
{
    public const int MethodLoadVerbose = 143;
    public const int MethodUnloadVerbose = 144;
}

public class ClrMethodRecord
{
    public long MethodID;
    public long MethodStartAddress;
    public long MethodSize;
    public string DisplayName;

    // Every field's byte offset (pointer-aware via PayloadReader.HostOffset,
    // same technique Gc/ClrGcPerHeapHistory.cs's ClrGcHeap.Decode already
    // uses): MethodID (pointer), ModuleID (pointer), MethodStartAddress
    // (pointer), MethodSize (Int32), MethodToken (Int32), MethodFlags
    // (Int32), then three null-terminated UTF-16 strings: Namespace,
    // MethodName, Signature. Same payload shape for MethodLoadVerbose/
    // MethodUnloadVerbose (regular CLR provider) as MethodDCStartVerbose
    // (Rundown provider) - confirmed against microsoft/perfview's
    // ClrTraceEventParser.cs: both are the MethodLoadUnloadVerboseTraceData
    // class, just registered under different EventIDs/providers.
    //
    // Byte offset immediately after the three fixed pointer/Int32 fields
    // (MethodID/ModuleID/MethodStartAddress/MethodSize/MethodToken/
    // MethodFlags) - where the first of the three trailing strings starts.
    // Shared by DecodeHeader/DecodeDisplayName/Decode so the "is this
    // payload even long enough" bounds check stays in exactly one place
    // rather than three independently-maintained copies.
    private static int StringsStart(PayloadReader reader)
    {
        return reader.HostOffset(24, 3);
    }

    // Cheap half: MethodID/MethodStartAddress/MethodSize only, no string
    // decode at all - see MethodSymbolTable.cs's own comment on why this
    // matters (a real capture's method-rundown volume is dominated by
    // methods a resolved stack frame never actually looks up, so eagerly
    // paying for the string half of every single one is pure waste for the
    // overwhelming majority). Same bounds check as the full Decode below
    // (requires the whole record, strings included, to be present) so
    // "which records are considered valid at all" stays identical between
    // the header-only and full decode paths - a truncated record that
    // would have failed Decode entirely still fails here, rather than
    // silently admitting a range with well-formed header fields but
    // unreadable string data.
    public static bool DecodeHeader(PayloadReader reader, out long methodId, out long methodStartAddress, out long methodSize)
    {
        if (reader.Length < StringsStart(reader))
        {
            methodId = 0;
            methodStartAddress = 0;
            methodSize = 0;
            return false;
        }

        methodId = reader.GetAddressAt(reader.HostOffset(0, 0));
        methodStartAddress = reader.GetAddressAt(reader.HostOffset(8, 2));
        methodSize = reader.GetInt32At(reader.HostOffset(12, 3));
        return true;
    }

    // Expensive half: the two-string decode+join DecodeHeader deliberately
    // skips. Callers must already know reader's payload is long enough (a
    // successful prior DecodeHeader call against the SAME payload already
    // proved this, since both share the identical StringsStart bounds
    // check) - MethodSymbolTable.cs only ever calls this lazily, against a
    // (PayloadBuffer, PayloadOffset, PayloadLength) triple it only stored
    // in the first place because DecodeHeader already succeeded for it.
    public static string DecodeDisplayName(PayloadReader reader)
    {
        int stringsStart = StringsStart(reader);

        string methodNamespace = reader.GetUnicodeStringAt(stringsStart);
        int methodNameOffset = reader.SkipUnicodeString(stringsStart);
        string methodName = reader.GetUnicodeStringAt(methodNameOffset);

        // Signature (the third string, immediately after methodName) isn't
        // needed for DisplayName - the two joined names are already
        // sufficient to disambiguate real BCL/user methods in the drill
        // down table, and skipping it avoids reading past a truncated
        // payload for no benefit.
        return string.IsNullOrEmpty(methodNamespace) ? methodName : $"{methodNamespace}.{methodName}";
    }

    // Full decode (header + DisplayName together) - kept for callers that
    // want the whole record in one call (tests, and any one-off decode not
    // on MethodSymbolTable's hot Build/Resolve path, which uses
    // DecodeHeader/DecodeDisplayName separately instead - see their own
    // comments for why).
    public static ClrMethodRecord Decode(PayloadReader reader)
    {
        long methodId;
        long methodStartAddress;
        long methodSize;
        if (!DecodeHeader(reader, out methodId, out methodStartAddress, out methodSize))
        {
            return null;
        }

        ClrMethodRecord method = new ClrMethodRecord();
        method.MethodID = methodId;
        method.MethodStartAddress = methodStartAddress;
        method.MethodSize = methodSize;
        method.DisplayName = DecodeDisplayName(reader);

        return method;
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.Rundown)

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////
