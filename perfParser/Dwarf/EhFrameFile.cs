////////////////////////////////////////////////////////////////////////////////
// Module: EhFrameFile.cs
//
// Notes:
// Parses a module's `.eh_frame` into a lookup from code address to the unwind
// description covering it.
//
// `.eh_frame` is the reason DWARF unwinding works on binaries nobody built for
// profiling. It is not debug information and is not stripped: C++ exceptions
// and Rust panics need to unwind, so the section is present and allocated in
// ordinary release builds with no `-g`. That is the whole value proposition
// over frame pointers, which a release build is free to omit.
//
// The section is a sequence of entries, each either a CIE (a shared prologue -
// alignment factors, the return-address register, the pointer encoding its
// FDEs use) or an FDE (one address range plus the instructions describing how
// to unwind within it). An entry's first word is its length; the next word
// distinguishes the two - zero means CIE, anything else is a BACKWARD offset
// to the FDE's own CIE.
//
// This builds a sorted index by walking the whole section once, rather than
// reading the `.eh_frame_hdr` binary-search table. The table would be faster to
// query, but it is optional, its own contents are pointer-encoded (usually
// datarel, whose base is the header's address), and a module without one would
// need the scan anyway. One scan per module, once, is not the cost that matters
// here - the per-sample unwinding is.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.Perf.Dwarf {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;

using DotnetInsights.Perf.Symbols;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public sealed class EhFrameCie
{
    public byte Version;
    public string Augmentation = string.Empty;
    public ulong CodeAlignmentFactor = 1;
    public long DataAlignmentFactor = 1;
    public ulong ReturnAddressRegister;
    public byte FdePointerEncoding = DwarfPointerEncoding.FormatAbsolutePointer;
    public bool HasAugmentationData;

    // Offset and length, within .eh_frame, of the instructions every FDE
    // sharing this CIE runs before its own.
    public int InitialInstructionsOffset;
    public int InitialInstructionsLength;

    // AArch64 signs return addresses with pointer authentication. The unwinder
    // must strip the signature before using one as an address, and whether a
    // given frame's is signed is itself CFI state
    // (DW_CFA_AARCH64_negate_ra_state).
    public bool IsSignalFrame;
}

public struct EhFrameFde
{
    public long PcBegin;
    public long PcRange;
    public int InstructionsOffset;
    public int InstructionsLength;
    public EhFrameCie Cie;
}

public sealed class EhFrameFile
{
    private const uint CieIdInEhFrame = 0;
    private const uint ExtendedLengthMarker = 0xFFFFFFFF;

    private byte[] section;
    private long sectionVirtualAddress;

    // Sorted by PcBegin. Binary searched per unwind step, which on a real
    // capture happens millions of times.
    private EhFrameFde[] sortedFdes = Array.Empty<EhFrameFde>();

    public int FdeCount => this.sortedFdes.Length;

    public bool IsEmpty => this.sortedFdes.Length == 0;

    public ReadOnlySpan<byte> Section => this.section;

    public long SectionVirtualAddress => this.sectionVirtualAddress;

    public static EhFrameFile TryLoad(ElfSections sections, out string error)
    {
        error = null;

        if (sections == null)
        {
            error = "no ELF sections";
            return null;
        }

        ElfSection ehFrame = sections.TryGet(".eh_frame");
        if (ehFrame == null || ehFrame.Data == null || ehFrame.Data.Length == 0)
        {
            error = "no .eh_frame section";
            return null;
        }

        EhFrameFile file = new EhFrameFile();
        file.section = ehFrame.Data;
        file.sectionVirtualAddress = ehFrame.Address;
        file.BuildIndex();

        if (file.sortedFdes.Length == 0)
        {
            error = ".eh_frame contains no usable FDEs";
            return null;
        }

        return file;
    }

    private void BuildIndex()
    {
        Dictionary<int, EhFrameCie> ciesByOffset = new Dictionary<int, EhFrameCie>();
        List<EhFrameFde> fdes = new List<EhFrameFde>();

        int position = 0;

        while (position + 4 <= this.section.Length)
        {
            int entryStart = position;

            DwarfReader reader = new DwarfReader(this.section, this.sectionVirtualAddress);
            reader.Position = position;

            uint length32 = reader.ReadUInt32();

            // A zero-length entry is the section terminator.
            if (length32 == 0)
            {
                break;
            }

            long entryLength;
            if (length32 == ExtendedLengthMarker)
            {
                entryLength = unchecked((long)reader.ReadUInt64());
            }
            else
            {
                entryLength = length32;
            }

            // Length counts from AFTER the length field itself.
            int contentStart = reader.Position;
            long entryEnd = contentStart + entryLength;

            if (entryLength <= 0 || entryEnd > this.section.Length)
            {
                break;
            }

            uint cieIdOrPointer = reader.ReadUInt32();

            if (cieIdOrPointer == CieIdInEhFrame)
            {
                EhFrameCie cie = this.ParseCie(ref reader, (int)entryEnd);
                if (cie != null)
                {
                    ciesByOffset[entryStart] = cie;
                }
            }
            else
            {
                // A BACKWARD offset from the CIE-pointer field's own position.
                int ciePointerFieldOffset = reader.Position - 4;
                int cieOffset = ciePointerFieldOffset - (int)cieIdOrPointer;

                EhFrameCie cie;
                if (ciesByOffset.TryGetValue(cieOffset, out cie))
                {
                    EhFrameFde fde;
                    if (this.TryParseFde(ref reader, (int)entryEnd, cie, out fde))
                    {
                        fdes.Add(fde);
                    }
                }
            }

            position = (int)entryEnd;
        }

        fdes.Sort(CompareByPcBegin);
        this.sortedFdes = fdes.ToArray();
    }

    private static int CompareByPcBegin(EhFrameFde left, EhFrameFde right)
    {
        return left.PcBegin.CompareTo(right.PcBegin);
    }

    private EhFrameCie ParseCie(ref DwarfReader reader, int entryEnd)
    {
        EhFrameCie cie = new EhFrameCie();
        cie.Version = reader.ReadUInt8();

        if (cie.Version != 1 && cie.Version != 3 && cie.Version != 4)
        {
            return null;
        }

        cie.Augmentation = reader.ReadNulTerminatedString();

        if (cie.Version == 4)
        {
            // address_size, segment_selector_size
            reader.Skip(2);
        }

        cie.CodeAlignmentFactor = reader.ReadUleb128();
        cie.DataAlignmentFactor = reader.ReadSleb128();

        // Version 1 stores the return-address register as a single byte;
        // later versions as a ULEB. Reading the wrong one shifts every field
        // after it.
        cie.ReturnAddressRegister = cie.Version == 1 ? reader.ReadUInt8() : reader.ReadUleb128();

        if (cie.Augmentation.Length > 0 && cie.Augmentation[0] == 'z')
        {
            cie.HasAugmentationData = true;

            ulong augmentationLength = reader.ReadUleb128();
            int augmentationEnd = reader.Position + (int)augmentationLength;

            for (int characterIndex = 1; characterIndex < cie.Augmentation.Length; ++characterIndex)
            {
                char augmentationCharacter = cie.Augmentation[characterIndex];

                if (augmentationCharacter == 'R')
                {
                    cie.FdePointerEncoding = reader.ReadUInt8();
                }
                else if (augmentationCharacter == 'P')
                {
                    byte personalityEncoding = reader.ReadUInt8();
                    long ignoredPersonality;
                    reader.TryReadEncodedPointer(personalityEncoding, 0, 0, out ignoredPersonality);
                }
                else if (augmentationCharacter == 'L')
                {
                    reader.ReadUInt8();
                }
                else if (augmentationCharacter == 'S')
                {
                    // A signal frame. Its return address is an exact address
                    // rather than one past a call, which changes the lookup
                    // below by one byte.
                    cie.IsSignalFrame = true;
                }
                else if (augmentationCharacter == 'B' || augmentationCharacter == 'G')
                {
                    // AArch64 pointer-authentication / MTE markers, no payload.
                }
            }

            // Trust the declared length over the walk: an augmentation
            // character this reader does not know would otherwise leave the
            // cursor short and misparse every instruction after it.
            reader.Position = augmentationEnd;
        }

        if (reader.Overran || reader.Position > entryEnd)
        {
            return null;
        }

        cie.InitialInstructionsOffset = reader.Position;
        cie.InitialInstructionsLength = entryEnd - reader.Position;
        return cie;
    }

    private bool TryParseFde(ref DwarfReader reader, int entryEnd, EhFrameCie cie, out EhFrameFde fde)
    {
        fde = default(EhFrameFde);

        long pcBegin;
        if (!reader.TryReadEncodedPointer(cie.FdePointerEncoding, 0, 0, out pcBegin))
        {
            return false;
        }

        // pc_range uses the same FORMAT as pc_begin but is never relative to
        // anything - it is a length. Applying the encoding's pcrel bias to it
        // produces a range covering most of the address space, which then
        // matches every lookup.
        byte rangeEncoding = (byte)(cie.FdePointerEncoding & DwarfPointerEncoding.FormatMask);

        long pcRange;
        if (!reader.TryReadEncodedPointer(rangeEncoding, 0, 0, out pcRange))
        {
            return false;
        }

        if (cie.HasAugmentationData)
        {
            ulong augmentationLength = reader.ReadUleb128();
            reader.Skip((int)augmentationLength);
        }

        if (reader.Overran || reader.Position > entryEnd || pcRange <= 0)
        {
            return false;
        }

        fde.PcBegin = pcBegin;
        fde.PcRange = pcRange;
        fde.InstructionsOffset = reader.Position;
        fde.InstructionsLength = entryEnd - reader.Position;
        fde.Cie = cie;
        return true;
    }

    // The nth FDE in address order. For tests and diagnostics that want to
    // sweep every function a binary describes rather than look one up.
    public bool TryFindFdeByIndex(int index, out EhFrameFde found)
    {
        if (index < 0 || index >= this.sortedFdes.Length)
        {
            found = default(EhFrameFde);
            return false;
        }

        found = this.sortedFdes[index];
        return true;
    }

    // The FDE covering an ELF virtual address, or false when none does -
    // which is a normal outcome, not an error: hand-written assembly and some
    // PLT stubs genuinely have no unwind description.
    public bool TryFindFde(long elfVirtualAddress, out EhFrameFde found)
    {
        found = default(EhFrameFde);

        int low = 0;
        int high = this.sortedFdes.Length - 1;
        int candidate = -1;

        while (low <= high)
        {
            int middle = low + ((high - low) / 2);

            if (this.sortedFdes[middle].PcBegin <= elfVirtualAddress)
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

        EhFrameFde entry = this.sortedFdes[candidate];
        if (elfVirtualAddress >= entry.PcBegin + entry.PcRange)
        {
            return false;
        }

        found = entry;
        return true;
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.Perf.Dwarf)

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////
