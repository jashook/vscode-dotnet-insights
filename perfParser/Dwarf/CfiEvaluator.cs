////////////////////////////////////////////////////////////////////////////////
// Module: CfiEvaluator.cs
//
// Notes:
// Runs a CIE's and an FDE's call-frame instructions up to a target address and
// reports the unwind state there: where the Canonical Frame Address is, and
// where each register the caller needs was saved.
//
// The instruction stream is a state machine over ADDRESSES. Every instruction
// either advances a virtual program counter or edits the current row of rules,
// and the answer for a target address is whatever the rules say once the
// virtual pc has advanced past it. That is why this cannot be a lookup table:
// unwind state changes several times inside a single function - within a
// prologue the return address may be in a register, then on the stack, and the
// CFA moves as the frame is built - so asking at the wrong address gives a
// well-formed answer for a different instruction.
//
// Only the rules an unwinder actually consumes are kept: the CFA, and per
// register whether it is unchanged, saved at an offset from the CFA, or held
// in another register. DWARF expressions (DW_CFA_def_cfa_expression and
// friends) are recognised, SKIPPED CORRECTLY so the rest of the stream still
// parses, and reported as unsupported - which stops that one unwind rather
// than silently producing a frame from a stale rule. They are rare in compiler
// output for ordinary functions and common in hand-written assembly, which is
// exactly where a wrong answer would be least detectable.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.Perf.Dwarf {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public enum CfiRegisterRuleKind : byte
{
    // Not described. For the return-address column this means "this is the
    // outermost frame" and the unwind stops - which is the correct end, not a
    // failure.
    Undefined = 0,

    // Unchanged from the callee's value.
    SameValue = 1,

    // Saved in memory at CFA + Offset.
    AtCfaOffset = 2,

    // Its VALUE is CFA + Offset - the register holds an address computed from
    // the CFA, not a value stored there.
    ValueAtCfaOffset = 3,

    // Held in another register, named by Offset.
    InRegister = 4,

    // Described by a DWARF expression. Recognised and declined; see this
    // file's header.
    Expression = 5
}

public struct CfiRegisterRule
{
    public CfiRegisterRuleKind Kind;
    public long Offset;
}

public struct CfiRow
{
    // The CFA is `register CfaRegister + CfaOffset`, which for most
    // compiler-generated code is the stack pointer at the call site.
    public int CfaRegister;
    public long CfaOffset;
    public bool CfaIsExpression;

    public CfiRegisterRule[] RegisterRules;

    // AArch64 pointer authentication: when set, the return address in this
    // frame is SIGNED and its top bits are an authentication code, not part of
    // the address. Toggled by DW_CFA_AARCH64_negate_ra_state, which shares an
    // opcode with DW_CFA_GNU_window_save - the two are distinguished only by
    // the architecture, which is why the caller passes it in.
    public bool ReturnAddressSigned;

    public bool IsValid;
}

public static class CfiEvaluator
{
    // DWARF register numbers run to 95 on x86-64 and 127 on AArch64 counting
    // vector registers, none of which participate in unwinding. 128 covers
    // every general-purpose register either architecture describes.
    public const int MaxRegisters = 128;

    private const byte OpcodeHighMask = 0xC0;
    private const byte OpcodeLowMask = 0x3F;

    private const byte AdvanceLoc = 0x40;
    private const byte Offset = 0x80;
    private const byte Restore = 0xC0;

    public static bool TryEvaluate(
        ReadOnlySpan<byte> section,
        long sectionVirtualAddress,
        in EhFrameFde fde,
        long targetAddress,
        bool isAArch64,
        out CfiRow row)
    {
        row = new CfiRow();
        row.RegisterRules = new CfiRegisterRule[MaxRegisters];
        row.CfaRegister = -1;

        EhFrameCie cie = fde.Cie;
        if (cie == null)
        {
            return false;
        }

        long currentAddress = fde.PcBegin;

        // The CIE's initial instructions establish the state every FDE starts
        // from, and their result is ALSO the state DW_CFA_restore returns a
        // register to - so it has to be captured separately, not just applied.
        if (!RunInstructions(section, sectionVirtualAddress, cie.InitialInstructionsOffset, cie.InitialInstructionsLength,
                cie, targetAddress, isAArch64, ref currentAddress, ref row, null))
        {
            return false;
        }

        CfiRegisterRule[] initialRules = new CfiRegisterRule[MaxRegisters];
        Array.Copy(row.RegisterRules, initialRules, MaxRegisters);

        if (!RunInstructions(section, sectionVirtualAddress, fde.InstructionsOffset, fde.InstructionsLength,
                cie, targetAddress, isAArch64, ref currentAddress, ref row, initialRules))
        {
            return false;
        }

        row.IsValid = true;
        return true;
    }

    private static bool RunInstructions(
        ReadOnlySpan<byte> section,
        long sectionVirtualAddress,
        int instructionsOffset,
        int instructionsLength,
        EhFrameCie cie,
        long targetAddress,
        bool isAArch64,
        ref long currentAddress,
        ref CfiRow row,
        CfiRegisterRule[] initialRules)
    {
        if (instructionsOffset < 0 || instructionsLength < 0 || instructionsOffset + instructionsLength > section.Length)
        {
            return false;
        }

        DwarfReader reader = new DwarfReader(section, sectionVirtualAddress);
        reader.Position = instructionsOffset;
        int end = instructionsOffset + instructionsLength;

        List<CfiRow> savedStates = null;

        while (reader.Position < end)
        {
            byte opcode = reader.ReadUInt8();
            byte high = (byte)(opcode & OpcodeHighMask);
            byte low = (byte)(opcode & OpcodeLowMask);

            if (high == AdvanceLoc)
            {
                currentAddress += low * (long)cie.CodeAlignmentFactor;

                // Everything past the target belongs to a later instruction and
                // must not be applied - the state at the target is what the
                // rows say up to here.
                if (currentAddress > targetAddress)
                {
                    return true;
                }

                continue;
            }

            if (high == Offset)
            {
                ulong offsetValue = reader.ReadUleb128();
                SetRule(ref row, low, CfiRegisterRuleKind.AtCfaOffset, (long)offsetValue * cie.DataAlignmentFactor);
                continue;
            }

            if (high == Restore)
            {
                if (initialRules != null && low < MaxRegisters)
                {
                    row.RegisterRules[low] = initialRules[low];
                }

                continue;
            }

            switch (opcode)
            {
                case 0x00: // DW_CFA_nop
                    break;

                case 0x01: // DW_CFA_set_loc
                {
                    long location;
                    if (!reader.TryReadEncodedPointer(cie.FdePointerEncoding, 0, 0, out location))
                    {
                        return false;
                    }

                    currentAddress = location;
                    if (currentAddress > targetAddress)
                    {
                        return true;
                    }

                    break;
                }

                case 0x02: // DW_CFA_advance_loc1
                    currentAddress += reader.ReadUInt8() * (long)cie.CodeAlignmentFactor;
                    if (currentAddress > targetAddress)
                    {
                        return true;
                    }

                    break;

                case 0x03: // DW_CFA_advance_loc2
                    currentAddress += reader.ReadUInt16() * (long)cie.CodeAlignmentFactor;
                    if (currentAddress > targetAddress)
                    {
                        return true;
                    }

                    break;

                case 0x04: // DW_CFA_advance_loc4
                    currentAddress += reader.ReadUInt32() * (long)cie.CodeAlignmentFactor;
                    if (currentAddress > targetAddress)
                    {
                        return true;
                    }

                    break;

                case 0x05: // DW_CFA_offset_extended
                {
                    ulong registerNumber = reader.ReadUleb128();
                    ulong offsetValue = reader.ReadUleb128();
                    SetRule(ref row, (int)registerNumber, CfiRegisterRuleKind.AtCfaOffset, (long)offsetValue * cie.DataAlignmentFactor);
                    break;
                }

                case 0x06: // DW_CFA_restore_extended
                {
                    ulong registerNumber = reader.ReadUleb128();
                    if (initialRules != null && registerNumber < MaxRegisters)
                    {
                        row.RegisterRules[registerNumber] = initialRules[registerNumber];
                    }

                    break;
                }

                case 0x07: // DW_CFA_undefined
                    SetRule(ref row, (int)reader.ReadUleb128(), CfiRegisterRuleKind.Undefined, 0);
                    break;

                case 0x08: // DW_CFA_same_value
                    SetRule(ref row, (int)reader.ReadUleb128(), CfiRegisterRuleKind.SameValue, 0);
                    break;

                case 0x09: // DW_CFA_register
                {
                    ulong registerNumber = reader.ReadUleb128();
                    ulong sourceRegister = reader.ReadUleb128();
                    SetRule(ref row, (int)registerNumber, CfiRegisterRuleKind.InRegister, (long)sourceRegister);
                    break;
                }

                case 0x0A: // DW_CFA_remember_state
                {
                    if (savedStates == null)
                    {
                        savedStates = new List<CfiRow>();
                    }

                    CfiRow snapshot = row;
                    snapshot.RegisterRules = new CfiRegisterRule[MaxRegisters];
                    Array.Copy(row.RegisterRules, snapshot.RegisterRules, MaxRegisters);
                    savedStates.Add(snapshot);
                    break;
                }

                case 0x0B: // DW_CFA_restore_state
                {
                    if (savedStates != null && savedStates.Count > 0)
                    {
                        CfiRow restored = savedStates[savedStates.Count - 1];
                        savedStates.RemoveAt(savedStates.Count - 1);

                        row.CfaRegister = restored.CfaRegister;
                        row.CfaOffset = restored.CfaOffset;
                        row.CfaIsExpression = restored.CfaIsExpression;
                        row.ReturnAddressSigned = restored.ReturnAddressSigned;
                        Array.Copy(restored.RegisterRules, row.RegisterRules, MaxRegisters);
                    }

                    break;
                }

                case 0x0C: // DW_CFA_def_cfa
                    row.CfaRegister = (int)reader.ReadUleb128();
                    row.CfaOffset = (long)reader.ReadUleb128();
                    row.CfaIsExpression = false;
                    break;

                case 0x0D: // DW_CFA_def_cfa_register
                    row.CfaRegister = (int)reader.ReadUleb128();
                    row.CfaIsExpression = false;
                    break;

                case 0x0E: // DW_CFA_def_cfa_offset
                    row.CfaOffset = (long)reader.ReadUleb128();
                    break;

                case 0x0F: // DW_CFA_def_cfa_expression
                {
                    // Skipped by its declared length so the remaining stream
                    // still parses, but the CFA is now undescribable by this
                    // evaluator and any frame using it must be abandoned.
                    ulong expressionLength = reader.ReadUleb128();
                    reader.Skip((int)expressionLength);
                    row.CfaIsExpression = true;
                    break;
                }

                case 0x10: // DW_CFA_expression
                {
                    ulong registerNumber = reader.ReadUleb128();
                    ulong expressionLength = reader.ReadUleb128();
                    reader.Skip((int)expressionLength);
                    SetRule(ref row, (int)registerNumber, CfiRegisterRuleKind.Expression, 0);
                    break;
                }

                case 0x11: // DW_CFA_offset_extended_sf
                {
                    ulong registerNumber = reader.ReadUleb128();
                    long offsetValue = reader.ReadSleb128();
                    SetRule(ref row, (int)registerNumber, CfiRegisterRuleKind.AtCfaOffset, offsetValue * cie.DataAlignmentFactor);
                    break;
                }

                case 0x12: // DW_CFA_def_cfa_sf
                    row.CfaRegister = (int)reader.ReadUleb128();
                    row.CfaOffset = reader.ReadSleb128() * cie.DataAlignmentFactor;
                    row.CfaIsExpression = false;
                    break;

                case 0x13: // DW_CFA_def_cfa_offset_sf
                    row.CfaOffset = reader.ReadSleb128() * cie.DataAlignmentFactor;
                    break;

                case 0x14: // DW_CFA_val_offset
                {
                    ulong registerNumber = reader.ReadUleb128();
                    ulong offsetValue = reader.ReadUleb128();
                    SetRule(ref row, (int)registerNumber, CfiRegisterRuleKind.ValueAtCfaOffset, (long)offsetValue * cie.DataAlignmentFactor);
                    break;
                }

                case 0x15: // DW_CFA_val_offset_sf
                {
                    ulong registerNumber = reader.ReadUleb128();
                    long offsetValue = reader.ReadSleb128();
                    SetRule(ref row, (int)registerNumber, CfiRegisterRuleKind.ValueAtCfaOffset, offsetValue * cie.DataAlignmentFactor);
                    break;
                }

                case 0x16: // DW_CFA_val_expression
                {
                    ulong registerNumber = reader.ReadUleb128();
                    ulong expressionLength = reader.ReadUleb128();
                    reader.Skip((int)expressionLength);
                    SetRule(ref row, (int)registerNumber, CfiRegisterRuleKind.Expression, 0);
                    break;
                }

                case 0x2D:
                {
                    // One opcode, two meanings, decided by architecture:
                    // DW_CFA_GNU_window_save on SPARC, and
                    // DW_CFA_AARCH64_negate_ra_state on AArch64 - where it
                    // toggles whether the return address is signed with pointer
                    // authentication. Treating it as a no-op on AArch64 leaves
                    // the PAC bits in the address, which then resolves to
                    // nothing.
                    if (isAArch64)
                    {
                        row.ReturnAddressSigned = !row.ReturnAddressSigned;
                    }

                    break;
                }

                case 0x2E: // DW_CFA_GNU_args_size
                    reader.ReadUleb128();
                    break;

                case 0x2F: // DW_CFA_GNU_negative_offset_extended
                {
                    ulong registerNumber = reader.ReadUleb128();
                    ulong offsetValue = reader.ReadUleb128();
                    SetRule(ref row, (int)registerNumber, CfiRegisterRuleKind.AtCfaOffset, -(long)offsetValue * cie.DataAlignmentFactor);
                    break;
                }

                default:
                    // An unknown opcode has an unknown operand length, so the
                    // cursor cannot be advanced past it and everything after is
                    // unparseable. Stopping here keeps whatever was already
                    // established rather than applying garbage.
                    return false;
            }

            if (reader.Overran)
            {
                return false;
            }
        }

        return true;
    }

    private static void SetRule(ref CfiRow row, int registerNumber, CfiRegisterRuleKind kind, long offset)
    {
        if (registerNumber < 0 || registerNumber >= MaxRegisters)
        {
            return;
        }

        row.RegisterRules[registerNumber].Kind = kind;
        row.RegisterRules[registerNumber].Offset = offset;
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.Perf.Dwarf)

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////
