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
using System.CommandLine;
using System.CommandLine.Invocation;
using System.Threading.Tasks;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////
    
public class Profiler
{
    public async Task<int> Main(string[] args)
    {
        var rootCommand = new RootCommand
        {
            new Option<string>(
                "-sha",
                description: "sha")
        };

        rootCommand.Description = "P99 Investigation";

        string shaValue = null;
        string branchName = null;

        string apiAccountName = null;
        string apiKey = null;
        string apiIpAddress = null;
        bool useCacheValue = true;

        // Note that the parameters of the handler method are matched according to the names of the options
        rootCommand.Handler = CommandHandler.Create<string, string, string, string, string, bool>((sha, branch, accountName, key, ipAddress, useCache) =>
        {
            shaValue = sha;
            branchName = branch;
            apiAccountName = accountName;
            apiKey = key;
            apiIpAddress = ipAddress;
            useCacheValue = useCache;
        });

        // Parse the incoming args and invoke the handler
        int success = await rootCommand.InvokeAsync(args);

        if (success != 0)
        {
            return success;
        }

    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

