////////////////////////////////////////////////////////////////////////////////
// Module: extension.ts
////////////////////////////////////////////////////////////////////////////////

import * as child from 'child_process';
import * as fs from 'fs';
import * as path from 'path';
import * as vscode from 'vscode';
import * as os from "os" 

import * as request from 'request';
import * as crypto from "crypto";

import * as targz from "targz";

import { DotnetInsightsTreeDataProvider, Dependency, DotnetInsights } from './dotnetInsights';
import { DotnetInsightsTextEditorProvider } from "./DotnetInightsTextEditor";
import { DotnetInsightsGcTreeDataProvider, GcDependency } from "./dotnetInsightsGc";
import { DotnetInsightsGcEditor } from "./DotnetInsightsGcEditor";

import { GcListener } from "./GcListener";

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

export function activate(context: vscode.ExtensionContext) {
    const outputChannel = vscode.window.createOutputChannel(`.NET Insights`);

    var config = vscode.workspace.getConfiguration();
    var dotnetInsightsSettings: any = config.get("dotnet-insights");

    outputChannel.appendLine('dotnetInsights: started');

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
    const latestListenerVersionNumber = "0.4.1";

    var childProcess: child.ChildProcess | undefined = undefined;
    var startupCallback: any = undefined;

    var startGcMonitor = vscode.commands.registerCommand("dotnetInsights.startGCMonitor", () => {
        if (startupCallback == undefined) {
            startupCallback = () => {
                if (insights.listener == undefined) return;

                insights.listener.sendShutdown = false;
                insights.listener.start();

                // Check if we are able to run to application
                childProcess = child.exec(insights.gcEventListenerPath, (stdout, stderr) => {
                    if (stderr.indexOf("ETW Event listening required Privilidged Access. Please run as Administrator") != -1) {
                        vscode.window.showInformationMessage(`To automatically launch VSCode must be run elevated. In an elevated command prompt run: ${insights.gcEventListenerPath}`);
                        childProcess = undefined;
                    }
                });

                insights.outputChannel.appendLine("Starting monitoring GCs.");
            };

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
            insights.listener.httpServer.close();
            insights.outputChannel.appendLine("Stopped monitoring GCs.");
        }
    }); 

    context.subscriptions.push(stopGCMonitor);

    // Setup
    setup(lastestVersionNumber, latestListenerVersionNumber, context, insights).then((success: boolean) => {
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

        if (startupCallback != undefined) {
            startupCallback();
        }

        const dotnetInsightsTreeDataProvider = new DotnetInsightsTreeDataProvider(insights);
        const dotnetInsightsGcTreeDataProvider = new DotnetInsightsGcTreeDataProvider(listener);

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

                                vscode.commands.executeCommand("vscode.diff", vscode.Uri.file(threeOnefilePath), vscode.Uri.file(outputFileName), ".Net Core 3.1/.Net Core 5.0 Tier 0 Diff");
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
    });
}

export function deactivate() {
    console.log("dotnetInsights: deactivated.");
}

function setupIlDasm(insights: DotnetInsights, callback: (insights: DotnetInsights) => Thenable<boolean>): Thenable<boolean> {
    const ilDasmPath = insights.ilDasmPath;
    insights.outputChannel.appendLine("ILDasm Path: " + ilDasmPath);

    // Verify that the ildasm path exists and the executable runs
    var ildasmCommand = `"${ilDasmPath}"` + " " + "/?";
    insights.outputChannel.appendLine(ildasmCommand);

    var childProcess : child.ChildProcess = child.exec(ildasmCommand, (error: any, stdout: string, stderr: string) => {
        var success = false;
        
        if (error != null) {
            insights.outputChannel.appendLine(stderr);
            if (os.platform() != "win32") {
                // If on !windows set chmod +x to corerun and ildasm
                fs.chmodSync(ilDasmPath, "0755");

                const coreRoot = path.basename(ilDasmPath);
                const coreRunPath = path.join(coreRoot, "corerun");
                fs.chmodSync(coreRunPath, "0755");

                try {
                    child.execSync(ilDasmPath);
                }
                catch (e) {
                    insights.outputChannel.appendLine(stderr);
                }
            }
        }
        else {
            try {
                // No error, output should have a version number
                var splitOutput = stdout.split("IL Disassembler.")[1];

                var versionNumber = splitOutput.split("Version ")[1].split("\n")[0];
                insights.outputChannel.appendLine("Working ilDasm: Version Number: " + versionNumber);

                insights.ilDasmPath = ilDasmPath;
                insights.ilDasmVersion = versionNumber;

                success = true;
            }
            catch {
                console.error("Failed to setup .NET ILDasm.");
            }
        }
    });

    return new Promise((resolve, reject) => {
        childProcess.addListener("close", (args: any) => {
            callback(insights).then((success: boolean) => {
                resolve(success);
            });
        });
    });
}

function setupSuperPmiForVersion(version: string, localRuntimeBuild: string | null) {
    if (localRuntimeBuild != null) {
        // We will not need to do a download
        var config = vscode.workspace.getConfiguration("defaultValue");

        console.log(config);
    }
    else {
        console.error("Local path currently is required the setup is incomplete.");
    }
}

function setupPmi(insights: DotnetInsights) : Thenable<boolean> {
    const pmiPath = insights.pmiPath;
    insights.outputChannel.appendLine("PMI Path: " + pmiPath);

    // Verify that the ildasm path exists and the executable runs
    insights.outputChannel.appendLine("Found PMI on disk.");

    // Verify it runs
    var pmiCommand = `"${insights.coreRunPath}"` + " " + `"${pmiPath}"` + " " + "-h";
    insights.outputChannel.appendLine(pmiCommand);

    var success = false;
    var childProcess : child.ChildProcess = child.exec(pmiCommand, (error: any, stdout: string, stderr: string) => {
        if (error != null && stdout == undefined) {
            insights.outputChannel.appendLine(stderr);
        }
        else {
            success = true;
            insights.outputChannel.appendLine("PMI setup successfully.");
        }
    });

    return new Promise((resolve, reject) => {
        childProcess.addListener("close", (args: any) => {
            resolve(success);
        });
    });
}

function checkForDotnetSdk(insights: DotnetInsights) : Thenable<boolean> {
    // Check for the dotnet sdk. This is not necessary, it is possible to look
    // at managed pe files as well. In addition, we can hijack the jit dropped 
    // into the publish folder for superPMI.

    var dotnetCommand = "dotnet --list-sdks"

    var childProcess: child.ChildProcess = child.exec(dotnetCommand, (error: any, stdout: string, stderr: string) => {
        if (error != null) {
            // The dotnet sdk is not installed.
            // We do not currently know what version to install.

            return;
        }
        else {
            var lines = stdout.split("\n");

            var installedSdks = [];

            insights.outputChannel.appendLine("Installed SDK Versions:");
            for (var index = 0; index < lines.length; ++index) {
                var sdkVersion = lines[index].split(" ")[0]

                insights.outputChannel.appendLine(sdkVersion);
                installedSdks.push(sdkVersion);
            }
            
            insights.sdkVersions = installedSdks;
        }
    });

    return new Promise((resolve, reject) => {
        childProcess.addListener("close", (args: any) => {
            setupPmi(insights).then((success: boolean) => {
                resolve(success);
            });
        });
    });
}

function downloadAnUnzip(insights: DotnetInsights, url: string, unzipFolder: string, outputPath: string) : Thenable<void> {
    const unzipName = path.join(unzipFolder, crypto.randomBytes(16).toString("hex") + ".tar.gz");

    try {
        const fileStream = fs.createWriteStream(unzipName);

        insights.outputChannel.appendLine(`[${url}] -> ${unzipName}`);

        var req = request(url).pipe(fileStream);

        return new Promise((resolve, reject) => {
            req.on("close", (response: any) => {
                insights.outputChannel.appendLine(`Download completed: ${unzipName}`);
                insights.outputChannel.appendLine(`unzip ${unzipName}`);

                targz.decompress({
                    src: unzipName,
                    dest: outputPath
                }, function(err){
                    if(err) {
                        insights.outputChannel.appendLine(`unzip failed: ${unzipName}`);
                        reject();
                    } else {
                        insights.outputChannel.appendLine(`unzip completed: ${unzipName}`);
                        resolve();
                    }
                });
            });
        });
    }
    catch (e) {
        // Clean up temp zip file, which is large
        if (fs.existsSync(unzipName)) {
            fs.unlinkSync(unzipName);
        }

        return Promise.resolve();
    }
}

function downloadRuntimes(insights: DotnetInsights, versionNumber: string, unzipFolder: string) : Thenable<void[]> {
    const runtimes = ["netcore3.1", "net5.0"];
    const coreRootFolder = unzipFolder;

    unzipFolder = path.join(unzipFolder, "temp");

    if (!fs.existsSync(coreRootFolder)) {
        fs.mkdirSync(coreRootFolder);
    }

    if (!fs.existsSync(unzipFolder)) {
        fs.mkdirSync(unzipFolder);
    }

    const baseRuntimeUrl = `https://github.com/jashook/vscode-dotnet-insights/releases/download/${versionNumber}/`;

    var promises = [];
    const arch = "x64";

    for (var index = 0; index < runtimes.length; ++index) {
        var osName = "osx";
        if (os.platform() == "win32") {
            osName = "win";
        }
        else if (os.platform() != "darwin") {
            osName = "linux";
        }

        const outputPath = path.join(coreRootFolder, runtimes[index]);

        const runtimeUrl = baseRuntimeUrl + `${osName}-${arch}-${runtimes[index]}.tar.gz`;
        promises.push(downloadAnUnzip(insights, runtimeUrl, unzipFolder, outputPath));
    }

    return Promise.all(promises);
}

function downloadPmiExe(insights: DotnetInsights, versionNumber: string, unzipFolder: string) : Thenable<void[]> {
    const runtimes = ["netcore3.1", "net5.0"];
    const pmiExeFolder = unzipFolder;

    unzipFolder = path.join(unzipFolder, "temp");

    if (!fs.existsSync(pmiExeFolder)) {
        fs.mkdirSync(pmiExeFolder);
    }

    if (!fs.existsSync(unzipFolder)) {
        fs.mkdirSync(unzipFolder);
    }

    var promises = [];

    const arch = "x64";
    const baseUrl = `https://github.com/jashook/vscode-dotnet-insights/releases/download/${versionNumber}/`;

    for (var index = 0; index < runtimes.length; ++index) {
        // Always download the windows built binaries for pmi
        var osName = "win";

        const outputPath = path.join(pmiExeFolder, runtimes[index]);

        const pmiUrl = baseUrl + `${osName}-${arch}-${runtimes[index]}-pmi.tar.gz`;
        promises.push(downloadAnUnzip(insights, pmiUrl, unzipFolder, outputPath));
    }

    return Promise.all(promises);
}

function downloadGcMonitorExe(insights: DotnetInsights, versionNumber: string, unzipFolder: string) : Thenable<void[]> {
    const exeFolder = unzipFolder;

    unzipFolder = path.join(unzipFolder, "temp");

    if (!fs.existsSync(exeFolder)) {
        fs.mkdirSync(exeFolder);
    }

    if (!fs.existsSync(unzipFolder)) {
        fs.mkdirSync(unzipFolder);
    }

    var promises = [];

    var osName = "osx";
    if (os.platform() == "win32") {
        osName = "win";
    }
    else if (os.platform() != "darwin") {
        osName = "linux";
    }

    const arch = "x64";
    const baseUrl = `https://github.com/jashook/vscode-dotnet-insights/releases/download/${versionNumber}/gcEventListener-${osName}.tar.gz`;

    promises.push(downloadAnUnzip(insights, baseUrl, unzipFolder, exeFolder));
    return Promise.all(promises);
}

function setup(lastestVersionNumber: string, latestListenerVersionNumber: string, context: vscode.ExtensionContext, insights: DotnetInsights) : Thenable<boolean>  {
    insights.outputChannel.appendLine("Setting up dotnetInsights.");

    const config = vscode.workspace.getConfiguration();
    var dotnetInsightsSettings: any = config.get("dotnet-insights");

    
    var ilDasmPath:any = undefined;
    var pmiPath: any = undefined;
    var netcoreThreePmiPath: any = undefined;
    var netcoreFivePmiPath: any = undefined;
    var customCoreRootPath: any  = undefined

    var gcEventListenerPath: any = undefined;

    var netcoreFiveCoreRootPath: any = undefined;
    var netcoreThreeCoreRootPath: any = undefined;

    var outputPath: any = undefined;

    if (dotnetInsightsSettings != undefined) {
        ilDasmPath = dotnetInsightsSettings["ildasmPath"];
        pmiPath = dotnetInsightsSettings["pmiPath"];
        customCoreRootPath = dotnetInsightsSettings["coreRoot"];
        outputPath = dotnetInsightsSettings["outputPath"];

        gcEventListenerPath = dotnetInsightsSettings["gcEventListenerPath"];

        if (dotnetInsightsSettings["preferNetCoreThreeOne"] != undefined && dotnetInsightsSettings["preferNetCoreThreeOne"] == true) {
            insights.useNetCoreThree = true;
        }

        if (customCoreRootPath == undefined) {
            customCoreRootPath = "";
        }
    }
    else {
        customCoreRootPath = "";
    }

    if (outputPath == undefined) {
        outputPath = context.globalStorageUri?.fsPath;
    }

    if (outputPath == "" || outputPath == undefined) {
        console.error("outputPath must be set!");
    }

    if (!fs.existsSync(outputPath)) {
        // Create the folder
        fs.mkdirSync(outputPath);
    }

    var ilDasmOutputPath = path.join(outputPath, "ilDasm");

    if (!fs.existsSync(ilDasmOutputPath)) {
        // Create the folder
        fs.mkdirSync(ilDasmOutputPath);
    }

    var pmiOutputPath = path.join(outputPath, "PMI");

    if (!fs.existsSync(pmiOutputPath)) {
        fs.mkdirSync(pmiOutputPath);
    }

    const pmiTempDir = path.join(outputPath, "pmiTemp");

    if (!fs.existsSync(pmiTempDir)) {
        fs.mkdirSync(pmiTempDir);
    }

    var promises: Thenable<boolean>[] = [];

    var osVer = "osx";
    if (os.platform() == 'win32') {
        osVer = "win";
    }
    else if (os.platform() != "darwin") {
        osVer = "linux";
    }

    const latestToolFile = path.join(outputPath, lastestVersionNumber + ".txt");
    const latestListenerFile = path.join(outputPath, latestListenerVersionNumber + ".txt");

    var forceDownload = false;
    if (!fs.existsSync(latestToolFile) || fs.readFileSync(latestToolFile).toString() != lastestVersionNumber) {
        forceDownload = true;
    }

    var forceListenerDownload = false;
    if (!fs.existsSync(latestListenerFile) || fs.readFileSync(latestToolFile).toString() != latestListenerVersionNumber) {
        forceListenerDownload = true;
    }

    // ildasm comes with the core_root
    if (ilDasmPath == undefined) {
        const coreRootPath = path.join(outputPath, "coreRoot");

        netcoreFiveCoreRootPath = path.join(coreRootPath, "net5.0", "Core_Root");
        netcoreThreeCoreRootPath = path.join(coreRootPath, "netcore3.1", "Core_Root");
        var ilDasmCoreRootPath = path.join(netcoreFiveCoreRootPath, "ildasm.exe");
        if (os.platform() != "win32") {
            ilDasmCoreRootPath = path.join(netcoreFiveCoreRootPath, "ildasm");
        }

        var doDownload = false;

        if (forceDownload ||
            !fs.existsSync(netcoreFiveCoreRootPath) || 
            !fs.existsSync(netcoreThreeCoreRootPath) ||
            !fs.existsSync(ilDasmCoreRootPath)) {
            doDownload = true;
        }

        if (doDownload) {
            var promise: Thenable<boolean> = new Promise((resolve, reject) => {
                downloadRuntimes(insights, lastestVersionNumber, coreRootPath).then(() => {
                    // We will expect to now have coreRootPath/net5.0/Core_Root and coreRootPath/netcoreapp3.1/Core_Root
                    var runtimeDownloadSucceeded = false;
    
                    if (fs.existsSync(netcoreFiveCoreRootPath) &&
                        fs.existsSync(netcoreThreeCoreRootPath) &&
                        fs.existsSync(ilDasmCoreRootPath)) {
                        runtimeDownloadSucceeded = true;
                    }
    
                    if (ilDasmPath == undefined) {
                        ilDasmPath = ilDasmCoreRootPath;
                    }
    
                    if (os.platform() != "win32") {
                        // If on !windows set chmod +x to corerun and ildasm
                        fs.chmodSync(ilDasmPath, "0755");
    
                        var coreRunPath = path.join(netcoreFiveCoreRootPath, "corerun");
                        fs.chmodSync(coreRunPath, "0755");

                        coreRunPath = path.join(netcoreThreeCoreRootPath, "corerun");
                        fs.chmodSync(coreRunPath, "0755");

                        if (customCoreRootPath != "") {
                            coreRunPath = path.join(customCoreRootPath, "corerun");
                            fs.chmodSync(coreRunPath, "0755");
                        }
                    }
    
                    if (!runtimeDownloadSucceeded) {
                        vscode.window.showWarningMessage("Unable to download runtime successfully.");
                    }

                    resolve(runtimeDownloadSucceeded);
                });
            });

            promises.push(promise);
        }
        else {
            const coreRootPath = path.join(outputPath, "coreRoot");
            netcoreFiveCoreRootPath = path.join(coreRootPath, "net5.0", "Core_Root");
            netcoreThreeCoreRootPath = path.join(coreRootPath, "netcore3.1", "Core_Root");

            if (ilDasmPath == undefined) {
                ilDasmPath = ilDasmCoreRootPath;
            }
        }
    }

    if (pmiPath == undefined) {
        const pmiExePath = path.join(outputPath, "pmiExe");

        const netcoreThreePmiPathDownload = path.join(pmiExePath, "netcore3.1", "netcoreapp3.1", "pmi.dll");
        const netcoreFivePmiPathDownload = path.join(pmiExePath, "net5.0", "net5.0", "pmi.dll");

        var doDownload = false;
        if (forceDownload || !fs.existsSync(netcoreThreePmiPathDownload) || !fs.existsSync(netcoreFivePmiPathDownload)) {
            doDownload = true;
        }

        if (doDownload) {
            var promise: Thenable<boolean> = new Promise((resolve, reject) => {
                downloadPmiExe(insights, lastestVersionNumber, pmiExePath).then(() => {
                    // We will expect to now have net5.0/net5.0/pmi.dll and netcore/netcoreapp3.1/pmi.dll
                    var pmiDownloadSucceeded = false;

                    if (fs.existsSync(netcoreFivePmiPathDownload) && fs.existsSync(netcoreThreePmiPathDownload)) {
                        pmiDownloadSucceeded = true;
                    }

                    netcoreThreePmiPath = netcoreThreePmiPathDownload;
                    netcoreFivePmiPath = netcoreFivePmiPathDownload;

                    if (!pmiDownloadSucceeded) {
                        vscode.window.showWarningMessage("Unable to download pmi successfully.");
                    }

                    resolve(pmiDownloadSucceeded);
                });
            });

            promises.push(promise)
        }
        else {
            netcoreFivePmiPath = netcoreFivePmiPathDownload;
            netcoreThreePmiPath = netcoreThreePmiPathDownload;
        }
    }

    if (gcEventListenerPath == undefined) {
        const gcEventListenerTempDir = path.join(outputPath, "gcEventListener");

        gcEventListenerPath = path.join(gcEventListenerTempDir, "gcEventListener", "gcEventListener.exe");
        insights.gcEventListenerPath = gcEventListenerPath;

        var doDownload = false;
        if (forceDownload || forceListenerDownload || !fs.existsSync(gcEventListenerTempDir) || !fs.existsSync(gcEventListenerPath)) {
            doDownload = true;
        }

        if (doDownload) {
            var promise: Thenable<boolean> = new Promise((resolve, reject) => {
                downloadGcMonitorExe(insights, latestListenerVersionNumber, gcEventListenerTempDir).then(() => {
                    
                    var downloadSucceeded = false;

                    if (fs.existsSync(gcEventListenerTempDir) && fs.existsSync(gcEventListenerPath)) {
                        downloadSucceeded = true;
                    }

                    if (!downloadSucceeded) {
                        vscode.window.showWarningMessage("Unable to download gcEventListenerPath successfully.");
                    }

                    resolve(downloadSucceeded);
                });
            });

            promises.push(promise)
        }
    }
    else {
        insights.outputChannel.appendLine(`gcEventListenerPath: ${gcEventListenerPath}`);
        insights.gcEventListenerPath = gcEventListenerPath;
    }

    if (promises.length > 0) {
        return new Promise((resolve, reject) => {
            Promise.all(promises).then((successes) => {
                var didSucceed = true;
                fs.writeFileSync(latestToolFile, lastestVersionNumber);
                fs.writeFileSync(latestListenerFile, latestListenerVersionNumber);

                for (var index = 0; index < successes.length; ++index) {
                    didSucceed = didSucceed && successes[index];
                }

                if (!didSucceed) {
                    resolve(false);
                }
                else {
                    resolve(continueSetup(insights, ilDasmPath, netcoreFivePmiPath, netcoreThreePmiPath, pmiPath, netcoreFiveCoreRootPath, netcoreThreeCoreRootPath, customCoreRootPath, outputPath, ilDasmOutputPath, pmiOutputPath, pmiTempDir));
                }
            });
        });
    }
    else {
        return continueSetup(insights, ilDasmPath, netcoreFivePmiPath, netcoreThreePmiPath, pmiPath, netcoreFiveCoreRootPath, netcoreThreeCoreRootPath, customCoreRootPath, outputPath, ilDasmOutputPath, pmiOutputPath, pmiTempDir);
    }
}


function continueSetup(insights: DotnetInsights, ilDasmPath: any, netcoreFivePmiPath: any, netcoreThreePmiPath: any, customPmiPath: any, netcoreFiveCoreRoot: any, netcoreThreeCoreRootPath: any, customCoreRootPath: any, outputPath: any, ilDasmOutputPath: string, pmiOutputPath: string, pmiTempDir: string) : Thenable<boolean> {
    if (typeof(ilDasmPath) != "string") {
        if (os.platform() == "darwin") {
            ilDasmPath = ilDasmPath["osx"];
        }
        else if (os.platform() == "linux") {
            ilDasmPath = ilDasmPath["linux"];
        }
        else {
            ilDasmPath = ilDasmPath["windows"];
        }
    }

    if (typeof(customPmiPath) != "string" && customPmiPath != undefined) {
        if (os.platform() == "darwin") {
            customPmiPath = customPmiPath["osx"];
        }
        else if (os.platform() == "linux") {
            customPmiPath = customPmiPath["linux"];
        }
        else {
            customPmiPath = customPmiPath["windows"];
        }
    }

    if (typeof(customCoreRootPath) != "string") {
        if (os.platform() == "darwin") {
            customCoreRootPath = customCoreRootPath["osx"];
        }
        else if (os.platform() == "linux") {
            customCoreRootPath = customCoreRootPath["linux"];
        }
        else {
            customCoreRootPath = customCoreRootPath["windows"];
        }
    }

    if (typeof(outputPath) != "string") {
        if (os.platform() == "darwin") {
            outputPath = outputPath["osx"];
        }
        else if (os.platform() == "linux") {
            outputPath = outputPath["linux"];
        }
        else {
            outputPath = outputPath["windows"];
        }
    }

    insights.ilDasmPath = ilDasmPath;

    insights.netcoreThreeCoreRootPath = netcoreThreeCoreRootPath;
    insights.netcoreFiveCoreRootPath = netcoreFiveCoreRoot;

    insights.customCoreRootPath = customCoreRootPath;

    insights.ilDasmOutputPath = ilDasmOutputPath;
    insights.pmiOutputPath = pmiOutputPath;

    insights.pmiTempDir = pmiTempDir;

    // Setup mostly will involve making sure we have the dotnet tools required
    // to provide insights.

    var coreRunExe = "";
    if (os.platform() == "win32") {
        coreRunExe = "CoreRun.exe";
    }
    else {
        coreRunExe = "corerun";
    }

    insights.netcoreFiveCoreRunPath = path.join(insights.netcoreFiveCoreRootPath, coreRunExe);
    insights.netcoreThreeCoreRunPath = path.join(insights.netcoreThreeCoreRootPath, coreRunExe);

    if (!fs.existsSync(insights.netcoreFiveCoreRunPath) || 
        !fs.existsSync(insights.netcoreThreeCoreRunPath)) {
        vscode.window.showWarningMessage("Failed to set corerun path.");
        Promise.resolve(false);
    }

    insights.netcoreThreePmiPath = netcoreThreePmiPath;
    insights.netcoreFivePmiPath = netcoreFivePmiPath;

    if (insights.useNetCoreThree) {
        insights.pmiPath = netcoreThreePmiPath;
    }
    else {
        insights.pmiPath = netcoreFivePmiPath;
    }

    if (customPmiPath != "" && customPmiPath != undefined) {
        insights.pmiPath = customPmiPath;
    }

    if (insights.customCoreRootPath == "" || insights.customCoreRootPath == "") {
        if (insights.useNetCoreThree) {
            insights.coreRunPath = insights.netcoreThreeCoreRunPath;
        }
        else {
            insights.coreRunPath = insights.netcoreFiveCoreRunPath;
        }
    }
    else {
        insights.customCoreRunPath = path.join(insights.customCoreRootPath, coreRunExe);
        insights.coreRunPath = insights.customCoreRunPath;
    }

    if (os.platform() != "win32") {
        fs.chmodSync(insights.ilDasmPath, "0755");
        const coreRoot = path.basename(insights.ilDasmPath);

        fs.chmodSync(insights.netcoreThreeCoreRunPath, "0755");
        fs.chmodSync(insights.netcoreFiveCoreRunPath, "0755");
    }

    return setupIlDasm(insights, checkForDotnetSdk);
}