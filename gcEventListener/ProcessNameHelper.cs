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

public static class ProcessNameHelper
{
    public static string GetProcessNameForPid(int processId)
    {
        string returnValue = null;
        try
        {
            Process proc = Process.GetProcessById(processId);
            returnValue = proc.ProcessName;
        }
        catch (Exception)
        {

        }

        return returnValue;
    }

    public static string GetProcessCommandLineForPid(int processId, int maxLength = 128)
    {
        string processName = "";

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher("SELECT CommandLine FROM Win32_Process WHERE ProcessId = " + processId))
                using (ManagementObjectCollection objects = searcher.Get())
                {
                    processName = objects.Cast<ManagementBaseObject>().SingleOrDefault()?["CommandLine"]?.ToString();
                }

            }
            catch(Exception)
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
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            try
            {
                Process process = Process.GetProcessById(processId);

                processName = process.ProcessName;
            }
            catch (Exception)
            {
                // Process died.
                return null;
            }

            ProcessStartInfo info = new ProcessStartInfo();
            info.FileName = "ps";
            info.Arguments = "aux";
            info.RedirectStandardOutput = true;
            info.RedirectStandardError = true;

            Process proc = Process.Start(info);
            proc.Start();

            string data = proc.StandardOutput.ReadToEnd();

            if (!proc.HasExited)
            {
                proc.WaitForExit();
            }

            string[] lines = data.Split('\n');

            bool found = false;

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
                    found = true;
                    processName = Regex.Split(line, @"\s+[0-9]+:[0-9.]+\s+")[1];
                    break;
                }
            }

            if (!found)
            {
                return null;
            }
        }
        else
        {
            try
            {
                Process process = Process.GetProcessById(processId);

                processName = process.ProcessName;
            }
            catch (Exception)
            {
                // Process died.
            }

            throw new NotImplementedException();
        }

        if (processName.Length > maxLength)
        {
            processName = processName.Substring(0, maxLength);
        }

        return processName;
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////