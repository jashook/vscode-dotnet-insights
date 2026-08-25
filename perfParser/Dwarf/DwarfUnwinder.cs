////////////////////////////////////////////////////////////////////////////////
// Module: DwarfUnwinder.cs
//
// Notes:
// Reconstructs a call stack from a `perf record --call-graph dwarf` sample:
// a register file plus a verbatim copy of the top of the user stack, taken at
// the instant of the sample. There is no live process by the time a capture is
// read, so every memory read the unwind needs must land inside that copy - and
// when it does not, the unwind stops. A truncated stack is the normal outcome
// for a deep call chain, not an error: perf copies a bounded window (8KB by
// default) and anything below it was never recorded.
//
// Why this exists at all: a release build is free to omit frame pointers, and
// on x86-64 rustc, gcc and clang all do. `.eh_frame` is present anyway - C++
// exceptions and Rust panics need it - so DWARF unwinding is the only way to
// get call stacks out of binaries nobody rebuilt for profiling. On AArch64 the
// procedure call standard keeps x29 as a frame pointer and LLVM defaults to
// emitting it, so the frame-pointer path already works there; this matters far
// more on x86-64.
//
// Three details that produce plausible-but-wrong stacks if missed:
//
//   - A RETURN ADDRESS POINTS PAST THE CALL. Looking up the FDE for it finds
//     whatever follows - frequently the NEXT function, when the call was the
//     last instruction of its caller. Every frame after the first is therefore
//     looked up at `returnAddress - 1`, while the address REPORTED stays the
//     real one.
//
//   - perf's register numbering is NOT DWARF's on x86-64. perf orders them
//     AX,BX,CX,DX,...; DWARF orders them RAX,RDX,RCX,RBX,... Reading a CFI
//     rule for "register 3" out of perf slot 3 swaps RBX and RDX, so the CFA
//     comes from the wrong register and the whole unwind proceeds confidently
//     down a garbage stack. AArch64's two numberings happen to agree, which
//     makes this a bug that only appears on the architecture that needs the
//     feature most.
//
//   - AArch64 may SIGN return addresses with pointer authentication. The
//     signature occupies the top bits, so an unstripped value resolves to no
//     module at all. Whether a frame's is signed is CFI state, tracked by
//     DW_CFA_AARCH64_negate_ra_state.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.Perf.Dwarf {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Buffers.Binary;
using System.Collections.Generic;

using DotnetInsights.Perf.PerfData;
using DotnetInsights.Perf.Symbols;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public enum UnwindArchitecture
{
    Unknown = 0,
    X86_64 = 1,
    AArch64 = 2
}

public sealed class DwarfUnwinder
{
    // A stack deeper than this is a runaway unwind, not a real call chain -
    // perf's own default sample_max_stack is 127.
    private const int MaxFrames = 192;

    private const int AArch64ReturnAddressRegister = 30;
    private const int AArch64StackPointerRegister = 31;
    private const int AArch64FramePointerRegister = 29;

    private const int X86StackPointerRegister = 7;
    private const int X86FramePointerRegister = 6;
    private const int X86ReturnAddressColumn = 16;

    // AArch64 pointer authentication puts the code in the upper bits. Real
    // user-space addresses on Linux fit in 48 bits.
    private const ulong AArch64AddressMask = 0x0000FFFFFFFFFFFFUL;

    private readonly PerfSymbolTable symbols;
    private readonly UnwindArchitecture architecture;

    private readonly Dictionary<string, EhFrameFile> ehFrameByModuleKey = new Dictionary<string, EhFrameFile>(StringComparer.Ordinal);
    private readonly HashSet<string> modulesWithoutEhFrame = new HashSet<string>(StringComparer.Ordinal);

    private readonly ulong[] registers = new ulong[CfiEvaluator.MaxRegisters];

    public long FramesRecovered { get; private set; }

    public long SamplesUnwound { get; private set; }

    public long SamplesStoppedNoFde { get; private set; }

    public long SamplesStoppedOffStack { get; private set; }

    public long SamplesStoppedExpression { get; private set; }

    public DwarfUnwinder(PerfSymbolTable symbols, string architectureName)
    {
        this.symbols = symbols;
        this.architecture = ParseArchitecture(architectureName);
    }

    public UnwindArchitecture Architecture => this.architecture;

    public static UnwindArchitecture ParseArchitecture(string architectureName)
    {
        if (string.IsNullOrEmpty(architectureName))
        {
            return UnwindArchitecture.Unknown;
        }

        if (architectureName.StartsWith("aarch64", StringComparison.OrdinalIgnoreCase) ||
            architectureName.StartsWith("arm64", StringComparison.OrdinalIgnoreCase))
        {
            return UnwindArchitecture.AArch64;
        }

        if (architectureName.StartsWith("x86_64", StringComparison.OrdinalIgnoreCase) ||
            architectureName.StartsWith("amd64", StringComparison.OrdinalIgnoreCase))
        {
            return UnwindArchitecture.X86_64;
        }

        return UnwindArchitecture.Unknown;
    }

    // Exposed for tests. The x86-64 mapping is the one piece of this file that
    // cannot be validated by any capture takeable on an Apple Silicon machine -
    // an emulated x86-64 process is sampled as the arm64 translator that is
    // really executing - so it is pinned directly instead.
    public static int PerfRegisterSlotForDwarfRegister(UnwindArchitecture architecture, int dwarfRegister)
    {
        return PerfRegisterForDwarf(architecture, dwarfRegister);
    }

    // DWARF register number -> the slot perf recorded it in. The two agree on
    // AArch64 and emphatically do not on x86-64; see this file's header.
    private static int PerfRegisterForDwarf(UnwindArchitecture architecture, int dwarfRegister)
    {
        if (architecture == UnwindArchitecture.AArch64)
        {
            return dwarfRegister <= 31 ? dwarfRegister : -1;
        }

        if (architecture != UnwindArchitecture.X86_64)
        {
            return -1;
        }

        switch (dwarfRegister)
        {
            case 0: return 0;   // RAX -> AX
            case 1: return 3;   // RDX -> DX
            case 2: return 2;   // RCX -> CX
            case 3: return 1;   // RBX -> BX
            case 4: return 4;   // RSI -> SI
            case 5: return 5;   // RDI -> DI
            case 6: return 6;   // RBP -> BP
            case 7: return 7;   // RSP -> SP
            default: break;
        }

        // R8..R15 are DWARF 8..15 and perf 16..23.
        if (dwarfRegister >= 8 && dwarfRegister <= 15)
        {
            return dwarfRegister + 8;
        }

        return -1;
    }

    private static int PerfProgramCounterRegister(UnwindArchitecture architecture)
    {
        if (architecture == UnwindArchitecture.AArch64)
        {
            return 32;
        }

        if (architecture == UnwindArchitecture.X86_64)
        {
            return 8;
        }

        return -1;
    }

    // Fills `frames` (leaf first) with the reconstructed call chain. Returns
    // false when the sample carries nothing to unwind - not when the unwind
    // merely stops early, which is expected and still yields the frames found
    // so far.
    public bool TryUnwind(int processId, PerfEventAttr attr, ref PerfSample sample, List<long> frames)
    {
        frames.Clear();

        if (this.architecture == UnwindArchitecture.Unknown || !sample.HasUserRegisters || !sample.HasUserStack)
        {
            return false;
        }

        Array.Clear(this.registers, 0, this.registers.Length);

        // Load the register file into DWARF numbering once, so every rule
        // lookup below is a direct index.
        for (int dwarfRegister = 0; dwarfRegister < 32; ++dwarfRegister)
        {
            int perfRegister = PerfRegisterForDwarf(this.architecture, dwarfRegister);
            if (perfRegister < 0)
            {
                continue;
            }

            int slot = attr.RegisterSlot(perfRegister);
            if (slot >= 0 && slot < sample.UserRegisters.Length)
            {
                this.registers[dwarfRegister] = sample.UserRegisters[slot];
            }
        }

        int programCounterSlot = attr.RegisterSlot(PerfProgramCounterRegister(this.architecture));
        ulong programCounter = programCounterSlot >= 0 && programCounterSlot < sample.UserRegisters.Length
            ? sample.UserRegisters[programCounterSlot]
            : sample.InstructionPointer;

        if (programCounter == 0)
        {
            return false;
        }

        ++this.SamplesUnwound;

        // The copied stack bytes correspond to addresses starting at the stack
        // pointer AT THE SAMPLE. Without this the copy is a bag of bytes with
        // no idea where it came from, and every read lands at the wrong offset
        // - which still succeeds, and still returns eight plausible bytes.
        int stackPointerForBase = this.architecture == UnwindArchitecture.AArch64
            ? AArch64StackPointerRegister
            : X86StackPointerRegister;

        sample.StackBase = this.registers[stackPointerForBase];

        if (sample.StackBase == 0)
        {
            return false;
        }

        bool returnAddressSigned = false;

        for (int depth = 0; depth < MaxFrames; ++depth)
        {
            frames.Add(unchecked((long)programCounter));
            ++this.FramesRecovered;

            // See this file's header: a return address points PAST its call, so
            // every frame but the innermost is looked up one byte back.
            long lookupAddress = unchecked((long)programCounter) - (depth == 0 ? 0 : 1);

            string modulePath;
            string moduleKey;
            long bias;
            if (!this.symbols.TryGetUnwindContext(processId, lookupAddress, out modulePath, out moduleKey, out bias))
            {
                ++this.SamplesStoppedNoFde;
                return true;
            }

            EhFrameFile ehFrame = this.GetOrLoadEhFrame(moduleKey, modulePath);
            if (ehFrame == null)
            {
                ++this.SamplesStoppedNoFde;
                return true;
            }

            long elfVirtualAddress = lookupAddress + bias;

            EhFrameFde fde;
            if (!ehFrame.TryFindFde(elfVirtualAddress, out fde))
            {
                ++this.SamplesStoppedNoFde;
                return true;
            }

            CfiRow row;
            if (!CfiEvaluator.TryEvaluate(ehFrame.Section, ehFrame.SectionVirtualAddress, in fde, elfVirtualAddress,
                    this.architecture == UnwindArchitecture.AArch64, out row))
            {
                ++this.SamplesStoppedNoFde;
                return true;
            }

            if (row.CfaIsExpression || row.CfaRegister < 0 || row.CfaRegister >= CfiEvaluator.MaxRegisters)
            {
                ++this.SamplesStoppedExpression;
                return true;
            }

            returnAddressSigned = returnAddressSigned ^ row.ReturnAddressSigned;

            ulong canonicalFrameAddress = unchecked(this.registers[row.CfaRegister] + (ulong)row.CfaOffset);

            int returnAddressRegister = this.architecture == UnwindArchitecture.AArch64
                ? AArch64ReturnAddressRegister
                : X86ReturnAddressColumn;

            if (fde.Cie != null && fde.Cie.ReturnAddressRegister < (ulong)CfiEvaluator.MaxRegisters)
            {
                returnAddressRegister = (int)fde.Cie.ReturnAddressRegister;
            }

            // A LEAF FUNCTION HAS NO RULE FOR ITS RETURN ADDRESS, and treating
            // that as "outermost frame" truncates every stack whose innermost
            // function is a leaf - which is most of them in a CPU profile,
            // because a leaf is where the samples land.
            //
            // Measured on the reference Rust capture: `hot_leaf_beta`, the
            // hottest function in the process, appeared in the flame tree in
            // exactly ONE place - directly under the root, with no callers -
            // while `iterator_chain` samples unwound seven frames correctly.
            // The difference is that a leaf never calls anything, so it never
            // saves the link register and `.eh_frame` says nothing about it.
            // The return address is simply still IN the link register, which
            // is the architecture's default rule for that column.
            //
            // Only honoured at depth 0. Any deeper frame reached a callee
            // through a call, so it must have saved its own return address,
            // and an absent rule there really is the end of the stack - the
            // live register at that point holds the LEAF's return address and
            // would send the walk back round a loop.
            ulong returnAddress;
            CfiRegisterRule returnAddressRule = row.RegisterRules[returnAddressRegister];

            if (returnAddressRule.Kind == CfiRegisterRuleKind.Undefined)
            {
                if (depth != 0)
                {
                    return true;
                }

                returnAddress = this.registers[returnAddressRegister];
            }
            else if (!this.TryApplyRule(in row, returnAddressRegister, canonicalFrameAddress, ref sample, out returnAddress))
            {
                ++this.SamplesStoppedOffStack;
                return true;
            }

            if (returnAddress == 0)
            {
                // The outermost frame: the thread entry point's return address
                // rule resolves to zero.
                return true;
            }

            if (this.architecture == UnwindArchitecture.AArch64 && returnAddressSigned)
            {
                returnAddress &= AArch64AddressMask;
            }

            // Registers the NEXT frame may need. Only the callee-saved ones
            // matter for unwinding, and computing all 32 per frame would be
            // pure cost - but the frame pointer specifically must be carried,
            // since x86-64 frames whose CFA is expressed against RBP depend on
            // it.
            // The caller's own link register, where a rule describes it. Kept
            // current so that if the caller is itself reached at a point where
            // its return address is still in a register, the value is the
            // CALLER's and not the leaf's.
            ulong restoredReturnAddressRegister;
            if (returnAddressRule.Kind != CfiRegisterRuleKind.Undefined
                && this.TryApplyRule(in row, returnAddressRegister, canonicalFrameAddress, ref sample, out restoredReturnAddressRegister))
            {
                this.registers[returnAddressRegister] = restoredReturnAddressRegister;
            }

            int framePointerRegister = this.architecture == UnwindArchitecture.AArch64
                ? AArch64FramePointerRegister
                : X86FramePointerRegister;

            ulong nextFramePointer;
            if (this.TryApplyRule(in row, framePointerRegister, canonicalFrameAddress, ref sample, out nextFramePointer))
            {
                this.registers[framePointerRegister] = nextFramePointer;
            }

            // By definition, the caller's stack pointer IS the CFA.
            int stackPointerRegister = this.architecture == UnwindArchitecture.AArch64
                ? AArch64StackPointerRegister
                : X86StackPointerRegister;

            // A CFA that does not advance means the unwind is not making
            // progress and would loop until the frame cap.
            if (canonicalFrameAddress <= this.registers[stackPointerRegister] && depth > 0)
            {
                return true;
            }

            this.registers[stackPointerRegister] = canonicalFrameAddress;
            programCounter = returnAddress;
        }

        return true;
    }

    private bool TryApplyRule(in CfiRow row, int registerNumber, ulong canonicalFrameAddress, ref PerfSample sample, out ulong value)
    {
        value = 0;

        if (registerNumber < 0 || registerNumber >= CfiEvaluator.MaxRegisters)
        {
            return false;
        }

        CfiRegisterRule rule = row.RegisterRules[registerNumber];

        switch (rule.Kind)
        {
            case CfiRegisterRuleKind.Undefined:
                // For the return-address column this is the top of the stack.
                // Reported as a zero rather than a failure so the caller ends
                // the walk cleanly.
                value = 0;
                return true;

            case CfiRegisterRuleKind.SameValue:
                value = this.registers[registerNumber];
                return true;

            case CfiRegisterRuleKind.AtCfaOffset:
                return TryReadStack(ref sample, unchecked(canonicalFrameAddress + (ulong)rule.Offset), out value);

            case CfiRegisterRuleKind.ValueAtCfaOffset:
                value = unchecked(canonicalFrameAddress + (ulong)rule.Offset);
                return true;

            case CfiRegisterRuleKind.InRegister:
                if (rule.Offset < 0 || rule.Offset >= CfiEvaluator.MaxRegisters)
                {
                    return false;
                }

                value = this.registers[rule.Offset];
                return true;

            default:
                return false;
        }
    }

    // Every memory read the unwind performs goes through here. The copied
    // window is the ONLY memory available, so a read outside it ends the walk -
    // which is exactly what should happen when the stack ran deeper than perf
    // was asked to copy.
    private static bool TryReadStack(ref PerfSample sample, ulong address, out ulong value)
    {
        value = 0;

        if (address < sample.StackBase)
        {
            return false;
        }

        ulong offset = address - sample.StackBase;
        if (offset + 8 > (ulong)sample.UserStack.Length)
        {
            return false;
        }

        value = BinaryPrimitives.ReadUInt64LittleEndian(sample.UserStack.Slice((int)offset, 8));
        return true;
    }

    private EhFrameFile GetOrLoadEhFrame(string moduleKey, string modulePath)
    {
        EhFrameFile cached;
        if (this.ehFrameByModuleKey.TryGetValue(moduleKey, out cached))
        {
            return cached;
        }

        if (this.modulesWithoutEhFrame.Contains(moduleKey))
        {
            return null;
        }

        string sectionError;
        ElfSections sections = ElfSections.TryLoad(modulePath, out sectionError);

        string ehFrameError;
        EhFrameFile ehFrame = sections != null ? EhFrameFile.TryLoad(sections, out ehFrameError) : null;

        if (ehFrame == null)
        {
            this.modulesWithoutEhFrame.Add(moduleKey);
            return null;
        }

        this.ehFrameByModuleKey[moduleKey] = ehFrame;
        return ehFrame;
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.Perf.Dwarf)

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////
