////////////////////////////////////////////////////////////////////////////////
// Module: extension.ts
////////////////////////////////////////////////////////////////////////////////

import * as child from 'child_process';
import * as crypto from "crypto";
import * as fs from 'fs';
import * as path from 'path';
import * as vscode from 'vscode';
import * as os from "os";

import { DotnetInsightsTreeDataProvider, Dependency, DotnetInsights } from './dotnetInsights';
import { DotnetInsightsTextEditorProvider } from "./DotnetInightsTextEditor";
import { DotnetInsightsGcTreeDataProvider, GcDependency } from "./dotnetInsightsGc";
import { DotnetInsightsGcEditor } from "./DotnetInsightsGcEditor";
import { DotnetInsightsGcSnapshotEditor } from "./DotnetInsightsGcSnapshotEditor";
import { DotnetInsightsRuntimeLoadEventsEditor } from "./DotnetInsightsRuntimeLoadEventsEditor";
import { DependencySetup } from "./DependencySetup";

import { GcListener } from "./GcListener";
import { OnSaveIlDasm } from './onSaveIlDasm';
import { DotnetInsightsJitTreeDataProvider, JitDependency } from './dotnetInsightsJit';

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

function compile(minOpts: boolean, jitDump: boolean, treeItem: Dependency, insights: DotnetInsights, outputFileName: string, coreRunPath: string, pmiPath: string) {
    var methodNameSplit = treeItem.label.split(":");
    var methodName = methodNameSplit[0];
    if (methodNameSplit.length > 2) {
        methodNameSplit = methodNameSplit.slice(0, methodNameSplit.length - 1);
        methodName = methodNameSplit.join(":");
    }

    var promise = new Promise((resolve, reject) => {
        if (methodName !== undefined) {
            var pmiCommand = `"${coreRunPath}"` + " " + `"${pmiPath}"` + " " + "PREPALL-QUIET" + " " + `"${treeItem.dllPath}"`;
            insights.outputChannel.appendLine(pmiCommand);

            var mb = 1024 * 1024;
            var maxBufferSize = 512 * mb;

            const selectMethodCwd = path.join(insights.pmiOutputPath, "selectMethod");

            if  (!fs.existsSync(selectMethodCwd)) {
                fs.mkdirSync(selectMethodCwd);
            }

            const endofLine = os.platform() === "win32" ? vscode.EndOfLine.CRLF : vscode.EndOfLine.LF;

            var envToUse: any = {
                "COMPlus_JitDisasm": `${methodName}`,
                "COMPlus_JITMinOpts": "1",
                "COMPlus_JitGCDump": `${methodName}`
            };

            if (minOpts === false) {
                envToUse = {
                    "COMPlus_JitDisasm": `${methodName}`,
                    "COMPlus_TieredCompilation": "0",
                    "COMPlus_TC_QuickJit": "0",
                    "COMPlus_JitGCDump": `${methodName}`
                };
            }

            if (jitDump === true) {
                envToUse["COMPlus_JitDump"] = `${methodName}`;
            }
            
            var childProcess = child.exec(pmiCommand, {
                maxBuffer: maxBufferSize,
                "cwd": selectMethodCwd,
                "env": envToUse
            }, (error: any, output: string, stderr: string) => {
                if (error) {
                    console.error("Failed to execute pmi.");
                    console.error(error);
                }

                var replaceRegex = /completed assembly.*\n/i;
                if (os.platform() === "win32") {
                    replaceRegex = /completed assembly.*\r\n/i;
                }

                output = output.replace(replaceRegex, "");

                fs.writeFile(outputFileName, output, (error) => {
                    if (error) {
                        reject();
                    }
                    
                    resolve(true);
                });
            });
        }
        else {
            reject();
        }
    });

    return promise;
}

function doDiffBetweenRuntimesTier0(treeItem: Dependency, insights: DotnetInsights, threeOne: boolean, five: boolean, six: boolean, seven: boolean, description: string) {
    var baseCoreRunPath: string|undefined = undefined;
    var basePmiPath: string|undefined = undefined;

    var diffCoreRunPath: string|undefined = undefined;
    var diffPmiPath: string|undefined = undefined;

    var baseIsFive: boolean = false;
    var baseIsSix: boolean = false;

    if (threeOne === true) {
        baseCoreRunPath = insights.netcoreThreeCoreRunPath;
        basePmiPath = insights.netcoreThreePmiPath;
    } else if (five === true) {
        baseCoreRunPath = insights.netcoreFiveCoreRunPath;
        basePmiPath = insights.netcoreFivePmiPath;
        baseIsFive = true;
    } else if (six === true) {
        baseCoreRunPath = insights.netcoreSixCoreRunPath;
        basePmiPath = insights.netcoreSixPmiPath;
        baseIsSix = true;
    }

    if (five === true && baseIsFive !== true) {
        diffCoreRunPath = insights.netcoreFiveCoreRunPath;
        diffPmiPath = insights.netcoreFivePmiPath;
    } else if (six === true && baseIsSix !== true) {
        diffCoreRunPath = insights.netcoreSixCoreRunPath;
        diffPmiPath = insights.netcoreSixPmiPath;
    } else {
        diffCoreRunPath = insights.netcoreSevenCoreRunPath;
        diffPmiPath = insights.netcoreSevenPmiPath;
    }

    var id = crypto.randomBytes(16).toString("hex");
    var basefilePath = path.join(insights.pmiOutputPath, id + ".asm");
    compile(true, false, treeItem, insights, basefilePath, baseCoreRunPath!, basePmiPath!).then((success: any) => {
        id = crypto.randomBytes(16).toString("hex");
        var outputFileName = path.join(insights.pmiOutputPath, id + ".asm");

        if (success !== undefined && !success) {
            return;
        }

        compile(true, false, treeItem, insights, outputFileName, diffCoreRunPath!, diffPmiPath!).then((success: any) => {
            if (success !== undefined && !success) {
                return;
            }
            
            // left - Left-hand side resource of the diff editor
            // right - Right-hand side resource of the diff editor
            // title - (optional) Human readable title for the diff editor
            
            insights.outputChannel.appendLine(".Net Core 3.1 file path: " + basefilePath);
            insights.outputChannel.appendLine(".Net Core 5.0 file path: " + outputFileName);

            vscode.commands.executeCommand("vscode.diff", vscode.Uri.file(basefilePath), vscode.Uri.file(outputFileName), description);
        });
    });
}

function doDiffBetweenRuntimesTier1(treeItem: Dependency, insights: DotnetInsights, threeOne: boolean, five: boolean, six: boolean, seven: boolean, description: string) {
    var baseCoreRunPath: string|undefined = undefined;
    var basePmiPath: string|undefined = undefined;

    var diffCoreRunPath: string|undefined = undefined;
    var diffPmiPath: string|undefined = undefined;

    var baseIsFive: boolean = false;
    var baseIsSix: boolean = false;

    if (threeOne === true) {
        baseCoreRunPath = insights.netcoreThreeCoreRunPath;
        basePmiPath = insights.netcoreThreePmiPath;
    } else if (five === true) {
        baseCoreRunPath = insights.netcoreFiveCoreRunPath;
        basePmiPath = insights.netcoreFivePmiPath;
        baseIsFive = true;
    } else if (six === true) {
        baseCoreRunPath = insights.netcoreSixCoreRunPath;
        basePmiPath = insights.netcoreSixPmiPath;
        baseIsSix = true;
    }

    if (five === true && baseIsFive !== true) {
        diffCoreRunPath = insights.netcoreFiveCoreRunPath;
        diffPmiPath = insights.netcoreFivePmiPath;
    } else if (six === true && baseIsSix !== true) {
        diffCoreRunPath = insights.netcoreSixCoreRunPath;
        diffPmiPath = insights.netcoreSixPmiPath;
    } else {
        diffCoreRunPath = insights.netcoreSevenCoreRunPath;
        diffPmiPath = insights.netcoreSevenPmiPath;
    }

    
    var id = crypto.randomBytes(16).toString("hex");
    var basefilePath = path.join(insights.pmiOutputPath, id + ".asm");
    compile(false, false, treeItem, insights, basefilePath, baseCoreRunPath!, basePmiPath!).then((success: any) => {
        id = crypto.randomBytes(16).toString("hex");
        var outputFileName = path.join(insights.pmiOutputPath, id + ".asm");

        if (success !== undefined && !success) {
            return;
        }

        compile(false, false, treeItem, insights, outputFileName, diffCoreRunPath!, diffPmiPath!).then((success: any) => {
            if (success !== undefined && !success) {
                return;
            }
            
            // left - Left-hand side resource of the diff editor
            // right - Right-hand side resource of the diff editor
            // title - (optional) Human readable title for the diff editor
            
            insights.outputChannel.appendLine(".Net Core 3.1 file path: " + basefilePath);
            insights.outputChannel.appendLine(".Net Core 5.0 file path: " + outputFileName);

            vscode.commands.executeCommand("vscode.diff", vscode.Uri.file(basefilePath), vscode.Uri.file(outputFileName), description);
        });
    });
}

export function activate(context: vscode.ExtensionContext) {
    const outputChannel = vscode.window.createOutputChannel(`.NET Insights`);

    var config = vscode.workspace.getConfiguration();
    var dotnetInsightsSettings: any = config.get("dotnet-insights");

    outputChannel.appendLine('dotnetInsights: started');

    var dotnetInsightsGcTreeDataProvider: DotnetInsightsGcTreeDataProvider | undefined = undefined;
    var dotnetInsightsJitTreeDataProvider: DotnetInsightsJitTreeDataProvider | undefined = undefined;

    if (dotnetInsightsSettings != undefined) {
        if (!dotnetInsightsSettings["surpressStartupMessage"]) {
            vscode.window.showInformationMessage(".NET Insights is starting");
        }
    }
    else {
        vscode.window.showInformationMessage(".NET Insights is starting");
    }

    var insights = new DotnetInsights(outputChannel);
    const lastestVersionNumber = "0.8.0";
    const latestListenerVersionNumber = "0.8.0";
    const latestRoslynVersionNumber = "0.8.0";

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
                dotnetInsightsJitTreeDataProvider?.refresh();

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
            console.assert(dotnetInsightsJitTreeDataProvider != undefined);

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
        dotnetInsightsJitTreeDataProvider = new DotnetInsightsJitTreeDataProvider(listener);


        // Set up the tree views
        listener.treeView = dotnetInsightsGcTreeDataProvider;
        listener.jitTreeView = dotnetInsightsJitTreeDataProvider;

        vscode.window.registerTreeDataProvider('dotnetInsights', dotnetInsightsTreeDataProvider);
        vscode.window.registerTreeDataProvider('dotnetInsightsGc', dotnetInsightsGcTreeDataProvider);
        vscode.window.registerTreeDataProvider('dotnetInsightsJit', dotnetInsightsJitTreeDataProvider);

        vscode.commands.registerCommand("dotnetInsights.diffThreeVsFiveTier0", (treeItem: Dependency) => {
            doDiffBetweenRuntimesTier0(treeItem, insights, true, true, false, false, ".Net Core 3.1/.Net Core 5.0 Tier 0 Diff");
        });

        vscode.commands.registerCommand("dotnetInsights.diffThreeVsSixTier0", (treeItem: Dependency) => {
            doDiffBetweenRuntimesTier0(treeItem, insights, true, false, true, false, ".Net Core 3.1/.Net Core 6.0 Tier 0 Diff");
        });

        vscode.commands.registerCommand("dotnetInsights.diffThreeVsSevenTier0", (treeItem: Dependency) => {
            doDiffBetweenRuntimesTier0(treeItem, insights, true, false, false, true, ".Net Core 3.1/.Net Core 7.0 Tier 0 Diff");
        });

        vscode.commands.registerCommand("dotnetInsights.diffFiveVsSixTier0", (treeItem: Dependency) => {
            doDiffBetweenRuntimesTier0(treeItem, insights, false, true, true, false, ".Net Core 5.0/.Net Core 6.0 Tier 0 Diff");
        });

        vscode.commands.registerCommand("dotnetInsights.diffFiveVsSevenTier0", (treeItem: Dependency) => {
            doDiffBetweenRuntimesTier0(treeItem, insights, false, true, false, true, ".Net Core 5.0/.Net Core 7.0 Tier 0 Diff");
        });

        vscode.commands.registerCommand("dotnetInsights.diffSixVsSevenTier0", (treeItem: Dependency) => {
            doDiffBetweenRuntimesTier0(treeItem, insights, false, false, true, true, ".Net Core 6.0/.Net Core 7.0 Tier 0 Diff");
        });

        vscode.commands.registerCommand("dotnetInsights.diffThreeVsFiveTier1", (treeItem: Dependency) => {
            doDiffBetweenRuntimesTier1(treeItem, insights, true, true, false, false, ".Net Core 3.1/.Net Core 5.0 Tier 1 Diff");
        });

        vscode.commands.registerCommand("dotnetInsights.diffThreeVsSixTier1", (treeItem: Dependency) => {
            doDiffBetweenRuntimesTier1(treeItem, insights, true, false, true, false, ".Net Core 3.1/.Net Core 6.0 Tier 1 Diff");
        });

        vscode.commands.registerCommand("dotnetInsights.diffThreeVsSevenTier1", (treeItem: Dependency) => {
            doDiffBetweenRuntimesTier1(treeItem, insights, true, false, false, true, ".Net Core 3.1/.Net Core 7.0 Tier 1 Diff");
        });

        vscode.commands.registerCommand("dotnetInsights.diffFiveVsSixTier1", (treeItem: Dependency) => {
            doDiffBetweenRuntimesTier1(treeItem, insights, false, true, true, false, ".Net Core 5.0/.Net Core 6.0 Tier 1 Diff");
        });

        vscode.commands.registerCommand("dotnetInsights.diffFiveVsSevenTier1", (treeItem: Dependency) => {
            doDiffBetweenRuntimesTier1(treeItem, insights, false, true, false, true, ".Net Core 5.0/.Net Core 7.0 Tier 1 Diff");
        });

        vscode.commands.registerCommand("dotnetInsights.diffSixVsSevenTier1", (treeItem: Dependency) => {
            doDiffBetweenRuntimesTier1(treeItem, insights, false, false, true, true, ".Net Core 6.0/.Net Core 7.0 Tier 1 Diff");
        });

        vscode.commands.registerCommand("dotnetInsights.diff", (treeItem: Dependency) => {
            doDiffBetweenRuntimesTier1(treeItem, insights, false, false, true, true, ".Net Core 6.0/.Net Core 7.0 Tier 1 Diff");
        });

        vscode.commands.registerCommand('dotnetInsights.minOpts', (treeItem: Dependency) => {
            const id = crypto.randomBytes(16).toString("hex");
            const outputFileName = path.join(insights.pmiOutputPath, id + ".asm");

            compile(true, false, treeItem, insights, outputFileName, insights.coreRunPath, insights.pmiPath).then((succes: any) => {
                if (succes !== undefined && success === true) {
                    vscode.workspace.openTextDocument(outputFileName).then(doc => {
                        vscode.window.showTextDocument(doc, 1);
                    });
                }
            });
        });

        vscode.commands.registerCommand("dotnetInsights.tier1", (treeItem: Dependency) => {
            const id = crypto.randomBytes(16).toString("hex");
            const outputFileName = path.join(insights.pmiOutputPath, id + ".asm");

            compile(false, false, treeItem, insights, outputFileName, insights.coreRunPath, insights.pmiPath).then((succes: any) => {
                if (succes !== undefined && success === true) {
                    vscode.workspace.openTextDocument(outputFileName).then(doc => {
                        vscode.window.showTextDocument(doc, 1);
                    });
                }
            });
        });

        vscode.commands.registerCommand('dotnetInsights.jitDumpTier0', (treeItem: Dependency) => {
            const id = crypto.randomBytes(16).toString("hex");
            const outputFileName = path.join(insights.pmiOutputPath, id + ".asm");

            compile(true, true, treeItem, insights, outputFileName, insights.coreRunPath, insights.pmiPath).then((succes: any) => {
                if (succes !== undefined && success === true) {
                    vscode.workspace.openTextDocument(outputFileName).then(doc => {
                        vscode.window.showTextDocument(doc, 1);
                    });
                }
            });
        });

        vscode.commands.registerCommand("dotnetInsights.jitDumpTier1", (treeItem: Dependency) => {
            const id = crypto.randomBytes(16).toString("hex");
            const outputFileName = path.join(insights.pmiOutputPath, id + ".asm");

            compile(false, true, treeItem, insights, outputFileName, insights.coreRunPath, insights.pmiPath).then((succes: any) => {
                if (succes !== undefined && success === true) {
                    vscode.workspace.openTextDocument(outputFileName).then(doc => {
                        vscode.window.showTextDocument(doc, 1);
                    });
                }
            });
        });

        var stopShowIlOnSave = vscode.commands.registerCommand("dotnetInsights.stopShowIlOnSave", () => {
            insights.listeningToAllSaveEvents = false;
        });

        context.subscriptions.push(stopShowIlOnSave);

        vscode.commands.registerCommand("dotnetInsights.showJitDump", () => {
            var activeFile  = vscode.window.activeTextEditor?.document.uri.fsPath;

            if (activeFile == undefined) {
                return;
            }

            console.log(path.extname(activeFile) == ".asm");

            // We have an asm file active, we have generated the jitdump side by
            // side, just display that file

            const visibleEditors = vscode.window.visibleTextEditors;
            var index = 0;

            for (index = 0; index < visibleEditors.length; ++index) {
                if (visibleEditors[index].document.uri.fsPath == activeFile) {
                    break;
                }
            }

            const jitDumpFile = activeFile?.replace(".asm", ".jitDump");

            if (!fs.existsSync(jitDumpFile)) {
                return;
            }

            vscode.workspace.openTextDocument(jitDumpFile).then(doc => {
                vscode.window.showTextDocument(doc, index);
            });
        });

        vscode.commands.registerCommand("dotnetInsights.showAsm", () => {
            var activeFile  = vscode.window.activeTextEditor?.document.uri.fsPath;

            if (activeFile == undefined) {
                return;
            }

            console.log(path.extname(activeFile) == ".jitdump" || path.extname(activeFile) == ".jitDump");

            // We have an asm file active, we have generated the jitdump side by
            // side, just display that file

            const visibleEditors = vscode.window.visibleTextEditors;
            var index = 0;

            for (index = 0; index < visibleEditors.length; ++index) {
                if (visibleEditors[index].document.uri.fsPath == activeFile) {
                    break;
                }
            }

            const asmFile = activeFile?.replace(".jitDump", ".asm");

            if (!fs.existsSync(asmFile)) {
                return;
            }

            vscode.workspace.openTextDocument(asmFile).then(doc => {
                vscode.window.showTextDocument(doc, index);
            });
        });

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

        vscode.commands.registerCommand("dotnetInsights.loadEvents", (treeItem: JitDependency) => {
            if (treeItem.label !== undefined) {
                let pid = treeItem.pid!;

                // We will create a json file with the load events and then open a custom
                // document
                
                const id = crypto.randomBytes(16).toString("hex");
                const outputFileName = path.join(insights.gcDataSaveLocation, pid + "---" + id + ".netloadinfo");

                const processInfo = listener?.processes.get(parseInt(pid!));
                var methodLoadEvents = Array.from(processInfo!.jitData);

                var dataToPass = [
                    processInfo?.processName,
                    methodLoadEvents
                ];

                const methodLoadEventsStr = JSON.stringify(dataToPass);

                fs.writeFile(outputFileName, methodLoadEventsStr, (error) => {
                    if (error) {
                        return;
                    }
                    vscode.commands.executeCommand("vscode.openWith", vscode.Uri.file(outputFileName), DotnetInsightsRuntimeLoadEventsEditor.viewType);
                });
            }
        });

        context.subscriptions.push(DotnetInsightsTextEditorProvider.register(context, insights));
        context.subscriptions.push(DotnetInsightsGcEditor.register(context, insights, listener));
        context.subscriptions.push(DotnetInsightsGcSnapshotEditor.register(context, insights));
        context.subscriptions.push(DotnetInsightsRuntimeLoadEventsEditor.register(context, insights));

        if (startupCallback != undefined) {
            startupCallback();
        }

        didFinishStartup = true;
    });
}

export function deactivate() {
    console.log("dotnetInsights: deactivated.");
}