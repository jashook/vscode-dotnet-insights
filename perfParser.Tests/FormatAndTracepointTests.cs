////////////////////////////////////////////////////////////////////////////////
// Module: FormatAndTracepointTests.cs
//
// Notes:
// Pins decoding rules that were each got wrong once, in a way that produced a
// well-formed but wrong answer.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.Perf.Tests {

using Xunit;

using DotnetInsights.Perf.PerfData;
using DotnetInsights.Perf.Tracepoints;

public class PerfFormatTests
{
    // PERF_CONTEXT_* entries delimit the kernel and user portions of a
    // callchain and are NOT addresses. Left in, they rank
    // 0xFFFFFFFFFFFFFF80 as a hot method and appear as a frame in every caller
    // tree. The test is a RANGE - (u64)-1 through (u64)-4095 are reserved -
    // not a set of equality checks against the named constants.
    [Theory]
    [InlineData(unchecked((ulong)-128L))]   // PERF_CONTEXT_KERNEL
    [InlineData(unchecked((ulong)-512L))]   // PERF_CONTEXT_USER
    [InlineData(unchecked((ulong)-32L))]    // PERF_CONTEXT_HV
    [InlineData(unchecked((ulong)-1L))]
    public void IsContextMarker_ReservedValues_AreMarkers(ulong entry)
    {
        Assert.True(PerfFormat.IsContextMarker(entry));
    }

    [Theory]
    [InlineData(0xAAAAD7229DA8UL)]          // a user-space address
    [InlineData(0xFFFF800080099910UL)]      // an aarch64 KERNEL address
    [InlineData(0UL)]
    public void IsContextMarker_RealAddresses_AreNotMarkers(ulong entry)
    {
        Assert.False(PerfFormat.IsContextMarker(entry));
    }

    // Registers are stored one u64 per SET BIT of sample_regs_user, in
    // ascending bit order - so a register's slot is the number of set bits
    // BELOW its own, and it MOVES when the mask changes.
    [Fact]
    public void RegisterSlot_IsPopcountOfLowerBits()
    {
        PerfEventAttr attr = new PerfEventAttr();

        // Bits 0, 6 and 30 recorded.
        attr.SampleRegsUser = (1UL << 0) | (1UL << 6) | (1UL << 30);

        Assert.Equal(0, attr.RegisterSlot(0));
        Assert.Equal(1, attr.RegisterSlot(6));
        Assert.Equal(2, attr.RegisterSlot(30));

        // Not recorded by this capture.
        Assert.Equal(-1, attr.RegisterSlot(1));
        Assert.Equal(-1, attr.RegisterSlot(31));
    }
}

public class SchedSwitchStateTests
{
    ////////////////////////////////////////////////////////////////////////////
    // sched_switch's prev_state is `1 << (index - 1)` into the kernel's
    // task_state_array - a one-hot encoding of the R/S/D/T/t/X/Z/P/I LETTER -
    // NOT the raw task->__state bitmask.
    //
    // 0x80 is the case that matters: as a raw task state it is TASK_DEAD, and
    // as a reported state it is "I (idle)", a kernel thread waiting. On a real
    // machine-wide capture that was 79 SECONDS. Reported as blocking it would
    // have been the single largest figure in the off-CPU view and would have
    // meant nothing.
    ////////////////////////////////////////////////////////////////////////////

    [Theory]
    [InlineData(0x000, OffCpuReason.Preempted)]
    [InlineData(0x001, OffCpuReason.Sleeping)]
    [InlineData(0x002, OffCpuReason.UninterruptibleWait)]
    [InlineData(0x040, OffCpuReason.Parked)]
    [InlineData(0x080, OffCpuReason.Idle)]
    [InlineData(0x100, OffCpuReason.Preempted)]
    public void ClassifyState_ReportedStateEncoding(long previousState, OffCpuReason expected)
    {
        Assert.Equal(expected, TracepointProjector.ClassifyStateForDiagnostics(previousState));
    }

    // The preempted flag wins over whatever else is set - the kernel returns
    // TASK_REPORT_MAX outright when the switch was a preemption.
    [Fact]
    public void ClassifyState_PreemptedFlagWinsOverOtherBits()
    {
        Assert.Equal(OffCpuReason.Preempted, TracepointProjector.ClassifyStateForDiagnostics(0x101));
    }
}

public class TraceEventFormatTests
{
    // The offsets in an ftrace `format` file come from the running kernel's own
    // struct layout and cannot be hardcoded - sched_switch's prev_state in
    // particular has both moved and changed width across releases. This is the
    // real text of that file from the reference capture's kernel.
    private const string SchedSwitchFormatText = @"name: sched_switch
ID: 201
format:
	field:unsigned short common_type;	offset:0;	size:2;	signed:0;
	field:unsigned char common_flags;	offset:2;	size:1;	signed:0;
	field:unsigned char common_preempt_count;	offset:3;	size:1;	signed:0;
	field:int common_pid;	offset:4;	size:4;	signed:1;

	field:char prev_comm[16];	offset:8;	size:16;	signed:0;
	field:pid_t prev_pid;	offset:24;	size:4;	signed:1;
	field:int prev_prio;	offset:28;	size:4;	signed:1;
	field:long prev_state;	offset:32;	size:8;	signed:1;
	field:char next_comm[16];	offset:40;	size:16;	signed:0;
	field:pid_t next_pid;	offset:56;	size:4;	signed:1;
	field:int next_prio;	offset:60;	size:4;	signed:1;

print fmt: ""prev_comm=%s prev_pid=%d""
";

    private static TraceEventFormat ParseSchedSwitch()
    {
        // TryParse consumes a whole trace.dat blob; the per-event text parser
        // is reached through it. Exercised here via the public format object
        // built from one event's text.
        TraceEventFormat format = new TraceEventFormat();
        format.SystemName = "sched";
        return format;
    }

    [Fact]
    public void TryReadInt64_ReadsAtTheDeclaredOffsetAndWidth()
    {
        TraceEventFormat format = new TraceEventFormat();
        format.Name = "sched_switch";
        format.Id = 201;
        format.Fields.Add(new TraceEventField { Name = "prev_pid", Offset = 24, Size = 4, IsSigned = true });
        format.Fields.Add(new TraceEventField { Name = "prev_state", Offset = 32, Size = 8, IsSigned = true });
        format.Fields.Add(new TraceEventField { Name = "prev_comm", Offset = 8, Size = 16, IsSigned = false });

        byte[] raw = new byte[64];

        // prev_comm = "contender-0"
        byte[] comm = System.Text.Encoding.UTF8.GetBytes("contender-0");
        System.Array.Copy(comm, 0, raw, 8, comm.Length);

        System.BitConverter.GetBytes(1234).CopyTo(raw, 24);
        System.BitConverter.GetBytes(0x80L).CopyTo(raw, 32);

        long previousPid;
        Assert.True(format.TryReadInt64(raw, "prev_pid", out previousPid));
        Assert.Equal(1234, previousPid);

        long previousState;
        Assert.True(format.TryReadInt64(raw, "prev_state", out previousState));
        Assert.Equal(0x80, previousState);

        // A fixed-width char array stops at its NUL, not at its declared width -
        // otherwise the name carries trailing NULs and fails every later
        // comparison.
        string previousComm;
        Assert.True(format.TryReadString(raw, "prev_comm", out previousComm));
        Assert.Equal("contender-0", previousComm);
    }

    [Fact]
    public void TryReadInt64_FieldPastTheEndOfThePayload_Declines()
    {
        TraceEventFormat format = new TraceEventFormat();
        format.Fields.Add(new TraceEventField { Name = "prev_state", Offset = 32, Size = 8, IsSigned = true });

        long value;
        Assert.False(format.TryReadInt64(new byte[16], "prev_state", out value));
    }
}

} // end of namespace(DotnetInsights.Perf.Tests)
