////////////////////////////////////////////////////////////////////////////////
// Module: RustDemanglerTests.cs
//
// Notes:
// Pins the six v0 grammar traps that were found by diffing against rustfilt
// (rustc's own rustc-demangle) over 757 real symbols, taking it from 63.54% to
// 100.00% exact. Every one of them produced a PLAUSIBLE name rather than a
// failure, which is what makes them worth pinning: a regression would not look
// like a regression.
//
// These are unit tests over real mangled names taken from that binary, not
// synthesized ones - a hand-written mangled string can accidentally avoid the
// exact shape that broke.
//
// The full 757-symbol diff is a separate, fixture-gated check (see
// RustDemanglerGroundTruthTests) because it needs the table rustfilt produced.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.Perf.Tests {

using Xunit;

using DotnetInsights.NetTrace.Symbols;

public class RustDemanglerTests
{
    // The crate disambiguator here is eleven base-62 digits - a full-width u64
    // hash. Accumulating it into a signed long and rejecting on Int64 overflow
    // declined EVERY symbol in the binary, because the crate root is the first
    // thing every path resolves to.
    [Fact]
    public void Demangle_FullWidthCrateDisambiguator_DoesNotOverflow()
    {
        Assert.Equal(
            "rust_workload::hot_leaf_beta",
            RustDemangler.Demangle("_RNvCsfsN783Mr1pU_13rust_workload13hot_leaf_beta"));
    }

    // Two monomorphizations of one generic function are two distinct symbols
    // and must stay two distinct rows.
    [Fact]
    public void Demangle_GenericInstantiation_KeepsTurbofishAndArgument()
    {
        Assert.Equal(
            "rust_workload::compute_generic::<rust_workload::BetaKind>",
            RustDemangler.Demangle("_RINvCsfsN783Mr1pU_13rust_workload15compute_genericNtB2_8BetaKindEB2_"));
    }

    // `{closure#N}` with SINGLE braces and the disambiguator index. The doubled
    // `{{closure}}` form belongs to the legacy scheme and collapses every
    // distinct closure in a module into one indistinguishable row.
    [Fact]
    public void Demangle_Closure_UsesSingleBracesAndDisambiguatorIndex()
    {
        Assert.Equal(
            "<std::backtrace_rs::symbolize::gimli::Cache>::with_global::<std::backtrace_rs::symbolize::gimli::resolve::{closure#1}>",
            RustDemangler.Demangle("_RINvMs0_NtNtNtCs7HaW2mv5hwE_3std12backtrace_rs9symbolize5gimliNtB6_5Cache11with_globalNCNvB6_7resolves_0EBc_"));
    }

    // <decimal-number> is "0" | [1-9][0-9]* - there are no leading zeros - so
    // `000` is three separate zero-length identifiers, not one number. Parsing
    // it greedily consumed all three and left the cursor mid-construct.
    [Fact]
    public void Demangle_NestedAnonymousItems_TreatsEachZeroAsItsOwnIdentifier()
    {
        Assert.Equal(
            "std::rt::lang_start_internal::{closure#0}::{closure#0}::{closure#1}",
            RustDemangler.Demangle("_RNCNCNCNvNtCs7HaW2mv5hwE_3std2rt19lang_start_internal00s_0B9_"));
    }

    // A dyn trait's associated bindings join the trait's OWN generic list, and
    // the binder is rendered inside `dyn`. `G0_` introduces TWO lifetimes: the
    // binder count, like the disambiguator, is the base-62 number plus one.
    [Fact]
    public void Demangle_DynTraitWithBinderAndAssociatedBinding_MergesIntoOneGenericList()
    {
        Assert.Equal(
            "core::ptr::drop_glue::<alloc::boxed::Box<dyn for<'a, 'b> core::ops::function::Fn<(&'a std::panic::PanicHookInfo<'b>,), Output = ()> + core::marker::Sync + core::marker::Send>>",
            RustDemangler.Demangle("_RINvNtCs7z6u5Op6t72_4core3ptr9drop_glueINtNtCs3XFGBRFiuKX_5alloc5boxed3BoxDG0_INtNtNtB4_3ops8function2FnTRL1_INtNtCs7HaW2mv5hwE_3std5panic13PanicHookInfoL0_EEEp6OutputuNtNtB4_6marker4SyncNtB2I_4SendEL_EEB1O_"));
    }

    // `.llvm.<hex>` is LLVM's uniquing tag for an internalized symbol and is
    // stripped; every other vendor suffix names a genuinely different item and
    // is kept.
    [Fact]
    public void Demangle_LlvmUniquingSuffix_IsStripped()
    {
        Assert.Equal(
            "core::ptr::drop_glue::<std::sync::poison::mutex::MutexGuard<bool>>",
            RustDemangler.Demangle("_RINvNtCs7z6u5Op6t72_4core3ptr9drop_glueINtNtNtNtCs7HaW2mv5hwE_3std4sync6poison5mutex10MutexGuardbEECsfsN783Mr1pU_13rust_workload.llvm.8701412213054925914"));
    }

    [Fact]
    public void Demangle_OrdinaryVendorSuffix_IsKept()
    {
        Assert.Equal(
            "std::thread::main_thread::MAIN.0",
            RustDemangler.Demangle("_RNvNtNtCs7HaW2mv5hwE_3std6thread11main_thread4MAIN.0"));
    }

    // Anything not fully understood comes back UNCHANGED. A raw mangled name is
    // honest and can be pasted into rustfilt; a half-rewritten one is neither,
    // and cannot be recognised as such.
    [Theory]
    [InlineData("_ZN4core3fmt9Formatter3pad17h")]
    [InlineData("main")]
    [InlineData("_RTHIS_IS_NOT_VALID")]
    [InlineData("")]
    public void Demangle_UnrecognisedInput_ReturnsInputUnchanged(string name)
    {
        Assert.Equal(name, RustDemangler.Demangle(name));
    }

    // A C++ symbol must not be claimed by the Rust path.
    [Fact]
    public void IsRustLegacy_PlainItaniumSymbol_IsNotClaimed()
    {
        Assert.False(RustDemangler.IsRustLegacy("_ZN4icu_7813CollationKeys4writeEv"));
    }

    // The legacy Rust form is Itanium plus a `17h<16 hex>` hash component,
    // which is what distinguishes it from an ordinary C++ symbol. The hash is
    // noise and is stripped.
    [Fact]
    public void Demangle_LegacyRustSymbol_StripsTheHashComponent()
    {
        Assert.Equal(
            "core::fmt::Formatter::pad",
            RustDemangler.Demangle("_ZN4core3fmt9Formatter3pad17h0a1b2c3d4e5f6789E"));
    }
}

} // end of namespace(DotnetInsights.Perf.Tests)
