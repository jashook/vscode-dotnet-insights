////////////////////////////////////////////////////////////////////////////////
// Module: TracepointProjector.cs
//
// Notes:
// Turns the scheduling and futex tracepoints in a capture into the two things
// the CPU view cannot answer: why a thread was NOT running, and which lock it
// was waiting on.
//
// This is the piece a CPU profile structurally cannot provide. `perf record -e
// cpu-clock` samples threads that are ON a CPU, so a thread blocked for the
// entire capture produces ZERO samples and is invisible - the reference Rust
// workload's deliberately-parked thread does exactly that. That is the
// opposite of the .NET runtime's own sampler, which fires on every managed
// thread whether it is running or not, making its sample counts a proxy for
// wall-clock time. Any threading analysis ported across that gap without
// changing its input is measuring a different quantity.
//
// Two sources, deliberately kept distinct because they answer different
// questions:
//
//   - `sched:sched_switch` fires when a thread leaves the CPU, IN THAT
//     THREAD'S CONTEXT - so the sample's callchain is the stack it blocked in.
//     Pairing each switch-out with the thread's next switch-in gives an
//     off-CPU interval attributable to a stack. `prev_state` says whether it
//     was descheduled while still runnable (preemption - CPU contention) or
//     because it actually blocked (a wait).
//
//   - `syscalls:sys_enter_futex` / `sys_exit_futex` bracket a real lock wait.
//     A userspace mutex only enters the kernel when it is CONTENDED, so every
//     pair here is contention by construction - which is precisely what
//     `lock:contention_*` does NOT give: those are KERNEL lock tracepoints and
//     say nothing about a std::sync::Mutex. Measured on this workload: four
//     threads hammering one mutex for ten seconds produced 39 lock:contention
//     events and thousands of futex waits.
//
// The futex pairing maps onto ContentionEvent exactly - a waiting thread, a
// duration, a lock identity (the futex word's address) and a stack - so the
// existing contention view and lock timeline render it with no new renderer.
// OwnerThreadId stays 0, which that model already documents as "unknown" and
// renders as such: the kernel does not tell the waiter who it is waiting for.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.Perf.Tracepoints {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;

using DotnetInsights.NetTrace;
using DotnetInsights.NetTrace.Contention;
using DotnetInsights.Perf.PerfData;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public enum OffCpuReason : byte
{
    Unknown = 0,

    // Descheduled while still runnable. The thread had work to do and the CPU
    // was taken from it - this is CPU oversubscription, not blocking, and
    // conflating the two makes a busy machine look like a blocked program.
    Preempted = 1,

    // An interruptible sleep: a lock, a condition variable, a timer, a socket.
    Sleeping = 2,

    // An uninterruptible sleep, characteristically disk I/O.
    UninterruptibleWait = 3,

    // A kernel thread sitting idle (TASK_IDLE - uninterruptible sleep flagged
    // TASK_NOLOAD, which is how rcu/kworker/ksoftirqd threads wait). Counted
    // separately because it is NOT the program blocking on anything: on a
    // machine-wide capture of this workload it was 79 SECONDS, which folded
    // into "blocked" would have been the largest single figure in the report
    // and would have meant nothing at all.
    Idle = 4,

    // A parked kernel thread.
    Parked = 5,

    // Stopped or traced.
    Stopped = 6,

    // Exiting or exited. Not a wait - the thread is never coming back, so the
    // interval it opens is closed only by the id being reused.
    Dead = 7
}

public struct OffCpuInterval
{
    public long ThreadId;
    public double StartMSec;
    public double DurationMSec;
    public int StackIndex;
    public OffCpuReason Reason;
}

public sealed class TracepointProjection
{
    public readonly List<OffCpuInterval> OffCpuIntervals = new List<OffCpuInterval>();
    public readonly List<ContentionEvent> ContentionEvents = new List<ContentionEvent>();

    // Thread id -> name, taken from sched_switch's own prev_comm/next_comm
    // fields. This is a better source than the COMM records: a switch names
    // BOTH threads involved, so it covers threads that existed before the
    // capture started and never re-execed - which is every thread of an
    // already-running process, and exactly the ones a COMM record is missing
    // for.
    public readonly Dictionary<long, string> ThreadNames = new Dictionary<long, string>();

    public long SchedSwitchCount;
    public long FutexWaitCount;
    public long UnpairedFutexEnters;
    public long UnpairedSwitchOuts;

    // Off-CPU milliseconds by the RAW prev_state value, kept so the state
    // decoding can be tuned from what a real kernel actually emits rather than
    // from a reading of task_state_array. Same discipline the CPU category
    // classifier follows: the residue is the feedback signal.
    public readonly Dictionary<long, double> OffCpuMSecByRawState = new Dictionary<long, double>();
}

public sealed class TracepointProjector
{
    ////////////////////////////////////////////////////////////////////////////
    // sched_switch's prev_state is NOT the raw task->__state bitmask, and
    // decoding it as one is wrong in a way that looks right.
    //
    // The kernel's __trace_sched_switch_state() reports `1 << (index - 1)`
    // where index is the position in task_state_array - the same table that
    // renders R/S/D/T/t/X/Z/P/I in /proc. So the values on the wire are a
    // one-hot encoding of a LETTER, not a set of state flags:
    //
    //     0x000  R  runnable, descheduled anyway  -> preemption
    //     0x001  S  interruptible sleep
    //     0x002  D  uninterruptible sleep
    //     0x004  T  stopped
    //     0x008  t  traced
    //     0x010  X  dead
    //     0x020  Z  zombie
    //     0x040  P  parked
    //     0x080  I  idle  (a kernel thread waiting, NOT the program blocking)
    //     0x100     preempted flag (TASK_REPORT_MAX)
    //
    // Read as raw task-state flags, 0x80 is TASK_DEAD and 0x40 is TASK_PARKED,
    // which is how an earlier version of this file classified them. Measured
    // against a real machine-wide capture, that put 79 SECONDS of idle kernel
    // threads into the "Unknown" bucket - and had the mask happened to match a
    // sleep bit instead, it would have reported them as the program blocking.
    ////////////////////////////////////////////////////////////////////////////

    private const long ReportedStateRunnable = 0x000;
    private const long ReportedStateInterruptible = 0x001;
    private const long ReportedStateUninterruptible = 0x002;
    private const long ReportedStateStopped = 0x004;
    private const long ReportedStateTraced = 0x008;
    private const long ReportedStateDead = 0x010;
    private const long ReportedStateZombie = 0x020;
    private const long ReportedStateParked = 0x040;
    private const long ReportedStateIdle = 0x080;
    private const long ReportedStatePreemptedFlag = 0x100;

    // futex op codes, after masking off FUTEX_PRIVATE_FLAG (128) and
    // FUTEX_CLOCK_REALTIME (256). Only the WAIT family is a lock wait; a WAKE
    // is the releasing side and takes no measurable time.
    private const long FutexOpMask = 0x7F;
    private const long FutexWait = 0;
    private const long FutexLockPi = 6;
    private const long FutexWaitBitset = 9;
    private const long FutexWaitRequeuePi = 11;

    private struct PendingFutex
    {
        public double StartMSec;
        public long LockAddress;
        public int StackIndex;
    }

    private struct PendingSwitchOut
    {
        public double StartMSec;
        public int StackIndex;
        public OffCpuReason Reason;
        public long RawState;
    }

    private readonly TraceEventFormat schedSwitchFormat;
    private readonly TraceEventFormat futexEnterFormat;
    private readonly TraceEventFormat futexExitFormat;

    private readonly Dictionary<long, PendingFutex> pendingFutexByThread = new Dictionary<long, PendingFutex>();
    private readonly Dictionary<long, PendingSwitchOut> pendingSwitchOutByThread = new Dictionary<long, PendingSwitchOut>();

    private readonly TracepointProjection projection = new TracepointProjection();

    public TracepointProjection Projection => this.projection;

    public bool HasSchedData => this.schedSwitchFormat != null;

    public bool HasFutexData => this.futexEnterFormat != null && this.futexExitFormat != null;

    public TracepointProjector(TraceEventFormats formats)
    {
        if (formats == null)
        {
            return;
        }

        this.schedSwitchFormat = formats.FindByName("sched:sched_switch");
        this.futexEnterFormat = formats.FindByName("syscalls:sys_enter_futex");
        this.futexExitFormat = formats.FindByName("syscalls:sys_exit_futex");
    }

    // Called once per tracepoint sample, in file order. Timestamps are
    // capture-relative milliseconds.
    public void Observe(int eventId, double relativeMSec, long threadId, int stackIndex, ReadOnlySpan<byte> raw)
    {
        if (this.schedSwitchFormat != null && eventId == this.schedSwitchFormat.Id)
        {
            this.ObserveSchedSwitch(relativeMSec, stackIndex, raw);
            return;
        }

        if (this.futexEnterFormat != null && eventId == this.futexEnterFormat.Id)
        {
            this.ObserveFutexEnter(relativeMSec, threadId, stackIndex, raw);
            return;
        }

        if (this.futexExitFormat != null && eventId == this.futexExitFormat.Id)
        {
            this.ObserveFutexExit(relativeMSec, threadId);
        }
    }

    private void ObserveSchedSwitch(double relativeMSec, int stackIndex, ReadOnlySpan<byte> raw)
    {
        long previousThreadId;
        long nextThreadId;
        long previousState;

        if (!this.schedSwitchFormat.TryReadInt64(raw, "prev_pid", out previousThreadId) ||
            !this.schedSwitchFormat.TryReadInt64(raw, "next_pid", out nextThreadId) ||
            !this.schedSwitchFormat.TryReadInt64(raw, "prev_state", out previousState))
        {
            return;
        }

        ++this.projection.SchedSwitchCount;

        string previousName;
        if (this.schedSwitchFormat.TryReadString(raw, "prev_comm", out previousName) && previousName.Length > 0)
        {
            this.projection.ThreadNames[previousThreadId] = previousName;
        }

        string nextName;
        if (this.schedSwitchFormat.TryReadString(raw, "next_comm", out nextName) && nextName.Length > 0)
        {
            this.projection.ThreadNames[nextThreadId] = nextName;
        }

        // The thread coming ON cpu closes whatever interval it was in. Done
        // first so a switch between two tracked threads is ordered correctly.
        PendingSwitchOut pending;
        if (this.pendingSwitchOutByThread.TryGetValue(nextThreadId, out pending))
        {
            this.pendingSwitchOutByThread.Remove(nextThreadId);

            OffCpuInterval interval = new OffCpuInterval();
            interval.ThreadId = nextThreadId;
            interval.StartMSec = pending.StartMSec;
            interval.DurationMSec = relativeMSec - pending.StartMSec;
            interval.StackIndex = pending.StackIndex;
            interval.Reason = pending.Reason;

            if (interval.DurationMSec > 0)
            {
                this.projection.OffCpuIntervals.Add(interval);

                double existingForState;
                this.projection.OffCpuMSecByRawState.TryGetValue(pending.RawState, out existingForState);
                this.projection.OffCpuMSecByRawState[pending.RawState] = existingForState + interval.DurationMSec;
            }
        }

        // The thread going OFF cpu opens one. Its callchain is this sample's,
        // because the tracepoint fires in the outgoing thread's own context.
        if (previousThreadId != 0)
        {
            PendingSwitchOut opened = new PendingSwitchOut();
            opened.StartMSec = relativeMSec;
            opened.StackIndex = stackIndex;
            opened.Reason = ClassifyState(previousState);
            opened.RawState = previousState;
            this.pendingSwitchOutByThread[previousThreadId] = opened;
        }
    }

    // Exposed for the diagnostic report only; the real path calls the private
    // one during projection.
    public static OffCpuReason ClassifyStateForDiagnostics(long previousState)
    {
        return ClassifyState(previousState);
    }

    private static OffCpuReason ClassifyState(long previousState)
    {
        // The preempted flag is set alongside nothing else - the kernel returns
        // TASK_REPORT_MAX outright when the switch was a preemption.
        if ((previousState & ReportedStatePreemptedFlag) != 0)
        {
            return OffCpuReason.Preempted;
        }

        switch (previousState)
        {
            case ReportedStateRunnable: return OffCpuReason.Preempted;
            case ReportedStateInterruptible: return OffCpuReason.Sleeping;
            case ReportedStateUninterruptible: return OffCpuReason.UninterruptibleWait;
            case ReportedStateStopped: return OffCpuReason.Stopped;
            case ReportedStateTraced: return OffCpuReason.Stopped;
            case ReportedStateDead: return OffCpuReason.Dead;
            case ReportedStateZombie: return OffCpuReason.Dead;
            case ReportedStateParked: return OffCpuReason.Parked;
            case ReportedStateIdle: return OffCpuReason.Idle;
            default: return OffCpuReason.Unknown;
        }
    }

    private void ObserveFutexEnter(double relativeMSec, long threadId, int stackIndex, ReadOnlySpan<byte> raw)
    {
        long operation;
        long lockAddress;

        if (!this.futexEnterFormat.TryReadInt64(raw, "op", out operation) ||
            !this.futexEnterFormat.TryReadInt64(raw, "uaddr", out lockAddress))
        {
            return;
        }

        long baseOperation = operation & FutexOpMask;

        if (baseOperation != FutexWait &&
            baseOperation != FutexWaitBitset &&
            baseOperation != FutexLockPi &&
            baseOperation != FutexWaitRequeuePi)
        {
            return;
        }

        PendingFutex pending = new PendingFutex();
        pending.StartMSec = relativeMSec;
        pending.LockAddress = lockAddress;
        pending.StackIndex = stackIndex;

        // A thread can only be inside one futex wait at a time, so an
        // overwrite here means the exit was missed - counted rather than
        // silently dropped.
        if (this.pendingFutexByThread.ContainsKey(threadId))
        {
            ++this.projection.UnpairedFutexEnters;
        }

        this.pendingFutexByThread[threadId] = pending;
    }

    private void ObserveFutexExit(double relativeMSec, long threadId)
    {
        PendingFutex pending;
        if (!this.pendingFutexByThread.TryGetValue(threadId, out pending))
        {
            return;
        }

        this.pendingFutexByThread.Remove(threadId);

        double durationMSec = relativeMSec - pending.StartMSec;
        if (durationMSec <= 0)
        {
            return;
        }

        ++this.projection.FutexWaitCount;

        // ContentionFlags is a CLR concept (managed vs native monitor). A futex
        // wait has no counterpart, so the default is used rather than inventing
        // a mapping that a reader would take at face value.
        this.projection.ContentionEvents.Add(new ContentionEvent(
            pending.StartMSec,
            durationMSec,
            default(ClrContentionFlags),
            threadId,
            pending.StackIndex,
            pending.LockAddress,
            0,
            0));
    }

    // Any wait still open when the capture ends. Counted, not fabricated: a
    // thread parked for the whole capture never produces an exit, and inventing
    // an end timestamp for it would report a duration the capture never
    // observed.
    public void Finish()
    {
        this.projection.UnpairedFutexEnters += this.pendingFutexByThread.Count;
        this.projection.UnpairedSwitchOuts += this.pendingSwitchOutByThread.Count;
    }

    public static string NameForReason(OffCpuReason reason)
    {
        switch (reason)
        {
            case OffCpuReason.Preempted: return "Preempted (runnable, CPU taken)";
            case OffCpuReason.Sleeping: return "Blocked / sleeping";
            case OffCpuReason.UninterruptibleWait: return "Uninterruptible wait (I/O)";
            case OffCpuReason.Idle: return "Idle kernel thread";
            case OffCpuReason.Parked: return "Parked kernel thread";
            case OffCpuReason.Stopped: return "Stopped / traced";
            case OffCpuReason.Dead: return "Exiting";
            default: return "Unknown";
        }
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.Perf.Tracepoints)

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////
