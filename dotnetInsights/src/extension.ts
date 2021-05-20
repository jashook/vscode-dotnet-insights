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
import { OnSaveIlDasm } from './onSaveIlDasm';

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

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

        var stopShowIlOnSave = vscode.commands.registerCommand("dotnetInsights.stopShowIlOnSave", () => {
            insights.listeningToAllSaveEvents = false;
        });

        context.subscriptions.push(stopShowIlOnSave);

        vscode.commands.registerCommand("dotnetInsights.realtimeIL", (reWriteFile?: boolean) => {
            // We have been asked to show realtime asm of the current file.

            var activeFile  = vscode.window.activeTextEditor?.document.uri.fsPath;
            insights.currentFile = activeFile;

            if (!insights.listenerSetup) {
                insights.listenerSetup = true;
                insights.listeningToAllSaveEvents = true;
                vscode.workspace.onDidSaveTextDocument(e => {
                    if (e.fileName == insights.currentFile) {
                        if (insights.listeningToAllSaveEvents) {
                            vscode.commands.executeCommand("dotnetInsights.realtimeIL", false)
                        }
                    }
                });

                vscode.workspace.onDidCloseTextDocument(e => {
                    if (e.fileName == insights.currentFile) {
                        insights.listeningToAllSaveEvents = false;
                    }
                })

                vscode.window.onDidChangeActiveTextEditor(e => {
                    if (e?.document.fileName.indexOf("generated") != -1) {
                        return;
                    }

                    if (e?.document.fileName.indexOf("extension-output") != -1) {
                        return;
                    }

                    if (e.document.fileName == insights.currentFile) {
                        return;
                    }
                    
                    if (e.document.fileName.indexOf(".asm") != -1) {
                        return;
                    }

                    if (e.document.languageId == "Log") {
                        return;
                    }

                    insights.listeningToAllSaveEvents = false;
                })
            }
            else {
                insights.listeningToAllSaveEvents = true;
            }

            if (!insights.listeningToAllSaveEvents) {
                insights.listeningToAllSaveEvents = true;
            }

            var activeEditor = vscode.window.activeTextEditor;
            if (activeEditor !== undefined) {
                vscode.commands.executeCommand<vscode.DocumentSymbol[]>('vscode.executeDocumentSymbolProvider', activeEditor.document.uri).then(symbols => {
                    var cursorLocation = vscode.window.activeTextEditor?.selection.active;
                    if (ilAsmDocuments == undefined) {
                        // Create a new one.
                        insights.onSaveIlDasm = new OnSaveIlDasm(insights, cursorLocation, symbols);
                    }

                    var ilAsmDocuments = insights.onSaveIlDasm;

                    ilAsmDocuments?.setupActiveMethod(cursorLocation, symbols);
                    ilAsmDocuments?.runRoslynHelperForFile(activeFile);
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