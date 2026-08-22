////////////////////////////////////////////////////////////////////////////////
// Module: CpuCategoryClassifier.cs
//
// Notes:
// Buckets CPU samples into a small set of coarse categories - "native GC",
// "TLS/crypto", "JIT", "locking" and so on - so the CPU view can answer
// "where did the time go" at a glance, before anyone reads a single method
// name. A ranked list of 3,200 functions says what is hot; it does not say
// that garbage collection is 10% of the process.
//
// TWO NUMBERS PER CATEGORY, because the obvious single number answers the
// wrong question about half the time:
//
//   - SELF: the sample's innermost frame is in this category. These sum to
//     exactly 100% of samples, so they are a real breakdown of the CPU.
//   - ON-STACK: the category appears ANYWHERE in the sample's stack. These
//     deliberately do NOT sum to 100% - one sample counts toward every
//     category it passes through - and answer "how much time is spent under
//     TLS at all", which is usually what somebody means by that question.
//
// A stack that goes app -> SslStream -> libcrypto -> kernel is SELF=Kernel and
// ON-STACK={Kernel, TLS/crypto, Application}. Reporting only self would file
// almost all real crypto work under "kernel"; reporting only on-stack would
// let the percentages add up to 300% with no explanation. Both together are
// unambiguous.
//
// MATCHING IS ON RESOLVED FRAME NAMES, which means this depends on native
// symbol resolution having run (see Symbols/). Without symbols most native
// frames read as "libcoreclr.so+0x5FC627" and land in Unresolved, which is
// itself worth seeing rather than hiding - a 40%-unresolved profile should
// look 40% unresolved.
//
// The rules below are ordered and the FIRST match wins, so more specific
// categories are listed before the modules that contain them: libcrypto is
// TLS/crypto rather than "native library", and a gc_heap frame is GC rather
// than "runtime". Rules are matched against the frame name with ordinal
// comparisons only - no regular expressions, no allocation - because this
// runs once per frame of every sampled stack.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.NetTrace.Cpu {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

// Order is the display order in the UI, not the matching order (see
// CpuCategoryClassifier.Classify for that). Uncategorized is last on purpose:
// it is the residue, and a large residue is a signal that these rules need
// extending rather than something to bury.
public enum CpuCategory
{
    GarbageCollection = 0,
    Allocation = 1,
    Jit = 2,
    TlsCrypto = 3,
    Compression = 4,
    Networking = 5,
    Serialization = 6,
    LockingAndSynchronization = 7,
    ThreadPoolAndScheduling = 8,
    Kernel = 9,
    RuntimeOther = 10,
    ManagedFramework = 11,
    ApplicationCode = 12,
    Globalization = 13,
    RuntimeGeneratedCode = 14,
    Unresolved = 15,
    Uncategorized = 16
}

public static class CpuCategoryClassifier
{
    public const int CategoryCount = 17;

    public static string DisplayName(CpuCategory category)
    {
        switch (category)
        {
            case CpuCategory.GarbageCollection: return "Garbage collection";
            case CpuCategory.Allocation: return "Allocation";
            case CpuCategory.Jit: return "JIT";
            case CpuCategory.TlsCrypto: return "TLS / crypto";
            case CpuCategory.Compression: return "Compression";
            case CpuCategory.Networking: return "Networking";
            case CpuCategory.Serialization: return "Serialization";
            case CpuCategory.LockingAndSynchronization: return "Locking / synchronization";
            case CpuCategory.ThreadPoolAndScheduling: return "Thread pool / scheduling";
            case CpuCategory.Kernel: return "Kernel";
            case CpuCategory.RuntimeOther: return "Runtime (other)";
            case CpuCategory.ManagedFramework: return "Managed framework";
            case CpuCategory.ApplicationCode: return "Application code";
            case CpuCategory.Globalization: return "Globalization / text";
            case CpuCategory.RuntimeGeneratedCode: return "Runtime-generated code";
            case CpuCategory.Unresolved: return "Unresolved";
            default: return "Uncategorized";
        }
    }

    public static string Description(CpuCategory category)
    {
        switch (category)
        {
            case CpuCategory.GarbageCollection: return "The collector itself - mark, plan, relocate, compact, sweep, and the GC's own thread joins.";
            case CpuCategory.Allocation: return "Handing out new objects: the runtime's allocation helpers and slow paths.";
            case CpuCategory.Jit: return "Compiling IL to native code, including tiering and the prestub.";
            case CpuCategory.TlsCrypto: return "TLS handshakes and record processing, plus hashing and cipher work.";
            case CpuCategory.Compression: return "Deflate/inflate and the native compression library.";
            case CpuCategory.Networking: return "Sockets, the async socket engine, and HTTP server request handling.";
            case CpuCategory.Serialization: return "Turning objects into bytes and back - JSON, protobuf, XML.";
            case CpuCategory.LockingAndSynchronization: return "Monitors, mutexes, waits and the contention paths around them.";
            case CpuCategory.ThreadPoolAndScheduling: return "Dispatching work items, parking and waking workers, and OS context switches.";
            case CpuCategory.Kernel: return "Kernel code with no more specific category - syscall entry, interrupts, memory management.";
            case CpuCategory.RuntimeOther: return "Runtime code that is none of the above - type loading, casting helpers, write barriers, PAL.";
            case CpuCategory.ManagedFramework: return "Managed code from System.* / Microsoft.* assemblies.";
            case CpuCategory.ApplicationCode: return "Managed code from everything else - your own assemblies and third-party packages.";
            case CpuCategory.Globalization: return "Culture-aware string comparison, collation and normalization, mostly inside ICU.";
            case CpuCategory.RuntimeGeneratedCode: return "Stubs the runtime generates at run time - precode, call-counting stubs, jump stubs, delegate thunks. Unnamed permanently: this code exists only in memory and no symbol file describes it.";
            case CpuCategory.Unresolved: return "Frames with no symbol. Fetching symbols (see the settings) usually moves most of this into the categories above.";
            default: return "Frames that matched no rule. A large share here means the category rules need extending.";
        }
    }

    // Ordered rules; first match wins. `frameName` is the RESOLVED name, so
    // this sees "SVR::gc_heap::plan_phase" rather than an address.
    //
    // isKernelFrame comes from the MODULE the address resolved out of, which
    // the caller knows and this function cannot: kernel symbols are plain C
    // identifiers with nothing in the text that marks them. It is load-bearing
    // rather than a refinement - GCC decorates kernel symbols with suffixes
    // like `.isra.0` and `.part.0`, so `finish_task_switch.isra.0` contains
    // dots, no "::", and therefore looked exactly like a managed
    // Namespace.Type.Method name. That misfiled the single hottest frame in
    // the reference capture (16,615 samples, 1.5% of the process) as
    // application code.
    public static CpuCategory Classify(string frameName, bool isKernelFrame = false)
    {
        if (string.IsNullOrEmpty(frameName))
        {
            return CpuCategory.Uncategorized;
        }

        // Checked before the unresolved test below, which its own "module+0x"
        // shape would otherwise match. These frames have no name and never
        // will - see UniversalSymbolTable.JitStubPrefix - so counting them as
        // "missing symbols" would overstate what fetching symbols can fix.
        if (frameName.StartsWith(DotnetInsights.NetTrace.Universal.UniversalSymbolTable.JitStubPrefix, StringComparison.Ordinal))
        {
            return CpuCategory.RuntimeGeneratedCode;
        }

        // The vDSO is kernel code mapped into every process. There are no
        // symbols to fetch for it either, and filing it under "unresolved"
        // implies otherwise.
        if (StartsWith(frameName, "[vdso]"))
        {
            return CpuCategory.Kernel;
        }

        // An unresolved frame is "module+0xADDR" or "<unresolved 0x...>". It
        // must be tested FIRST among the remaining rules: the module name
        // inside it would otherwise match a library rule below and report
        // symbol-less time as if it had been attributed.
        if (frameName[0] == '<' || IsModuleOffsetName(frameName))
        {
            return CpuCategory.Unresolved;
        }

        // --- The GC, before anything else that mentions the heap ---
        if (Contains(frameName, "gc_heap::") ||
            Contains(frameName, "GCHeap::") ||
            Contains(frameName, "SVR::t_join") ||
            Contains(frameName, "WKS::t_join") ||
            Contains(frameName, "gc_t_join") ||
            Contains(frameName, "GCToEEInterface") ||
            Contains(frameName, "System.GC."))
        {
            return CpuCategory.GarbageCollection;
        }

        // Allocation is deliberately NOT folded into the GC. The allocation
        // helper is the single hottest runtime function on an allocation-heavy
        // service (14,597 samples on the reference capture), and merging it
        // would make "GC" look like collection cost when most of it is the
        // mutator handing out objects.
        if (Contains(frameName, "RhpNew") ||
            Contains(frameName, "RhNew") ||
            Contains(frameName, "AllocateString") ||
            Contains(frameName, "RhpGcAlloc") ||
            Contains(frameName, "GcAllocate") ||
            frameName == "Alloc" ||
            Contains(frameName, "JIT_New") ||
            Contains(frameName, "JIT_TrialAlloc") ||
            Contains(frameName, "AllocateObject") ||
            Contains(frameName, "Alloc(") ||
            Contains(frameName, "::Alloc"))
        {
            return CpuCategory.Allocation;
        }

        if (Contains(frameName, "libclrjit") ||
            Contains(frameName, "Compiler::") ||
            Contains(frameName, "emitter::") ||
            Contains(frameName, "PreStubWorker") ||
            Contains(frameName, "DoPrestub") ||
            Contains(frameName, "MethodDesc::JitCompile") ||
            Contains(frameName, "UnsafeJitFunction"))
        {
            return CpuCategory.Jit;
        }

        if (Contains(frameName, "libssl") ||
            Contains(frameName, "libcrypto") ||
            Contains(frameName, "System.Security.Cryptography") ||
            Contains(frameName, "System.Net.Security") ||
            Contains(frameName, "SslStream") ||
            Contains(frameName, "Interop+OpenSsl") ||
            Contains(frameName, "Interop+Crypto") ||
            // OpenSSL's own C symbols. It exports a small number of stable
            // prefixes, and its error-stack helper alone was 2,965 samples of
            // the reference capture's residue.
            StartsWith(frameName, "SSL_") ||
            StartsWith(frameName, "EVP_") ||
            StartsWith(frameName, "ERR_") ||
            StartsWith(frameName, "CRYPTO_") ||
            StartsWith(frameName, "OPENSSL_") ||
            StartsWith(frameName, "ossl_") ||
            StartsWith(frameName, "RSA_") ||
            StartsWith(frameName, "EC_") ||
            StartsWith(frameName, "BN_") ||
            StartsWith(frameName, "SHA") ||
            StartsWith(frameName, "AES_") ||
            StartsWith(frameName, "asn1_") ||
            StartsWith(frameName, "ASN1_") ||
            StartsWith(frameName, "X509_"))
        {
            return CpuCategory.TlsCrypto;
        }

        if (Contains(frameName, "Compression") ||
            Contains(frameName, "deflate") ||
            Contains(frameName, "inflate") ||
            Contains(frameName, "build_tree") ||
            Contains(frameName, "libz.so") ||
            Contains(frameName, "libzstd") ||
            Contains(frameName, "Brotli") ||
            // zlib / zlib-ng internals, which is where the actual compression
            // time is - the reference capture spent 4,376 samples in
            // longest_match_avx2 alone, none of it previously categorized.
            Contains(frameName, "longest_match") ||
            Contains(frameName, "insert_string") ||
            Contains(frameName, "compress_block") ||
            Contains(frameName, "fill_window") ||
            Contains(frameName, "slide_hash") ||
            Contains(frameName, "_tr_flush") ||
            Contains(frameName, "crc32") ||
            Contains(frameName, "adler32") ||
            Contains(frameName, "updatewindow") ||
            Contains(frameName, "send_tree") ||
            Contains(frameName, "deflate_") ||
            Contains(frameName, "inflate_"))
        {
            return CpuCategory.Compression;
        }

        if (Contains(frameName, "System.Net.Sockets") ||
            Contains(frameName, "SocketAsyncEngine") ||
            Contains(frameName, "System.Net.Http") ||
            Contains(frameName, "Kestrel") ||
            Contains(frameName, "SystemNative_") ||
            Contains(frameName, "Microsoft.AspNetCore"))
        {
            return CpuCategory.Networking;
        }

        if (Contains(frameName, "System.Text.Json") ||
            Contains(frameName, "Google.Protobuf") ||
            Contains(frameName, "System.Xml") ||
            Contains(frameName, "Serializ") ||
            Contains(frameName, "Deserializ") ||
            Contains(frameName, "MessagePack") ||
            Contains(frameName, "Newtonsoft"))
        {
            return CpuCategory.Serialization;
        }

        if (Contains(frameName, "Monitor_") ||
            Contains(frameName, "System.Threading.Monitor") ||
            Contains(frameName, "AwareLock") ||
            Contains(frameName, "pthread_mutex") ||
            Contains(frameName, "pthread_cond") ||
            Contains(frameName, "futex") ||
            Contains(frameName, "System.Threading.Lock") ||
            Contains(frameName, "SpinWait") ||
            Contains(frameName, "CrstBase") ||
            Contains(frameName, "raw_spin") ||
            Contains(frameName, "Interlocked"))
        {
            return CpuCategory.LockingAndSynchronization;
        }

        if (Contains(frameName, "ThreadPool") ||
            Contains(frameName, "LowLevelLifoSemaphore") ||
            Contains(frameName, "TimerQueue") ||
            Contains(frameName, "finish_task_switch") ||
            Contains(frameName, "__schedule") ||
            Contains(frameName, "schedule_") ||
            Contains(frameName, "try_to_wake_up") ||
            Contains(frameName, "System.Threading.Tasks"))
        {
            return CpuCategory.ThreadPoolAndScheduling;
        }

        // ICU, reached through culture-aware String.Compare/ToUpper and the
        // like. Worth its own bucket rather than "runtime": culture-sensitive
        // string work is a well-known and fixable cost, and on the reference
        // capture ICU collation alone was 1,600 samples.
        if (StartsWith(frameName, "icu_") ||
            StartsWith(frameName, "ucol_") ||
            StartsWith(frameName, "unorm") ||
            StartsWith(frameName, "uloc_") ||
            StartsWith(frameName, "u_str") ||
            Contains(frameName, "System.Globalization") ||
            Contains(frameName, "GlobalizationNative_"))
        {
            return CpuCategory.Globalization;
        }

        // Time sources the kernel exposes through the vDSO, which is mapped
        // into every process and so is not part of the kernel IMAGE the module
        // check below recognizes.
        if (Contains(frameName, "clock_gettime") ||
            Contains(frameName, "gettimeofday"))
        {
            return CpuCategory.Kernel;
        }

        // Anything still unmatched that came out of the kernel image is kernel
        // time. Placed AFTER the specific rules on purpose: a kernel spinlock
        // is more usefully counted as locking, and the scheduler as
        // scheduling, than both being folded into one opaque "kernel" number.
        if (isKernelFrame)
        {
            return CpuCategory.Kernel;
        }

        // Managed frames carry a dotted namespace and, when JIT'd, a tier
        // suffix. Checked before the native fallbacks so a managed type whose
        // name happens to contain a native-looking token is not misfiled.
        if (LooksManaged(frameName))
        {
            if (StartsWith(frameName, "System.") ||
                StartsWith(frameName, "Microsoft.") ||
                StartsWith(frameName, "Internal.") ||
                StartsWith(frameName, "Interop"))
            {
                return CpuCategory.ManagedFramework;
            }

            return CpuCategory.ApplicationCode;
        }

        // Thread-local storage access, and the C library's string/memory
        // primitives. These are everywhere in a real profile and are runtime
        // plumbing rather than anything the reader can act on directly.
        if (Contains(frameName, "__tls_get_addr") ||
            Contains(frameName, "memmove") ||
            Contains(frameName, "memcpy") ||
            Contains(frameName, "memset") ||
            Contains(frameName, "strlen") ||
            Contains(frameName, "strcmp") ||
            Contains(frameName, "malloc") ||
            Contains(frameName, "__libc_free") ||
            Contains(frameName, "tcache") ||
            StartsWith(frameName, "operator new") ||
            Contains(frameName, "_int_malloc") ||
            Contains(frameName, "_int_free") ||
            Contains(frameName, "cfree") ||
            Contains(frameName, "write_event_") ||
            Contains(frameName, "COMInterlocked") ||
            // JIT_-prefixed symbols are runtime HELPERS the jitted code calls,
            // not the compiler - the genuine compiler frames were matched by
            // the JIT rule far above, so anything reaching here is a helper.
            StartsWith(frameName, "JIT_") ||
            Contains(frameName, "pthread_getspecific") ||
            Contains(frameName, "__errno_location") ||
            Contains(frameName, "sched_getcpu") ||
            Contains(frameName, "PublishObject") ||
            Contains(frameName, "PInvoke"))
        {
            return CpuCategory.RuntimeOther;
        }

        if (Contains(frameName, "libcoreclr") ||
            Contains(frameName, "CorUnix::") ||
            Contains(frameName, "MethodTable") ||
            Contains(frameName, "MethodDesc") ||
            Contains(frameName, "CastHelpers") ||
            Contains(frameName, "WriteBarrier") ||
            Contains(frameName, "InlinedMemmove") ||
            Contains(frameName, "EventPipe") ||
            Contains(frameName, "ep_") ||
            Contains(frameName, "ObjectNative"))
        {
            return CpuCategory.RuntimeOther;
        }

        // Kernel symbols are plain lowercase C identifiers and there is no
        // reliable token that marks them, so this is decided by the CALLER,
        // which knows the module a frame resolved from. Classify sees only a
        // name, so anything left here is genuinely unknown.
        return CpuCategory.Uncategorized;
    }

    // "libcoreclr.so+0x5FC627" / "vmlinux+0x1234". A '+' followed by "0x" is
    // the shape UniversalSymbolTable emits and nothing else uses.
    private static bool IsModuleOffsetName(string frameName)
    {
        int plusIndex = frameName.LastIndexOf('+');

        if (plusIndex < 0 || plusIndex + 2 >= frameName.Length)
        {
            return false;
        }

        return frameName[plusIndex + 1] == '0' && frameName[plusIndex + 2] == 'x';
    }

    // A managed method name is Namespace.Type.Method, optionally with a
    // " [Tier]" suffix. Native C symbols have no dot; C++ symbols use "::",
    // which the demangler leaves in place.
    private static bool LooksManaged(string frameName)
    {
        if (Contains(frameName, "::"))
        {
            return false;
        }

        return frameName.IndexOf('.') > 0;
    }

    private static bool Contains(string value, string token)
    {
        return value.IndexOf(token, StringComparison.Ordinal) >= 0;
    }

    private static bool StartsWith(string value, string token)
    {
        return value.StartsWith(token, StringComparison.Ordinal);
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.Cpu)

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////
