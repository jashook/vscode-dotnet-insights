////////////////////////////////////////////////////////////////////////////////
// Module: extension.ts
////////////////////////////////////////////////////////////////////////////////

import * as child from 'child_process';
import * as crypto from "crypto";
import * as fs from 'fs';
import * as path from 'path';
import * as vscode from 'vscode';
import * as os from "os";

import * as request from 'request';
import * as targz from "targz";
import * as rimraf from "rimraf";

import { DotnetInsightsTreeDataProvider, Dependency, DotnetInsights } from './dotnetInsights';
import { DotnetInsightsTextEditorProvider } from "./DotnetInightsTextEditor";
import { DotnetInsightsGcTreeDataProvider, GcDependency } from "./dotnetInsightsGc";
import { DotnetInsightsGcEditor } from "./DotnetInsightsGcEditor";
import { DependencySetup } from "./DependencySetup";

import { GcListener } from "./GcListener";
import { PmiCommand } from "./PmiCommand";
import { JitOrder } from "./JitOrder";
import { ILDasmParser } from './ilDamParser';

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

function getLeafNodesWithType(symbols: vscode.DocumentSymbol[], parent: vscode.DocumentSymbol | undefined, leafNodes: [vscode.DocumentSymbol, vscode.DocumentSymbol][] | undefined): [vscode.DocumentSymbol, vscode.DocumentSymbol][] {
    if (leafNodes == undefined) {
        leafNodes = [] as [vscode.DocumentSymbol, vscode.DocumentSymbol][];
    }

    for (var index = 0; index < symbols.length; ++index) {
        if (symbols[index].children.length > 0) {
            getLeafNodesWithType(symbols[index].children, symbols[index], leafNodes);
        }
        else {
            leafNodes.push([parent!, symbols[index]]);
        }
    }

    return leafNodes;
}

function findSymbol(symbols: vscode.DocumentSymbol[], position: vscode.Position | undefined): [vscode.DocumentSymbol, vscode.DocumentSymbol] | undefined {
    // Get all the leaf nodes into one list
    if (position == undefined) return undefined;

    var leafNodes:  [vscode.DocumentSymbol, vscode.DocumentSymbol][] = getLeafNodesWithType(symbols, undefined, undefined);

    var returnValue : [vscode.DocumentSymbol, vscode.DocumentSymbol] | undefined;
    var found = false;
    for (var index = 0; index < leafNodes.length; ++index) {
        if (position?.line > leafNodes[index][1].range.start.line && position?.line < leafNodes[index][1].range.end.line) {
            returnValue = leafNodes[index];
            found = true;
            break;
        }
    }

    console.assert(found);
    return returnValue;
}

export function activate(context: vscode.ExtensionContext) {
    const outputChannel = vscode.window.createOutputChannel(`.NET Insights`);

    var config = vscode.workspace.getConfiguration();
    var dotnetInsightsSettings: any = config.get("dotnet-insights");

    outputChannel.appendLine('dotnetInsights: started');

    var dotnetInsightsGcTreeDataProvider: DotnetInsightsGcTreeDataProvider | undefined = undefined;

    if (dotnetInsightsSettings != undefined) {
        if (!dotnetInsightsSettings["surpressStartupMessage"]) {
            vscode.window.showInformationMessage(".NET Insights is starting");
        }
    }
    else {
        vscode.window.showInformationMessage(".NET Insights is starting");
    }

    var insights = new DotnetInsights(outputChannel);
    const lastestVersionNumber = "0.4.0";
    const latestListenerVersionNumber = "0.6.0";
    const latestRoslynVersionNumber = "0.6.1";

    var childProcess: child.ChildProcess | undefined = undefined;
    var startupCallback: any = undefined;
    var didFinishStartup = false;

    var isRunningGcMonitor: boolean = false;

    var startGcMonitor = vscode.commands.registerCommand("dotnetInsights.startGCMonitor", () => {
        if (startupCallback == undefined) {
            startupCallback = () => {
                if (insights.listener == undefined) return;
                if (isRunningGcMonitor) return;

                insights.listener.sendShutdown = false;
                insights.listener.start();

                isRunningGcMonitor = true;

                dotnetInsightsGcTreeDataProvider?.listener.processes.clear();
                dotnetInsightsGcTreeDataProvider?.refresh();

                // Check if we are able to run to application
                childProcess = child.exec(`"${insights.gcEventListenerPath}"`, (exception: child.ExecException | null, stdout: string, stderr: string) => {
                    if (stdout.indexOf("ETW Event listening required Privilidged Access. Please run as Administrator") != -1) {
                        vscode.window.showInformationMessage(`To automatically launch VSCode must be run elevated. In an elevated command prompt run: ${insights.gcEventListenerPath}`);
                        childProcess = undefined;
                    }
                    if (stderr.indexOf("ETW Event listening required Privilidged Access. Please run as Administrator") != -1) {
                        vscode.window.showInformationMessage(`To automatically launch VSCode must be run elevated. In an elevated command prompt run: ${insights.gcEventListenerPath}`);
                        childProcess = undefined;
                    }
                });

                insights.outputChannel.appendLine("Starting monitoring GCs.");
            };
        }

        if (!didFinishStartup) {
            return;
        }

        startupCallback();
    });

    var setupExtension = vscode.commands.registerCommand("dotnetInsights.loadExtension", () => {
        // no op
    });

    context.subscriptions.push(startGcMonitor);
    context.subscriptions.push(setupExtension);

    var stopGCMonitor = vscode.commands.registerCommand("dotnetInsights.stopGCMonitor", () => {
        if (insights.listener != undefined) {
            insights.listener.sendShutdown = true;

            try {
                insights.listener.httpServer.close();
            }
            catch(e) {
                
            }

            try {
                childProcess?.kill();
            }
            catch(e) {
                
            }

            isRunningGcMonitor = false;
            console.assert(dotnetInsightsGcTreeDataProvider != undefined);

            insights.outputChannel.appendLine("Stopped monitoring GCs.");
        }
    }); 

    context.subscriptions.push(stopGCMonitor);

    let dependencySetup = new DependencySetup(lastestVersionNumber, latestListenerVersionNumber, latestRoslynVersionNumber, context, insights);

    // Setup
    dependencySetup.setup().then((success: boolean) => {
        if (!success) {
            vscode.window.showWarningMessage(".NET Insights failed to start.");
            return;
        }

        if (dotnetInsightsSettings != undefined) {
            if (!dotnetInsightsSettings["surpressStartupMessage"]) {
                vscode.window.showInformationMessage(".NET Insights is setup. Please dismiss.");
            }

            insights.outputChannel.appendLine(".NET Insights is setup.");
        }
        else {
            vscode.window.showInformationMessage(".NET Insights is setup. To surpress this message add \"dotnet-insights.surpressStartupMessage\" : true to settings.json.");
        }

        var listener = new GcListener();
        insights.listener = listener;

        const dotnetInsightsTreeDataProvider = new DotnetInsightsTreeDataProvider(insights);
        dotnetInsightsGcTreeDataProvider = new DotnetInsightsGcTreeDataProvider(listener);

        listener.treeView = dotnetInsightsGcTreeDataProvider;

        vscode.window.registerTreeDataProvider('dotnetInsights', dotnetInsightsTreeDataProvider);
        vscode.window.registerTreeDataProvider('dotnetInsightsGc', dotnetInsightsGcTreeDataProvider);

        vscode.commands.registerCommand("dotnetInsights.diffThreeVsFiveTier0", (treeItem: Dependency) => {
            if (treeItem.label != undefined) {
                var pmiCommand = `"${insights.netcoreThreeCoreRunPath}"` + " " + `"${insights.netcoreThreePmiPath}"` + " " + "PREPALL-QUIET" + " " + `"${treeItem.dllPath}"`;
                outputChannel.appendLine(pmiCommand);

                var mb = 1024 * 1024;
                var maxBufferSize = 512 * mb;

                const selectMethodCwd = path.join(insights.pmiOutputPath, "selectMethod");

                if  (!fs.existsSync(selectMethodCwd)) {
                    fs.mkdirSync(selectMethodCwd);
                }

                const endofLine = os.platform() == "win32" ? vscode.EndOfLine.CRLF : vscode.EndOfLine.LF;

                var id = crypto.randomBytes(16).toString("hex");
                var threeOnefilePath = path.join(insights.pmiOutputPath, id + ".asm");
                
                var childProcess = child.exec(pmiCommand, {
                    maxBuffer: maxBufferSize,
                    "cwd": selectMethodCwd,
                    "env": {
                        "COMPlus_JitDisasm": `${treeItem.label}`,
                        "COMPlus_JitMinOpts": "1",
                        "COMPlus_JitDiffableDasm": "1"
                    }
                }, (error: any, output: string, stderr: string) => {
                    if (error) {
                        console.error("Failed to execute pmi.");
                        console.error(error);
                    }

                    var replaceRegex = /completed assembly.*\n/i;
                    if (os.platform() == "win32") {
                        replaceRegex = /completed assembly.*\r\n/i;
                    }

                    output = output.replace(replaceRegex, "");

                    fs.writeFile(threeOnefilePath, output, (error) => {
                        if (error) {
                            return;
                        }

                        id = crypto.randomBytes(16).toString("hex");
                        var outputFileName = path.join(insights.pmiOutputPath, id + ".asm");

                        var pmiCommand = `"${insights.netcoreFiveCoreRunPath}"` + " " + `"${insights.pmiPath}"` + " " + "PREPALL-QUIET" + " " + `"${treeItem.dllPath}"`;
                        
                        childProcess = child.exec(pmiCommand, {
                            maxBuffer: maxBufferSize,
                            "cwd": selectMethodCwd,
                            "env": {
                                "COMPlus_JitMinOpts": "1",
                                "COMPlus_JitDiffableDasm": "1",
                                "COMPlus_JitDisasm": `${treeItem.label}`
                            }
                        }, (error: any, output: string, stderr: string) => {
                            if (error) {
                                console.error("Failed to execute pmi.");
                                console.error(error);
                            }

                            var replaceRegex = /completed assembly.*\n/i;
                            if (os.platform() == "win32") {
                                replaceRegex = /completed assembly.*\r\n/i;
                            }

                            output = output.replace(replaceRegex, "");

                            fs.writeFile(outputFileName, output, (error) => {
                                if (error) {
                                    return;
                                }
                                
                                // left - Left-hand side resource of the diff editor
                                // right - Right-hand side resource of the diff editor
                                // title - (optional) Human readable title for the diff editor
                                
                                outputChannel.appendLine(".Net Core 3.1 file path: " + threeOnefilePath);
                                outputChannel.appendLine(".Net Core 5.0 file path: " + outputFileName);

                                vscode.commands.executeCommand("vscode.diff", vscode.Uri.file(threeOnefilePath), vscode.Uri.file(outputFileName), ".Net Core 3.1/.Net Core 5.0 Tier 0 Diff");
                            });
                        });
                    });
                });
            }
        });

        vscode.commands.registerCommand("dotnetInsights.diffThreeVsFiveTier1", (treeItem: Dependency) => {
            if (treeItem.label != undefined) {
                var pmiCommand = `"${insights.netcoreThreeCoreRunPath}"` + " " + `"${insights.netcoreThreePmiPath}"` + " " + "PREPALL-QUIET" + " " + `"${treeItem.dllPath}"`;
                outputChannel.appendLine(pmiCommand);

                var mb = 1024 * 1024;
                var maxBufferSize = 512 * mb;

                const selectMethodCwd = path.join(insights.pmiOutputPath, "selectMethod");

                if  (!fs.existsSync(selectMethodCwd)) {
                    fs.mkdirSync(selectMethodCwd);
                }

                const endofLine = os.platform() == "win32" ? vscode.EndOfLine.CRLF : vscode.EndOfLine.LF;

                var id = crypto.randomBytes(16).toString("hex");
                var threeOnefilePath = path.join(insights.pmiOutputPath, id + ".asm");
                
                var childProcess = child.exec(pmiCommand, {
                    maxBuffer: maxBufferSize,
                    "cwd": selectMethodCwd,
                    "env": {
                        "COMPlus_JitDiffableDasm": "1",
                        "COMPlus_TieredCompilation": "0",
                        "COMPlus_TC_QuickJit": "0",
                        "COMPlus_JitDisasm": `${treeItem.label}`
                    }
                }, (error: any, output: string, stderr: string) => {
                    if (error) {
                        console.error("Failed to execute pmi.");
                        console.error(error);
                    }

                    var replaceRegex = /completed assembly.*\n/i;
                    if (os.platform() == "win32") {
                        replaceRegex = /completed assembly.*\r\n/i;
                    }

                    output = output.replace(replaceRegex, "");

                    fs.writeFile(threeOnefilePath, output, (error) => {
                        if (error) {
                            return;
                        }

                        id = crypto.randomBytes(16).toString("hex");
                        var outputFileName = path.join(insights.pmiOutputPath, id + ".asm");

                        var pmiCommand = `"${insights.netcoreFiveCoreRunPath}"` + " " + `"${insights.pmiPath}"` + " " + "PREPALL-QUIET" + " " + `"${treeItem.dllPath}"`;
                        
                        childProcess = child.exec(pmiCommand, {
                            maxBuffer: maxBufferSize,
                            "cwd": selectMethodCwd,
                            "env": {
                                "COMPlus_JitDiffableDasm": "1",
                                "COMPlus_TieredCompilation": "0",
                                "COMPlus_TC_QuickJit": "0",
                                "COMPlus_JitDisasm": `${treeItem.label}`
                            }
                        }, (error: any, output: string, stderr: string) => {
                            if (error) {
                                console.error("Failed to execute pmi.");
                                console.error(error);
                            }

                            var replaceRegex = /completed assembly.*\n/i;
                            if (os.platform() == "win32") {
                                replaceRegex = /completed assembly.*\r\n/i;
                            }

                            output = output.replace(replaceRegex, "");

                            fs.writeFile(outputFileName, output, (error) => {
                                if (error) {
                                    return;
                                }
                                
                                // left - Left-hand side resource of the diff editor
                                // right - Right-hand side resource of the diff editor
                                // title - (optional) Human readable title for the diff editor
                                
                                outputChannel.appendLine("Tier 0 file path: " + threeOnefilePath);
                                outputChannel.appendLine("Tier 1 file path: " + outputFileName);

                                vscode.commands.executeCommand("vscode.diff", vscode.Uri.file(threeOnefilePath), vscode.Uri.file(outputFileName), ".Net Core 3.1/.Net Core 5.0 Tier 1 Diff");
                            });
                        });
                    });
                });
            }
        });

        vscode.commands.registerCommand("dotnetInsights.diff", (treeItem: Dependency) => {
            if (treeItem.label != undefined) {
                var pmiCommand = `"${insights.coreRunPath}"` + " " + `"${insights.pmiPath}"` + " " + "PREPALL-QUIET" + " " + `"${treeItem.dllPath}"`;
                outputChannel.appendLine(pmiCommand);

                var mb = 1024 * 1024;
                var maxBufferSize = 512 * mb;

                const selectMethodCwd = path.join(insights.pmiOutputPath, "selectMethod");

                if  (!fs.existsSync(selectMethodCwd)) {
                    fs.mkdirSync(selectMethodCwd);
                }

                const endofLine = os.platform() == "win32" ? vscode.EndOfLine.CRLF : vscode.EndOfLine.LF;

                var id = crypto.randomBytes(16).toString("hex");
                var minOptsOutputFileName = path.join(insights.pmiOutputPath, id + ".asm");
                
                var childProcess = child.exec(pmiCommand, {
                    maxBuffer: maxBufferSize,
                    "cwd": selectMethodCwd,
                    "env": {
                        "COMPlus_JitDisasm": `${treeItem.label}`,
                        "COMPlus_JitMinOpts": "1",
                        "COMPlus_JitDiffableDasm": "1"
                    }
                }, (error: any, output: string, stderr: string) => {
                    if (error) {
                        console.error("Failed to execute pmi.");
                        console.error(error);
                    }

                    var replaceRegex = /completed assembly.*\n/i;
                    if (os.platform() == "win32") {
                        replaceRegex = /completed assembly.*\r\n/i;
                    }

                    output = output.replace(replaceRegex, "");

                    fs.writeFile(minOptsOutputFileName, output, (error) => {
                        if (error) {
                            return;
                        }

                        id = crypto.randomBytes(16).toString("hex");
                        var outputFileName = path.join(insights.pmiOutputPath, id + ".asm");
                        
                        childProcess = child.exec(pmiCommand, {
                            maxBuffer: maxBufferSize,
                            "cwd": selectMethodCwd,
                            "env": {
                                "COMPlus_JitDiffableDasm": "1",
                                "COMPlus_TieredCompilation": "0",
                                "COMPlus_TC_QuickJit": "0",
                                "COMPlus_JitDisasm": `${treeItem.label}`
                            }
                        }, (error: any, output: string, stderr: string) => {
                            if (error) {
                                console.error("Failed to execute pmi.");
                                console.error(error);
                            }

                            var replaceRegex = /completed assembly.*\n/i;
                            if (os.platform() == "win32") {
                                replaceRegex = /completed assembly.*\r\n/i;
                            }

                            output = output.replace(replaceRegex, "");

                            fs.writeFile(outputFileName, output, (error) => {
                                if (error) {
                                    return;
                                }
                                
                                // left - Left-hand side resource of the diff editor
                                // right - Right-hand side resource of the diff editor
                                // title - (optional) Human readable title for the diff editor
                                
                                outputChannel.appendLine("Tier 0 file path: " + minOptsOutputFileName);
                                outputChannel.appendLine("Tier 1 file path: " + outputFileName);

                                vscode.commands.executeCommand("vscode.diff", vscode.Uri.file(minOptsOutputFileName), vscode.Uri.file(outputFileName), "Tier 0/Tier 1 Diff");
                            });
                        });
                    });
                });
            }
        });

        vscode.commands.registerCommand('dotnetInsights.minOpts', (treeItem: Dependency) => {
            if (treeItem.label != undefined) {
                var pmiCommand = `"${insights.coreRunPath}"` + " " + `"${insights.pmiPath}"` + " " + "PREPALL-QUIET" + " " + `"${treeItem.dllPath}"`;
                outputChannel.appendLine(pmiCommand);

                var mb = 1024 * 1024;
                var maxBufferSize = 512 * mb;

                const selectMethodCwd = path.join(insights.pmiOutputPath, "selectMethod");

                if  (!fs.existsSync(selectMethodCwd)) {
                    fs.mkdirSync(selectMethodCwd);
                }

                const endofLine = os.platform() == "win32" ? vscode.EndOfLine.CRLF : vscode.EndOfLine.LF;
                const id = crypto.randomBytes(16).toString("hex");
                const outputFileName = path.join(insights.pmiOutputPath, id + ".asm");
                
                var childProcess = child.exec(pmiCommand, {
                    maxBuffer: maxBufferSize,
                    "cwd": selectMethodCwd,
                    "env": {
                        "COMPlus_JitDisasm": `${treeItem.label}`,
                        "COMPlus_JitMinOpts": "1",
                        "COMPlus_JitGCDump": `${treeItem.label}`
                    }
                }, (error: any, output: string, stderr: string) => {
                    if (error) {
                        console.error("Failed to execute pmi.");
                        console.error(error);
                    }

                    var replaceRegex = /completed assembly.*\n/i;
                    if (os.platform() == "win32") {
                        replaceRegex = /completed assembly.*\r\n/i;
                    }

                    output = output.replace(replaceRegex, "");

                    fs.writeFile(outputFileName, output, (error) => {
                        if (error) {
                            return;
                        }
                        vscode.workspace.openTextDocument(outputFileName).then(doc => {
                            vscode.window.showTextDocument(doc, 1);
                        });
                    });
                });
            }
        });

        vscode.commands.registerCommand("dotnetInsights.tier1", (treeItem: Dependency) => {
            if (treeItem.label != undefined) {
                var pmiCommand = `"${insights.coreRunPath}"` + " " + `"${insights.pmiPath}"` + " " + "PREPALL-QUIET" + " " + `"${treeItem.dllPath}"`;
                outputChannel.appendLine(pmiCommand);

                var mb = 1024 * 1024;
                var maxBufferSize = 512 * mb;

                const selectMethodCwd = path.join(insights.pmiOutputPath, "selectMethod");

                if  (!fs.existsSync(selectMethodCwd)) {
                    fs.mkdirSync(selectMethodCwd);
                }

                const endofLine = os.platform() == "win32" ? vscode.EndOfLine.CRLF : vscode.EndOfLine.LF;

                const id = crypto.randomBytes(16).toString("hex");

                const outputFileName = path.join(insights.pmiOutputPath, id + ".asm");
                
                var childProcess = child.exec(pmiCommand, {
                    maxBuffer: maxBufferSize,
                    "cwd": selectMethodCwd,
                    "env": {
                        "COMPlus_JitDisasm": `${treeItem.label}`,
                        "COMPlus_TieredCompilation": "0",
                        "COMPlus_TC_QuickJit": "0",
                        "COMPlus_JitGCDump": `${treeItem.label}`
                    }
                }, (error: any, output: string, stderr: string) => {
                    if (error) {
                        console.error("Failed to execute pmi.");
                        console.error(error);
                    }

                    var replaceRegex = /completed assembly.*\n/i;
                    if (os.platform() == "win32") {
                        replaceRegex = /completed assembly.*\r\n/i;
                    }

                    output = output.replace(replaceRegex, "");

                    fs.writeFile(outputFileName, output, (error) => {
                        if (error) {
                            return;
                        }
                        vscode.workspace.openTextDocument(outputFileName).then(doc => {
                            vscode.window.showTextDocument(doc, 1);
                        });
                    });
                });
            }
        });

        vscode.commands.registerCommand('dotnetInsights.jitDumpTier0', (treeItem: Dependency) => {
            if (treeItem.label != undefined) {
                var pmiCommand = `"${insights.coreRunPath}"` + " " + `"${insights.pmiPath}"` + " " + "PREPALL-QUIET" + " " + `"${treeItem.dllPath}"`;
                outputChannel.appendLine(pmiCommand);

                var mb = 1024 * 1024;
                var maxBufferSize = 512 * mb;

                const selectMethodCwd = path.join(insights.pmiOutputPath, "selectMethod");

                if  (!fs.existsSync(selectMethodCwd)) {
                    fs.mkdirSync(selectMethodCwd);
                }

                const endofLine = os.platform() == "win32" ? vscode.EndOfLine.CRLF : vscode.EndOfLine.LF;
                const id = crypto.randomBytes(16).toString("hex");
                const outputFileName = path.join(insights.pmiOutputPath, id + ".jitdump");
                
                var childProcess = child.exec(pmiCommand, {
                    maxBuffer: maxBufferSize,
                    "cwd": selectMethodCwd,
                    "env": {
                        "COMPlus_JitDisasm": `${treeItem.label}`,
                        "COMPlus_JitMinOpts": "1",
                        "COMPlus_JitDump": `${treeItem.label}`
                    }
                }, (error: any, output: string, stderr: string) => {
                    if (error) {
                        console.error("Failed to execute pmi.");
                        console.error(error);
                    }

                    var replaceRegex = /completed assembly.*\n/i;
                    if (os.platform() == "win32") {
                        replaceRegex = /completed assembly.*\r\n/i;
                    }

                    output = output.replace(replaceRegex, "");

                    fs.writeFile(outputFileName, output, (error) => {
                        if (error) {
                            return;
                        }
                        vscode.workspace.openTextDocument(outputFileName).then(doc => {
                            vscode.window.showTextDocument(doc, 1);
                        });
                    });
                });
            }
        });

        vscode.commands.registerCommand("dotnetInsights.jitDumpTier1", (treeItem: Dependency) => {
            if (treeItem.label != undefined) {
                var pmiCommand = `"${insights.coreRunPath}"` + " " + `"${insights.pmiPath}"` + " " + "PREPALL-QUIET" + " " + `"${treeItem.dllPath}"`;
                outputChannel.appendLine(pmiCommand);

                var mb = 1024 * 1024;
                var maxBufferSize = 512 * mb;

                const selectMethodCwd = path.join(insights.pmiOutputPath, "selectMethod");

                if  (!fs.existsSync(selectMethodCwd)) {
                    fs.mkdirSync(selectMethodCwd);
                }

                const endofLine = os.platform() == "win32" ? vscode.EndOfLine.CRLF : vscode.EndOfLine.LF;

                const id = crypto.randomBytes(16).toString("hex");

                const outputFileName = path.join(insights.pmiOutputPath, id + ".jitdump");
                
                var childProcess = child.exec(pmiCommand, {
                    maxBuffer: maxBufferSize,
                    "cwd": selectMethodCwd,
                    "env": {
                        "COMPlus_JitDisasm": `${treeItem.label}`,
                        "COMPlus_TieredCompilation": "0",
                        "COMPlus_TC_QuickJit": "0",
                        "COMPlus_JitDump": `${treeItem.label}`
                    }
                }, (error: any, output: string, stderr: string) => {
                    if (error) {
                        console.error("Failed to execute pmi.");
                        console.error(error);
                    }

                    var replaceRegex = /completed assembly.*\n/i;
                    if (os.platform() == "win32") {
                        replaceRegex = /completed assembly.*\r\n/i;
                    }

                    output = output.replace(replaceRegex, "");

                    fs.writeFile(outputFileName, output, (error) => {
                        if (error) {
                            return;
                        }
                        vscode.workspace.openTextDocument(outputFileName).then(doc => {
                            vscode.window.showTextDocument(doc, 1);
                        });
                    });
                });
            }
        });

        var roslynHelper: child.ChildProcess | undefined = undefined;
        let roslynHelperPath = insights.roslynHelperPath;

        let roslynHelperTempDir = insights.pmiTempDir;
        let roslynHelperIlFile = path.join(roslynHelperTempDir, "generated.dll");
        let realtimeDasmFile = path.join(roslynHelperTempDir, "generated.asm");
        let roslynHelperCommand = `"${roslynHelperPath}" "${roslynHelperIlFile}"`;

        vscode.commands.registerCommand("dotnetInsights.realtimeIL", () => {
            // We have been asked to show realtime asm of the current file.

            var activeFile  = vscode.window.activeTextEditor?.document.uri.fsPath;

            var activeEditor = vscode.window.activeTextEditor;
            if (activeEditor !== undefined) {
                vscode.commands.executeCommand<vscode.DocumentSymbol[]>('vscode.executeDocumentSymbolProvider', activeEditor.document.uri).then(symbols => {
                    var cursorLocation = vscode.window.activeTextEditor?.selection.active;

                    // We will need the active method.
                    var activeMethod: any = undefined;
                    var methodNameForActiveMethod: string = "";

                    if (symbols !== undefined) {
                        var symbol = findSymbol(symbols, cursorLocation);

                        if (symbol != undefined) {
                            var typeNameWithoutAssembly = symbol[0].name.split(".")[1];
                            var methodNameWithoutArgs = symbol[1].name.split("\(")[0];

                            methodNameForActiveMethod = methodNameWithoutArgs;

                            if (symbol[1].kind == vscode.SymbolKind.Constructor) {
                                activeMethod = `${typeNameWithoutAssembly}:.ctor`;
                            }
                            else {
                                activeMethod = `${typeNameWithoutAssembly}:${methodNameWithoutArgs}`;
                            }
                        }
                    }
                    else {
                        vscode.window.showWarningMessage("Unable to determine method. Check that the C# extension is installed and Omnisharp has loaded this project.");
                        return;
                    }

                    insights.isInlineIL = true;

                    insights.outputChannel.appendLine(`pmi for method: ${activeMethod}`);

                    if (roslynHelper == undefined) {
                        roslynHelper = child.exec(roslynHelperCommand, (error: any, stdout: string, stderr: string) => {
                            // No op, should not finish

                            console.log(error);
                            console.log(stdout);
                        });

                        roslynHelper.stdout?.on('data', data => {
                            let response = data != null ? data.toString().trim() : "";
                            insights.outputChannel.appendLine(response);

                            if (response == "Compilation succeeded") {
                                // We have written IL to roslynHelperIlFile

                                insights.inlineIlCallback = (e: any) => {
                                    console.assert(insights.ilDasmOutput != undefined);

                                    let ildasmParser = new ILDasmParser(insights.ilDasmOutput);
                                    ildasmParser.parse();

                                    let lineNumber = ildasmParser.methodMap.get(methodNameForActiveMethod);

                                    if (lineNumber == undefined) {
                                        // Name does not directly match. Look for a loose match

                                        let it = ildasmParser.methodMap.keys()
                                        let current = it.next();
                                        while(current.value != undefined) {
                                            let key = current.value;
                                            if (key.indexOf(methodNameForActiveMethod) != -1) {
                                                lineNumber = ildasmParser.methodMap.get(key);
                                                break;
                                            }

                                            current = it.next();
                                        }
                                    }

                                    if (lineNumber == undefined) {
                                        vscode.window.showWarningMessage("Unable to determine method. Check that the C# extension is installed and Omnisharp has loaded this project.");
                                        return;
                                    }

                                    const currentVisibleRange = e.visibleRanges[0];
                                    const size = currentVisibleRange.end.line - currentVisibleRange.start.line;

                                    e.revealRange(new vscode.Range(lineNumber, 0, lineNumber + size, 0));
                                };

                                vscode.commands.executeCommand("vscode.openWith", vscode.Uri.file(roslynHelperIlFile), DotnetInsightsTextEditorProvider.viewType, vscode.ViewColumn.Beside);

                                // Also pmi the file.
                                var jitOrder = new JitOrder(insights.coreRunPath, insights, roslynHelperIlFile);
                                jitOrder.execute().then(output => {
                                    // Determine the method from the output

                                    // Split the output by newline
                                    var newLine = "\n";
                                    if (os.platform() == "win32") {
                                        newLine = "\r\n";
                                    }

                                    var lines = output.split(newLine);
                                    var matchedMethod: any = undefined;
                                    for (var index = 0; index < lines.length; ++index) {
                                        if (lines[index].indexOf(activeMethod) != -1) {
                                            let methodSplit = lines[index].split(' | ');

                                            matchedMethod = methodSplit[methodSplit.length - 1].trim();
                                            break;
                                        }
                                    }

                                    console.assert(matchedMethod != undefined);
                                    
                                    var pmiMethod = new PmiCommand(insights.coreRunPath, insights, roslynHelperIlFile);
                                    pmiMethod.execute(matchedMethod).then(value => {
                                        let unique_id = value[0];
                                        let output = value[1];

                                        const outputFileName = path.join(insights.pmiOutputPath, unique_id + ".asm");

                                        fs.writeFile(outputFileName, output, (error) => {
                                            if (error) {
                                                return;
                                            }
                                            
                                            let splitIndex = vscode.window.visibleTextEditors.length + 1;

                                            vscode.workspace.openTextDocument(outputFileName).then(doc => {
                                                vscode.window.showTextDocument(doc, splitIndex);
                                            });
                                        });
                                    });
                                });
                            } else {
                                // Failed. TODO, most likely references.
                            }
                        });
                    }

                    var success = false;

                    while (!success) {
                        try {
                            roslynHelper?.stdin?.write(activeFile);

                            if (os.platform() == "win32") {
                                roslynHelper?.stdin?.write("\r\n");
                            }
                            else {
                                roslynHelper?.stdin?.write("\n");
                            }
                            success = true;
                        }
                        catch (e) {
                            console.log(e);
                        }
                    }
                });
            }
        });

        vscode.commands.registerCommand("dotnetInsights.selectNode", (treeItem: Dependency) => {
            if (treeItem.lineNumber != undefined) {
                const lineNumber: number = treeItem.lineNumber;

                vscode.workspace.openTextDocument(treeItem.fsPath).then(doc => {
                    vscode.window.showTextDocument(doc).then(e => {
                        const currentVisibleRange = e.visibleRanges[0];
                        const size = currentVisibleRange.end.line - currentVisibleRange.start.line;

                        e.revealRange(new vscode.Range(lineNumber, 0, lineNumber + size, 0));
                    });
                });
            }
        });

        vscode.commands.registerCommand("dotnetInsightsGc.selectPid", (item: GcDependency) => {
            if (item.label != undefined) {
                const outputPath = path.dirname(insights.pmiOutputPath);
                const gcStats = path.join(outputPath, "gcStats");

                if (!fs.existsSync(gcStats)) {
                    fs.mkdirSync(gcStats);
                }

                const pidPath = path.join(gcStats, item.pid + ".gcstats");
                fs.writeFileSync(pidPath, "eol");

                vscode.commands.executeCommand("vscode.openWith", vscode.Uri.file(pidPath), DotnetInsightsGcEditor.viewType);
            }
        });

        context.subscriptions.push(DotnetInsightsTextEditorProvider.register(context, insights));
        context.subscriptions.push(DotnetInsightsGcEditor.register(context, insights, listener));

        if (startupCallback != undefined) {
            startupCallback();
        }

        didFinishStartup = true;
    });
}

export function deactivate() {
    console.log("dotnetInsights: deactivated.");
}