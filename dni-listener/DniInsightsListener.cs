////////////////////////////////////////////////////////////////////////////////
// Module: DniInsightsListener.cs
//
// Notes:
//  Command line tool for automatically capturing managed traces.
////////////////////////////////////////////////////////////////////////////////

using System;
using System.CommandLine;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

namespace DniListener
{
    class DniInsightsListener
    {
        static async Task Main(string[] args)
        {
            List<string> collectionTypes = new List<string>()
            {
                "gc", "gc-alloc", "threads", "cpu-sample", "jit-events"
            };

            var rootCommand = new RootCommand("Tool for automatically collecting profiles when certain metrics have been hit.");
            var psCommand = new Command("ps", "List all managed applications pids");
            
            var collectCommand = new Command("collect", "Collect a trace.");
            var processIdOptions = new Option<int>(name: "--process-id", description: "Process ID to collect.", getDefaultValue: static () => -1);
            var durationInMsOption = new Option<int>(name: "--duration-ms", description: "Duration to collect in milliseconds, default is 60s (60000).", getDefaultValue: static () => 60 * 1000);
            var outputFileNameOption = new Option<string?>(name: "--output-filename", description: "Output name to write.", getDefaultValue: static() => null );
            
            var collectionArgument = new Argument<IEnumerable<string>>(name: "--collection-type", description: "Type of collection to do. For example, gc, gc-allocs, threads, ect. Multiple allowed. Run dni-listener list-collection-types for an acurate list.");

            collectCommand.Add(collectionArgument);
            collectCommand.Add(processIdOptions);
            collectCommand.Add(durationInMsOption);
            collectCommand.Add(outputFileNameOption);

            rootCommand.Add(psCommand);
            rootCommand.Add(collectCommand);

            psCommand.SetHandler(static () => {
                List<(int, string)> processes = DniPs.GetManagedProcesses();

                foreach ((int processId, string commandLine) in processes)
                {
                    Console.WriteLine($"{processId}    {commandLine}");
                }
            });

            collectCommand.SetHandler(static (int processIdOptions, 
                                              IEnumerable<string> collectionArgument,
                                              int durationInMs, 
                                              string? outputFileName) => {
                if (processIdOptions == -1)
                {
                    Console.WriteLine("Process ID is required.");
                    return;
                }

                if (!DniPs.IsManagedProcess(processIdOptions))
                {
                    Console.WriteLine("Process ID is either to a non-manged process, or a .NET application that does not support EventPipe < 2.0");
                    return;
                }

                bool collectGc = false;
                bool collectGcAllocs = false;
                bool collectClrThreads = false;
                bool collectCpuSample = false;
                bool collectJitEvents = false;

                foreach (string item in collectionArgument)
                {
                    if (item == "gc")
                    {
                        collectGc = true;
                    }
                    else if (item == "gc-alloc")
                    {
                        collectGcAllocs = true;
                    }
                    else if (item == "threads")
                    {
                        collectClrThreads = true;
                    }
                    else if (item == "cpu-sample")
                    {
                        collectCpuSample = true;
                    }
                    else if (item == "jit-events")
                    {
                        collectJitEvents = true;
                    }
                    else if (item == "--collection-type")
                    {
                        continue;
                    }
                    else
                    {
                        Console.WriteLine($"Unknown collection-type {item}. Please use list-types for valid types.");
                        return;
                    }
                }

                // Start collecting
                ProcessEventCollector collector = new ProcessEventCollector(processIdOptions,
                                                                            collectGc,
                                                                            collectGcAllocs,
                                                                            collectClrThreads,
                                                                            collectCpuSample,
                                                                            collectJitEvents,
                                                                            durationInMs,
                                                                            outputFileName,
                                                                            writeToStdOut: true);

                collector.Collect();
                
            }, processIdOptions, collectionArgument, durationInMsOption, outputFileNameOption);

            await rootCommand.InvokeAsync(args);
        }
    }
}