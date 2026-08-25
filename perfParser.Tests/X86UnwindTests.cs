////////////////////////////////////////////////////////////////////////////////
// Module: X86UnwindTests.cs
//
// Notes:
// Validates the x86-64 DWARF unwind path, which is the one part of this project
// that no capture taken on the development machine can exercise.
//
// Measured, not assumed: an x86-64 Docker container on Apple Silicon reports
// `uname -m = x86_64` and maps its guest binaries at the x86-64 PIE base, but
// the kernel is arm64 and the code really executing is Rosetta's translation of
// it. Recording such a process from the host produced samples at
// 0x80000008f83c inside /run/rosetta/rosetta - the translator, not the program.
// PERF_SAMPLE_REGS_USER is defined per HOST architecture, so an arm64 kernel
// can never emit an x86-64 register file. There is no x86-64 capture to be had
// here at any amount of effort.
//
// What IS available is real x86-64 ELF binaries, and the x86-64 System V ABI's
// guarantee at every function entry: `call` pushes the return address, so on
// entry CFA = RSP + 8 and the return address is at CFA - 8. That is checkable
// against thousands of real functions, and it fails loudly if either the CFI
// evaluation or the DWARF register numbering is wrong.
//
// Set X86_ELF_FIXTURE to a real x86-64 shared object to run the ELF-backed
// tests; the register-mapping and synthetic-unwind tests need no fixture.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.Perf.Tests {

using System;
using System.Collections.Generic;
using System.IO;

using Xunit;

using DotnetInsights.Perf.Dwarf;
using DotnetInsights.Perf.PerfData;
using DotnetInsights.Perf.Symbols;

public class X86RegisterMappingTests
{
    ////////////////////////////////////////////////////////////////////////////
    // perf orders x86-64 registers AX,BX,CX,DX,SI,DI,BP,SP,IP,...  and DWARF
    // orders them RAX,RDX,RCX,RBX,RSI,RDI,RBP,RSP,R8...  Reading a CFI rule for
    // "register 3" out of perf slot 3 swaps RBX and RDX.
    //
    // The two orderings AGREE on AArch64, which is what makes this a bug that
    // could only ever appear on the architecture that needs DWARF unwinding
    // most - and the reason it is pinned here rather than left to a capture.
    ////////////////////////////////////////////////////////////////////////////

    [Theory]
    [InlineData(0, 0)]    // RAX -> AX
    [InlineData(1, 3)]    // RDX -> DX   (NOT 1)
    [InlineData(2, 2)]    // RCX -> CX
    [InlineData(3, 1)]    // RBX -> BX   (NOT 3)
    [InlineData(4, 4)]    // RSI -> SI
    [InlineData(5, 5)]    // RDI -> DI
    [InlineData(6, 6)]    // RBP -> BP
    [InlineData(7, 7)]    // RSP -> SP
    [InlineData(8, 16)]   // R8  -> perf 16
    [InlineData(15, 23)]  // R15 -> perf 23
    public void X86_64_DwarfRegisterMapsToThePerfSlot(int dwarfRegister, int expectedPerfSlot)
    {
        Assert.Equal(expectedPerfSlot, DwarfUnwinder.PerfRegisterSlotForDwarfRegister(UnwindArchitecture.X86_64, dwarfRegister));
    }

    // AArch64's numberings coincide - x0..x30 are DWARF 0..30 and SP is 31.
    [Theory]
    [InlineData(0, 0)]
    [InlineData(29, 29)]   // frame pointer
    [InlineData(30, 30)]   // link register / return address column
    [InlineData(31, 31)]   // stack pointer
    public void AArch64_DwarfRegisterNumbersMatchPerfSlots(int dwarfRegister, int expectedPerfSlot)
    {
        Assert.Equal(expectedPerfSlot, DwarfUnwinder.PerfRegisterSlotForDwarfRegister(UnwindArchitecture.AArch64, dwarfRegister));
    }

    [Fact]
    public void ArchitectureIsParsedFromTheCaptureHeader()
    {
        Assert.Equal(UnwindArchitecture.X86_64, DwarfUnwinder.ParseArchitecture("x86_64"));
        Assert.Equal(UnwindArchitecture.X86_64, DwarfUnwinder.ParseArchitecture("amd64"));
        Assert.Equal(UnwindArchitecture.AArch64, DwarfUnwinder.ParseArchitecture("aarch64"));
        Assert.Equal(UnwindArchitecture.AArch64, DwarfUnwinder.ParseArchitecture("arm64"));

        // An architecture this unwinder has no register mapping for must be
        // declined, not guessed at - a wrong mapping walks a garbage stack
        // confidently.
        Assert.Equal(UnwindArchitecture.Unknown, DwarfUnwinder.ParseArchitecture("riscv64"));
        Assert.Equal(UnwindArchitecture.Unknown, DwarfUnwinder.ParseArchitecture(null));
    }
}

public class X86EhFrameTests
{
    private const string FixtureEnvironmentVariable = "X86_ELF_FIXTURE";

    private static string FixturePath()
    {
        string path = Environment.GetEnvironmentVariable(FixtureEnvironmentVariable);
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        path = path.Replace("~", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        return File.Exists(path) ? path : null;
    }

    private const int X86StackPointerRegister = 7;
    private const int X86ReturnAddressColumn = 16;

    [Fact]
    public void EhFrame_OfARealX86_64Binary_Parses()
    {
        string fixturePath = FixturePath();
        if (fixturePath == null)
        {
            return;
        }

        string sectionError;
        ElfSections sections = ElfSections.TryLoad(fixturePath, out sectionError);
        Assert.NotNull(sections);

        string ehFrameError;
        EhFrameFile ehFrame = EhFrameFile.TryLoad(sections, out ehFrameError);
        Assert.True(ehFrame != null, ehFrameError);
        Assert.True(ehFrame.FdeCount > 100, "expected a real binary to describe many functions, got " + ehFrame.FdeCount);
    }

    ////////////////////////////////////////////////////////////////////////////
    // GROUND-TRUTH DIFF against readelf's own CFI interpretation.
    //
    // This replaced an "assert the x86-64 ABI invariant at every function
    // entry" test, which FAILED at 75% - and was wrong to. readelf agreed with
    // this evaluator exactly on every one of the disagreeing addresses: they
    // are split/cold function fragments (0x2880d..0x2881b is fourteen bytes)
    // whose CFI legitimately begins with an already-established frame, so
    // CFA is rbp+16 rather than rsp+8 at their first address. The invariant
    // was the wrong oracle; the reference implementation is the right one.
    //
    // Regenerate the fixture with:
    //   readelf --debug-dump=frames-interp <binary> > <binary>.frames-interp.txt
    //
    // and point X86_ELF_FIXTURE at the binary. The dump is expected beside it
    // as "<binary>.frames-interp.txt".
    //
    // readelf's format:
    //   00000018 ... FDE cie=00000000 pc=0000000000028000..00000000000283e0
    //      LOC           CFA      rbx   rbp   ra
    //   0000000000028000 rsp+16   c-56  c-16  c-8
    //
    // The column set differs per FDE, so the header row is what says where CFA
    // and ra are.
    ////////////////////////////////////////////////////////////////////////////
    [Fact]
    public void CfiEvaluation_MatchesReadelfRowForRow()
    {
        string fixturePath = FixturePath();
        if (fixturePath == null)
        {
            return;
        }

        string groundTruthPath = fixturePath + ".frames-interp.txt";
        if (!File.Exists(groundTruthPath))
        {
            return;
        }

        string sectionError;
        ElfSections sections = ElfSections.TryLoad(fixturePath, out sectionError);
        Assert.NotNull(sections);

        string ehFrameError;
        EhFrameFile ehFrame = EhFrameFile.TryLoad(sections, out ehFrameError);
        Assert.NotNull(ehFrame);

        int compared = 0;
        int matched = 0;
        List<string> mismatches = new List<string>();

        int cfaColumn = -1;
        int returnAddressColumn = -1;

        foreach (string rawLine in File.ReadLines(groundTruthPath))
        {
            string line = rawLine.TrimEnd();

            if (line.Contains(" LOC ") || line.TrimStart().StartsWith("LOC", StringComparison.Ordinal))
            {
                // A column header: remember where CFA and ra sit.
                string[] headers = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                cfaColumn = Array.IndexOf(headers, "CFA");
                returnAddressColumn = Array.IndexOf(headers, "ra");
                continue;
            }

            if (cfaColumn < 0 || returnAddressColumn < 0 || line.Length == 0 || line.Contains("FDE") || line.Contains("CIE"))
            {
                continue;
            }

            // readelf spells a register rule "r9 (r9)" - TWO whitespace
            // separated tokens - so a naive split shifts every column after
            // the first such rule. That artifact, not any decoding
            // disagreement, produced the only 8 "mismatches" this test ever
            // reported: at 0x450f4 it read r14's "c+32" as the ra column, and
            // at 0x135d0f it read a shifted "u" where readelf really prints
            // "c-8". Both were verified against readelf's RAW opcode dump.
            string[] parts = StripParentheticals(line).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length <= returnAddressColumn)
            {
                continue;
            }

            long location;
            if (!long.TryParse(parts[0], System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out location))
            {
                continue;
            }

            string expectedCfa = parts[cfaColumn];
            string expectedReturnAddress = parts[returnAddressColumn];

            // A CFA described by a DWARF expression is declined by this
            // evaluator by design; readelf prints "exp".
            if (expectedCfa == "exp")
            {
                continue;
            }

            EhFrameFde fde;
            if (!ehFrame.TryFindFde(location, out fde))
            {
                continue;
            }

            CfiRow row;
            if (!CfiEvaluator.TryEvaluate(ehFrame.Section, ehFrame.SectionVirtualAddress, in fde, location, false, out row))
            {
                continue;
            }

            ++compared;

            string actualCfa = row.CfaIsExpression
                ? "exp"
                : DwarfRegisterName(row.CfaRegister) + (row.CfaOffset >= 0 ? "+" : "-") + Math.Abs(row.CfaOffset);

            CfiRegisterRule returnAddressRule = row.RegisterRules[X86ReturnAddressColumn];
            string actualReturnAddress = DescribeRule(returnAddressRule);

            if (actualCfa == expectedCfa && actualReturnAddress == expectedReturnAddress)
            {
                ++matched;
            }
            else if (mismatches.Count < 6)
            {
                mismatches.Add("0x" + location.ToString("x") + ": readelf cfa=" + expectedCfa + " ra=" + expectedReturnAddress
                    + " | ours cfa=" + actualCfa + " ra=" + actualReturnAddress);
            }
        }

        Assert.True(compared > 500, "only compared " + compared + " rows against readelf");
        Assert.True(matched == compared,
            (compared - matched) + " of " + compared + " CFI rows disagreed with readelf: " + string.Join(" | ", mismatches));
    }

    // Removes the " (name)" that readelf appends to a register-valued rule, so
    // one rule is one token.
    private static string StripParentheticals(string line)
    {
        System.Text.StringBuilder builder = new System.Text.StringBuilder(line.Length);
        int depth = 0;

        for (int index = 0; index < line.Length; ++index)
        {
            char current = line[index];

            if (current == '(')
            {
                ++depth;
                continue;
            }

            if (current == ')')
            {
                if (depth > 0)
                {
                    --depth;
                }

                continue;
            }

            if (depth == 0)
            {
                builder.Append(current);
            }
        }

        return builder.ToString();
    }

    // readelf's spelling of the x86-64 DWARF register numbers.
    private static string DwarfRegisterName(int dwarfRegister)
    {
        switch (dwarfRegister)
        {
            case 0: return "rax";
            case 1: return "rdx";
            case 2: return "rcx";
            case 3: return "rbx";
            case 4: return "rsi";
            case 5: return "rdi";
            case 6: return "rbp";
            case 7: return "rsp";
            default: return "r" + dwarfRegister;
        }
    }

    // readelf's spelling of a register rule: "c-8" saved at CFA-8, "r6" held in
    // another register, "u" undefined, "s" same value.
    private static string DescribeRule(CfiRegisterRule rule)
    {
        switch (rule.Kind)
        {
            case CfiRegisterRuleKind.AtCfaOffset:
                return "c" + (rule.Offset >= 0 ? "+" : "-") + Math.Abs(rule.Offset);

            case CfiRegisterRuleKind.ValueAtCfaOffset:
                return "v" + (rule.Offset >= 0 ? "+" : "-") + Math.Abs(rule.Offset);

            case CfiRegisterRuleKind.InRegister:
                return "r" + rule.Offset;

            case CfiRegisterRuleKind.SameValue:
                return "s";

            case CfiRegisterRuleKind.Expression:
                return "exp";

            default:
                return "u";
        }
    }
}

} // end of namespace(DotnetInsights.Perf.Tests)
