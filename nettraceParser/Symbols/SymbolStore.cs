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

    public SymbolServer(string baseUrl, bool isDebuginfod)
    {
        this.BaseUrl = baseUrl.TrimEnd('/');
        this.IsDebuginfod = isDebuginfod;
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
    public static IReadOnlyList<SymbolServer> Default => new SymbolServer[]
    {
        new SymbolServer(MicrosoftSymbolServerUrl, false)
    };

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
    private const string MissMarkerFileName = ".miss";

    // A symbol file is large (138MB for libcoreclr.so.dbg) and comes over the
    // network, so a stall must not hang a capture open indefinitely. Generous
    // because the payload really is that big, bounded because the alternative
    // is a progress bar that never finishes.
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(10);

    private readonly string rootDirectory;
    private readonly IReadOnlyList<SymbolServer> servers;
    private readonly bool allowDownload;

    private readonly Dictionary<string, ElfSymbolFile> loadedByBuildId = new Dictionary<string, ElfSymbolFile>(StringComparer.OrdinalIgnoreCase);

    public int DownloadedCount { get; private set; }

    public int CacheHitCount { get; private set; }

    public int MissCount { get; private set; }

    public long DownloadedBytes { get; private set; }

    public SymbolStore(string rootDirectory, IReadOnlyList<SymbolServer> servers, bool allowDownload)
    {
        this.rootDirectory = rootDirectory;
        this.servers = servers ?? SymbolServer.Default;
        this.allowDownload = allowDownload;
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

        if (!this.TryDownload(buildId, moduleDisplayName, cachedPath, out downloadError))
        {
            ++this.MissCount;
            this.loadedByBuildId[buildId] = null;
            this.WriteMissMarker(buildId);
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

            // A file that is present but not readable as ELF would otherwise
            // be retried and re-parsed on every open forever.
            this.WriteMissMarker(buildId);
            return null;
        }

        status = $"{loaded.SymbolCount} symbols";
        return loaded;
    }

    private bool TryDownload(string buildId, string moduleDisplayName, string destinationPath, out string error)
    {
        error = null;

        List<string> attempts = new List<string>();

        for (int serverIndex = 0; serverIndex < this.servers.Count; ++serverIndex)
        {
            SymbolServer server = this.servers[serverIndex];
            string url = server.BuildUrl(buildId);

            string attemptError;

            if (this.TryDownloadFrom(url, destinationPath, out attemptError))
            {
                return true;
            }

            attempts.Add($"{server.BaseUrl}: {attemptError}");
        }

        error = attempts.Count == 0 ? "no symbol servers configured" : string.Join("; ", attempts);
        return false;
    }

    private bool TryDownloadFrom(string url, string destinationPath, out string error)
    {
        error = null;

        // Downloaded to a temp file beside the destination and moved into
        // place only once complete, so an interrupted download can never be
        // mistaken for a cached symbol file on the next open.
        string directory = Path.GetDirectoryName(destinationPath);
        string temporaryPath = destinationPath + ".partial-" + Guid.NewGuid().ToString("N");

        try
        {
            Directory.CreateDirectory(directory);

            using (HttpClient client = new HttpClient())
            {
                client.Timeout = RequestTimeout;

                using (HttpResponseMessage response = client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult())
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
            }

            File.Move(temporaryPath, destinationPath, true);
            return true;
        }
        catch (Exception downloadError) when (downloadError is HttpRequestException || downloadError is IOException || downloadError is TaskCanceledException || downloadError is UnauthorizedAccessException)
        {
            error = downloadError.Message;
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

    private void WriteMissMarker(string buildId)
    {
        try
        {
            string markerPath = this.MissMarkerPathForBuildId(buildId);
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
