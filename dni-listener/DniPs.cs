////////////////////////////////////////////////////////////////////////////////
// Module: DniPs.cs
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Diagnostics;

using Microsoft.Diagnostics.NETCore.Client;

using DotnetInsights;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

namespace DniListener
{
    static class DniPs
    {
        public static List<(int, string)> GetManagedProcesses()
        {
            IEnumerable<int> processIds = DiagnosticsClient.GetPublishedProcesses();

            List<(int, string)> mangedProcesses = new();
            foreach (int processId in processIds)
            {
                string processName = ProcessNameHelper.GetProcessCommandLineForPid(processId);

                if (!string.IsNullOrEmpty(processName))
                {
                    mangedProcesses.Add((processId, processName));
                }
            }

            return mangedProcesses;
        }

        public static bool IsManagedProcess(int pid)
        {
            var processes = DniPs.GetManagedProcesses();

            foreach ((int processId, _) in processes)
            {
                if (processId == pid)
                {
                    return true;
                }
            }

            return false;
        }
    }
}