////////////////////////////////////////////////////////////////////////////////
// Module: ProcessNameHelper.cs
//
// Notes:
// Return the process name and arguments for a pid.
//
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Diagnostics;
using System.Linq;
using System.Management;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

internal static class ProcessNameHelper
{
    public static string GetProcessNameForPid(int processId)
    {
        string processName = "";

        try
        {
            Process proc = Process.GetProcessById(processId);

            using (ManagementObjectSearcher searcher = new ManagementObjectSearcher("SELECT CommandLine FROM Win32_Process WHERE ProcessId = " + proc.Id))
            using (ManagementObjectCollection objects = searcher.Get())
            {
                processName = objects.Cast<ManagementBaseObject>().SingleOrDefault()?["CommandLine"]?.ToString();
            }

        }
        catch(Exception e)
        {

        }

        if (string.IsNullOrWhiteSpace(processName))
        {
            processName = "";
            Console.WriteLine($"Unable to get info for {processId}");
        }
        else
        {
            processName = processName.Replace("\"", "");
            processName = processName.Replace("\\", "\\\\");
        }

        return processName;
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////