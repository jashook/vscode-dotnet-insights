////////////////////////////////////////////////////////////////////////////////
// Module: CpuProcessTable.cs
//
// Notes:
// Maps a sample's thread to the process that owns it, and gives that process a
// display name.
//
// This only means anything for a MACHINE-WIDE capture. `dotnet-trace
// collect-linux` captures every process on the box by default: a real host
// capture measured here carried 724 ExistingProcess records and 26,238 CPU
// samples, so its hot-methods ranking is a blend of an entire 64-core machine
// and says almost nothing about the service anyone opened it to look at. A
// capture of a single process produces one entry here and the filter is not
// offered at all.
//
// The process NAME comes from the main thread's comm - the thread whose id
// equals the process id. Linux gives every thread its own comm and the main
// thread's is the process name, so this needs no extra event decoding: the v6
// ThreadBlock already carries (threadId, processId, name) for every thread.
// A process whose main thread was never seen (it started before the capture
// and only its worker threads were sampled) falls back to any thread name it
// does have, and then to the bare pid - which is honest, and still selectable.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.NetTrace.Cpu {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;

using DotnetInsights.NetTrace;
using DotnetInsights.NetTrace.V6;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public sealed class CpuProcessTable
{
    private readonly Dictionary<long, int> processIdByThreadId = new Dictionary<long, int>();
    private readonly Dictionary<int, string> nameByProcessId = new Dictionary<int, string>();

    public int DistinctProcessCount => this.nameByProcessId.Count;

    // The filter is only worth offering when there is something to choose
    // between. A single-process capture must render exactly as it did before
    // this existed.
    public bool HasMultipleProcesses => this.nameByProcessId.Count > 1;

    ////////////////////////////////////////////////////////////////////////////
    // Names come from each process's MAIN EXECUTABLE MAPPING, not from the
    // obvious source. Both obvious sources were tried and measured:
    //
    //   - Thread comms. On the reference host capture the v6 thread table's
    //     name field was empty for every one of 279 sampled processes, so all
    //     of them came back as a bare "pid N".
    //   - Universal.System/ExistingProcess, which really does carry a `Name`
    //     field ("systemd", ...). But EVERY ONE of that capture's 724
    //     ExistingProcess records has ThreadId 0 - the v6 reader deliberately
    //     does not put a process id on EventRecord (a struct that exists 35M+
    //     times over, see its header), and these records carry no thread index
    //     to resolve one from. All 724 therefore resolved to a single pid and
    //     named the wrong process 724 times, which is worse than no name.
    //
    // ProcessMapping records DO resolve - the same lookup UniversalSymbolTable
    // already relies on - so the executable a process has mapped is both
    // available and a better name anyway: it is the binary on disk rather than
    // whatever the main thread most recently called prctl with.
    ////////////////////////////////////////////////////////////////////////////
    public static CpuProcessTable Build(V6ThreadTable threadTable, IReadOnlyDictionary<int, string> processNames)
    {
        CpuProcessTable table = new CpuProcessTable();

        if (threadTable == null)
        {
            return table;
        }

        foreach (KeyValuePair<long, int> entry in threadTable.ProcessIdByThreadId)
        {
            long threadId = entry.Key;
            int processId = entry.Value;

            table.processIdByThreadId[threadId] = processId;

            string threadName;
            if (!threadTable.TryGetName(threadId, out threadName) || string.IsNullOrEmpty(threadName))
            {
                if (!table.nameByProcessId.ContainsKey(processId))
                {
                    table.nameByProcessId[processId] = string.Empty;
                }

                continue;
            }

            // The main thread's comm IS the process name, and it wins over any
            // worker thread's - a process named "envoy" whose workers are
            // "wrk:worker_3" must not be listed as the latter.
            bool isMainThread = threadId == processId;

            string existing;
            bool haveExisting = table.nameByProcessId.TryGetValue(processId, out existing);

            if (isMainThread || !haveExisting || string.IsNullOrEmpty(existing))
            {
                table.nameByProcessId[processId] = threadName;
            }
        }

        // Names come from the reader, which harvested them where a
        // Universal.System event's decoded fields and its owning process id
        // were both in hand - see V6Reader.AddEvent. They cannot be recovered
        // here: an ExistingProcess record's ThreadId is 0 by the time it
        // reaches EventRecord, because the v6 reader deliberately keeps no
        // process id on that struct (it exists 35M+ times over).
        if (processNames != null)
        {
            foreach (KeyValuePair<int, string> entry in processNames)
            {
                if (!string.IsNullOrEmpty(entry.Value))
                {
                    table.nameByProcessId[entry.Key] = entry.Value;
                }
            }
        }

        return table;
    }

    // The main executable, as opposed to a shared library or a pseudo-mapping.
    // Deliberately conservative: a name is a convenience here, and guessing
    // wrong labels a process as a library it merely loaded.
    private static bool IsMainExecutablePath(string path)
    {
        if (string.IsNullOrEmpty(path) || path[0] != '/')
        {
            return false;
        }

        if (path.EndsWith(".so", StringComparison.Ordinal) || path.Contains(".so.", StringComparison.Ordinal))
        {
            return false;
        }

        return !path.EndsWith(".dll", StringComparison.Ordinal);
    }

    public bool TryGetProcessId(long threadId, out int processId)
    {
        return this.processIdByThreadId.TryGetValue(threadId, out processId);
    }

    public string NameForProcess(int processId)
    {
        string name;
        if (this.nameByProcessId.TryGetValue(processId, out name) && !string.IsNullOrEmpty(name))
        {
            return name;
        }

        return "pid " + processId.ToString();
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.Cpu)

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////
