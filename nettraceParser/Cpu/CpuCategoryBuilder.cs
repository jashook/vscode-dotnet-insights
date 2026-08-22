////////////////////////////////////////////////////////////////////////////////
// Module: CpuCategoryBuilder.cs
//
// Notes:
// Computes the per-category CPU breakdown described in
// Cpu/CpuCategoryClassifier.cs - "garbage collection is 9.8% of this process,
// TLS/crypto is 3.1%" - and writes it into the CPU profile's JSON.
//
// Two numbers per category, and they answer different questions (see the
// classifier's header): SELF sums to 100% of samples and is a real breakdown
// of the CPU; ON-STACK counts a sample toward every category its stack passes
// through and so deliberately sums to more.
//
// COST is kept off the per-sample path by memoizing twice. A category depends
// only on a frame's resolved NAME, so it is computed once per distinct frame
// (~3,200 on the reference capture, against 1.09M samples). A stack's
// (self category, set of categories present) then depends only on the stack,
// and stacks are deduplicated by content at decode time, so that is computed
// once per distinct stack rather than once per sample. What remains per
// sample is two array increments and a 15-bit mask walk.
//
// The category set is a bitmask in an int rather than a HashSet per stack -
// there are 15 categories, they fit, and a per-stack allocation across
// hundreds of thousands of stacks is exactly the kind of cost this codebase
// has repeatedly measured and removed.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.NetTrace.Cpu {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;
using System.Text.Json;

using DotnetInsights.NetTrace.Rundown;
using DotnetInsights.NetTrace.Universal;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public static class CpuCategoryBuilder
{
    // How many methods each category lists as its top contributors. Enough to
    // show what a bucket is made of - which is the difference between a number
    // somebody believes and one they don't - without turning the summary into
    // another full method ranking.
    private const int TopMethodsPerCategory = 8;

    public struct CategoryTotals
    {
        public long SelfSamples;
        public long OnStackSamples;

        // frameId -> self samples, for this category only. Reduced to the top
        // few names at write time.
        public Dictionary<int, long> SelfSamplesByFrameId;
    }

    // categoryByFrameId is handed back so the caller can group STACKS by
    // category without redoing the per-frame classification - see
    // CpuProfileJsonExporter's category caller trees, which is what lets a
    // bucket be opened into real call paths rather than a flat method list.
    public static CategoryTotals[] Build(
        List<SampleEvent> sampleEvents,
        StackTable stackTable,
        MethodSymbolTable symbolTable,
        UniversalSymbolTable nativeSymbols,
        out Dictionary<int, CpuCategory> categoryByFrameId)
    {
        CategoryTotals[] totals = new CategoryTotals[CpuCategoryClassifier.CategoryCount];
        categoryByFrameId = new Dictionary<int, CpuCategory>();

        if (sampleEvents == null || stackTable == null || symbolTable == null)
        {
            return totals;
        }

        // -1 marks "not computed yet" for the per-stack memo. A stack index is
        // never negative, so this cannot collide with a real answer.
        sbyte[] selfCategoryByStack = new sbyte[stackTable.Count];
        int[] categoryMaskByStack = new int[stackTable.Count];

        for (int stackIndex = 0; stackIndex < selfCategoryByStack.Length; ++stackIndex)
        {
            selfCategoryByStack[stackIndex] = -1;
        }

        for (int sampleIndex = 0; sampleIndex < sampleEvents.Count; ++sampleIndex)
        {
            SampleEvent sample = sampleEvents[sampleIndex];

            int stackIndex = sample.StackIndex;

            if (stackIndex < 0 || stackIndex >= selfCategoryByStack.Length)
            {
                ++totals[(int)CpuCategory.Uncategorized].SelfSamples;
                continue;
            }

            if (selfCategoryByStack[stackIndex] < 0)
            {
                ClassifyStack(
                    stackTable.FramesAt(stackIndex),
                    sample.RelativeMSec,
                    symbolTable,
                    nativeSymbols,
                    categoryByFrameId,
                    out CpuCategory selfCategory,
                    out int categoryMask);

                selfCategoryByStack[stackIndex] = (sbyte)selfCategory;
                categoryMaskByStack[stackIndex] = categoryMask;
            }

            int selfCategoryIndex = selfCategoryByStack[stackIndex];
            ++totals[selfCategoryIndex].SelfSamples;

            // Attributed to the LEAF frame, matching what SelfSamples counts.
            long[] leafFrames = stackTable.FramesAt(stackIndex);

            if (leafFrames.Length > 0)
            {
                int leafFrameId = symbolTable.ResolveId(leafFrames[0], sample.RelativeMSec);

                if (totals[selfCategoryIndex].SelfSamplesByFrameId == null)
                {
                    totals[selfCategoryIndex].SelfSamplesByFrameId = new Dictionary<int, long>();
                }

                totals[selfCategoryIndex].SelfSamplesByFrameId.TryGetValue(leafFrameId, out long existing);
                totals[selfCategoryIndex].SelfSamplesByFrameId[leafFrameId] = existing + 1;
            }

            int mask = categoryMaskByStack[stackIndex];

            while (mask != 0)
            {
                int categoryIndex = System.Numerics.BitOperations.TrailingZeroCount(mask);
                ++totals[categoryIndex].OnStackSamples;
                mask &= mask - 1;
            }
        }

        return totals;
    }

    private static void ClassifyStack(
        long[] frames,
        double relativeMSec,
        MethodSymbolTable symbolTable,
        UniversalSymbolTable nativeSymbols,
        Dictionary<int, CpuCategory> categoryByFrameId,
        out CpuCategory selfCategory,
        out int categoryMask)
    {
        selfCategory = CpuCategory.Uncategorized;
        categoryMask = 0;

        if (frames.Length == 0)
        {
            categoryMask = 1 << (int)CpuCategory.Uncategorized;
            return;
        }

        for (int frameIndex = 0; frameIndex < frames.Length; ++frameIndex)
        {
            int frameId = symbolTable.ResolveId(frames[frameIndex], relativeMSec);

            CpuCategory category;

            if (!categoryByFrameId.TryGetValue(frameId, out category))
            {
                string frameName = symbolTable.NameForId(frameId);

                // Whether this frame is kernel code comes from the module it
                // resolved out of, not from its name - see Classify's own
                // comment for the frame that proved why.
                bool isKernelFrame = nativeSymbols != null && nativeSymbols.IsKernelSymbol(frameName);

                category = CpuCategoryClassifier.Classify(frameName, isKernelFrame);
                categoryByFrameId[frameId] = category;
            }

            // Index 0 is the innermost frame, so the first iteration is the
            // sample's own self category.
            if (frameIndex == 0)
            {
                selfCategory = category;
            }

            categoryMask |= 1 << (int)category;
        }
    }

    public static void Write(Utf8JsonWriter writer, CategoryTotals[] totals, long totalSampleCount, MethodSymbolTable symbolTable = null)
    {
        writer.WritePropertyName("categories");
        writer.WriteStartObject();

        writer.WriteNumber("totalSamples", totalSampleCount);

        writer.WritePropertyName("rows");
        writer.WriteStartArray();

        // Emitted in enum order, which is display order, and every category is
        // emitted even at zero. A category that is genuinely absent is
        // information ("no JIT time at all"), and a table whose rows appear and
        // disappear between captures is much harder to compare against another
        // run.
        for (int categoryIndex = 0; categoryIndex < totals.Length; ++categoryIndex)
        {
            CpuCategory category = (CpuCategory)categoryIndex;

            writer.WriteStartObject();

            // The enum ordinal, so the UI can pair a row with its drill-down
            // tree by identity. The rows are re-sorted for display, so a
            // positional index would silently pair a bucket with another
            // bucket's call paths.
            writer.WriteNumber("id", categoryIndex);
            writer.WriteString("name", CpuCategoryClassifier.DisplayName(category));
            writer.WriteString("description", CpuCategoryClassifier.Description(category));
            writer.WriteNumber("selfSamples", totals[categoryIndex].SelfSamples);
            writer.WriteNumber("onStackSamples", totals[categoryIndex].OnStackSamples);
            writer.WriteNumber("selfPercent", totalSampleCount > 0 ? (totals[categoryIndex].SelfSamples * 100.0) / totalSampleCount : 0.0);
            writer.WriteNumber("onStackPercent", totalSampleCount > 0 ? (totals[categoryIndex].OnStackSamples * 100.0) / totalSampleCount : 0.0);

            WriteTopMethods(writer, totals[categoryIndex].SelfSamplesByFrameId, symbolTable);

            writer.WriteEndObject();
        }

        writer.WriteEndArray();

        WriteUnresolvedModules(writer, totals[(int)CpuCategory.Unresolved].SelfSamplesByFrameId, symbolTable, totalSampleCount);

        writer.WriteEndObject();
    }

    // Unresolved samples grouped by the MODULE they landed in, most-costly
    // first. This is the actionable form of the Unresolved bucket: "12% of
    // this profile has no symbols" is a complaint, while "8.1% of it is
    // libcrypto.so.3" names the one package whose debug symbols would fix
    // most of it. Grouping is on the module prefix of the "module+0xADDR"
    // name, which is the only thing an unresolved frame knows about itself.
    private static void WriteUnresolvedModules(Utf8JsonWriter writer, Dictionary<int, long> selfSamplesByFrameId, MethodSymbolTable symbolTable, long totalSampleCount)
    {
        writer.WritePropertyName("unresolvedModules");
        writer.WriteStartArray();

        if (selfSamplesByFrameId != null && symbolTable != null)
        {
            Dictionary<string, long> samplesByModule = new Dictionary<string, long>(StringComparer.Ordinal);

            foreach (KeyValuePair<int, long> entry in selfSamplesByFrameId)
            {
                string frameName = symbolTable.NameForId(entry.Key);
                string moduleName = ModuleNameOf(frameName);

                samplesByModule.TryGetValue(moduleName, out long existing);
                samplesByModule[moduleName] = existing + entry.Value;
            }

            List<KeyValuePair<string, long>> ranked = new List<KeyValuePair<string, long>>(samplesByModule);
            ranked.Sort(CompareModulesBySamplesDescending);

            for (int rankIndex = 0; rankIndex < ranked.Count; ++rankIndex)
            {
                writer.WriteStartObject();
                writer.WriteString("module", ranked[rankIndex].Key);
                writer.WriteNumber("selfSamples", ranked[rankIndex].Value);
                writer.WriteNumber("selfPercent", totalSampleCount > 0 ? (ranked[rankIndex].Value * 100.0) / totalSampleCount : 0.0);
                writer.WriteEndObject();
            }
        }

        writer.WriteEndArray();
    }

    private static string ModuleNameOf(string frameName)
    {
        if (frameName == null)
        {
            return "<unknown>";
        }

        int plusIndex = frameName.LastIndexOf('+');

        if (plusIndex > 0)
        {
            return frameName.Substring(0, plusIndex);
        }

        return "<no module>";
    }

    private static int CompareModulesBySamplesDescending(KeyValuePair<string, long> left, KeyValuePair<string, long> right)
    {
        int bySamples = right.Value.CompareTo(left.Value);

        if (bySamples != 0)
        {
            return bySamples;
        }

        return string.CompareOrdinal(left.Key, right.Key);
    }

    private static void WriteTopMethods(Utf8JsonWriter writer, Dictionary<int, long> selfSamplesByFrameId, MethodSymbolTable symbolTable)
    {
        writer.WritePropertyName("topMethods");
        writer.WriteStartArray();

        if (selfSamplesByFrameId != null && symbolTable != null)
        {
            List<KeyValuePair<int, long>> ranked = new List<KeyValuePair<int, long>>(selfSamplesByFrameId);

            ranked.Sort(CompareBySelfSamplesDescending);

            int emitted = ranked.Count < TopMethodsPerCategory ? ranked.Count : TopMethodsPerCategory;

            for (int rankIndex = 0; rankIndex < emitted; ++rankIndex)
            {
                writer.WriteStartObject();
                writer.WriteString("name", symbolTable.NameForId(ranked[rankIndex].Key));
                writer.WriteNumber("selfSamples", ranked[rankIndex].Value);
                writer.WriteEndObject();
            }
        }

        writer.WriteEndArray();
    }

    private static int CompareBySelfSamplesDescending(KeyValuePair<int, long> left, KeyValuePair<int, long> right)
    {
        int bySamples = right.Value.CompareTo(left.Value);

        if (bySamples != 0)
        {
            return bySamples;
        }

        // Deterministic ties, same reason WriteHotMethods breaks its own.
        return left.Key.CompareTo(right.Key);
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.Cpu)

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////
