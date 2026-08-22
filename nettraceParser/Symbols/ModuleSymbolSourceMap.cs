////////////////////////////////////////////////////////////////////////////////
// Module: ModuleSymbolSourceMap.cs
//
// Notes:
// Answers "which kind of symbol server is likely to have THIS module", so a
// lookup asks the server that can actually help first instead of walking a
// fixed list.
//
// Without it every module is offered to every server in a fixed order, which
// is not merely untidy - it is measurably wrong for the modules that matter
// most. Microsoft's symbol server has .NET's own native modules and nothing
// else; a distribution's debuginfod has glibc and OpenSSL and has never heard
// of libcoreclr. So a glibc lookup used to spend two guaranteed 404s before
// reaching the only server that could answer, and on a capture where that
// server was slow it paid the timeout last instead of first.
//
// THE CLASSIFICATION IS DELIBERATELY CONSERVATIVE. Anything this map does not
// recognise returns Unknown, and Unknown means "use the configured order
// exactly as before" - the routing can only ever reorder servers for modules
// it is confident about, never drop one or invent a new one. A capture is full
// of modules nothing well-known covers (a third-party profiler's .so, an
// application's own private native library) and those must keep working
// exactly as they did.
//
// PATH FIRST, THEN NAME. A module's directory is the strongest signal and the
// one that generalises: everything under the .NET install root is Microsoft's,
// everything under the system library directories is the distribution's. The
// name lists exist for the cases where the path cannot settle it - most
// importantly a SELF-CONTAINED .NET app, which ships libcoreclr.so next to
// the application binary in a directory with no recognisable shape at all.
//
// Both lists were built from the module set of a real `dotnet-trace
// collect-linux` capture (180 mappings) rather than from memory.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.NetTrace.Symbols {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public enum ModuleSymbolSource
{
    // No opinion. The caller uses its configured server order unchanged.
    Unknown = 0,

    // Shipped as part of .NET - Microsoft's symbol server has these.
    DotnetRuntime = 1,

    // Shipped by the operating system distribution - its debuginfod has these.
    DistributionLibrary = 2,

    // Nothing serves symbols for these, so asking is pure latency: code
    // generated at run time, kernel-provided mappings with no file behind
    // them, and managed assemblies (whose method names come from the trace's
    // own rundown events, never from a symbol server).
    NotFetchable = 3
}

public static class ModuleSymbolSourceMap
{
    // The .NET install root on every Linux layout seen so far - distro
    // packages, the install script, and the official container images all use
    // one of these.
    private static readonly string[] DotnetPathMarkers = new string[]
    {
        "/usr/share/dotnet/",
        "/usr/lib/dotnet/",
        "/usr/local/share/dotnet/",
        "/dotnet/shared/",
        "/dotnet/host/"
    };

    // Native modules that ARE .NET, wherever they happen to sit. This is what
    // covers a self-contained application, where these live beside the app
    // rather than under a recognisable install root.
    private static readonly string[] DotnetModuleNames = new string[]
    {
        "libcoreclr.so",
        "libclrjit.so",
        "libclrgc.so",
        "libhostfxr.so",
        "libhostpolicy.so",
        "libmscordaccore.so",
        "libmscordbi.so",
        "libnethost.so",
        "createdump"
    };

    // The interop shims are all named libSystem.<Area>.Native.so, so a prefix
    // match covers the whole family without listing each one.
    private const string DotnetNativeShimPrefix = "libSystem.";
    private const string DotnetNativeShimSuffix = ".Native.so";

    private static readonly string[] DistributionPathMarkers = new string[]
    {
        "/usr/lib/x86_64-linux-gnu/",
        "/usr/lib/aarch64-linux-gnu/",
        "/lib/x86_64-linux-gnu/",
        "/lib/aarch64-linux-gnu/",
        "/usr/lib64/",
        "/usr/lib/"
    };

    // Matched on the name's leading portion, because a distribution library
    // carries its soname version in the file name (libssl.so.3,
    // libstdc++.so.6.0.35, libicuuc.so.78.2) and the version moves with every
    // package update.
    private static readonly string[] DistributionModulePrefixes = new string[]
    {
        "libc.so",
        "libm.so",
        "libdl.so",
        "librt.so",
        "libpthread.so",
        "ld-linux",
        "ld.so",
        "libstdc++.so",
        "libgcc_s.so",
        "libcrypto.so",
        "libssl.so",
        "libz.so",
        "libzstd.so",
        "liblzma.so",
        "libbrotli",
        "libicu",
        "libkrb5",
        "libgssapi",
        "libcurl.so",
        "libnuma.so",
        "libatomic.so",
        "libunwind"
    };

    // Names with no file behind them at all, plus the runtime's own code heap.
    private static readonly string[] NotFetchableNames = new string[]
    {
        "[vdso]",
        "[vsyscall]",
        "[heap]",
        "[stack]",
        "memfd:doublemapper",
        "anon_inode"
    };

    public static ModuleSymbolSource Classify(string modulePath)
    {
        if (string.IsNullOrEmpty(modulePath))
        {
            return ModuleSymbolSource.Unknown;
        }

        string fileName = GetFileName(modulePath);

        for (int index = 0; index < NotFetchableNames.Length; ++index)
        {
            if (modulePath.IndexOf(NotFetchableNames[index], StringComparison.Ordinal) >= 0)
            {
                return ModuleSymbolSource.NotFetchable;
            }
        }

        // A managed assembly's methods are named from the trace's own method
        // rundown, never from a symbol server, so a lookup can only ever be a
        // wasted round trip.
        if (EndsWith(fileName, ".dll") || EndsWith(fileName, ".ni.dll"))
        {
            return ModuleSymbolSource.NotFetchable;
        }

        // The kernel image. Distributions ship its debuginfo through the same
        // debuginfod as everything else.
        if (fileName == "vmlinux" || StartsWith(fileName, "vmlinuz"))
        {
            return ModuleSymbolSource.DistributionLibrary;
        }

        // Name before path for .NET, so a self-contained app's own copy of
        // libcoreclr.so is recognised even though it sits in an application
        // directory that looks like nothing in particular.
        if (IsDotnetModuleName(fileName))
        {
            return ModuleSymbolSource.DotnetRuntime;
        }

        for (int index = 0; index < DotnetPathMarkers.Length; ++index)
        {
            if (modulePath.IndexOf(DotnetPathMarkers[index], StringComparison.Ordinal) >= 0)
            {
                return ModuleSymbolSource.DotnetRuntime;
            }
        }

        for (int index = 0; index < DistributionModulePrefixes.Length; ++index)
        {
            if (StartsWith(fileName, DistributionModulePrefixes[index]))
            {
                return ModuleSymbolSource.DistributionLibrary;
            }
        }

        // The path check for distributions comes LAST of the positive tests.
        // /usr/lib/ is where a distribution puts its own libraries, but it is
        // also where third-party packages install theirs, and those are served
        // by neither kind of server - so a name match is trusted and a bare
        // path match is only used once nothing more specific applied.
        for (int index = 0; index < DistributionPathMarkers.Length; ++index)
        {
            if (modulePath.IndexOf(DistributionPathMarkers[index], StringComparison.Ordinal) >= 0)
            {
                return ModuleSymbolSource.DistributionLibrary;
            }
        }

        return ModuleSymbolSource.Unknown;
    }

    private static bool IsDotnetModuleName(string fileName)
    {
        for (int index = 0; index < DotnetModuleNames.Length; ++index)
        {
            if (fileName == DotnetModuleNames[index])
            {
                return true;
            }
        }

        return StartsWith(fileName, DotnetNativeShimPrefix) && EndsWith(fileName, DotnetNativeShimSuffix);
    }

    private static string GetFileName(string path)
    {
        int lastSeparator = path.LastIndexOf('/');

        if (lastSeparator < 0 || lastSeparator == path.Length - 1)
        {
            return path;
        }

        return path.Substring(lastSeparator + 1);
    }

    private static bool StartsWith(string value, string token)
    {
        return value.StartsWith(token, StringComparison.Ordinal);
    }

    private static bool EndsWith(string value, string token)
    {
        return value.EndsWith(token, StringComparison.Ordinal);
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.Symbols)

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////
