////////////////////////////////////////////////////////////////////////////////
// Module: extension.ts
////////////////////////////////////////////////////////////////////////////////

import * as child from 'child_process';
import * as fs from 'fs';
import * as path from 'path';
import * as vscode from 'vscode';
import * as os from "os"
import * as assert from "assert";

import { DepNodeProvider, Dependency, DotnetInsights } from './dotnetInsights';
import { DotnetInsightsTextEditorProvider } from "./DotnetInightsTextEditor";

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

export function activate(context: vscode.ExtensionContext) {
    console.log('dotnetInsights: started');

    var insights = new DotnetInsights();

    // Setup
    setup(insights).then(() => {
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

function setupIlDasm(insights: DotnetInsights, callback: (insights: DotnetInsights, success: boolean) => void) {
    const ilDasmPath = insights.ilDasmPath;
    console.log("ILDasm Path: " + ilDasmPath);

    // Verify that the ildasm path exists and the executable runs
    fs.exists(ilDasmPath, (exists: boolean) => {
        if (exists) {
            console.log("Found ILDasm on disk.");

            // Verify it runs
            var ildasmCommand = ilDasmPath + " " + "/?";
            console.log(ildasmCommand);

            var foo : child.ChildProcess = child.exec(ildasmCommand, (error: any, stdout: string, stderr: string) => {
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

                callback(insights, success);
            });
        }
        else {
            console.log("Error setting incorrect, will download ildasm.");
        }
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

function setupPmi(insights: DotnetInsights) {
    const pmiPath = insights.pmiPath;
    console.log("PMI Path: " + pmiPath);

    // Verify that the ildasm path exists and the executable runs
    fs.exists(pmiPath, (exists: boolean) => {
        if (exists) {
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

            var foo : child.ChildProcess = child.exec(pmiCommand, (error: any, stdout: string, stderr: string) => {
                var success = false;
                
                if (error != null && stdout == undefined) {
                    console.log(stderr);
                }
                else {
                    console.log("PMI setup successfully.");
                }
            });
        }
        else {
            console.log("Error setting incorrect, will download ildasm.");
        }
    });
}

function checkForDotnetSdk(insights: DotnetInsights, success: boolean) {
    if (!success) {
        return;
    }

    // Check for the dotnet sdk. This is not necessary, it is possible to look
    // at managed pe files as well. In addition, we can hijack the jit dropped 
    // into the publish folder for superPMI.

    var dotnetCommand = "dotnet --list-sdks"

    var foo: child.ChildProcess = child.exec(dotnetCommand, (error: any, stdout: string, stderr: string) => {
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

            setupPmi(insights);
        }
    });
}

function setup(insights: DotnetInsights) : Thenable<void>  {
    console.log("Setting up dotnetInsights.");

    const config = vscode.workspace.getConfiguration();
    var dotnetInsightsSettings: any = config.get("dotnet-insights");

    var ilDasmPath = dotnetInsightsSettings["ildasmPath"];
    var pmiPath = dotnetInsightsSettings["pmiPath"];
    var coreRoot = dotnetInsightsSettings["coreRoot"];

    var outputPath = dotnetInsightsSettings["outputPath"];

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

    if (pmiPath == undefined || ilDasmPath == undefined || coreRoot == undefined) {
        console.error("PMI Path and ILDasm Path must be set.");

        return Promise.resolve();
    }

    insights.ilDasmPath = ilDasmPath;
    insights.pmiPath = pmiPath;
    insights.coreRoot = coreRoot;
    insights.ilDasmOutputPath = ilDasmOutputPath;
    insights.pmiOutputPath = pmiOutputPath;

    // Setup mostly will involve making sure we have the dotnet tools required
    // to provide insights.

    setupIlDasm(insights, checkForDotnetSdk);

    return Promise.resolve();
}
