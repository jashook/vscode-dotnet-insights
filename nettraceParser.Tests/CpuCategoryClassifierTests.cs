////////////////////////////////////////////////////////////////////////////////
// Module: CpuCategoryClassifierTests.cs
//
// Notes:
// Covers the coarse CPU bucketing in Cpu/CpuCategoryClassifier.cs. Every frame
// name below is a REAL resolved symbol from a `dotnet-trace collect-linux`
// capture of a production service - the rules exist to cope with the shapes
// the runtime, the kernel and the system libraries actually emit, and
// invented names would have missed all three of the bugs these tests pin.
////////////////////////////////////////////////////////////////////////////////

using DotnetInsights.NetTrace.Cpu;

using Xunit;

namespace DotnetInsights.NetTrace.Tests {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public class CpuCategoryClassifierTests
{
    [Theory]
    [InlineData("SVR::gc_heap::find_first_object", CpuCategory.GarbageCollection)]
    [InlineData("SVR::gc_heap::background_sweep", CpuCategory.GarbageCollection)]
    [InlineData("SVR::t_join::join", CpuCategory.GarbageCollection)]
    [InlineData("RhpNewFast", CpuCategory.Allocation)]
    [InlineData("RhpGcAlloc", CpuCategory.Allocation)]
    [InlineData("ObjectNative::Monitor_TryEnter_FastPath", CpuCategory.LockingAndSynchronization)]
    [InlineData("__pthread_mutex_lock", CpuCategory.LockingAndSynchronization)]
    [InlineData("ERR_clear_error", CpuCategory.TlsCrypto)]
    [InlineData("longest_match_avx2", CpuCategory.Compression)]
    [InlineData("deflate_medium", CpuCategory.Compression)]
    [InlineData("icu_78::CollationKeys::writeSortKeyUpToQuaternary", CpuCategory.Globalization)]
    [InlineData("__tls_get_addr", CpuCategory.RuntimeOther)]
    [InlineData("__libc_malloc", CpuCategory.RuntimeOther)]
    [InlineData("System.Threading.ThreadPoolWorkQueue.Dispatch [OptimizedTier1]", CpuCategory.ThreadPoolAndScheduling)]
    [InlineData("System.Uri.CheckCanonical [OptimizedTier1]", CpuCategory.ManagedFramework)]
    [InlineData("Contoso.Caching.ExpirableDictionary.TraverseAndPurge [OptimizedTier1]", CpuCategory.ApplicationCode)]
    public void Classify_BucketsRealFrameNames(string frameName, CpuCategory expected)
    {
        Assert.Equal(expected, CpuCategoryClassifier.Classify(frameName));
    }

    // REGRESSION. GCC decorates kernel symbols with suffixes like `.isra.0`,
    // so `finish_task_switch.isra.0` contains dots and no "::" - which is
    // exactly the shape of a managed Namespace.Type.Method name. That misfiled
    // the single hottest frame in the reference capture (16,615 samples, 1.5%
    // of the whole process) as application code. Only the MODULE an address
    // resolved from can settle it, which is why the flag is a parameter.
    [Fact]
    public void Classify_DoesNotMistakeADecoratedKernelSymbolForManagedCode()
    {
        // Without the module flag this reads as Namespace.Type.Method and is
        // filed as application code - the actual bug, shown here rather than
        // described.
        Assert.Equal(CpuCategory.ApplicationCode, CpuCategoryClassifier.Classify("__slab_free.isra.0", isKernelFrame: false));

        Assert.Equal(CpuCategory.Kernel, CpuCategoryClassifier.Classify("__slab_free.isra.0", isKernelFrame: true));
        Assert.Equal(CpuCategory.Kernel, CpuCategoryClassifier.Classify("kmem_cache_alloc.constprop.0", isKernelFrame: true));
    }

    // The frame that exposed the bug above happens to match the scheduling
    // rule, so it lands correctly either way - which is exactly why it could
    // not be the regression test.
    [Fact]
    public void Classify_StillPrefersSchedulingForTheSchedulerItself()
    {
        Assert.Equal(CpuCategory.ThreadPoolAndScheduling, CpuCategoryClassifier.Classify("finish_task_switch.isra.0", isKernelFrame: true));
    }

    [Fact]
    public void Classify_FilesUnmatchedKernelFramesAsKernel()
    {
        Assert.Equal(CpuCategory.Kernel, CpuCategoryClassifier.Classify("do_syscall_64", isKernelFrame: true));
        Assert.Equal(CpuCategory.Kernel, CpuCategoryClassifier.Classify("perf_swevent_event", isKernelFrame: true));
    }

    // A kernel frame still gets the more specific bucket when one applies -
    // a spinlock is more useful counted as locking than as an opaque "kernel"
    // number.
    [Fact]
    public void Classify_PrefersASpecificCategoryOverKernelForKernelFrames()
    {
        Assert.Equal(CpuCategory.LockingAndSynchronization, CpuCategoryClassifier.Classify("_raw_spin_unlock_irqrestore", isKernelFrame: true));
        Assert.Equal(CpuCategory.ThreadPoolAndScheduling, CpuCategoryClassifier.Classify("__schedule", isKernelFrame: true));
    }

    // An unresolved frame must be tested FIRST. "libcrypto.so.3+0x1234" would
    // otherwise match the TLS rule on its module name and report symbol-less
    // time as if it had been attributed - which is the one thing this
    // breakdown must never do, since the whole point of the Unresolved bucket
    // is to show how much of the profile is not yet explained.
    [Theory]
    [InlineData("libcrypto.so.3+0x1234")]
    [InlineData("libcoreclr.so+0x5FC627")]
    [InlineData("libz.so.1+0xABC")]
    [InlineData("<unresolved 0x7D6283E45627>")]
    public void Classify_ReportsUnresolvedFramesAsUnresolvedRatherThanGuessingFromTheModule(string frameName)
    {
        Assert.Equal(CpuCategory.Unresolved, CpuCategoryClassifier.Classify(frameName));
    }

    // Allocation is deliberately NOT folded into garbage collection. The
    // allocation helper is the hottest runtime function on an allocation-heavy
    // service, and merging it would make "GC" read as collection cost when
    // most of it is the mutator handing out objects.
    [Fact]
    public void Classify_KeepsAllocationSeparateFromCollection()
    {
        Assert.Equal(CpuCategory.Allocation, CpuCategoryClassifier.Classify("RhpNewFast"));
        Assert.Equal(CpuCategory.GarbageCollection, CpuCategoryClassifier.Classify("SVR::gc_heap::plan_phase"));
    }

    // JIT_-prefixed symbols are runtime HELPERS the jitted code calls, not the
    // compiler. Filing them under JIT would invent compilation time that never
    // happened.
    [Fact]
    public void Classify_DoesNotCountRuntimeHelpersAsJitCompilation()
    {
        Assert.Equal(CpuCategory.RuntimeOther, CpuCategoryClassifier.Classify("JIT_InitPInvokeFrame"));
        Assert.Equal(CpuCategory.RuntimeOther, CpuCategoryClassifier.Classify("JIT_ByRefWriteBarrier"));
        Assert.Equal(CpuCategory.Jit, CpuCategoryClassifier.Classify("Compiler::compCompile"));
    }

    // Runtime-generated stubs and the vDSO are unnamed for PERMANENT reasons -
    // there is no symbol file anywhere that describes either - so counting
    // them as "missing symbols" overstates what fetching symbols can fix. On
    // the reference capture separating them took the Unresolved bucket from
    // 12.05% to 7.51%, and what is left is three Ubuntu packages.
    [Fact]
    public void Classify_SeparatesPermanentlyUnnamedCodeFromFetchableSymbols()
    {
        Assert.Equal(CpuCategory.RuntimeGeneratedCode, CpuCategoryClassifier.Classify("[jit] memfd:doublemapper+0x40"));
        Assert.Equal(CpuCategory.Kernel, CpuCategoryClassifier.Classify("[vdso]+0x7C0"));
        Assert.Equal(CpuCategory.Unresolved, CpuCategoryClassifier.Classify("libcrypto.so.3+0x1234"));
    }

    [Fact]
    public void Classify_TreatsEmptyInputAsUncategorizedRatherThanThrowing()
    {
        Assert.Equal(CpuCategory.Uncategorized, CpuCategoryClassifier.Classify(null));
        Assert.Equal(CpuCategory.Uncategorized, CpuCategoryClassifier.Classify(""));
    }

    [Fact]
    public void EveryCategoryHasADisplayNameAndDescription()
    {
        for (int categoryIndex = 0; categoryIndex < CpuCategoryClassifier.CategoryCount; ++categoryIndex)
        {
            CpuCategory category = (CpuCategory)categoryIndex;

            Assert.False(string.IsNullOrWhiteSpace(CpuCategoryClassifier.DisplayName(category)));
            Assert.False(string.IsNullOrWhiteSpace(CpuCategoryClassifier.Description(category)));
        }
    }

    // The enum has to fit the bitmask CpuCategoryBuilder packs a stack's
    // category set into.
    [Fact]
    public void CategoryCountFitsInTheBuildersBitmask()
    {
        Assert.True(CpuCategoryClassifier.CategoryCount <= 32);
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.Tests)

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////
