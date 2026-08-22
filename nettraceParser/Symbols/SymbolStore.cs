////////////////////////////////////////////////////////////////////////////////
// Module: SymbolStore.cs
//
// Notes:
// A local, content-addressed cache of native symbol files, plus the symbol
// servers they are fetched from when the cache misses.
//
// WHY THIS EXISTS: a `dotnet-trace collect-linux` capture describes the
// machine's address space but does NOT contain symbols for most of it. On the
// reference capture only 42 of 561 modules shipped symbols; libcoreclr.so -
// which owns 5.4% of all CPU samples, more than any managed method - shipped
// none. What every module DOES carry is a ProcessMappingMetadata record with
// its ELF `build_id`, which is precisely the key a symbol server is looked up
// by. So the capture contains the recipe, and this fetches the ingredients.
//
// KEYED BY BUILD-ID, NOT BY FILENAME. A build id identifies one exact build of
// one exact binary, so a cached file can never be the wrong version of the
// right name - which is the failure mode that makes symbol caches produce
// confidently wrong answers. It also means the cache is shared correctly
// across captures, machines and runtime versions without any invalidation
// rule: a different build is simply a different key. (Contrast the tool
// version-marker cache in DependencySetup.ts, whose staleness trap CLAUDE.md
// documents at length - this cache cannot have that problem by construction.)
//
// LAYOUT: <root>/<build-id>/symbols  plus a sibling `.miss` marker.
// The miss marker is what stops a capture full of Ubuntu system libraries -
// which Microsoft's server has never heard of - from re-requesting the same
// 404s on every single open. It records that a lookup was made and came back
// empty, which is a different fact from "not looked up yet".
//
// SERVERS: Microsoft's public symbol server serves .NET's own native modules
// (libcoreclr.so, libclrjit.so, libSystem.*.Native.so) keyed by ELF build id;
// this was verified end to end against the reference capture - the returned
// libcoreclr.so.dbg is 138MB, reports the exact build id the capture recorded,
// and names 31 of the 32 libcoreclr frames in that capture's top 200.
// debuginfod servers (Ubuntu's, Debian's, Fedora's) use a different URL shape
// and cover the DISTRO's libraries - libc, openssl, zlib - which Microsoft's
// does not. Both shapes are supported; which servers are consulted is the
// caller's choice, because it is a network-access decision, not a technical
// one.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.NetTrace.Symbols {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

// What a server is GOOD FOR, which is a different question from the URL shape
// it speaks - Microsoft's server and a distribution's debuginfod cover
// completely disjoint sets of modules. Used by ModuleSymbolSourceMap to ask
// the one that can actually answer first.
public enum SymbolServerKind
{
    // Named by the user with --symbol-server. Always tried first: an explicit
    // instruction outranks anything inferred.
    Explicit = 0,
    Microsoft = 1,
    Distribution = 2,

    // A federation that forwards to several distributions' servers. Useful for
    // a distribution the capture did not identify, but never the best first
    // guess for one it did.
    Federated = 3
}

// One symbol server and the URL shape it speaks.
public sealed class SymbolServer
{
    // Microsoft's ELF convention: the debug file is keyed by
    // `_.debug/elf-buildid-sym-<buildid>/_.debug`.
    public const string MicrosoftSymbolServerUrl = "https://msdl.microsoft.com/download/symbols";

    // The debuginfod protocol (Ubuntu/Debian/Fedora): `buildid/<buildid>/debuginfo`.
    public const string UbuntuDebuginfodUrl = "https://debuginfod.ubuntu.com";

    public string BaseUrl { get; }

    public bool IsDebuginfod { get; }

    public SymbolServerKind Kind { get; }

    // Kind defaults to Explicit so a hand-constructed server is treated as a
    // deliberate choice rather than being silently deprioritised.
    public SymbolServer(string baseUrl, bool isDebuginfod, SymbolServerKind kind = SymbolServerKind.Explicit)
    {
        this.BaseUrl = baseUrl.TrimEnd('/');
        this.IsDebuginfod = isDebuginfod;
        this.Kind = kind;
    }

    public string BuildUrl(string buildId)
    {
        if (this.IsDebuginfod)
        {
            return $"{this.BaseUrl}/buildid/{buildId}/debuginfo";
        }

        return $"{this.BaseUrl}/_.debug/elf-buildid-sym-{buildId}/_.debug";
    }

    // The default set. Microsoft's server only - it is the one that covers
    // .NET's own native modules, which is where a .NET capture's unresolved
    // native time overwhelmingly is, and it is the only one this project has
    // verified end to end. A distro debuginfod can be added by the caller.
    // The elfutils federation, which forwards to a number of distribution
    // debuginfod servers. Kept as a fallback after Microsoft's because it
    // costs one 404 when it does not have a build and can name modules from
    // distributions the capture did not identify.
    public const string FederatedDebuginfodUrl = "https://debuginfod.elfutils.org";

    public static IReadOnlyList<SymbolServer> Default => new SymbolServer[]
    {
        new SymbolServer(MicrosoftSymbolServerUrl, false, SymbolServerKind.Microsoft),
        new SymbolServer(FederatedDebuginfodUrl, true, SymbolServerKind.Federated)
    };

    // debuginfod endpoints for the distributions a collect-linux capture can
    // name in its own VersionMetadata. Microsoft's symbol server carries only
    // .NET's own native modules, so without one of these a capture's libc,
    // openssl and libstdc++ frames can never be named - on the reference
    // capture that is 268 of the 312 frames still showing as an offset.
    //
    // Matched against the `os` field the capture itself reports, so nothing
    // here is guessed from the machine running the parser - which would be
    // wrong exactly when it matters, since a Linux capture is routinely read
    // on a Mac or a Windows box.
    public static SymbolServer ForDistribution(string distributionId)
    {
        switch (distributionId.ToLowerInvariant())
        {
            case "ubuntu":
            {
                return new SymbolServer(UbuntuDebuginfodUrl, true, SymbolServerKind.Distribution);
            }

            case "debian":
            {
                return new SymbolServer("https://debuginfod.debian.net", true, SymbolServerKind.Distribution);
            }

            case "fedora":
            case "rhel":
            case "centos":
            {
                return new SymbolServer("https://debuginfod.fedoraproject.org", true, SymbolServerKind.Distribution);
            }

            case "arch":
            {
                return new SymbolServer("https://debuginfod.archlinux.org", true, SymbolServerKind.Distribution);
            }

            case "alpine":
            {
                return new SymbolServer("https://debuginfod.alpinelinux.org", true, SymbolServerKind.Distribution);
            }

            case "opensuse":
            case "sles":
            {
                return new SymbolServer("https://debuginfod.opensuse.org", true, SymbolServerKind.Distribution);
            }

            default:
            {
                return null;
            }
        }
    }

    public static SymbolServer Parse(string value)
    {
        // A debuginfod server is named by prefixing it, since the two speak
        // different URL shapes and nothing in the URL itself says which.
        if (value.StartsWith("debuginfod:", StringComparison.OrdinalIgnoreCase))
        {
            return new SymbolServer(value.Substring("debuginfod:".Length), true);
        }

        return new SymbolServer(value, false);
    }
}

public sealed class SymbolStore
{
    private const string SymbolFileName = "symbols";
    // Named ".notfound", not ".miss", and the rename is deliberate rather than
    // cosmetic. The old marker was written on ANY failed download, which
    // conflated "no server has symbols for this build" with "the server did
    // not answer" - so a transient outage cached a PERMANENT negative and the
    // build was never retried once the server came back. Renaming the file
    // makes every marker written under the old rule invisible, so a cache
    // poisoned by an outage heals itself with no user action.
    private const string MissMarkerFileName = ".notfound";

    // Recorded when the lookup was INCONCLUSIVE - some server never answered,
    // so "these symbols do not exist" was never actually established. Honored
    // only for a while, then retried, which is what keeps a transient outage
    // from costing either a permanent wrong answer or a stall on every single
    // run.
    private const string UnavailableMarkerFileName = ".unavailable";

    // How long an inconclusive result is trusted. Long enough that a run of
    // captures during an outage does not pay the timeout repeatedly, short
    // enough that a recovered server is picked up the same day.
    private static readonly TimeSpan InconclusiveRetryDelay = TimeSpan.FromHours(6);

    // The whole request, INCLUDING streaming the body. Generous because
    // libcoreclr.so.dbg really is 138MB, bounded because the alternative is a
    // progress bar that never finishes.
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(10);

    // Establishing the CONNECTION, which is a completely different question
    // from transferring a large body and must not inherit that generous
    // budget. This was found the hard way: with only the request timeout,
    // pointing at a symbol server the network cannot reach made every module
    // wait the full 10 minutes before failing, so a capture with 8 unreachable
    // modules would have hung for over an hour on nothing but dead TCP
    // connects. A user behind a proxy or offline would have hit exactly that.
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);

    // How long to wait for RESPONSE HEADERS, enforced with an explicit
    // CancellationToken rather than by trusting the handler's own timeouts.
    // ConnectTimeout alone was not enough: on a network that blackholes the
    // route (rather than refusing it) the request still hung well past it, so
    // the only bound that actually holds is one this code applies itself. The
    // body transfer is deliberately NOT under this - headers arriving proves
    // the server is alive, and after that a 138MB download legitimately takes
    // minutes.
    private static readonly TimeSpan ResponseHeadersTimeout = TimeSpan.FromSeconds(20);

    // Total wall-clock this store will spend on the network for one capture.
    // Symbols are an enhancement; past this the capture opens with whatever
    // has been resolved so far rather than making the user wait longer.
    private static readonly TimeSpan TotalNetworkBudget = TimeSpan.FromMinutes(15);

    // One client for the whole store rather than one per download: a fresh
    // HttpClient per request leaks sockets in TIME_WAIT, and these downloads
    // are large enough that connection reuse across modules is worth having.
    private static readonly HttpClient SharedClient = CreateClient();

    private static HttpClient CreateClient()
    {
        SocketsHttpHandler handler = new SocketsHttpHandler();
        handler.ConnectTimeout = ConnectTimeout;

        HttpClient client = new HttpClient(handler);
        client.Timeout = RequestTimeout;
        return client;
    }

    private readonly string rootDirectory;
    private readonly IReadOnlyList<SymbolServer> servers;
    private readonly bool allowDownload;

    private readonly Dictionary<string, ElfSymbolFile> loadedByBuildId = new Dictionary<string, ElfSymbolFile>(StringComparer.OrdinalIgnoreCase);

    // A server that could not be reached at all is not asked again for the
    // rest of this capture. A 404 is per-build-id information and says nothing
    // about the next module; a failure to CONNECT is about the server, and
    // re-learning it once per module is how a 5-second timeout still turns
    // into a minute of stalling.
    private readonly HashSet<string> unreachableServers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    private readonly System.Diagnostics.Stopwatch networkStopwatch = System.Diagnostics.Stopwatch.StartNew();

    private readonly Dictionary<ModuleSymbolSource, IReadOnlyList<SymbolServer>> orderedServersBySource = new Dictionary<ModuleSymbolSource, IReadOnlyList<SymbolServer>>();

    public bool NetworkBudgetExhausted { get; private set; }

    public int UnreachableServerCount => this.unreachableServers.Count;

    public int DownloadedCount { get; private set; }

    public int CacheHitCount { get; private set; }

    public int MissCount { get; private set; }

    public long DownloadedBytes { get; private set; }

    // Directories searched BEFORE any network, in the standard build-id
    // layout every Linux debug-symbol tool already uses:
    //
    //     <dir>/<first 2 hex chars>/<remaining 38 chars>.debug
    //
    // This is what `apt install libc6-dbgsym` populates under
    // /usr/lib/debug/.build-id, and what extracting a .ddeb by hand produces.
    // It matters because a symbol SERVER is not the only way to have symbols,
    // and right now it is not even a reliable one - debuginfod.ubuntu.com was
    // observed accepting TLS and then never answering, hours apart, which
    // leaves a capture's glibc and OpenSSL frames unresolvable through any
    // amount of retrying. Pointing at a directory sidesteps the server
    // entirely.
    private readonly IReadOnlyList<string> localSearchPaths;

    public int LocalHitCount { get; private set; }

    public static IReadOnlyList<string> DefaultLocalSearchPaths => new string[]
    {
        "/usr/lib/debug/.build-id",
        "/usr/lib/debug/.dwz"
    };

    public SymbolStore(string rootDirectory, IReadOnlyList<SymbolServer> servers, bool allowDownload, IReadOnlyList<string> localSearchPaths = null)
    {
        this.rootDirectory = rootDirectory;
        this.servers = servers ?? SymbolServer.Default;
        this.allowDownload = allowDownload;
        this.localSearchPaths = localSearchPaths ?? DefaultLocalSearchPaths;
    }

    // The conventional build-id path for one directory, or null when the id is
    // not a usable build id.
    public static string BuildIdPathIn(string directory, string buildId)
    {
        if (string.IsNullOrEmpty(buildId) || buildId.Length < 3)
        {
            return null;
        }

        return Path.Combine(directory, buildId.Substring(0, 2), buildId.Substring(2) + ".debug");
    }

    private ElfSymbolFile TryLoadFromLocalPaths(string buildId, out string status)
    {
        status = null;

        for (int pathIndex = 0; pathIndex < this.localSearchPaths.Count; ++pathIndex)
        {
            string candidate = BuildIdPathIn(this.localSearchPaths[pathIndex], buildId);

            if (candidate == null || !File.Exists(candidate))
            {
                continue;
            }

            string loadError;
            ElfSymbolFile loaded = ElfSymbolFile.TryLoad(candidate, out loadError);

            if (loaded != null)
            {
                ++this.LocalHitCount;
                this.loadedByBuildId[buildId] = loaded;
                status = $"{loaded.SymbolCount} symbols (local)";
                return loaded;
            }
        }

        return null;
    }

    // Where symbols live when the caller names no directory. Under the user's
    // own cache directory rather than beside the capture: it is shared across
    // every capture they open, and a 138MB file should not be copied per
    // trace.
    public static string DefaultRootDirectory()
    {
        string cacheHome = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");

        if (string.IsNullOrEmpty(cacheHome))
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            cacheHome = Path.Combine(home, ".cache");
        }

        return Path.Combine(cacheHome, "dotnet-insights", "symbols");
    }

    public string PathForBuildId(string buildId)
    {
        return Path.Combine(this.rootDirectory, buildId, SymbolFileName);
    }

    private string MissMarkerPathForBuildId(string buildId)
    {
        return Path.Combine(this.rootDirectory, buildId, MissMarkerFileName);
    }

    // Returns null when this module's symbols are not available - cached miss,
    // no server has them, downloads are disabled, or the file that came back
    // was not a usable ELF. Never throws: symbols are an enhancement, and a
    // capture must still open without them.
    public ElfSymbolFile TryGetSymbols(string buildId, string moduleDisplayName, out string status)
    {
        status = null;

        if (string.IsNullOrEmpty(buildId))
        {
            status = "no build id";
            return null;
        }

        ElfSymbolFile alreadyLoaded;

        if (this.loadedByBuildId.TryGetValue(buildId, out alreadyLoaded))
        {
            status = alreadyLoaded == null ? "previously unavailable" : "already loaded";
            return alreadyLoaded;
        }

        // Local directories first, ahead of even the negative markers: a
        // marker records what a SERVER said, and symbols appearing on disk
        // afterwards should take effect immediately rather than waiting for a
        // retry window to expire.
        string localStatus;
        ElfSymbolFile localSymbols = this.TryLoadFromLocalPaths(buildId, out localStatus);

        if (localSymbols != null)
        {
            status = localStatus;
            return localSymbols;
        }

        // The miss marker is checked BEFORE the cached file, not after. A
        // marker means a previous run already determined this build id cannot
        // be used, and that verdict has to win even when a file is sitting
        // next to it - which is exactly the case when what came back was not
        // usable ELF (a 404 page saved to disk, a truncated download). With
        // the checks the other way round that file is re-read and re-parsed on
        // every single open, which is the cost the marker exists to avoid.
        if (File.Exists(this.MissMarkerPathForBuildId(buildId)))
        {
            ++this.MissCount;
            this.loadedByBuildId[buildId] = null;
            status = "no usable symbols for this build (cached)";
            return null;
        }

        if (this.HasFreshUnavailableMarker(buildId))
        {
            ++this.MissCount;
            this.loadedByBuildId[buildId] = null;
            status = "symbol server was unavailable recently (cached, will retry later)";
            return null;
        }

        string cachedPath = this.PathForBuildId(buildId);

        if (File.Exists(cachedPath))
        {
            ++this.CacheHitCount;
            ElfSymbolFile fromCache = this.LoadAndRemember(buildId, cachedPath, out status);
            return fromCache;
        }

        if (!this.allowDownload)
        {
            status = "not cached, and downloads are disabled";
            return null;
        }

        string downloadError;

        bool definitiveNegative;

        if (!this.TryDownload(buildId, moduleDisplayName, cachedPath, out downloadError, out definitiveNegative))
        {
            ++this.MissCount;
            this.loadedByBuildId[buildId] = null;

            // Only a real answer is cached. A server that hung, timed out or
            // refused the connection has told us nothing about whether these
            // symbols exist, and recording that as a permanent miss is how a
            // one-off outage silently costs every future run - which is not
            // hypothetical: debuginfod.ubuntu.com was observed accepting TLS
            // and then never sending a byte, on every path, for 180 seconds.
            // The in-memory map still suppresses retries for the rest of THIS
            // capture, so a dead server is asked once either way.
            if (definitiveNegative)
            {
                this.WriteMarker(buildId, MissMarkerFileName);
            }
            else
            {
                this.WriteMarker(buildId, UnavailableMarkerFileName);
            }

            status = downloadError;
            return null;
        }

        ++this.DownloadedCount;
        return this.LoadAndRemember(buildId, cachedPath, out status);
    }

    private ElfSymbolFile LoadAndRemember(string buildId, string path, out string status)
    {
        string loadError;
        ElfSymbolFile loaded = ElfSymbolFile.TryLoad(path, out loadError);

        this.loadedByBuildId[buildId] = loaded;

        if (loaded == null)
        {
            status = $"unusable symbol file: {loadError}";

            // Definitive: the file was fetched and IS unusable, which is a
            // real answer about this build rather than a failure to get one.
            // Without the marker it would be re-read and re-parsed on every
            // open forever.
            this.WriteMarker(buildId, MissMarkerFileName);
            return null;
        }

        status = $"{loaded.SymbolCount} symbols";
        return loaded;
    }

    // definitiveNegative is true only when at least one server gave a real
    // HTTP answer and no server had the build - i.e. the "these symbols do not
    // exist" conclusion is actually supported by evidence.
    private bool TryDownload(string buildId, string moduleDisplayName, string destinationPath, out string error, out bool definitiveNegative)
    {
        error = null;
        definitiveNegative = false;

        bool anyServerAnswered = false;
        bool anyServerUnreachable = false;

        List<string> attempts = new List<string>();

        // Ask the server that can actually answer for THIS module first. Falls
        // back to the configured order untouched for anything the map does not
        // recognise - see ModuleSymbolSourceMap.
        IReadOnlyList<SymbolServer> orderedServers = this.OrderServersFor(ModuleSymbolSourceMap.Classify(moduleDisplayName));

        for (int serverIndex = 0; serverIndex < orderedServers.Count; ++serverIndex)
        {
            SymbolServer server = orderedServers[serverIndex];

            if (this.unreachableServers.Contains(server.BaseUrl))
            {
                // Skipped by the circuit breaker, but it still never answered
                // for this build - so the verdict is no more conclusive than
                // if it had been asked again.
                anyServerUnreachable = true;
                continue;
            }

            if (this.networkStopwatch.Elapsed > TotalNetworkBudget)
            {
                this.NetworkBudgetExhausted = true;
                error = "symbol download time budget exhausted";
                return false;
            }

            string attemptError;
            bool serverUnreachable;

            if (this.TryDownloadFrom(server.BuildUrl(buildId), destinationPath, out attemptError, out serverUnreachable))
            {
                return true;
            }

            if (serverUnreachable)
            {
                this.unreachableServers.Add(server.BaseUrl);
                anyServerUnreachable = true;
            }
            else
            {
                anyServerAnswered = true;
            }

            attempts.Add($"{server.BaseUrl}: {attemptError}");
        }

        // "These symbols do not exist" requires that EVERY server we would
        // consult said so. One 404 from Microsoft's server proves nothing
        // about a glibc build - and on the reference capture that is exactly
        // the shape of the failure: msdl and the elfutils federation both
        // answered 404 while the one server that would actually have Ubuntu's
        // glibc, debuginfod.ubuntu.com, never responded at all. Treating that
        // as definitive cached a permanent "no symbols" for the three modules
        // most worth resolving.
        definitiveNegative = anyServerAnswered && !anyServerUnreachable;
        error = attempts.Count == 0 ? "no symbol servers answered" : string.Join("; ", attempts);
        return false;
    }

    // serverUnreachable distinguishes "this server does not have this build"
    // (a 404, which says nothing about the next module) from "this server
    // cannot be reached" (which says everything about the next module).
    // The order this store would consult its servers in for one module.
    // Public so the routing can be tested for what it IS rather than only
    // through which URLs happen to get requested.
    public IReadOnlyList<SymbolServer> ServerOrderForModule(string modulePath)
    {
        return this.OrderServersFor(ModuleSymbolSourceMap.Classify(modulePath));
    }

    // The configured servers, reordered so the kind most likely to hold this
    // module comes first. Nothing is ever added or removed - a server the map
    // deprioritises is still asked, just later - so routing can only change
    // how fast the right answer is found, never whether it is found at all.
    //
    // Cached per source because there are four of them and this is asked once
    // per module.
    private IReadOnlyList<SymbolServer> OrderServersFor(ModuleSymbolSource source)
    {
        if (source == ModuleSymbolSource.Unknown || source == ModuleSymbolSource.NotFetchable)
        {
            return this.servers;
        }

        IReadOnlyList<SymbolServer> cached;

        if (this.orderedServersBySource.TryGetValue(source, out cached))
        {
            return cached;
        }

        List<SymbolServer> ordered = new List<SymbolServer>(this.servers.Count);

        // Explicit servers first regardless of the module: the user asked for
        // them by name, which outranks anything this map infers.
        AppendServersOfKind(ordered, SymbolServerKind.Explicit);

        if (source == ModuleSymbolSource.DotnetRuntime)
        {
            AppendServersOfKind(ordered, SymbolServerKind.Microsoft);
            AppendServersOfKind(ordered, SymbolServerKind.Federated);
            AppendServersOfKind(ordered, SymbolServerKind.Distribution);
        }
        else
        {
            AppendServersOfKind(ordered, SymbolServerKind.Distribution);
            AppendServersOfKind(ordered, SymbolServerKind.Federated);
            AppendServersOfKind(ordered, SymbolServerKind.Microsoft);
        }

        this.orderedServersBySource[source] = ordered;
        return ordered;
    }

    private void AppendServersOfKind(List<SymbolServer> destination, SymbolServerKind kind)
    {
        for (int serverIndex = 0; serverIndex < this.servers.Count; ++serverIndex)
        {
            if (this.servers[serverIndex].Kind == kind)
            {
                destination.Add(this.servers[serverIndex]);
            }
        }
    }

    private bool TryDownloadFrom(string url, string destinationPath, out string error, out bool serverUnreachable)
    {
        error = null;
        serverUnreachable = false;

        // Downloaded to a temp file beside the destination and moved into
        // place only once complete, so an interrupted download can never be
        // mistaken for a cached symbol file on the next open.
        string directory = Path.GetDirectoryName(destinationPath);
        string temporaryPath = destinationPath + ".partial-" + Guid.NewGuid().ToString("N");

        try
        {
            Directory.CreateDirectory(directory);

            using (CancellationTokenSource headersCancellation = new CancellationTokenSource(ResponseHeadersTimeout))
            using (HttpResponseMessage response = SharedClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, headersCancellation.Token).GetAwaiter().GetResult())
            {
                if (!response.IsSuccessStatusCode)
                {
                    error = $"HTTP {(int)response.StatusCode}";
                    return false;
                }

                using (Stream responseStream = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult())
                using (FileStream fileStream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    responseStream.CopyTo(fileStream);
                    this.DownloadedBytes += fileStream.Length;
                }
            }

            File.Move(temporaryPath, destinationPath, true);
            return true;
        }
        catch (Exception downloadError) when (downloadError is HttpRequestException || downloadError is IOException || downloadError is TaskCanceledException || downloadError is OperationCanceledException || downloadError is UnauthorizedAccessException)
        {
            error = downloadError.Message;

            // A connect timeout surfaces as TaskCanceledException and a DNS or
            // refused connection as HttpRequestException with no status code.
            // Either way the server itself is the problem, not this build id.
            HttpRequestException requestError = downloadError as HttpRequestException;
            serverUnreachable = downloadError is OperationCanceledException || (requestError != null && requestError.StatusCode == null);

            return false;
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (IOException)
                {
                    // Best effort - a leftover .partial file is harmless, it is
                    // never mistaken for a symbol file.
                }
            }
        }
    }

    private bool HasFreshUnavailableMarker(string buildId)
    {
        string markerPath = Path.Combine(this.rootDirectory, buildId, UnavailableMarkerFileName);

        if (!File.Exists(markerPath))
        {
            return false;
        }

        try
        {
            return DateTime.UtcNow - File.GetLastWriteTimeUtc(markerPath) < InconclusiveRetryDelay;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private void WriteMarker(string buildId, string markerFileName)
    {
        try
        {
            string markerPath = Path.Combine(this.rootDirectory, buildId, markerFileName);
            Directory.CreateDirectory(Path.GetDirectoryName(markerPath));
            File.WriteAllText(markerPath, DateTime.UtcNow.ToString("o"));
        }
        catch (IOException)
        {
            // A cache that cannot be written still works, it just re-asks.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.Symbols)

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////
