////////////////////////////////////////////////////////////////////////////////
// Module: DwarfConstants.cs
//
// Notes:
// The DWARF tag / attribute / form codes this project reads. Only the ones
// actually consulted are named; the rest still have to be SKIPPED correctly,
// which is what DwarfForm.TrySkip exists for - an unknown attribute whose size
// cannot be computed makes every attribute after it in the same DIE decode at
// the wrong offset, and DIEs are a flat stream with no per-entry length.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.Perf.Dwarf {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public static class DwarfTag
{
    public const int CompileUnit = 0x11;
    public const int Subprogram = 0x2E;
    public const int InlinedSubroutine = 0x1D;
    public const int LexicalBlock = 0x0B;
}

public static class DwarfAttribute
{
    public const int LowPc = 0x11;
    public const int HighPc = 0x12;
    public const int Name = 0x03;
    public const int Ranges = 0x55;
    public const int AbstractOrigin = 0x31;
    public const int Specification = 0x47;
    public const int LinkageName = 0x6E;

    // The pre-standard GNU spelling, still emitted by some producers.
    public const int GnuLinkageName = 0x2007;
}

public static class DwarfForm
{
    public const int Addr = 0x01;
    public const int Block2 = 0x03;
    public const int Block4 = 0x04;
    public const int Data2 = 0x05;
    public const int Data4 = 0x06;
    public const int Data8 = 0x07;
    public const int String = 0x08;
    public const int Block = 0x09;
    public const int Block1 = 0x0A;
    public const int Data1 = 0x0B;
    public const int Flag = 0x0C;
    public const int Sdata = 0x0D;
    public const int Strp = 0x0E;
    public const int Udata = 0x0F;
    public const int RefAddr = 0x10;
    public const int Ref1 = 0x11;
    public const int Ref2 = 0x12;
    public const int Ref4 = 0x13;
    public const int Ref8 = 0x14;
    public const int RefUdata = 0x15;
    public const int Indirect = 0x16;
    public const int SecOffset = 0x17;
    public const int Exprloc = 0x18;
    public const int FlagPresent = 0x19;
    public const int Strx = 0x1A;
    public const int Addrx = 0x1B;
    public const int RefSup4 = 0x1C;
    public const int StrpSup = 0x1D;
    public const int Data16 = 0x1E;
    public const int LineStrp = 0x1F;
    public const int RefSig8 = 0x20;
    public const int ImplicitConst = 0x21;
    public const int Loclistx = 0x22;
    public const int Rnglistx = 0x23;
    public const int Strx1 = 0x25;
    public const int Strx2 = 0x26;
    public const int Strx3 = 0x27;
    public const int Strx4 = 0x28;
    public const int Addrx1 = 0x29;
    public const int Addrx2 = 0x2A;
    public const int Addrx3 = 0x2B;
    public const int Addrx4 = 0x2C;
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.Perf.Dwarf)

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////
