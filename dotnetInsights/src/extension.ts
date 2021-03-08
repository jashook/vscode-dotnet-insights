////////////////////////////////////////////////////////////////////////////////
// Module: extension.ts
////////////////////////////////////////////////////////////////////////////////

import * as child from 'child_process';
import * as fs from 'fs';
import * as path from 'path';
import * as vscode from 'vscode';

import { DepNodeProvider, Dependency, DotnetInsights } from './dotnetInsights';
import { IlDasmTextEditor } from "./IlDasmTextEditor";

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

export function activate(context: vscode.ExtensionContext) {
    console.log('dotnetInsights: started');

    var insights = new DotnetInsights();

    // Setup
    setup(insights);

    let disposable = vscode.commands.registerCommand('dotnetInsights.helloWorld', () => {
        vscode.window.showInformationMessage('Hello world!');
    });
    
    context.subscriptions.push(IlDasmTextEditor.register(context));
    context.subscriptions.push(disposable);
}

export function deactivate() {
    console.log("dotnetInsights: deactivated.");
}

function setupIlDasm(insights: DotnetInsights, callback: (insights: DotnetInsights, success: boolean) => void) {
    const config = vscode.workspace.getConfiguration();

    var ilDasmPath = "";

    var dotnetInsightsSettings: any = config.get("dotnet-insights");

    // Check to see if we have set the configuration for the ildasm path.
    if (dotnetInsightsSettings.ildasmPath != undefined) {
        console.log("Found configuration for a local ILDasm");
        
        ilDasmPath = dotnetInsightsSettings.ildasmPath;
    } 
    else {
        console.error("Please set setting: dotnet-insights.ildasmPath");
        ilDasmPath = "";
    }

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
                        var splitOutput = stdout.split(".NET Framework IL Disassembler.")[1];

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
        }
    });
}

function setup(insights: DotnetInsights) {
    console.log("Setting up dotnetInsights.");

    // Setup mostly will involve making sure we have the dotnet tools required
    // to provide insights.

    setupIlDasm(insights, checkForDotnetSdk);
}
