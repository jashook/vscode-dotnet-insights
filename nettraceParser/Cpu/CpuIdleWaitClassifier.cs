////////////////////////////////////////////////////////////////////////////////
// Module: CpuIdleWaitClassifier.cs
//
// Notes:
// C# port of dotnetInsights/media/snapshotGcStats.js's
// CPU_TIMELINE_WAIT_TYPE_PREFIXES/CPU_TIMELINE_WAIT_EXACT_NAMES/
// isKnownCpuIdleWaitLeafMethodName - same list, same "known BCL/CLR/interop
// blocking primitive, not a fuzzy 'contains Wait' match" heuristic (see that
// file's own comment for the full rationale: undercounts rather than
// misclassifies). Duplicated here (not shared source) because the frontend
// list is plain JS bundled into the webview and this is a separate C#
// process - keep the two in sync by hand when either changes.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.NetTrace.Cpu {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public static class CpuIdleWaitClassifier
{
    private static readonly string[] WaitTypePrefixes = new string[]
    {
        "System.Threading.WaitHandle.",
        "System.Threading.Monitor.",
        "System.Threading.SemaphoreSlim.",
        "System.Threading.Semaphore.",
        "System.Threading.ManualResetEventSlim.",
        "System.Threading.ManualResetEvent.",
        "System.Threading.AutoResetEvent.",
        "System.Threading.LowLevelLifoSemaphore.",
        "System.Threading.LowLevelMonitor.",
        "System.Threading.SpinWait.",
        "System.Threading.SpinLock."
    };

    private static readonly string[] WaitExactNames = new string[]
    {
        "System.Threading.Thread.Sleep",
        "System.Threading.Thread.Join",
        "System.Threading.Tasks.Task.Wait",
        "System.Threading.Tasks.Task.InternalWait",
        "Interop+Sys.Read",
        "Interop+Sys.Write",
        "Interop+Sys.Poll"
    };

    public static bool IsKnownIdleWaitLeafMethodName(string rawName)
    {
        if (string.IsNullOrEmpty(rawName))
        {
            return false;
        }

        for (int prefixIndex = 0; prefixIndex < WaitTypePrefixes.Length; ++prefixIndex)
        {
            if (rawName.StartsWith(WaitTypePrefixes[prefixIndex], System.StringComparison.Ordinal))
            {
                return true;
            }
        }

        for (int exactIndex = 0; exactIndex < WaitExactNames.Length; ++exactIndex)
        {
            if (rawName == WaitExactNames[exactIndex])
            {
                return true;
            }
        }

        return rawName.Contains("PollGCWorker");
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.Cpu)
