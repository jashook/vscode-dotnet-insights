////////////////////////////////////////////////////////////////////////////////
// Module: ModuleSymbolSourceMapTests.cs
//
// Notes:
// Covers the well-known module -> symbol-server-kind map
// (Symbols/ModuleSymbolSourceMap.cs) and the routing it drives.
//
// EVERY PATH BELOW IS REAL, taken verbatim from the 180 module mappings of a
// `dotnet-trace collect-linux` capture of a production service. That includes
// the awkward ones - a third-party profiler's .so under /usr/bin, an
// application's own native library under /app/lib - which exist here
// specifically to pin the fallback: a module the map does not recognise must
// keep the configured server order exactly as it was.
////////////////////////////////////////////////////////////////////////////////

using System.Collections.Generic;

using DotnetInsights.NetTrace.Symbols;

using Xunit;

namespace DotnetInsights.NetTrace.Tests {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public class ModuleSymbolSourceMapTests
{
    [Theory]
    [InlineData("/usr/share/dotnet/shared/Microsoft.NETCore.App/10.0.10/libcoreclr.so")]
    [InlineData("/usr/share/dotnet/shared/Microsoft.NETCore.App/10.0.10/libclrjit.so")]
    [InlineData("/usr/share/dotnet/shared/Microsoft.NETCore.App/10.0.10/libhostpolicy.so")]
    [InlineData("/usr/share/dotnet/shared/Microsoft.NETCore.App/10.0.10/libSystem.Native.so")]
    [InlineData("/usr/share/dotnet/shared/Microsoft.NETCore.App/10.0.10/libSystem.IO.Compression.Native.so")]
    [InlineData("/usr/share/dotnet/shared/Microsoft.NETCore.App/10.0.10/libSystem.Security.Cryptography.Native.OpenSsl.so")]
    [InlineData("/usr/share/dotnet/host/fxr/10.0.10/libhostfxr.so")]
    [InlineData("/usr/share/dotnet/dotnet")]
    public void Classify_RoutesDotnetsOwnNativeModulesToMicrosoft(string modulePath)
    {
        Assert.Equal(ModuleSymbolSource.DotnetRuntime, ModuleSymbolSourceMap.Classify(modulePath));
    }

    // A self-contained application ships the runtime beside its own binary,
    // where the path says nothing at all - so the module NAME has to settle
    // it. This is the case the path markers alone would get wrong.
    [Theory]
    [InlineData("/app/libcoreclr.so")]
    [InlineData("/opt/myservice/libclrjit.so")]
    [InlineData("/srv/publish/libSystem.Native.so")]
    public void Classify_RecognisesTheRuntimeInASelfContainedApp(string modulePath)
    {
        Assert.Equal(ModuleSymbolSource.DotnetRuntime, ModuleSymbolSourceMap.Classify(modulePath));
    }

    [Theory]
    [InlineData("/usr/lib/x86_64-linux-gnu/libc.so.6")]
    [InlineData("/usr/lib/x86_64-linux-gnu/libcrypto.so.3")]
    [InlineData("/usr/lib/x86_64-linux-gnu/libssl.so.3")]
    [InlineData("/usr/lib/x86_64-linux-gnu/libstdc++.so.6.0.35")]
    [InlineData("/usr/lib/x86_64-linux-gnu/libicuuc.so.78.2")]
    [InlineData("/usr/lib/x86_64-linux-gnu/libz.so.1.3.1")]
    [InlineData("/usr/lib/x86_64-linux-gnu/libzstd.so.1.5.7")]
    [InlineData("/usr/lib/x86_64-linux-gnu/ld-linux-x86-64.so.2")]
    [InlineData("/usr/lib/x86_64-linux-gnu/libgcc_s.so.1")]
    [InlineData("vmlinux")]
    public void Classify_RoutesDistributionLibrariesToTheDistroServer(string modulePath)
    {
        Assert.Equal(ModuleSymbolSource.DistributionLibrary, ModuleSymbolSourceMap.Classify(modulePath));
    }

    // Version suffixes move with every package update, so matching has to be
    // on the leading portion of the name rather than the whole thing.
    [Theory]
    [InlineData("/usr/lib/x86_64-linux-gnu/libssl.so.3")]
    [InlineData("/usr/lib/x86_64-linux-gnu/libssl.so.1.1")]
    [InlineData("/lib/aarch64-linux-gnu/libc.so.6")]
    [InlineData("/usr/lib64/libcrypto.so.3.0.7")]
    public void Classify_IgnoresSonameVersionSuffixes(string modulePath)
    {
        Assert.Equal(ModuleSymbolSource.DistributionLibrary, ModuleSymbolSourceMap.Classify(modulePath));
    }

    [Theory]
    [InlineData("/memfd:doublemapper")]
    [InlineData("[vdso]")]
    [InlineData("[vsyscall]")]
    [InlineData("/usr/share/dotnet/shared/Microsoft.NETCore.App/10.0.10/System.Private.CoreLib.dll")]
    [InlineData("/usr/share/dotnet/shared/Microsoft.AspNetCore.App/10.0.10/Microsoft.AspNetCore.Mvc.Core.dll")]
    public void Classify_MarksCodeNoServerCanEverSupplyAsNotFetchable(string modulePath)
    {
        Assert.Equal(ModuleSymbolSource.NotFetchable, ModuleSymbolSourceMap.Classify(modulePath));
    }

    // THE FALLBACK, and the reason the map is allowed to be opinionated: a
    // module it does not recognise is left entirely alone, so the configured
    // server order still applies. Both of these are real modules from the
    // reference capture that neither well-known server covers.
    [Theory]
    [InlineData("/usr/bin/Pyroscope.Profiler.Native.so")]
    [InlineData("/usr/bin/Pyroscope.Linux.ApiWrapper.x64.so")]
    [InlineData("/app/lib/libmmap_thp.so")]
    [InlineData("/some/vendor/path/libwhatever.so")]
    public void Classify_LeavesUnrecognisedModulesUnknown(string modulePath)
    {
        Assert.Equal(ModuleSymbolSource.Unknown, ModuleSymbolSourceMap.Classify(modulePath));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Classify_TreatsMissingPathsAsUnknownRatherThanThrowing(string modulePath)
    {
        Assert.Equal(ModuleSymbolSource.Unknown, ModuleSymbolSourceMap.Classify(modulePath));
    }

    // A .NET module under an unrecognised path must still beat the generic
    // /usr/lib rule - the name is the stronger signal and is checked first.
    [Fact]
    public void Classify_PrefersTheDotnetNameOverAGenericSystemPath()
    {
        Assert.Equal(ModuleSymbolSource.DotnetRuntime, ModuleSymbolSourceMap.Classify("/usr/lib/libcoreclr.so"));
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public class SymbolServerRoutingTests
{
    private static readonly SymbolServer Microsoft = new SymbolServer("https://msdl.example", false, SymbolServerKind.Microsoft);
    private static readonly SymbolServer Federated = new SymbolServer("https://federated.example", true, SymbolServerKind.Federated);
    private static readonly SymbolServer Distribution = new SymbolServer("https://distro.example", true, SymbolServerKind.Distribution);
    private static readonly SymbolServer Explicit = new SymbolServer("https://mine.example", false, SymbolServerKind.Explicit);

    private static IReadOnlyList<SymbolServer> AllServers => new SymbolServer[] { Microsoft, Federated, Explicit, Distribution };

    private static List<string> OrderFor(string modulePath)
    {
        SymbolStore store = new SymbolStore("/tmp/does-not-matter", AllServers, false);
        List<string> order = new List<string>();

        foreach (SymbolServer server in store.ServerOrderForModule(modulePath))
        {
            order.Add(server.BaseUrl);
        }

        return order;
    }

    [Fact]
    public void DotnetModulesAskMicrosoftBeforeAnyDistributionServer()
    {
        List<string> order = OrderFor("/usr/share/dotnet/shared/Microsoft.NETCore.App/10.0.10/libcoreclr.so");

        Assert.Equal("https://mine.example", order[0]);
        Assert.Equal("https://msdl.example", order[1]);
        Assert.True(order.IndexOf("https://msdl.example") < order.IndexOf("https://distro.example"));
    }

    // The case that motivated the whole map: glibc used to spend two
    // guaranteed 404s before reaching the only server that could answer.
    [Fact]
    public void DistributionLibrariesAskTheDistroServerBeforeMicrosoft()
    {
        List<string> order = OrderFor("/usr/lib/x86_64-linux-gnu/libc.so.6");

        Assert.Equal("https://mine.example", order[0]);
        Assert.Equal("https://distro.example", order[1]);
        Assert.True(order.IndexOf("https://distro.example") < order.IndexOf("https://msdl.example"));
    }

    // Routing REORDERS, it never drops - a deprioritised server is still
    // asked, so a module the map guesses wrong about still resolves.
    [Fact]
    public void RoutingKeepsEveryConfiguredServer()
    {
        Assert.Equal(AllServers.Count, OrderFor("/usr/lib/x86_64-linux-gnu/libc.so.6").Count);
        Assert.Equal(AllServers.Count, OrderFor("/usr/share/dotnet/libcoreclr.so").Count);
    }

    // The fallback the user asked for explicitly: no opinion means the
    // configured order, unchanged.
    [Fact]
    public void UnknownModulesKeepTheConfiguredOrderExactly()
    {
        List<string> order = OrderFor("/usr/bin/Pyroscope.Profiler.Native.so");

        Assert.Equal(
            new List<string> { "https://msdl.example", "https://federated.example", "https://mine.example", "https://distro.example" },
            order);
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.Tests)

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////
