////////////////////////////////////////////////////////////////////////////////
// Module: UniversalSymbolTableTests.cs
//
// Notes:
// Covers the two pieces of collect-linux support that are pure functions of
// their input and so can be pinned exactly: the symbol-name formatter
// (Universal/UniversalSymbolTable.cs) and the CLR event version inference
// (V6/V6ClrEventVersions.cs).
//
// Every managed and native name below is a REAL symbol taken verbatim from a
// `dotnet-trace collect-linux` capture of a production service, not an
// invented one - the formatter's whole job is coping with the shapes the CLR
// and the system toolchain actually emit, and inventing inputs would have
// missed the C++ case that broke it.
////////////////////////////////////////////////////////////////////////////////

using DotnetInsights.NetTrace.Universal;
using DotnetInsights.NetTrace.V6;

using Xunit;

namespace DotnetInsights.NetTrace.Tests {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public class UniversalSymbolNameFormatterTests
{
    [Fact]
    public void FormatSymbolName_RewritesTheClrPerfMapFormIntoNamespaceTypeMethod()
    {
        string formatted = UniversalSymbolTable.FormatSymbolName(
            "instance void [Roblox.AssetsCore.ClientSideCaching] Roblox.AssetsCore.ClientSideCaching.ExpirableDictionary::TraverseAndPurge(object)[OptimizedTier1]");

        Assert.Equal("Roblox.AssetsCore.ClientSideCaching.ExpirableDictionary.TraverseAndPurge [OptimizedTier1]", formatted);
    }

    [Fact]
    public void FormatSymbolName_KeepsTheJitTier()
    {
        string formatted = UniversalSymbolTable.FormatSymbolName(
            "instance bool [System.Private.CoreLib] System.Threading.ThreadPoolWorkQueue::Dequeue(class Foo&, bool&)[QuickJitted]");

        Assert.Equal("System.Threading.ThreadPoolWorkQueue.Dequeue [QuickJitted]", formatted);
    }

    [Fact]
    public void FormatSymbolName_HandlesAMethodWithNoTierSuffix()
    {
        string formatted = UniversalSymbolTable.FormatSymbolName(
            "instance void [System.Private.CoreLib] System.Threading.TimerQueue::FireNextTimers()");

        Assert.Equal("System.Threading.TimerQueue.FireNextTimers", formatted);
    }

    // Explicit interface implementations carry more than one "::", so
    // replacing only the first leaves a hybrid that is neither form.
    [Fact]
    public void FormatSymbolName_ReplacesEveryScopeOperator()
    {
        string formatted = UniversalSymbolTable.FormatSymbolName(
            "instance void [Some.Assembly] Some.Type::Google.Protobuf.IBufferMessage.InternalWriteTo(class W&)[OptimizedTier1]");

        Assert.DoesNotContain("::", formatted);
        Assert.Equal("Some.Type.Google.Protobuf.IBufferMessage.InternalWriteTo [OptimizedTier1]", formatted);
    }

    // A return type can carry parentheses of its own. Taking the FIRST '('
    // as the start of the signature cut the body off before the type began
    // and left the whole raw symbol in the CPU view.
    [Fact]
    public void FormatSymbolName_IgnoresParenthesesInsideTheReturnType()
    {
        string formatted = UniversalSymbolTable.FormatSymbolName(
            "instance void modreq([System.Runtime]System.Runtime.CompilerServices.IsExternalInit) [Roblox.AssetDelivery.Api] Roblox.Web.Assets.TokenCacheEntry::set_Token(string)[OptimizedTier1]");

        Assert.Equal("Roblox.Web.Assets.TokenCacheEntry.set_Token [OptimizedTier1]", formatted);
    }

    // REGRESSION. "contains ::" is not enough to identify a managed name -
    // C++ symbols are full of it, and a collect-linux capture resolves plenty
    // of them out of the runtime's own native dependencies. An earlier version
    // keyed only on "::" and rewrote these into a mangled hybrid
    // ("icu_78.CollationKeys::writeSortKeyUpToQuaternary") that is neither the
    // real symbol nor a valid managed name. Native symbols have no assembly
    // bracket, which is what makes them safe to leave alone.
    [Theory]
    [InlineData("icu_78::CollationKeys::writeSortKeyUpToQuaternary(icu_78::CollationIterator&, signed char const*)")]
    [InlineData("std::__atomic_futex_unsigned_base::_M_futex_wait_until(unsigned int*, unsigned int, bool)")]
    [InlineData("CorProfilerCallback::EventPipeEventDelivered(unsigned long, unsigned int)")]
    [InlineData("icu_78::RuleBasedCollator::getSortKey(char16_t const*, int, unsigned char*, int) const")]
    public void FormatSymbolName_LeavesNativeCppSymbolsExactlyAsTheyAre(string rawName)
    {
        Assert.Equal(rawName, UniversalSymbolTable.FormatSymbolName(rawName));
    }

    [Theory]
    [InlineData("finish_task_switch.isra.0")]
    [InlineData("_raw_spin_unlock_irqrestore")]
    [InlineData("__pthread_mutex_lock")]
    [InlineData("do_syscall_64")]
    public void FormatSymbolName_LeavesPlainCSymbolsAlone(string rawName)
    {
        Assert.Equal(rawName, UniversalSymbolTable.FormatSymbolName(rawName));
    }

    // A generic argument list is bracketed too, so the tier extractor has to
    // tell "[OptimizedTier1]" from "[[System.Int32, System.Private.CoreLib]]".
    [Fact]
    public void FormatSymbolName_DoesNotMistakeGenericArgumentsForAJitTier()
    {
        string formatted = UniversalSymbolTable.FormatSymbolName(
            "instance void [System.Private.CoreLib] System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1[System.__Canon]::GetStateMachineBox()[OptimizedTier1]");

        Assert.Equal("System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1[System.__Canon].GetStateMachineBox [OptimizedTier1]", formatted);
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public class V6ClrEventVersionsTests
{
    // The versions a real runtime emits, taken from four v5 captures'
    // metadata (which carries both the id and the version). A v6 capture
    // carries neither, so these are what the reader has to supply.
    [Theory]
    [InlineData(1, 2)]     // GCStart
    [InlineData(2, 1)]     // GCEnd
    [InlineData(4, 2)]     // GCHeapStats
    [InlineData(9, 1)]     // GCSuspendEEBegin
    [InlineData(10, 4)]    // GCAllocationTick
    [InlineData(80, 1)]    // ExceptionThrown
    [InlineData(204, 3)]   // GCPerHeapHistory
    [InlineData(205, 4)]   // GCGlobalHeapHistory
    public void EmittedVersionFor_MatchesWhatAV5CaptureReports(int eventId, int expectedVersion)
    {
        Assert.Equal(expectedVersion, V6ClrEventVersions.EmittedVersionFor(eventId));
    }

    // The gates these versions exist to clear: ExceptionEventProjector drops
    // anything below 1, AllocationEventProjector anything below 2, and
    // GcEventProjector decodes GCPerHeapHistory only at 3 or above. A version
    // of 0 is not a harmless default - it silently empties whole views.
    [Fact]
    public void Resolve_ClearsTheProjectorVersionGates()
    {
        Assert.True(V6ClrEventVersions.Resolve(80, 182, 0, -1) >= 1);
        Assert.True(V6ClrEventVersions.Resolve(10, 64, 0, -1) >= 2);
        Assert.True(V6ClrEventVersions.Resolve(204, 486, 0, -1) >= 3);
    }

    // An explicit Version label on the event wins over everything - v6 lets a
    // LabelList override metadata, and the reference capture's
    // GCPerHeapHistory events really do use it.
    [Fact]
    public void Resolve_PrefersALabelVersionOverEverythingElse()
    {
        Assert.Equal(7, V6ClrEventVersions.Resolve(1, 26, 2, 7));
    }

    [Fact]
    public void Resolve_PrefersMetadataVersionOverTheTable()
    {
        Assert.Equal(1, V6ClrEventVersions.Resolve(1, 26, 1, -1));
    }

    // The payload length is the tie-breaker the decoders cannot apply
    // themselves: GCEnd and GCSuspendEEBegin read a field as int32 at version
    // 1 and int16 below it, and both widths fit in a long-enough payload, so
    // claiming a version the payload cannot support returns a plausible wrong
    // number rather than failing.
    [Theory]
    [InlineData(1, 26, 2)]    // GCStart V2
    [InlineData(1, 18, 1)]    // GCStart V1 - no ClientSequenceNumber
    [InlineData(1, 16, 0)]    // GCStart V0
    [InlineData(2, 10, 1)]    // GCEnd V1
    [InlineData(2, 6, 0)]     // GCEnd V0 - int16 Count/Depth
    [InlineData(4, 110, 2)]   // GCHeapStats V2
    [InlineData(4, 94, 1)]    // GCHeapStats V1
    [InlineData(9, 10, 1)]    // GCSuspendEEBegin V1
    [InlineData(9, 4, 0)]     // GCSuspendEEBegin V0
    public void Resolve_TakesTheVersionDownToWhatThePayloadCanSupport(int eventId, int payloadLength, int expectedVersion)
    {
        Assert.Equal(expectedVersion, V6ClrEventVersions.Resolve(eventId, payloadLength, 0, -1));
    }

    [Fact]
    public void EmittedVersionFor_ReportsUnknownForAnIdItHasNeverSeen()
    {
        Assert.Equal(V6ClrEventVersions.UnknownVersion, V6ClrEventVersions.EmittedVersionFor(31337));
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.Tests)

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////
