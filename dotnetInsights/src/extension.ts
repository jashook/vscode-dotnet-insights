////////////////////////////////////////////////////////////////////////////////
// Module: extension.ts
////////////////////////////////////////////////////////////////////////////////

import * as child from 'child_process';
import * as fs from 'fs';
import * as path from 'path';
import * as vscode from 'vscode';
import * as os from "os"
import * as assert from "assert";

import { DotnetInsightsTreeDataProvider, Dependency, DotnetInsights } from './dotnetInsights';
import { DotnetInsightsTextEditorProvider } from "./DotnetInightsTextEditor";

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

export function activate(context: vscode.ExtensionContext) {
    console.log('dotnetInsights: started');

    var insights = new DotnetInsights();

    // Setup
    setup(insights).then((success: boolean) => {
        if (!success) {
            return;
        }

        const dotnetInsightsTreeDataProvider = new DotnetInsightsTreeDataProvider(insights);
        vscode.window.registerTreeDataProvider('dotnetInsights', dotnetInsightsTreeDataProvider);
        vscode.commands.registerCommand('nodeDependencies.refreshEntry', () => dotnetInsightsTreeDataProvider.refresh());

        let disposablePmi = vscode.commands.registerCommand('dotnetInsights.usePmi', () => {
            insights.setUsePmi();
        });
    
        let disposableIlDasm = vscode.commands.registerCommand('dotnetInsights.useIlDasm', () => {
            insights.setUseIldasm();
        });
        
        context.subscriptions.push(DotnetInsightsTextEditorProvider.register(context, insights));
        context.subscriptions.push(disposablePmi);
        context.subscriptions.push(disposableIlDasm);
    });
}

export function deactivate() {
    console.log("dotnetInsights: deactivated.");
}

function setupIlDasm(insights: DotnetInsights, callback: (insights: DotnetInsights) => Thenable<boolean>): Thenable<boolean> {
    const ilDasmPath = insights.ilDasmPath;
    console.log("ILDasm Path: " + ilDasmPath);

    // Verify that the ildasm path exists and the executable runs
    var ildasmCommand = ilDasmPath + " " + "/?";
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

    insights.coreRunPath = insights.coreRoot + coreRunExe ;

    // Verify it runs
    var pmiCommand = insights.coreRoot + coreRunExe + " " + pmiPath + " " + "-h";
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

function setup(insights: DotnetInsights) : Thenable<boolean>  {
    console.log("Setting up dotnetInsights.");

    const config = vscode.workspace.getConfiguration();
    var dotnetInsightsSettings: any = config.get("dotnet-insights");

    var ilDasmPath = dotnetInsightsSettings["ildasmPath"];
    var pmiPath = dotnetInsightsSettings["pmiPath"];
    var coreRoot = dotnetInsightsSettings["coreRoot"];

    var outputPath = dotnetInsightsSettings["outputPath"];

    if (ilDasmPath == undefined) {
        vscode.window.showErrorMessage("dotnet-insights.ilDasmPath must be set.");
        return Promise.resolve(false);
    }
    else if (pmiPath == undefined) {
        vscode.window.showErrorMessage("dotnet-insights.pmiPath must be set.");
        return Promise.resolve(false);
    }
    else if (coreRoot == undefined) {
        vscode.window.showErrorMessage("dotnet-insights.coreRoot must be set.");
        return Promise.resolve(false);
    }
    else if (outputPath == undefined) {
        vscode.window.showErrorMessage("dotnet-insights.outputPath must be set.");
        return Promise.resolve(false);
    }

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

    if (pmiPath == undefined || ilDasmPath == undefined || coreRoot == undefined) {
        console.error("PMI Path and ILDasm Path must be set.");

        return Promise.resolve(false);
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
