////////////////////////////////////////////////////////////////////////////////
// Module: extension.ts
////////////////////////////////////////////////////////////////////////////////

import * as child from 'child_process';
import * as fs from 'fs';
import * as path from 'path';
import * as vscode from 'vscode';
import * as os from "os"
import * as assert from "assert";
import * as http from "http"

import * as request from 'request';

import * as unzipper from 'unzipper';

import * as crypto from "crypto";

import { DotnetInsightsTreeDataProvider, Dependency, DotnetInsights } from './dotnetInsights';
import { DotnetInsightsTextEditorProvider } from "./DotnetInightsTextEditor";
import { promises } from 'node:dns';
import { platform } from 'node:os';
import { rejects } from 'node:assert';

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

export function activate(context: vscode.ExtensionContext) {
    console.log('dotnetInsights: started');

    var insights = new DotnetInsights();

    const lastestVersionNumber = "0.1.1";

    // Setup
    setup(lastestVersionNumber, context, insights).then((success: boolean) => {
        if (!success) {
            return;
        }

        const dotnetInsightsTreeDataProvider = new DotnetInsightsTreeDataProvider(insights);
        vscode.window.registerTreeDataProvider('dotnetInsights', dotnetInsightsTreeDataProvider);

        vscode.commands.registerCommand("dotnetInsights.diff", (treeItem: Dependency) => {
            if (treeItem.label != undefined) {
                var pmiCommand = `"${insights.coreRunPath}"` + " " + `"${insights.pmiPath}"` + " " + "PREPALL-QUIET" + " " + `"${treeItem.dllPath}"`;
                console.log(pmiCommand);

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
                        "COMPlus_TieredCompilation": "0",
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
                                
                                console.log("Tier 0 file path: " + minOptsOutputFileName);
                                console.log("Tier 1 file path: " + outputFileName);

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
                console.log(pmiCommand);

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
                        "COMPlus_JitMinOpts": "1"
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
                console.log(pmiCommand);

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
                        "COMPlus_TieredCompilation": "0"
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

        context.subscriptions.push(DotnetInsightsTextEditorProvider.register(context, insights));
    });
}

export function deactivate() {
    console.log("dotnetInsights: deactivated.");
}

function setupIlDasm(insights: DotnetInsights, callback: (insights: DotnetInsights) => Thenable<boolean>): Thenable<boolean> {
    const ilDasmPath = insights.ilDasmPath;
    console.log("ILDasm Path: " + ilDasmPath);

    // Verify that the ildasm path exists and the executable runs
    var ildasmCommand = `"${ilDasmPath}"` + " " + "/?";
    console.log(ildasmCommand);

    var childProcess : child.ChildProcess = child.exec(ildasmCommand, (error: any, stdout: string, stderr: string) => {
        var success = false;
        
        if (error != null) {
            console.log(stderr);
        }
        else {
            try {
                // No error, output should have a version number
                var splitOutput = stdout.split("IL Disassembler.")[1];

                var versionNumber = splitOutput.split("Version ")[1].split("\n")[0];
                console.log("Working ilDasm: Version Number: " + versionNumber);

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
    console.log("PMI Path: " + pmiPath);

    // Verify that the ildasm path exists and the executable runs
    console.log("Found PMI on disk.");

    var coreRunExe = "";
    if (os.platform() == "win32") {
        coreRunExe = "CoreRun.exe";
    }
    else {
        coreRunExe = "corerun";
    }

    insights.coreRunPath = path.join(insights.coreRoot, coreRunExe) ;

    // Verify it runs
    var pmiCommand = `"${insights.coreRunPath}"` + " " + `"${pmiPath}"` + " " + "-h";
    console.log(pmiCommand);

    var success = false;
    var childProcess : child.ChildProcess = child.exec(pmiCommand, (error: any, stdout: string, stderr: string) => {
        if (error != null && stdout == undefined) {
            console.log(stderr);
        }
        else {
            success = true;
            console.log("PMI setup successfully.");
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

            console.log("Installed SDK Versions:");
            for (var index = 0; index < lines.length; ++index) {
                var sdkVersion = lines[index].split(" ")[0]

                console.log(sdkVersion);
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

function downloadAnUnzip(url: string, unzipFolder: string, outputPath: string) : Thenable<void> {
    const unzipName = path.join(unzipFolder, crypto.randomBytes(16).toString("hex") + ".zip");

    try {
        const fileStream = fs.createWriteStream(unzipName);

        console.log(`[${url}] -> ${unzipName}`);

        var req = request(url).pipe(fileStream);

        return new Promise((resolve, reject) => {
            req.on("close", (response: any) => {
                console.log(`Download completed: ${unzipName}`);
                console.log(`unzip ${unzipName}`);

                var unzipStream = fs.createReadStream(unzipName).pipe(unzipper.Extract({ path: outputPath }));

                unzipStream.on("close", () => {
                    if (fs.existsSync(unzipName)) {
                        fs.unlinkSync(unzipName);
                    }

                    console.log(`unzip completed: ${unzipName}`);

                    resolve();
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

function downloadRuntimes(versionNumber: string, unzipFolder: string) : Thenable<void[]> {
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

        const runtimeUrl = baseRuntimeUrl + `${osName}-${arch}-${runtimes[index]}.zip`;
        promises.push(downloadAnUnzip(runtimeUrl, unzipFolder, outputPath));
    }

    return Promise.all(promises);
}

function downloadPmiExe(versionNumber: string, unzipFolder: string) : Thenable<void[]> {
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

        const pmiUrl = baseUrl + `${osName}-${arch}-${runtimes[index]}-pmi.zip`;
        promises.push(downloadAnUnzip(pmiUrl, unzipFolder, outputPath));
    }

    return Promise.all(promises);
}

function setup(lastestVersionNumber: string, context: vscode.ExtensionContext, insights: DotnetInsights) : Thenable<boolean>  {
    console.log("Setting up dotnetInsights.");

    const config = vscode.workspace.getConfiguration();
    var dotnetInsightsSettings: any = config.get("dotnet-insights");

    var ilDasmPath:any = dotnetInsightsSettings?.get("ildasmPath");
    var pmiPath: any = dotnetInsightsSettings?.get("pmiPath");
    var coreRoot: any  = dotnetInsightsSettings?.get("coreRoot");

    var outputPath = dotnetInsightsSettings?.get("outputPath");

    if (outputPath == undefined) {
        outputPath = context.storageUri?.fsPath;
    }

    if (outputPath == "" || outputPath == undefined) {
        console.error("outputPath must be set!");
        assert(false);
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

    // ildasm comes with the core_root
    if (ilDasmPath == undefined || coreRoot == undefined) {
        const coreRootPath = path.join(outputPath, "coreRoot");

        const netCoreFivePath = path.join(coreRootPath, "net5.0", "Core_Root");
        var ilDasmCoreRootPath = path.join(netCoreFivePath, "ildasm.exe");
        if (os.platform() != "win32") {
            ilDasmCoreRootPath = path.join(netCoreFivePath, "ildasm");
        }

        var doDownload = false;

        if (!fs.existsSync(netCoreFivePath) || !fs.existsSync(ilDasmCoreRootPath)) {
            doDownload = true;
        }

        if (doDownload) {
            var promise: Thenable<boolean> = new Promise((resolve, reject) => {
                downloadRuntimes(lastestVersionNumber, coreRootPath).then(() => {
                    // We will expect to now have coreRootPath/net5.0/Core_Root and coreRootPath/netcoreapp3.1/Core_Root
                    var runtimeDownloadSucceeded = false;
    
                    if (fs.existsSync(netCoreFivePath) && fs.existsSync(ilDasmPath)) {
                        runtimeDownloadSucceeded = true;
                    }
    
                    if (coreRoot == undefined) {
                        coreRoot = netCoreFivePath
                    }
    
                    if (ilDasmPath == undefined) {
                        ilDasmPath = ilDasmCoreRootPath;
                    }
    
                    if (os.platform() != "win32") {
                        // If on !windows set chmod +x to corerun and ildasm
                        fs.chmodSync(ilDasmPath, "0755");
    
                        const coreRunPath = path.join(coreRoot, "corerun");
                        fs.chmodSync(coreRunPath, "0755");
                    }
    
                    resolve(runtimeDownloadSucceeded);
                });
            });

            promises.push(promise);
        }
        else {
            if (coreRoot == undefined) {
                coreRoot = netCoreFivePath
            }

            if (ilDasmPath == undefined) {
                ilDasmPath = ilDasmCoreRootPath;
            }
        }
    }

    if (pmiPath == undefined) {
        const pmiExePath = path.join(outputPath, "pmiExe");

        const pmiPathDownloaded = path.join(pmiExePath, "net5.0", "net5.0", "pmi.dll");

        var doDownload = false;
        if (!fs.existsSync(pmiPathDownloaded)) {
            doDownload = true;
        }

        if (doDownload) {
            var promise: Thenable<boolean> = new Promise((resolve, reject) => {
                downloadPmiExe(lastestVersionNumber, pmiExePath).then(() => {
                    // We will expect to now have net5.0/net5.0/pmi.dll and netcore/netcoreapp3.1/pmi.dll
                    var pmiDownloadSucceeded = false;
                        
                    if (fs.existsSync(pmiPathDownloaded)) {
                        pmiDownloadSucceeded = true;
                    }

                    pmiPath = pmiPathDownloaded;
                    resolve(pmiDownloadSucceeded);
                });
            });

            promises.push(promise)
        }
        else {
            pmiPath = pmiPathDownloaded;
        }
    }

    if (promises.length > 0) {
        return new Promise((resolve, reject) => {
            Promise.all(promises).then((successes) => {
                var didSucceed = true;

                for (var index = 0; index < successes.length; ++index) {
                    didSucceed = didSucceed && successes[index];
                }

                if (!didSucceed) {
                    resolve(false);
                }
                else {
                    resolve(continueSetup(insights, ilDasmPath, pmiPath, coreRoot, outputPath, ilDasmOutputPath, pmiOutputPath, pmiTempDir));
                }
            });
        });
    }
    else {
        return continueSetup(insights, ilDasmPath, pmiPath, coreRoot, outputPath, ilDasmOutputPath, pmiOutputPath, pmiTempDir);
    }
}


function continueSetup(insights: DotnetInsights, ilDasmPath: any, pmiPath: any, coreRoot: any, outputPath: any, ilDasmOutputPath: string, pmiOutputPath: string, pmiTempDir: string) : Thenable<boolean> {
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

    if (typeof(pmiPath) != "string") {
        if (os.platform() == "darwin") {
            pmiPath = pmiPath["osx"];
        }
        else if (os.platform() == "linux") {
            pmiPath = pmiPath["linux"];
        }
        else {
            pmiPath = pmiPath["windows"];
        }
    }

    if (typeof(coreRoot) != "string") {
        if (os.platform() == "darwin") {
            coreRoot = coreRoot["osx"];
        }
        else if (os.platform() == "linux") {
            coreRoot = coreRoot["linux"];
        }
        else {
            coreRoot = coreRoot["windows"];
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
    insights.pmiPath = pmiPath;
    insights.coreRoot = coreRoot;
    insights.ilDasmOutputPath = ilDasmOutputPath;
    insights.pmiOutputPath = pmiOutputPath;

    insights.pmiTempDir = pmiTempDir;

    // Setup mostly will involve making sure we have the dotnet tools required
    // to provide insights.

    return setupIlDasm(insights, checkForDotnetSdk);
}