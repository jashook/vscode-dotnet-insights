# .NET Insights

**This extension uses Unsigned tools. This extension is not meant to be used in a production environment**

An extension for drilling into .NET MSIL and Jitted ASM for managed executables (PE Files). This is a cross platform extension that works on Linux (Ubuntu) OSX and Windows x64. The extension has a few different quality of life improvements. It is intended as an extension to improve .NET development in general. Please see the full feature list below.

Future work to include Linux arm64. Currently 32-bit support is not expected to be worked on; however, feel free to contribute.

# Issues

There are known issues with the extension. See [Bugs](https://github.com/jashook/vscode-dotnet-insights/issues?q=is%3Aissue+is%3Aopen+label%3Abug) for more information.

# Overview

## TOC

1. [GC Monitoring](#GC-Monitoring)
2. [IL / DASM on Save](#IL-/-DASM-on-Save)
3. [Inspect PE File](#Inspect-PE-File)

# GC Monitoring

The extension allows for monitoring all .NET Core applications that are running on the target machine. The extension will use TraceEvent to connect to all .NET Core applications 3.x+ to receive GC allocation, start and stop events. It will then compile the information per heap and display it by process.

![GC Monitoring Gif](https://raw.githubusercontent.com/jashook/vscode-dotnet-insights/master/dotnetInsights/media/gcMonitoring.gif)

## GC Monitoring Usage

1. `ctrl (cmd) + shift + p` to get to enter a command
2. Start GC Monitoring

At this point you have started a background server which will monitor and communicate back to the .NET Insights extension. **This is expensive both for VScode and ALL running .NET Applications on the machine.** 

### **After monitoring the Stop GC Monitoring Command via ctrl (cmd) + shift + p!**

Navigate to the `---` icon on the activity bar. Each managed .NET Core process which has GCs will propogate in this view.

When a process is selected a custom view will come up with the GC Statistics for the process during the monitor window. *Please remember that the GCs display are **only** post the monitor command, and pre the stop monitoring command. This is defined to be the monitoring window.* While selected, real time information about GCs will continue to update in realtime.

<br/>

![GC Monitoring](https://raw.githubusercontent.com/jashook/vscode-dotnet-insights/master/dotnetInsights/media/gcMonitoring.png)

## Saving GC Files for later

`<ctrl> s` or `<cmd> s` will save the file. Currently this is saved in a known location which can be seen
via the extension's output logs.

![Saving GC Info](https://raw.githubusercontent.com/jashook/vscode-dotnet-insights/master/dotnetInsights/media/saveGcInfo.gif)

## Reading Raw Data from Perfview

Perfview is an extremely powerful tool for viewing GC data. While the extension will not go into depth on how to collect via perfview, we can instead understand how to capture raw gc data from perfview and visualize it in .NET Insights.

An existing or future perfview collection which has GC data can be exported as raw data. An example of this would be the following:
`
1. Collect gc `perview.exe collect /AcceptEula /NoGui /GCCollectOnly`
2. Open the `etl` file created and navigate to `Memory Groups/GCStats`
    - GCStats contains all Managed (.NET applications) that emitted events during the collection window. Navigate to the process you are interested in
3. Click the link `RAW Data XML file(for debugging)`
4. Save the file with an extension `.gcInfo` and load the file in vscode

You will see the gc events visualized similarly to a collection done using the extension. Note that there are a few caveats to this. First, there is no allocation information in the same way there is allocation information with the extension. Second, some events are recorded differently with Perfview and may not have the same fidelity.

![GC info from perview](https://raw.githubusercontent.com/jashook/vscode-dotnet-insights/master/dotnetInsights/media/perfviewVisualization.gif)

<br/>

# IL / DASM on Save

**C# Extension is required**.

Showing IL/Dasm on save allows viewing the IL/ASM for a generated file quickly without inspecting the PE file. It specifically is useful when attempting to optimize a particular method. It is possible to view the IL as it is being changed, and then the optimized ASM that would be produced. Another slightly advanced feature is being able to view the JIT's output for the method JITTed. This advanced feature allows investigating what happened while JITTing the method, and allows understanding why a particular optimization may or may have not fired.

<br/>

![Example IL/ASM](https://raw.githubusercontent.com/jashook/vscode-dotnet-insights/master/dotnetInsights/media/ilAsm.gif)

<br/>

# Inspect PE File

1. Select a managed PE file. This is generally a .NET DLL under the `${workspaceFolder}/bin/Release/application.dll`
2. By default the .NET MSIL will be dumped and opened. Once this is done, the IL+ view on the side will have a list of types and methods for the DLL.
3. Click on a method to dump the Optimized ASM generated by the .NET Jit. Optionally right click to dump either the .NET Tier 0 Jitted code or diff Tier 0/Tier 1.

<br/>

![.NET Insights Example Usage](https://raw.githubusercontent.com/jashook/vscode-dotnet-insights/master/dotnetInsights/media/peFile.gif)

<br/>

## Notes

Dumping the ASM for a Debug built DLL will **always** dump debuggable code (Tier 0).

The first startup of the extension will download five private builds of the .NET runtime 6.0 and 7.0, 8.0, 9.0 and 10.0. This is expected to take ~5 minutes to setup.

## Future planned work

1. [Add thread metrics](https://github.com/jashook/vscode-dotnet-insights/issues/20)
2. [Add JIT Metrics](https://github.com/jashook/vscode-dotnet-insights/issues/21)