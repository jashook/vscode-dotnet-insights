////////////////////////////////////////////////////////////////////////////////
// Module: DebugInfoIndex.cs
//
// Notes:
// Recovers INLINED frames. `.eh_frame` unwinding reconstructs the chain of
// physical stack frames; it cannot show a function that has no frame because
// the compiler inlined it into its caller. That is not an edge case in Rust -
// a release build inlines hard, and the reference workload's `run_dynamic` and
// `AlphaWorkload::run` vanished from every reconstructed stack, leaving the
// tree jumping straight from `dynamic_thread` to `hot_leaf_beta`.
//
// The information lives in `.debug_info`: each `DW_TAG_inlined_subroutine` DIE
// records the address range its inlined body occupies, nested inside the
// `DW_TAG_subprogram` that physically contains it. One address can therefore
// be inside several nested inlined bodies at once, and the answer for a frame
// is the whole chain, innermost first.
//
// Scope, decided by measuring the real binary rather than by covering the spec:
// rustc emitted DWARF **4** with `.debug_ranges` (748KB of it - discontiguous
// ranges are what optimized code produces) and `.debug_str`, and no
// `.debug_rnglists` / `.debug_str_offsets` / `.debug_addr`. So version 4 is
// implemented fully, version 5's unit header and its common forms are handled,
// and anything else DECLINES - returning no inline information rather than
// wrong information, the same rule the demangler follows.
//
// Two passes over `.debug_info`, not one. An inlined subroutine names itself
// through `DW_AT_abstract_origin`, a reference to the abstract instance DIE
// elsewhere in the unit. That reference usually points backwards, but nothing
// guarantees it, and a single pass that resolved names as it went would
// silently produce unnamed frames for the forward cases.
//
// Names come from `DW_AT_linkage_name` where present, in preference to
// `DW_AT_name`: the linkage name is the mangled symbol, which demangles to the
// same full path the symbol table produces. `DW_AT_name` is the bare source
// name (`hot_leaf_alpha`), which would make an inlined frame look like a
// different function from the same function sampled directly.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.Perf.Dwarf {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Buffers.Binary;
using System.Collections.Generic;

using DotnetInsights.NetTrace.Symbols;
using DotnetInsights.Perf.Symbols;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public sealed class DebugInfoIndex
{
    // One inlined body (or one physical function), as an address range plus how
    // deeply nested it is. Depth 0 is the physical subprogram; every larger
    // depth is a body inlined into it.
    private struct InlineRange
    {
        public long StartAddress;
        public long EndAddress;
        public int Depth;
        public int NameIndex;
    }

    private sealed class AbbreviationDeclaration
    {
        public int Tag;
        public bool HasChildren;
        public readonly List<int> AttributeCodes = new List<int>();
        public readonly List<int> AttributeForms = new List<int>();

        // DW_FORM_implicit_const carries its value in the abbrev, not the DIE.
        public readonly List<long> ImplicitConstants = new List<long>();
    }

    private InlineRange[] sortedRanges = Array.Empty<InlineRange>();
    private string[] names = Array.Empty<string>();

    // Bounds how far back a query has to scan from the last range starting at
    // or before it - the same trick MethodSymbolTable uses for overlapping
    // method ranges, and necessary here because inlined ranges nest and so
    // genuinely overlap.
    private long maximumRangeLength;

    public int RangeCount => this.sortedRanges.Length;

    public bool IsEmpty => this.sortedRanges.Length == 0;

    public static DebugInfoIndex TryLoad(ElfSections sections, out string error)
    {
        error = null;

        if (sections == null)
        {
            error = "no ELF sections";
            return null;
        }

        ElfSection info = sections.TryGet(".debug_info");
        ElfSection abbrev = sections.TryGet(".debug_abbrev");

        if (info == null || abbrev == null || info.Data == null || abbrev.Data == null)
        {
            error = "no .debug_info/.debug_abbrev (module built without -g?)";
            return null;
        }

        ElfSection strings = sections.TryGet(".debug_str");
        ElfSection lineStrings = sections.TryGet(".debug_line_str");
        ElfSection ranges = sections.TryGet(".debug_ranges");
        ElfSection rangeLists = sections.TryGet(".debug_rnglists");

        DebugInfoIndex index = new DebugInfoIndex();

        Builder builder = new Builder(
            info.Data,
            abbrev.Data,
            strings != null ? strings.Data : null,
            lineStrings != null ? lineStrings.Data : null,
            ranges != null ? ranges.Data : null,
            rangeLists != null ? rangeLists.Data : null);

        if (!builder.Build(index))
        {
            error = "no inlined subroutines found in .debug_info";
            return null;
        }

        return index;
    }

    // The chain of inlined function names covering an ELF virtual address,
    // INNERMOST FIRST, excluding the physical subprogram itself (the caller
    // already has that name from the symbol table). Empty when the address is
    // in no inlined body, which is the common case and not an error.
    public int GetInlineChain(long elfVirtualAddress, List<string> namesOut)
    {
        namesOut.Clear();

        if (this.sortedRanges.Length == 0)
        {
            return 0;
        }

        int low = 0;
        int high = this.sortedRanges.Length - 1;
        int candidate = -1;

        while (low <= high)
        {
            int middle = low + ((high - low) / 2);

            if (this.sortedRanges[middle].StartAddress <= elfVirtualAddress)
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
            return 0;
        }

        // Ranges nest, so several starting before this address can contain it.
        // Walk back only as far as the longest range could reach.
        List<InlineRange> containing = new List<InlineRange>();

        for (int rangeIndex = candidate; rangeIndex >= 0; --rangeIndex)
        {
            InlineRange range = this.sortedRanges[rangeIndex];

            if (elfVirtualAddress - range.StartAddress > this.maximumRangeLength)
            {
                break;
            }

            if (elfVirtualAddress < range.EndAddress)
            {
                containing.Add(range);
            }
        }

        if (containing.Count == 0)
        {
            return 0;
        }

        // Innermost first: the most deeply nested inlined body is the one the
        // instruction is actually in.
        containing.Sort(static (left, right) => right.Depth.CompareTo(left.Depth));

        for (int rangeIndex = 0; rangeIndex < containing.Count; ++rangeIndex)
        {
            // Depth 0 is the physical function, whose name the symbol table
            // already supplies. Including it would duplicate the frame.
            if (containing[rangeIndex].Depth == 0)
            {
                continue;
            }

            namesOut.Add(this.names[containing[rangeIndex].NameIndex]);
        }

        return namesOut.Count;
    }

    ////////////////////////////////////////////////////////////////////////////
    // Building
    ////////////////////////////////////////////////////////////////////////////

    private sealed class Builder
    {
        private readonly byte[] info;
        private readonly byte[] abbrev;
        private readonly byte[] strings;
        private readonly byte[] lineStrings;
        private readonly byte[] ranges;
        private readonly byte[] rangeLists;

        // DIE offset (from the start of .debug_info) -> its name, from pass
        // one. Pass two resolves DW_AT_abstract_origin through this.
        private readonly Dictionary<long, string> nameByDieOffset = new Dictionary<long, string>();

        private readonly List<InlineRange> built = new List<InlineRange>();
        private readonly List<string> nameTable = new List<string>();
        private readonly Dictionary<string, int> nameIndexByName = new Dictionary<string, int>(StringComparer.Ordinal);

        public Builder(byte[] info, byte[] abbrev, byte[] strings, byte[] lineStrings, byte[] ranges, byte[] rangeLists)
        {
            this.info = info;
            this.abbrev = abbrev;
            this.strings = strings;
            this.lineStrings = lineStrings;
            this.ranges = ranges;
            this.rangeLists = rangeLists;
        }

        public bool Build(DebugInfoIndex index)
        {
            this.WalkAllUnits(true);
            this.WalkAllUnits(false);

            if (this.built.Count == 0)
            {
                return false;
            }

            InlineRange[] sorted = this.built.ToArray();
            Array.Sort(sorted, static (left, right) => left.StartAddress.CompareTo(right.StartAddress));

            long longest = 0;
            for (int rangeIndex = 0; rangeIndex < sorted.Length; ++rangeIndex)
            {
                long length = sorted[rangeIndex].EndAddress - sorted[rangeIndex].StartAddress;
                if (length > longest)
                {
                    longest = length;
                }
            }

            index.sortedRanges = sorted;
            index.names = this.nameTable.ToArray();
            index.maximumRangeLength = longest;
            return true;
        }

        private int InternName(string name)
        {
            int existing;
            if (this.nameIndexByName.TryGetValue(name, out existing))
            {
                return existing;
            }

            int assigned = this.nameTable.Count;
            this.nameTable.Add(name);
            this.nameIndexByName[name] = assigned;
            return assigned;
        }

        private void WalkAllUnits(bool namesOnlyPass)
        {
            int position = 0;

            while (position + 11 <= this.info.Length)
            {
                int unitStart = position;

                uint unitLength32 = BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>(this.info, position, 4));

                // The 64-bit DWARF format is signalled by 0xFFFFFFFF. It is
                // vanishingly rare on Linux and is declined rather than
                // half-supported.
                if (unitLength32 == 0xFFFFFFFF || unitLength32 == 0)
                {
                    return;
                }

                long unitEnd = position + 4 + unitLength32;
                if (unitEnd > this.info.Length)
                {
                    return;
                }

                position += 4;
                int version = BinaryPrimitives.ReadUInt16LittleEndian(new ReadOnlySpan<byte>(this.info, position, 2));
                position += 2;

                long abbrevOffset;
                int addressSize;

                if (version >= 5)
                {
                    // unit_type, address_size, debug_abbrev_offset
                    position += 1;
                    addressSize = this.info[position];
                    position += 1;
                    abbrevOffset = BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>(this.info, position, 4));
                    position += 4;
                }
                else if (version >= 2)
                {
                    abbrevOffset = BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>(this.info, position, 4));
                    position += 4;
                    addressSize = this.info[position];
                    position += 1;
                }
                else
                {
                    position = (int)unitEnd;
                    continue;
                }

                Dictionary<long, AbbreviationDeclaration> declarations = this.ReadAbbreviations(abbrevOffset);

                if (declarations != null && addressSize == 8)
                {
                    this.WalkUnit(unitStart, position, (int)unitEnd, version, declarations, namesOnlyPass);
                }

                position = (int)unitEnd;
            }
        }

        private Dictionary<long, AbbreviationDeclaration> ReadAbbreviations(long offset)
        {
            if (offset < 0 || offset >= this.abbrev.Length)
            {
                return null;
            }

            Dictionary<long, AbbreviationDeclaration> declarations = new Dictionary<long, AbbreviationDeclaration>();

            DwarfReader reader = new DwarfReader(this.abbrev, 0);
            reader.Position = (int)offset;

            while (!reader.AtEnd)
            {
                ulong code = reader.ReadUleb128();
                if (code == 0)
                {
                    break;
                }

                AbbreviationDeclaration declaration = new AbbreviationDeclaration();
                declaration.Tag = (int)reader.ReadUleb128();
                declaration.HasChildren = reader.ReadUInt8() != 0;

                while (true)
                {
                    int attribute = (int)reader.ReadUleb128();
                    int form = (int)reader.ReadUleb128();

                    long implicitConstant = 0;
                    if (form == DwarfForm.ImplicitConst)
                    {
                        implicitConstant = reader.ReadSleb128();
                    }

                    if (attribute == 0 && form == 0)
                    {
                        break;
                    }

                    if (reader.Overran)
                    {
                        return null;
                    }

                    declaration.AttributeCodes.Add(attribute);
                    declaration.AttributeForms.Add(form);
                    declaration.ImplicitConstants.Add(implicitConstant);
                }

                declarations[(long)code] = declaration;
            }

            return declarations;
        }

        private void WalkUnit(int unitStart, int dieStart, int unitEnd, int version, Dictionary<long, AbbreviationDeclaration> declarations, bool namesOnlyPass)
        {
            DwarfReader reader = new DwarfReader(this.info, 0);
            reader.Position = dieStart;

            // The subprogram/inlined-subroutine nesting, as a stack of depths.
            // A lexical block does not add a level - only an inlined body does.
            List<int> openDepths = new List<int>();
            int currentDepth = -1;

            // DW_AT_low_pc of the enclosing compile unit, which is the base
            // every .debug_ranges entry in it is relative to.
            long unitBaseAddress = 0;

            while (reader.Position < unitEnd && !reader.Overran)
            {
                long dieOffset = reader.Position;
                ulong abbreviationCode = reader.ReadUleb128();

                if (abbreviationCode == 0)
                {
                    // End of a sibling chain: close the innermost open scope.
                    if (openDepths.Count > 0)
                    {
                        openDepths.RemoveAt(openDepths.Count - 1);
                        currentDepth = openDepths.Count > 0 ? openDepths[openDepths.Count - 1] : -1;
                    }

                    continue;
                }

                AbbreviationDeclaration declaration;
                if (!declarations.TryGetValue((long)abbreviationCode, out declaration))
                {
                    // The abbrev table and the DIE stream have gone out of
                    // step; nothing after this point in the unit is meaningful.
                    return;
                }

                long lowPc = 0;
                long highPc = 0;
                bool hasLowPc = false;
                bool hasHighPc = false;
                bool highPcIsOffset = false;
                long rangesOffset = -1;
                string dieName = null;
                string linkageName = null;
                long abstractOrigin = -1;

                for (int attributeIndex = 0; attributeIndex < declaration.AttributeCodes.Count; ++attributeIndex)
                {
                    int attribute = declaration.AttributeCodes[attributeIndex];
                    int form = declaration.AttributeForms[attributeIndex];

                    switch (attribute)
                    {
                        case DwarfAttribute.LowPc:
                            hasLowPc = this.TryReadAddress(ref reader, form, out lowPc);
                            break;

                        case DwarfAttribute.HighPc:
                            // DWARF 4 onwards: a CONSTANT form means high_pc is
                            // an offset from low_pc, an ADDRESS form means it is
                            // an address. Treating an offset as an address
                            // produces a range starting near zero and covering
                            // the whole binary.
                            highPcIsOffset = form != DwarfForm.Addr;
                            hasHighPc = highPcIsOffset
                                ? this.TryReadConstant(ref reader, form, out highPc)
                                : this.TryReadAddress(ref reader, form, out highPc);
                            break;

                        case DwarfAttribute.Ranges:
                        {
                            long value;
                            if (this.TryReadConstant(ref reader, form, out value))
                            {
                                rangesOffset = value;
                            }

                            break;
                        }

                        case DwarfAttribute.Name:
                            dieName = this.ReadString(ref reader, form);
                            break;

                        case DwarfAttribute.LinkageName:
                        case DwarfAttribute.GnuLinkageName:
                            linkageName = this.ReadString(ref reader, form);
                            break;

                        case DwarfAttribute.AbstractOrigin:
                        case DwarfAttribute.Specification:
                        {
                            long reference;
                            if (this.TryReadReference(ref reader, form, unitStart, out reference))
                            {
                                abstractOrigin = reference;
                            }

                            break;
                        }

                        default:
                            this.SkipForm(ref reader, form, declaration.ImplicitConstants[attributeIndex]);
                            break;
                    }
                }

                if (reader.Overran)
                {
                    return;
                }

                if (declaration.Tag == DwarfTag.CompileUnit && hasLowPc)
                {
                    unitBaseAddress = lowPc;
                }

                if (namesOnlyPass)
                {
                    string preferredName = linkageName ?? dieName;
                    if (preferredName != null)
                    {
                        this.nameByDieOffset[dieOffset] = preferredName;
                    }
                }
                else if (declaration.Tag == DwarfTag.InlinedSubroutine || declaration.Tag == DwarfTag.Subprogram)
                {
                    int depth = declaration.Tag == DwarfTag.Subprogram ? 0 : currentDepth + 1;

                    string resolvedName = linkageName ?? dieName;
                    if (resolvedName == null && abstractOrigin >= 0)
                    {
                        this.nameByDieOffset.TryGetValue(abstractOrigin, out resolvedName);
                    }

                    if (resolvedName != null)
                    {
                        int nameIndex = this.InternName(NativeSymbolDemangler.Demangle(resolvedName));

                        if (hasLowPc && hasHighPc)
                        {
                            long end = highPcIsOffset ? lowPc + highPc : highPc;
                            this.AddRange(lowPc, end, depth, nameIndex);
                        }
                        else if (rangesOffset >= 0)
                        {
                            this.AddRangesFromList(rangesOffset, unitBaseAddress, version, depth, nameIndex);
                        }
                    }

                    if (declaration.HasChildren)
                    {
                        currentDepth = depth;
                    }
                }

                if (declaration.HasChildren)
                {
                    openDepths.Add(currentDepth);
                }
            }
        }

        private void AddRange(long startAddress, long endAddress, int depth, int nameIndex)
        {
            if (endAddress <= startAddress)
            {
                return;
            }

            InlineRange range = new InlineRange();
            range.StartAddress = startAddress;
            range.EndAddress = endAddress;
            range.Depth = depth;
            range.NameIndex = nameIndex;
            this.built.Add(range);
        }

        // DWARF 4 `.debug_ranges`: pairs of (start, end) offsets from a base
        // address, terminated by (0, 0). A pair whose first word is all-ones is
        // a BASE ADDRESS SELECTOR and sets the base for what follows - missing
        // that makes every subsequent range in the list land at the wrong
        // address.
        private void AddRangesFromList(long offset, long unitBaseAddress, int version, int depth, int nameIndex)
        {
            if (version >= 5)
            {
                // .debug_rnglists has a different encoding entirely. Declined
                // rather than guessed at; see this file's header.
                return;
            }

            if (this.ranges == null || offset < 0 || offset + 16 > this.ranges.Length)
            {
                return;
            }

            long base_ = unitBaseAddress;
            int position = (int)offset;

            while (position + 16 <= this.ranges.Length)
            {
                long first = BinaryPrimitives.ReadInt64LittleEndian(new ReadOnlySpan<byte>(this.ranges, position, 8));
                long second = BinaryPrimitives.ReadInt64LittleEndian(new ReadOnlySpan<byte>(this.ranges, position + 8, 8));
                position += 16;

                if (first == 0 && second == 0)
                {
                    return;
                }

                if (first == -1L)
                {
                    base_ = second;
                    continue;
                }

                this.AddRange(base_ + first, base_ + second, depth, nameIndex);
            }
        }

        ////////////////////////////////////////////////////////////////////////
        // Form decoding
        ////////////////////////////////////////////////////////////////////////

        private bool TryReadAddress(ref DwarfReader reader, int form, out long value)
        {
            value = 0;

            if (form == DwarfForm.Addr)
            {
                value = unchecked((long)reader.ReadUInt64());
                return true;
            }

            // DW_FORM_addrx needs .debug_addr, which this binary does not have.
            this.SkipForm(ref reader, form, 0);
            return false;
        }

        private bool TryReadConstant(ref DwarfReader reader, int form, out long value)
        {
            value = 0;

            switch (form)
            {
                case DwarfForm.Data1: value = reader.ReadUInt8(); return true;
                case DwarfForm.Data2: value = reader.ReadUInt16(); return true;
                case DwarfForm.Data4: value = reader.ReadUInt32(); return true;
                case DwarfForm.Data8: value = unchecked((long)reader.ReadUInt64()); return true;
                case DwarfForm.SecOffset: value = reader.ReadUInt32(); return true;
                case DwarfForm.Udata: value = unchecked((long)reader.ReadUleb128()); return true;
                case DwarfForm.Sdata: value = reader.ReadSleb128(); return true;
                default:
                    this.SkipForm(ref reader, form, 0);
                    return false;
            }
        }

        // A reference to another DIE. The common forms are relative to the
        // start of the compilation unit; DW_FORM_ref_addr is relative to the
        // whole section. Confusing the two resolves an abstract origin to a
        // completely unrelated DIE, which yields a wrong NAME rather than no
        // name.
        private bool TryReadReference(ref DwarfReader reader, int form, int unitStart, out long value)
        {
            value = 0;

            switch (form)
            {
                case DwarfForm.Ref1: value = unitStart + reader.ReadUInt8(); return true;
                case DwarfForm.Ref2: value = unitStart + reader.ReadUInt16(); return true;
                case DwarfForm.Ref4: value = unitStart + reader.ReadUInt32(); return true;
                case DwarfForm.Ref8: value = unitStart + unchecked((long)reader.ReadUInt64()); return true;
                case DwarfForm.RefUdata: value = unitStart + unchecked((long)reader.ReadUleb128()); return true;
                case DwarfForm.RefAddr: value = reader.ReadUInt32(); return true;
                default:
                    this.SkipForm(ref reader, form, 0);
                    return false;
            }
        }

        private string ReadString(ref DwarfReader reader, int form)
        {
            switch (form)
            {
                case DwarfForm.String:
                    return reader.ReadNulTerminatedString();

                case DwarfForm.Strp:
                {
                    long offset = reader.ReadUInt32();
                    return ReadFromStringSection(this.strings, offset);
                }

                case DwarfForm.LineStrp:
                {
                    long offset = reader.ReadUInt32();
                    return ReadFromStringSection(this.lineStrings, offset);
                }

                default:
                    this.SkipForm(ref reader, form, 0);
                    return null;
            }
        }

        private static string ReadFromStringSection(byte[] section, long offset)
        {
            if (section == null || offset < 0 || offset >= section.Length)
            {
                return null;
            }

            int end = (int)offset;
            while (end < section.Length && section[end] != 0)
            {
                ++end;
            }

            return System.Text.Encoding.UTF8.GetString(section, (int)offset, end - (int)offset);
        }

        // Every form must be skippable even when its value is not wanted - a
        // DIE is a bare sequence of values with no lengths, so one unskippable
        // attribute desynchronises everything after it.
        private void SkipForm(ref DwarfReader reader, int form, long implicitConstant)
        {
            switch (form)
            {
                case DwarfForm.Addr: reader.Skip(8); break;
                case DwarfForm.Block1: reader.Skip(reader.ReadUInt8()); break;
                case DwarfForm.Block2: reader.Skip(reader.ReadUInt16()); break;
                case DwarfForm.Block4: reader.Skip((int)reader.ReadUInt32()); break;
                case DwarfForm.Block:
                case DwarfForm.Exprloc: reader.Skip((int)reader.ReadUleb128()); break;
                case DwarfForm.Data1:
                case DwarfForm.Flag:
                case DwarfForm.Ref1:
                case DwarfForm.Strx1:
                case DwarfForm.Addrx1: reader.Skip(1); break;
                case DwarfForm.Data2:
                case DwarfForm.Ref2:
                case DwarfForm.Strx2:
                case DwarfForm.Addrx2: reader.Skip(2); break;
                case DwarfForm.Strx3:
                case DwarfForm.Addrx3: reader.Skip(3); break;
                case DwarfForm.Data4:
                case DwarfForm.Ref4:
                case DwarfForm.RefSup4:
                case DwarfForm.SecOffset:
                case DwarfForm.Strp:
                case DwarfForm.LineStrp:
                case DwarfForm.StrpSup:
                case DwarfForm.RefAddr:
                case DwarfForm.Strx4:
                case DwarfForm.Addrx4: reader.Skip(4); break;
                case DwarfForm.Data8:
                case DwarfForm.Ref8:
                case DwarfForm.RefSig8: reader.Skip(8); break;
                case DwarfForm.Data16: reader.Skip(16); break;
                case DwarfForm.String: reader.ReadNulTerminatedString(); break;
                case DwarfForm.Sdata: reader.ReadSleb128(); break;
                case DwarfForm.Udata:
                case DwarfForm.RefUdata:
                case DwarfForm.Strx:
                case DwarfForm.Addrx:
                case DwarfForm.Loclistx:
                case DwarfForm.Rnglistx: reader.ReadUleb128(); break;
                case DwarfForm.FlagPresent:
                case DwarfForm.ImplicitConst: break;
                case DwarfForm.Indirect: this.SkipForm(ref reader, (int)reader.ReadUleb128(), 0); break;
                default:
                    // An unknown form has an unknown length. Abandoning the
                    // unit is the only safe response; guessing desynchronises
                    // the DIE stream and invents ranges.
                    reader.Skip(reader.Remaining);
                    break;
            }
        }
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.Perf.Dwarf)

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////
