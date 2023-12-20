////////////////////////////////////////////////////////////////////////////////
// Module: Profiler.cs
//
// Notes:
// Setup a profiler for the process id passed
////////////////////////////////////////////////////////////////////////////////

namespace ev31 {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.Diagnostics;
using System.Threading.Tasks;

using Microsoft.Diagnostics.NETCore.Client;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////
    
public class Profiler
{
    public static async Task<int> Main(string[] args)
    {
        var rootCommand = new RootCommand
        {
            new Option<string>(
                "-pid",
                description: "pid for the process to profile")
        };

        rootCommand.Description = ".NET Insights Profiler";

        int processId = 0;

        // Note that the parameters of the handler method are matched according to the names of the options
        rootCommand.Handler = CommandHandler.Create<string>((pid) =>
        {
            if (string.IsNullOrEmpty(pid)) return -2;

            IEnumerable<int> processIds = DiagnosticsClient.GetPublishedProcesses();

            int passedPid = 0;
            try
            {
                passedPid = int.Parse(pid);
            }
            catch
            {
                return -3;
            }

            bool found = false;
            foreach (int id in processIds)
            {
                if (passedPid == id)
                {
                    found = true;
                    break;
                }
            }

            if (!found) return -1;
            processId = passedPid;

            return 0;
        });

        // Parse the incoming args and invoke the handler
        int success = await rootCommand.InvokeAsync(args);

        if (success != 0)
        {
            return success;
        }

        ProfilerSetup setup = new ProfilerSetup(processId);
        // Now that the profiler has been setup
        // we will wait for the duration of the process attached to to 
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // namepsace ev31

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////
