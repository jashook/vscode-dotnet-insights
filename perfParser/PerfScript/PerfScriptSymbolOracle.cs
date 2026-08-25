////////////////////////////////////////////////////////////////////////////////
// Module: PerfScriptSymbolOracle.cs
//
// Notes:
// Harvests `address -> symbol name` from `perf script` text so a perf.data can
// be analysed with the symbolization that was done AT THE SOURCE.
//
// This exists because the two artifacts a capture can produce fail in opposite
// directions:
//
//   - perf.data has everything structural - mappings, timestamps, thread ids,
//     tracepoint payloads, raw addresses - and can be re-analysed later. Its
//     symbolization depends on having the module binaries, which for a
//     containerised service on a production host is exactly what the analysing
//     machine does not have.
//   - perf script text has symbolization that cannot be beaten: perf resolved
//     it on the machine that was running the code, with the real binaries and
//     the real /proc/kallsyms. But it is lossy and irreversible - no mappings,
//     no tracepoint payloads, nothing to re-analyse.
//
// Shipping both costs almost nothing (the perf.data is 30-100x SMALLER than
// the text it is shipped beside) and gives the analysing machine both halves.
//
// The merge needs no correlation between the two files. It does NOT match
// samples, or assume the same ordering, or care whether the text was produced
// from the same invocation - it only harvests the address/name pairs perf
// printed, which are facts about the process's address space. That makes it
// robust to `perf script` being run with different -F fields, a subset of the
// events, or even a longer capture of the same process.
//
// Addresses that appear in the text but not the capture are simply never
// looked up; addresses in the capture but not the text fall back to local ELF
// resolution, so a partial oracle degrades instead of failing.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.Perf.PerfScript {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public sealed class PerfScriptSymbolOracle
{
    private readonly Dictionary<long, string> nameByAddress = new Dictionary<long, string>();

    public int Count => this.nameByAddress.Count;

    public bool IsEmpty => this.nameByAddress.Count == 0;

    public long LinesRead { get; private set; }

    public long UnresolvedFramesSkipped { get; private set; }

    // Where a companion text file would conventionally sit next to a capture.
    // Checked so the common case - the two files downloaded together - needs no
    // flag at all.
    public static string FindCompanionScriptFile(string capturePath)
    {
        string directory = Path.GetDirectoryName(capturePath);
        if (string.IsNullOrEmpty(directory))
        {
            directory = ".";
        }

        string fileName = Path.GetFileName(capturePath);

        // Strip the whole ".perf.data" suffix, not just the last extension -
        // GetFileNameWithoutExtension on "run.perf.data" leaves "run.perf",
        // which matches none of the names collect-perf.sh writes.
        string baseName = fileName.EndsWith(".perf.data", StringComparison.OrdinalIgnoreCase)
            ? fileName.Substring(0, fileName.Length - ".perf.data".Length)
            : Path.GetFileNameWithoutExtension(fileName);

        string[] candidates = new string[]
        {
            Path.Combine(directory, baseName + ".stacks"),
            Path.Combine(directory, baseName + ".perf.stacks"),
            Path.Combine(directory, baseName + ".script.txt"),
            Path.Combine(directory, fileName + ".stacks"),
            Path.Combine(directory, "perf.stacks")
        };

        for (int candidateIndex = 0; candidateIndex < candidates.Length; ++candidateIndex)
        {
            if (File.Exists(candidates[candidateIndex]))
            {
                return candidates[candidateIndex];
            }
        }

        return null;
    }

    public bool TryLoad(string scriptPath, out string error)
    {
        error = null;

        if (!File.Exists(scriptPath))
        {
            error = "no such file: " + scriptPath;
            return false;
        }

        using (StreamReader reader = new StreamReader(scriptPath))
        {
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                ++this.LinesRead;

                // Only indented frame lines carry address/name pairs.
                if (line.Length == 0 || (line[0] != ' ' && line[0] != '\t'))
                {
                    continue;
                }

                this.HarvestFrameLine(line);
            }
        }

        if (this.nameByAddress.Count == 0)
        {
            error = "no address/symbol pairs found - was this produced by `perf script`?";
            return false;
        }

        return true;
    }

    // "\t    aaaac9efb184 hot_leaf_alpha+0x88 (/work/rust_workload)"
    private void HarvestFrameLine(string line)
    {
        string trimmed = line.Trim();
        if (trimmed.Length == 0)
        {
            return;
        }

        int firstSpace = trimmed.IndexOf(' ');
        if (firstSpace <= 0)
        {
            return;
        }

        long address;
        if (!long.TryParse(trimmed.AsSpan(0, firstSpace), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out address))
        {
            return;
        }

        string remainder = trimmed.Substring(firstSpace + 1).Trim();

        // Drop the trailing "(module)".
        int moduleStart = remainder.LastIndexOf(" (", StringComparison.Ordinal);
        if (moduleStart >= 0 && remainder.EndsWith(")", StringComparison.Ordinal))
        {
            remainder = remainder.Substring(0, moduleStart);
        }

        if (remainder.Length == 0)
        {
            return;
        }

        // An unresolved frame in the text teaches nothing - and recording it
        // would SHADOW a local resolution that might succeed, turning a
        // partial oracle into a worse answer than no oracle.
        if (remainder.StartsWith("[unknown]", StringComparison.Ordinal))
        {
            ++this.UnresolvedFramesSkipped;
            return;
        }

        // The +0xNN offset is dropped: the whole point of a name is that every
        // address inside one function shares it. Keeping the offset would give
        // each sampled instruction its own row.
        int plus = remainder.LastIndexOf("+0x", StringComparison.Ordinal);
        if (plus > 0)
        {
            remainder = remainder.Substring(0, plus);
        }

        // First writer wins. perf prints the same address consistently, so a
        // conflict means the two files describe different processes - in which
        // case the earlier answer is no worse than the later one, and silently
        // preferring the last would make the result depend on file order.
        if (!this.nameByAddress.ContainsKey(address))
        {
            this.nameByAddress[address] = remainder;
        }
    }

    public bool TryResolve(long address, out string name)
    {
        return this.nameByAddress.TryGetValue(address, out name);
    }

    // Emits each harvested pair as a synthesized Universal.System ProcessSymbol
    // covering exactly that address, so resolution goes through the same path
    // every other symbol source in this project uses.
    //
    // A one-address range per entry is deliberate: the text says what the
    // symbol at THIS address is and nothing about the extent of the function,
    // so inventing a range would attribute neighbouring addresses to it.
    // Nothing is lost by that - MethodSymbolTable interns by NAME, so the many
    // addresses inside one function still collapse to a single row.
    public void AppendProcessSymbolEvents(List<DotnetInsights.NetTrace.EventRecord> events)
    {
        foreach (KeyValuePair<long, string> entry in this.nameByAddress)
        {
            Dictionary<string, object> fields = new Dictionary<string, object>(StringComparer.Ordinal);
            fields["StartAddress"] = entry.Key;
            fields["EndAddress"] = entry.Key + 1;
            fields["Name"] = DotnetInsights.NetTrace.Symbols.NativeSymbolDemangler.Demangle(entry.Value);

            events.Add(new DotnetInsights.NetTrace.EventRecord(
                DotnetInsights.NetTrace.V6.V6Format.UniversalSystemProviderName,
                "ProcessSymbol",
                0,
                1,
                0,
                0,
                DotnetInsights.NetTrace.StackTable.EmptyStackIndex,
                fields,
                null,
                0,
                0));
        }
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.Perf.PerfScript)

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////
