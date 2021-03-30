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
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

internal static class ProcessNameHelper
{
    public static string GetProcessNameForPid(int processId)
    {
        string processName = "";

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
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
        }
        else
        {
            try
            {
                Process process = Process.GetProcessById(processId);

                processName = process.ProcessName;
            }
            catch (Exception e)
            {
                // Process died.
            }

            ProcessStartInfo info = new ProcessStartInfo();
            info.FileName = "ps";
            info.Arguments = "aux";
            info.RedirectStandardOutput = true;

            Process proc = Process.Start(info);
            proc.Start();

            string data = proc.StandardOutput.ReadToEnd();
            string[] lines = data.Split('\n');

            foreach (string line in lines)
            {
                if (line.Length == 0)
                {
                    continue;
                }

                string input = Regex.Split(line, @"[a-zA-Z]+\s+")[1];

                if (input.Length == 0) continue;

                string newInput = Regex.Split(input, @"\s+")[0];
                int pid = int.Parse(newInput);
                
                if (pid == processId)
                {
                    processName = Regex.Split(line, @"\s+[0-9]+:[0-9.]+\s+")[1];
                }
            }
        }

        return processName;
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////