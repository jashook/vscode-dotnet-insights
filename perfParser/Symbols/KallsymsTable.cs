////////////////////////////////////////////////////////////////////////////////
// Module: KallsymsTable.cs
//
// Notes:
// The kernel's own symbol table, as /proc/kallsyms renders it. Needed because
// a perf.data contains NO kernel symbol names: perf resolves them at report
// time by reading /proc/kallsyms on the machine doing the reporting, which is
// the recording machine in perf's usual workflow and is emphatically not this
// tool's - a Linux capture read on a Mac has no kernel to ask.
//
// This matters far more for off-CPU analysis than for CPU profiling. A CPU
// profile's hot frames are overwhelmingly user-space; an off-CPU stack, by
// construction, was captured while the thread was inside the kernel going to
// sleep, so its innermost frames are ALWAYS kernel ones. Without this table
// every blocked-thread stack reads as a column of raw addresses, which is
// exactly the information a reader came for and cannot use.
//
// Two sources, in order:
//   - the TRACING_DATA section, which embeds the recording kernel's kallsyms
//     when the capture used ftrace-backed tracepoints;
//   - an explicit file, for the common case where it does not.
//
// Addresses are absolute kernel virtual addresses and are affected by KASLR,
// so a kallsyms file only means anything against a capture from the SAME boot.
// A mismatch is not detectable from the data - the addresses simply resolve to
// the wrong symbols - which is why the file is never guessed at from the local
// machine and must be named.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.Perf.Symbols {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public sealed class KallsymsTable
{
    private long[] addresses = Array.Empty<long>();
    private string[] names = Array.Empty<string>();

    public int Count => this.addresses.Length;

    public bool IsEmpty => this.addresses.Length == 0;

    public static KallsymsTable TryLoadFromFile(string filePath, out string error)
    {
        error = null;

        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
        {
            error = "no kallsyms file at " + (filePath ?? "(null)");
            return null;
        }

        try
        {
            return TryParse(File.ReadAllText(filePath), out error);
        }
        catch (IOException ioException)
        {
            error = ioException.Message;
            return null;
        }
    }

    // Lines are "<hex address> <type> <name>[\t[module]]". Only code symbols
    // are kept: 't'/'T' (text) and 'w'/'W' (weak text). A data symbol can share
    // an address range with code in this flat table and would otherwise win the
    // binary search for an instruction pointer.
    public static KallsymsTable TryParse(string text, out string error)
    {
        error = null;

        if (string.IsNullOrEmpty(text))
        {
            error = "kallsyms text is empty";
            return null;
        }

        List<long> parsedAddresses = new List<long>();
        List<string> parsedNames = new List<string>();

        int position = 0;
        while (position < text.Length)
        {
            int lineEnd = text.IndexOf('\n', position);
            if (lineEnd < 0)
            {
                lineEnd = text.Length;
            }

            ReadOnlySpan<char> line = text.AsSpan(position, lineEnd - position);
            position = lineEnd + 1;

            int firstSpace = line.IndexOf(' ');
            if (firstSpace <= 0 || firstSpace + 2 >= line.Length)
            {
                continue;
            }

            char symbolType = line[firstSpace + 1];
            if (symbolType != 't' && symbolType != 'T' && symbolType != 'w' && symbolType != 'W')
            {
                continue;
            }

            long address;
            if (!long.TryParse(line.Slice(0, firstSpace), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out address))
            {
                continue;
            }

            // A kernel with kptr_restrict set reports every address as zero.
            // Keeping those would resolve every kernel frame to whichever
            // symbol sorted first.
            if (address == 0)
            {
                continue;
            }

            int nameStart = firstSpace + 3;
            if (nameStart >= line.Length)
            {
                continue;
            }

            ReadOnlySpan<char> name = line.Slice(nameStart);

            // Module symbols carry a trailing "\t[module]".
            int tab = name.IndexOf('\t');
            if (tab >= 0)
            {
                name = name.Slice(0, tab);
            }

            name = name.TrimEnd('\r');
            if (name.Length == 0)
            {
                continue;
            }

            parsedAddresses.Add(address);
            parsedNames.Add(new string(name));
        }

        if (parsedAddresses.Count == 0)
        {
            error = "kallsyms text contained no usable code symbols (is kptr_restrict set?)";
            return null;
        }

        long[] addressArray = parsedAddresses.ToArray();
        string[] nameArray = parsedNames.ToArray();
        Array.Sort(addressArray, nameArray);

        KallsymsTable table = new KallsymsTable();
        table.addresses = addressArray;
        table.names = nameArray;
        return table;
    }

    // Emits each kernel symbol as a synthesized Universal.System ProcessSymbol
    // event, so UniversalSymbolTable resolves kernel frames through exactly
    // the path it already uses for a v6 capture's own symbols. See
    // Analysis/PerfCaptureAnalysis.cs for why synthesis is preferred over a
    // parallel resolver.
    //
    // A symbol's end is the next symbol's start - kallsyms records no sizes -
    // so the last one is dropped rather than given an invented extent.
    public void AppendProcessSymbolEvents(List<DotnetInsights.NetTrace.EventRecord> events)
    {
        for (int symbolIndex = 0; symbolIndex + 1 < this.addresses.Length; ++symbolIndex)
        {
            long start = this.addresses[symbolIndex];
            long end = this.addresses[symbolIndex + 1];

            if (end <= start)
            {
                continue;
            }

            Dictionary<string, object> fields = new Dictionary<string, object>(StringComparer.Ordinal);
            fields["StartAddress"] = start;
            fields["EndAddress"] = end;
            fields["Name"] = this.names[symbolIndex];

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

    // The symbol containing an address, plus how far into it. kallsyms gives
    // no sizes, so a symbol's extent is "until the next one" - which is why an
    // address past the last symbol cannot be attributed and is declined rather
    // than folded into that symbol.
    public bool TryResolve(long address, out string name, out long offsetIntoSymbol)
    {
        name = null;
        offsetIntoSymbol = 0;

        if (this.addresses.Length == 0)
        {
            return false;
        }

        // Unsigned comparison: kernel addresses have the top bit set and read
        // as negative int64, which sorts them below every user address and
        // makes a signed search miss every one of them. The same trap this
        // repo already recorded for UniversalSymbolTable's binary search.
        ulong target = unchecked((ulong)address);

        int low = 0;
        int high = this.addresses.Length - 1;
        int candidate = -1;

        while (low <= high)
        {
            int middle = low + ((high - low) / 2);

            if (unchecked((ulong)this.addresses[middle]) <= target)
            {
                candidate = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        if (candidate < 0)
        {
            return false;
        }

        if (candidate == this.addresses.Length - 1)
        {
            return false;
        }

        name = this.names[candidate];
        offsetIntoSymbol = unchecked((long)(target - (ulong)this.addresses[candidate]));
        return true;
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.Perf.Symbols)

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////
