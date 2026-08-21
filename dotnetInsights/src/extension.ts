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
import { DotnetInsightsGcDumpEditor } from "./DotnetInsightsGcDumpEditor";
import { DotnetInsightsNettraceEditor } from "./DotnetInsightsNettraceEditor";
import { DotnetInsightsRuntimeLoadEventsEditor } from "./DotnetInsightsRuntimeLoadEventsEditor";
import { DependencySetup } from "./DependencySetup";
import { showNettraceDiff, pickCapturesToDiff } from "./NettraceDiffPanel";

import { GcListener } from "./GcListener";
import { OnSaveIlDasm } from './onSaveIlDasm';
import { DotnetInsightsJitTreeDataProvider, JitDependency } from './dotnetInsightsJit';

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

/**
 * Shows a disassembly or JIT dump this extension has just generated.
 *
 * preview: false is the load-bearing part. A preview tab gets reused, so
 * generating a second listing closed the first one's tab - and comparing two
 * listings (min opts against tier 1, asm against jit dump) is the reason to
 * generate them in the first place. Same family as issue #99: opening one of
 * this extension's own outputs must not close an editor the user already had.
 */
export function showGeneratedDocument(outputFileName: string): Thenable<vscode.TextEditor> {
    return vscode.workspace.openTextDocument(outputFileName).then(doc => {
        return vscode.window.showTextDocument(doc, {
            viewColumn: vscode.ViewColumn.One,
            preview: false
        });
    });
}

/**
 * Shows the counterpart of a listing the user is looking at - the jit dump for
 * an .asm, or the .asm for a jit dump - in the column that listing occupies.
 *
 * The column comes from the editor itself. This used to find the active file's
 * position in visibleTextEditors and pass that 0-based array index as a 1-based
 * ViewColumn, which lined up only by accident: index 0 resolves to column One,
 * so a listing in the FIRST group happened to work, while one in the second
 * group produced index 1 - column One again - and put the counterpart in the
 * wrong group. visibleTextEditors is not ordered by column either.
 */
export function showCounterpartListing(counterpartFileName: string, sourceEditor: vscode.TextEditor): Thenable<vscode.TextEditor> {
    var viewColumn = sourceEditor.viewColumn === undefined ? vscode.ViewColumn.Active : sourceEditor.viewColumn;

    return vscode.workspace.openTextDocument(counterpartFileName).then(doc => {
        return vscode.window.showTextDocument(doc, {
            viewColumn: viewColumn,
            preview: false
        });
    });
}

function compile(minOpts: boolean, jitDump: boolean, treeItem: Dependency, insights: DotnetInsights, outputFileName: string, coreRunPath: string, pmiPath: string) {
    var methodName = treeItem.label;
    var methodNameSplit = treeItem.label.split(":");
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
                // eslint-disable-next-line @typescript-eslint/naming-convention
                "COMPlus_JitDisasm": `${methodName}`,
                // eslint-disable-next-line @typescript-eslint/naming-convention
                "COMPlus_JITMinOpts": "1",
                // eslint-disable-next-line @typescript-eslint/naming-convention
                "COMPlus_JitGCDump": `${methodName}`
            };

            if (minOpts === false) {
                envToUse = {
                    // eslint-disable-next-line @typescript-eslint/naming-convention
                    "COMPlus_JitDisasm": `${methodName}`,
                    // eslint-disable-next-line @typescript-eslint/naming-convention
                    "COMPlus_TieredCompilation": "0",
                    // eslint-disable-next-line @typescript-eslint/naming-convention
                    "COMPlus_TC_QuickJit": "0",
                    // eslint-disable-next-line @typescript-eslint/naming-convention
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

function doDiffBetweenRuntimesTier0(treeItem: Dependency, insights: DotnetInsights, six: boolean, seven: boolean, eight: boolean, nine:boolean, ten: boolean,  description: string) {
    var baseCoreRunPath: string|undefined = undefined;
    var basePmiPath: string|undefined = undefined;

    var diffCoreRunPath: string|undefined = undefined;
    var diffPmiPath: string|undefined = undefined;

    const isArm64 = process.arch === "arm64";

    var paths = new Map<number, string[]>();

    if (six === true) {
        const useArm64 = isArm64 && insights.netcoreSixArm64CoreRunPath !== "";

        paths.set(6, [
            useArm64 ? insights.netcoreSixArm64CoreRunPath : insights.netcoreSixX64CoreRunPath,
            insights.netcoreSixPmiPath
        ]);
    }
    if (seven === true) {
        const useArm64 = isArm64 && insights.netcoreSevenArm64CoreRunPath !== "";

        paths.set(7, [
            useArm64 ? insights.netcoreSevenArm64CoreRunPath : insights.netcoreSevenX64CoreRunPath,
            insights.netcoreSevenPmiPath
        ]);
    }
    if (eight === true) {
        const useArm64 = isArm64 && insights.netcoreEightArm64CoreRunPath !== "";

        paths.set(8, [
            useArm64 ? insights.netcoreEightArm64CoreRunPath : insights.netcoreEightX64CoreRunPath,
            insights.netcoreEightPmiPath
        ]);
    }
    if (nine === true) {
        const useArm64 = isArm64 && insights.netcoreNineArm64CoreRunPath !== "";

        paths.set(9, [
            useArm64 ? insights.netcoreNineArm64CoreRunPath : insights.netcoreNineX64CoreRunPath,
            insights.netcoreNinePmiPath
        ]);
    }
    if (ten === true) {
        const useArm64 = isArm64 && insights.netcoreTenArm64CoreRunPath !== "";

        paths.set(10, [
            useArm64 ? insights.netcoreTenArm64CoreRunPath : insights.netcoreTenX64CoreRunPath,
            insights.netcoreTenPmiPath
        ]);
    }

    var keys: number[] = Array.from(paths.keys());

    var baseLineItem = keys[0] < keys[1] ? keys[0] : keys[1];
    var diffLineItem = keys[0] < keys[1] ? keys[1] : keys[0];

    baseCoreRunPath = paths.get(baseLineItem)![0];
    diffCoreRunPath = paths.get(diffLineItem)![0];

    basePmiPath = paths.get(baseLineItem)![1];
    diffPmiPath = paths.get(diffLineItem)![1];

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

            vscode.commands.executeCommand("vscode.diff", vscode.Uri.file(basefilePath), vscode.Uri.file(outputFileName), description);
        });
    });
}

function doDiffBetweenRuntimesTier1(treeItem: Dependency, insights: DotnetInsights, six: boolean, seven: boolean, eight: boolean, nine:boolean, ten: boolean,  description: string) {
    var baseCoreRunPath: string|undefined = undefined;
    var basePmiPath: string|undefined = undefined;

    var diffCoreRunPath: string|undefined = undefined;
    var diffPmiPath: string|undefined = undefined;

    const isArm64 = process.arch === "arm64";

    var paths = new Map<number, string[]>();

    if (six === true) {
        const useArm64 = isArm64 && insights.netcoreSixArm64CoreRunPath !== "";

        paths.set(6, [
            useArm64 ? insights.netcoreSixArm64CoreRunPath : insights.netcoreSixX64CoreRunPath,
            insights.netcoreSixPmiPath
        ]);
    }
    if (seven === true) {
        const useArm64 = isArm64 && insights.netcoreSevenArm64CoreRunPath !== "";

        paths.set(7, [
            useArm64 ? insights.netcoreSevenArm64CoreRunPath : insights.netcoreSevenX64CoreRunPath,
            insights.netcoreSevenPmiPath
        ]);
    }
    if (eight === true) {
        const useArm64 = isArm64 && insights.netcoreEightArm64CoreRunPath !== "";

        paths.set(8, [
            useArm64 ? insights.netcoreEightArm64CoreRunPath : insights.netcoreEightX64CoreRunPath,
            insights.netcoreEightPmiPath
        ]);
    }
    if (nine === true) {
        const useArm64 = isArm64 && insights.netcoreNineArm64CoreRunPath !== "";

        paths.set(9, [
            useArm64 ? insights.netcoreNineArm64CoreRunPath : insights.netcoreNineX64CoreRunPath,
            insights.netcoreNinePmiPath
        ]);
    }
    if (ten === true) {
        const useArm64 = isArm64 && insights.netcoreTenArm64CoreRunPath !== "";

        paths.set(10, [
            useArm64 ? insights.netcoreTenArm64CoreRunPath : insights.netcoreTenX64CoreRunPath,
            insights.netcoreTenPmiPath
        ]);
    }

    var keys: number[] = Array.from(paths.keys());

    var baseLineItem = keys[0] < keys[1] ? keys[0] : keys[1];
    var diffLineItem = keys[0] < keys[1] ? keys[1] : keys[0];

    baseCoreRunPath = paths.get(baseLineItem)![0];
    diffCoreRunPath = paths.get(diffLineItem)![0];

    basePmiPath = paths.get(baseLineItem)![1];
    diffPmiPath = paths.get(diffLineItem)![1];
    
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

            vscode.commands.executeCommand("vscode.diff", vscode.Uri.file(basefilePath), vscode.Uri.file(outputFileName), description);
        });
    });
}

export async function activate(context: vscode.ExtensionContext) {
    const outputChannel = vscode.window.createOutputChannel(`.NET Insights`);

    var config = vscode.workspace.getConfiguration();
    var dotnetInsightsSettings: any = config.get("dotnet-insights");

    outputChannel.appendLine('dotnetInsights: started');

    var dotnetInsightsGcTreeDataProvider: DotnetInsightsGcTreeDataProvider | undefined = undefined;
    var dotnetInsightsJitTreeDataProvider: DotnetInsightsJitTreeDataProvider | undefined = undefined;

    if (dotnetInsightsSettings !== undefined) {
        if (!dotnetInsightsSettings["surpressStartupMessage"]) {
            vscode.window.showInformationMessage(".NET Insights is starting");
        }
    }
    else {
        vscode.window.showInformationMessage(".NET Insights is starting");
    }

    var insights = new DotnetInsights(outputChannel);
    const lastestVersionNumber = "0.8.3";
    const latestListenerVersionNumber = "1.6.2";
    const latestRoslynVersionNumber = "1.6.2";
    // Bumped for the CPU/Contention/Exceptions view rework - unified inline
    // drill-down tables with manual + IO-bound row hiding (cascading into
    // per-table tiles and timeline charts), a new Exceptions timeline chart
    // (exceptionSummary.timeline is new JSON this binary didn't used to
    // emit at all), a real fix for a signature-mismatch bug that silently
    // broke the Contention chart and, via the same uncaught exception,
    // Contention row-click reliability, and an auto-descend fix for the
    // Exceptions caller tree. Per CLAUDE.md's "stale-cache trap", an
    // already-downloaded pre-this-work binary doesn't just look
    // incomplete - the Exceptions view's timeline chart section wouldn't
    // render at all (its own HTML is gated on exceptionSummary.timeline
    // being present), so a real version bump is required here, not a
    // same-tag re-upload.
    //
    // Bumped again for 1.7.0: the Overview time breakdown, the lock ownership
    // timeline, the Threading tab and capture diffing are all fed by JSON
    // sections (timeBreakdown, contentionSummary's lockTimeline/longestWaits,
    // threadingSummary) that a pre-1.7.0 binary does not emit at all, plus a
    // background-GC pause-attribution fix that changes the numbers every GC
    // view shows. Same reasoning as above: those views are gated on their own
    // JSON being present, so a stale cached binary silently renders an empty
    // tab rather than an obviously broken one.
    //
    // Bumped again for 1.8.0: the Threading tab's Pool Adjustments drill-down
    // reads threadSnapshot on each adjustment, and its Thread Creations "Why"
    // column reads isPoolWorker/causeReasonName - none of which a 1.7.x binary
    // emits. Also a real correctness change rather than a new field: the
    // sample-correlated views now look only BACKWARD from an adjustment, over
    // 3ms instead of +/-25ms, so a stale binary would keep reporting stacks
    // taken after the decision it claims to explain.
    // Bumped from 1.8.0 for .gcdump support. This bump is not optional: a
    // machine that already downloaded 1.8.0 keeps using it forever unless the
    // version string changes (see CLAUDE.md's "stale-cache trap"), and that
    // binary has no --gcdump mode at all - so every .gcdump would fail to open
    // with a confusing error while the code here looked correct.
    //
    // Bumped from 1.9.0 for the Threading view's thread classification, and
    // this one is not optional either: the whole feature lives in a new
    // "threadActivity" block that only the new binary emits. A cached 1.9.0
    // would leave the Threading tab silently showing its old, noisier tables
    // with no error anywhere to explain why - the exact failure mode the
    // stale-cache trap note in CLAUDE.md was written about.
    // 1.9.3 adds --gcdump-from-dump (core dumps via ClrMD - see
    // nettraceParser/CoreDump/). Opening a .dmp calls a flag an older cached
    // binary does not have, and the stale-cache trap means every machine that
    // already downloaded 1.9.1 keeps using it until this constant moves - so
    // this bump is what makes the feature reachable at all, not housekeeping.
    const latestNettraceParserVersionNumber = "1.9.3";

    var childProcess: child.ChildProcess | undefined = undefined;
    var startupCallback: any = undefined;
    var didFinishStartup = false;

    var isRunningGcMonitor: boolean = false;

    var startGcMonitor = vscode.commands.registerCommand("dotnetInsights.startGCMonitor", () => {
        if (startupCallback === undefined) {
            startupCallback = () => {
                if (insights.listener === undefined) {
                    return;
                }
                if (isRunningGcMonitor) {
                    return;
                }

                insights.listener.sendShutdown = false;
                insights.listener.start();

                isRunningGcMonitor = true;

                dotnetInsightsGcTreeDataProvider?.listener.processes.clear();
                dotnetInsightsGcTreeDataProvider?.refresh();
                dotnetInsightsJitTreeDataProvider?.refresh();

                // Check if we are able to run to application
                childProcess = child.exec(`"${insights.gcEventListenerPath}"`, (exception: child.ExecException | null, stdout: string, stderr: string) => {
                    if (stdout.indexOf("ETW Event listening required Privilidged Access. Please run as Administrator") !== -1) {
                        vscode.window.showInformationMessage(`To automatically launch VSCode must be run elevated. In an elevated command prompt run: ${insights.gcEventListenerPath}`);
                        childProcess = undefined;
                    }
                    if (stderr.indexOf("ETW Event listening required Privilidged Access. Please run as Administrator") !== -1) {
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
        if (insights.listener !== undefined) {
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
            console.assert(dotnetInsightsGcTreeDataProvider !== undefined);
            console.assert(dotnetInsightsJitTreeDataProvider !== undefined);

            insights.outputChannel.appendLine("Stopped monitoring GCs.");
        }
    }); 

    context.subscriptions.push(stopGCMonitor);

    let dependencySetup = new DependencySetup(lastestVersionNumber, latestListenerVersionNumber, latestRoslynVersionNumber, latestNettraceParserVersionNumber, context, insights);

    // Setup
    var success:boolean =await dependencySetup.setup();
    if (!success) {
        vscode.window.showWarningMessage(".NET Insights failed to start.");
        return;
    }

    if (dotnetInsightsSettings !== undefined) {
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

    vscode.commands.registerCommand("dotnetInsights.diffSixVsSevenTier0", (treeItem: Dependency) => {
        doDiffBetweenRuntimesTier0(treeItem, insights, true, true, false, false, false, ".Net Core 6.0/.Net Core 7.0 Tier 0 Diff");
    });

    vscode.commands.registerCommand("dotnetInsights.diffSixVsEightTier0", (treeItem: Dependency) => {
        doDiffBetweenRuntimesTier0(treeItem, insights, true, false, true, false, false, ".Net Core 6.0/.Net Core 8.0 Tier 0 Diff");
    });

    vscode.commands.registerCommand("dotnetInsights.diffSixVsNineTier0", (treeItem: Dependency) => {
        doDiffBetweenRuntimesTier0(treeItem, insights, true, false, false, true, false, ".Net Core 6.0/.Net Core 9.0 Tier 0 Diff");
    });

    vscode.commands.registerCommand("dotnetInsights.diffSixVsTenTier0", (treeItem: Dependency) => {
        doDiffBetweenRuntimesTier0(treeItem, insights, true, false, false, false, true, ".Net Core 6.0/.Net Core 10.0 Tier 0 Diff");
    });
    vscode.commands.registerCommand("dotnetInsights.diffSevenVsEightTier0", (treeItem: Dependency) => {
        doDiffBetweenRuntimesTier0(treeItem, insights, false, true, true, false, false, ".Net Core 7.0/.Net Core 8.0 Tier 0 Diff");
    });

    vscode.commands.registerCommand("dotnetInsights.diffSevenVsNineTier0", (treeItem: Dependency) => {
        doDiffBetweenRuntimesTier0(treeItem, insights, false, true, false, true, false, ".Net Core 7.0/.Net Core 9.0 Tier 0 Diff");
    });

    vscode.commands.registerCommand("dotnetInsights.diffSevenVsTenTier0", (treeItem: Dependency) => {
        doDiffBetweenRuntimesTier0(treeItem, insights, false, true, false, false, true, ".Net Core 7.0/.Net Core 10.0 Tier 0 Diff");
    });

    vscode.commands.registerCommand("dotnetInsights.diffEightVsNineTier0", (treeItem: Dependency) => {
        doDiffBetweenRuntimesTier0(treeItem, insights, false, false, true, true, false, ".Net Core 8.0/.Net Core 9.0 Tier 0 Diff");
    });

    vscode.commands.registerCommand("dotnetInsights.diffEightVsTenTier0", (treeItem: Dependency) => {
        doDiffBetweenRuntimesTier0(treeItem, insights, false, false, true, false, true, ".Net Core 8.0/.Net Core 10.0 Tier 0 Diff");
    });

    vscode.commands.registerCommand("dotnetInsights.diffNineVsTenTier0", (treeItem: Dependency) => {
        doDiffBetweenRuntimesTier0(treeItem, insights, false, false, false, true, true, ".Net Core 9.0/.Net Core 10.0 Tier 0 Diff");
    });

    vscode.commands.registerCommand("dotnetInsights.diffSixVsSevenTier1", (treeItem: Dependency) => {
        doDiffBetweenRuntimesTier1(treeItem, insights, true, true, false, false, false, ".Net Core 6.0/.Net Core 7.0 Tier 1 Diff");
    });
    vscode.commands.registerCommand("dotnetInsights.diffSixVsEightTier1", (treeItem: Dependency) => {
        doDiffBetweenRuntimesTier1(treeItem, insights, true, false, true, false, false, ".Net Core 6.0/.Net Core 8.0 Tier 1 Diff");
    });
    vscode.commands.registerCommand("dotnetInsights.diffSixVsNineTier1", (treeItem: Dependency) => {
        doDiffBetweenRuntimesTier1(treeItem, insights, true, false, false, true, false, ".Net Core 6.0/.Net Core 9.0 Tier 1 Diff");
    });
    vscode.commands.registerCommand("dotnetInsights.diffSixVsTenTier1", (treeItem: Dependency) => {
        doDiffBetweenRuntimesTier1(treeItem, insights, true, false, false, false, true, ".Net Core 6.0/.Net Core 10.0 Tier 1 Diff");
    });
    vscode.commands.registerCommand("dotnetInsights.diffSevenVsEightTier1", (treeItem: Dependency) => {
        doDiffBetweenRuntimesTier1(treeItem, insights, false, true, true, false, false, ".Net Core 7.0/.Net Core 8.0 Tier 1 Diff");
    });
    vscode.commands.registerCommand("dotnetInsights.diffSevenVsNineTier1", (treeItem: Dependency) => {
        doDiffBetweenRuntimesTier1(treeItem, insights, false, true, false, true, false, ".Net Core 7.0/.Net Core 9.0 Tier 1 Diff");
    });
    vscode.commands.registerCommand("dotnetInsights.diffSevenVsTenTier1", (treeItem: Dependency) => {
        doDiffBetweenRuntimesTier1(treeItem, insights, false, true, false, false, true, ".Net Core 7.0/.Net Core 10.0 Tier 1 Diff");
    });
    vscode.commands.registerCommand("dotnetInsights.diffEightVsNineTier1", (treeItem: Dependency) => {
        doDiffBetweenRuntimesTier1(treeItem, insights, false, false, true, true, false, ".Net Core 8.0/.Net Core 9.0 Tier 1 Diff");
    });
    vscode.commands.registerCommand("dotnetInsights.diffEightVsTenTier1", (treeItem: Dependency) => {
        doDiffBetweenRuntimesTier1(treeItem, insights, false, false, true, false, true, ".Net Core 8.0/.Net Core 10.0 Tier 1 Diff");
    });
    vscode.commands.registerCommand("dotnetInsights.diffNineVsTenTier1", (treeItem: Dependency) => {
        doDiffBetweenRuntimesTier1(treeItem, insights, false, false, false, true, true, ".Net Core 9.0/.Net Core 10.0 Tier 1 Diff");
    });

    vscode.commands.registerCommand("dotnetInsights.diff", (treeItem: Dependency) => {
        doDiffBetweenRuntimesTier1(treeItem, insights, false, false, true, false, true, ".Net Core 8.0/.Net Core 10.0 Tier 1 Diff");
    });

    vscode.commands.registerCommand('dotnetInsights.minOpts', (treeItem: Dependency) => {
        const id = crypto.randomBytes(16).toString("hex");
        const outputFileName = path.join(insights.pmiOutputPath, id + ".asm");

        compile(true, false, treeItem, insights, outputFileName, insights.coreRunPath, insights.pmiPath).then((succes: any) => {
            if (succes !== undefined && success === true) {
                showGeneratedDocument(outputFileName);
            }
        });
    });

    vscode.commands.registerCommand("dotnetInsights.tier1", (treeItem: Dependency) => {
        const id = crypto.randomBytes(16).toString("hex");
        const outputFileName = path.join(insights.pmiOutputPath, id + ".asm");

        compile(false, false, treeItem, insights, outputFileName, insights.coreRunPath, insights.pmiPath).then((succes: any) => {
            if (succes !== undefined && success === true) {
                showGeneratedDocument(outputFileName);
            }
        });
    });

    vscode.commands.registerCommand('dotnetInsights.jitDumpTier0', (treeItem: Dependency) => {
        const id = crypto.randomBytes(16).toString("hex");
        const outputFileName = path.join(insights.pmiOutputPath, id + ".asm");

        compile(true, true, treeItem, insights, outputFileName, insights.coreRunPath, insights.pmiPath).then((succes: any) => {
            if (succes !== undefined && success === true) {
                showGeneratedDocument(outputFileName);
            }
        });
    });

    vscode.commands.registerCommand("dotnetInsights.jitDumpTier1", (treeItem: Dependency) => {
        const id = crypto.randomBytes(16).toString("hex");
        const outputFileName = path.join(insights.pmiOutputPath, id + ".asm");

        compile(false, true, treeItem, insights, outputFileName, insights.coreRunPath, insights.pmiPath).then((succes: any) => {
            if (succes !== undefined && success === true) {
                showGeneratedDocument(outputFileName);
            }
        });
    });

    var stopShowIlOnSave = vscode.commands.registerCommand("dotnetInsights.stopShowIlOnSave", () => {
        insights.listeningToAllSaveEvents = false;
    });

    context.subscriptions.push(stopShowIlOnSave);

    vscode.commands.registerCommand("dotnetInsights.showJitDump", () => {
        // We have an asm file active, we have generated the jitdump side by
        // side, just display that file
        const activeEditor = vscode.window.activeTextEditor;

        if (activeEditor === undefined) {
            return;
        }

        const jitDumpFile = activeEditor.document.uri.fsPath.replace(".asm", ".jitDump");

        if (!fs.existsSync(jitDumpFile)) {
            return;
        }

        showCounterpartListing(jitDumpFile, activeEditor);
    });

    vscode.commands.registerCommand("dotnetInsights.showAsm", () => {
        // We have a jitdump file active, we have generated the asm side by
        // side, just display that file
        const activeEditor = vscode.window.activeTextEditor;

        if (activeEditor === undefined) {
            return;
        }

        const asmFile = activeEditor.document.uri.fsPath.replace(".jitDump", ".asm");

        if (!fs.existsSync(asmFile)) {
            return;
        }

        showCounterpartListing(asmFile, activeEditor);
    });

    vscode.commands.registerCommand("dotnetInsights.realtimeIL", (reWriteFile?: boolean) => {
        // We have been asked to show realtime asm of the current file.

        var activeFile  = vscode.window.activeTextEditor?.document.uri.fsPath;
        insights.currentFile = activeFile;

        if (!insights.listenerSetup) {
            insights.listenerSetup = true;
            insights.listeningToAllSaveEvents = true;
            vscode.workspace.onDidSaveTextDocument(e => {
                if (e.fileName === insights.currentFile) {
                    if (insights.listeningToAllSaveEvents) {
                        vscode.commands.executeCommand("dotnetInsights.realtimeIL", false);
                    }
                }
            });

            vscode.workspace.onDidCloseTextDocument(e => {
                if (e.fileName === insights.currentFile) {
                    insights.listeningToAllSaveEvents = false;
                }
            });

            vscode.window.onDidChangeActiveTextEditor(e => {
                if (e?.document.fileName.indexOf("generated") !==-1) {
                    return;
                }

                if (e?.document.fileName.indexOf("extension-output") !== -1) {
                    return;
                }

                if (e.document.fileName === insights.currentFile) {
                    return;
                }
                
                if (e.document.fileName.indexOf(".asm") !== -1) {
                    return;
                }

                if (e.document.languageId === "Log") {
                    return;
                }

                insights.listeningToAllSaveEvents = false;
            });
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
                if (ilAsmDocuments === undefined) {
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
        if (treeItem.lineNumber !== undefined) {
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
        if (item.label !== undefined) {
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

    // Compare two .nettrace captures. VS Code passes an explorer multi-select
    // as (clickedUri, selectedUris), so right-clicking two selected captures
    // needs no prompting; invoking from the palette falls back to two pickers
    // (see pickCapturesToDiff).
    context.subscriptions.push(vscode.commands.registerCommand("dotnetInsights.diffNettrace", async (contextUri?: vscode.Uri, selectedUris?: vscode.Uri[]) => {
        const captures = await pickCapturesToDiff(contextUri, selectedUris);

        if (captures === null) {
            return;
        }

        if (captures.baseline === captures.comparison) {
            vscode.window.showWarningMessage("Select two different captures to compare.");
            return;
        }

        await showNettraceDiff(context, insights, captures.baseline, captures.comparison);
    }));

    context.subscriptions.push(DotnetInsightsTextEditorProvider.register(context, insights));
    context.subscriptions.push(DotnetInsightsGcEditor.register(context, insights, listener));
    context.subscriptions.push(DotnetInsightsGcSnapshotEditor.register(context, insights));
    context.subscriptions.push(DotnetInsightsNettraceEditor.register(context, insights));
    context.subscriptions.push(DotnetInsightsGcDumpEditor.register(context, insights));
    context.subscriptions.push(DotnetInsightsRuntimeLoadEventsEditor.register(context, insights));

    if (startupCallback !== undefined) {
        startupCallback();
    }

    didFinishStartup = true;
    
}

export function deactivate() {
    console.log("dotnetInsights: deactivated.");
}