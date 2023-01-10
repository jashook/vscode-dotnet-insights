import * as child from 'child_process';
import * as fs from 'fs';
import * as path from 'path';
import * as vscode from 'vscode';
import * as os from "os" 

import * as request from 'request';
import * as crypto from "crypto";

import * as targz from "targz";

import * as rimraf from "rimraf";

import { DotnetInsightsTreeDataProvider, Dependency, DotnetInsights } from './dotnetInsights';

export class DependencySetup {
    ////////////////////////////////////////////////////////////////////////////
    // Member variables
    ////////////////////////////////////////////////////////////////////////////

    private lastestVersionNumber: string;
    private latestListenerVersionNumber: string;
    private latestRoslynVersionNumber: string;
    private context: vscode.ExtensionContext;
    private insights: DotnetInsights;

    ////////////////////////////////////////////////////////////////////////////
    // Public methods
    ////////////////////////////////////////////////////////////////////////////

    constructor(lastestVersionNumber: string, latestListenerVersionNumber: string, latestRoslynVersionNumber: string, context: vscode.ExtensionContext, insights: DotnetInsights) {
        this.lastestVersionNumber = lastestVersionNumber;
        this.latestListenerVersionNumber = latestListenerVersionNumber;
        this.latestRoslynVersionNumber = latestRoslynVersionNumber;
        this.context = context;
        this.insights = insights;
    }

    ////////////////////////////////////////////////////////////////////////////
    // Public methods
    ////////////////////////////////////////////////////////////////////////////

    public setup() : Thenable<boolean>  {
        this.insights.outputChannel.appendLine("Setting up dotnetInsights.");

        const config = vscode.workspace.getConfiguration();
        var dotnetInsightsSettings: any = config.get("dotnet-insights");

        var ilDasmPath:any = undefined;
        var pmiPath: any = undefined;
        var netcoreThreePmiPath: any = undefined;
        var netcoreFivePmiPath: any = undefined;
        var netcoreSixPmiPath: any = undefined;
        var netcoreSevenPmiPath: any = undefined;

        var customCoreRootPath: any  = undefined;

        var roslynHelperPath: any = undefined;
        var gcEventListenerPath: any = undefined;

        var netcoreThreeCoreRootPath: any = undefined;
        var netcoreFiveCoreRootPath: any = undefined;
        var netcoreSixCoreRootPath: any = undefined;
        var netcoreSevenCoreRootPath: any = undefined;

        var gcStatsLocation: any = undefined;

        var outputPath: any = undefined;

        if (dotnetInsightsSettings != undefined) {
            ilDasmPath = dotnetInsightsSettings["ildasmPath"];
            pmiPath = dotnetInsightsSettings["pmiPath"];
            customCoreRootPath = dotnetInsightsSettings["coreRoot"];
            gcStatsLocation = dotnetInsightsSettings["gcDataPath"];
            outputPath = dotnetInsightsSettings["outputPath"];

            gcEventListenerPath = dotnetInsightsSettings["gcEventListenerPath"];
            roslynHelperPath = dotnetInsightsSettings["roslynHelperPath"];

            if (dotnetInsightsSettings["useNetCoreLts"] != undefined && dotnetInsightsSettings["useNetCoreLts"] == true) {
                this.insights.useNetCoreLts = true;
            }
            else {
                this.insights.useNetCoreLts = false;
            }

            if (customCoreRootPath == undefined) {
                customCoreRootPath = "";
            }

            if (gcStatsLocation == undefined) {
                gcStatsLocation = "";
            }
        }
        else {
            customCoreRootPath = "";
        }

        if (outputPath == undefined) {
            outputPath = this.context.globalStorageUri?.fsPath;
        }

        if (outputPath == "" || outputPath == undefined) {
            console.error("outputPath must be set!");
        }

        if (!fs.existsSync(outputPath)) {
            // Create the folder
            fs.mkdirSync(outputPath);
        }

        if (gcStatsLocation == "") {
            // Setup the gcData location. This will store all of the saved gc dumps
            // with a .gcstats expension.

            gcStatsLocation = path.join(outputPath, "gcInfo");

            if (!fs.existsSync(gcStatsLocation)) {
                fs.mkdirSync(gcStatsLocation);
            }

            this.insights.gcDataSaveLocation = gcStatsLocation;
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

        const latestToolFile = path.join(outputPath, this.lastestVersionNumber + ".txt");
        const latestListenerFile = path.join(outputPath, this.latestListenerVersionNumber + ".txt");

        var forceDownload = false;
        if (!fs.existsSync(latestToolFile) || fs.readFileSync(latestToolFile).toString() != this.lastestVersionNumber) {
            forceDownload = true;
        }

        var forceListenerDownload = false;
        if (!fs.existsSync(latestListenerFile) || fs.readFileSync(latestListenerFile).toString() != this.latestListenerVersionNumber) {
            forceListenerDownload = true;
        }

        // ildasm comes with the core_root
        if (ilDasmPath == undefined) {
            const coreRootPath = path.join(outputPath, "coreRoot");

            netcoreFiveCoreRootPath = path.join(coreRootPath, "net5.0", "Core_Root");
            netcoreThreeCoreRootPath = path.join(coreRootPath, "netcore3.1", "Core_Root");
            netcoreSixCoreRootPath = path.join(coreRootPath, "net6.0", "Core_Root");
            netcoreSevenCoreRootPath = path.join(coreRootPath, "net7.0", "Core_Root");

            var ilDasmCoreRootPath = path.join(netcoreSixCoreRootPath, "ildasm.exe");
            if (os.platform() != "win32") {
                ilDasmCoreRootPath = path.join(netcoreSixCoreRootPath, "ildasm");
            }

            var doDownload = false;

            if (forceDownload ||
                !fs.existsSync(netcoreThreeCoreRootPath) ||
                !fs.existsSync(netcoreFiveCoreRootPath) || 
                !fs.existsSync(netcoreSixCoreRootPath) ||
                !fs.existsSync(netcoreSevenCoreRootPath) ||
                !fs.existsSync(ilDasmCoreRootPath)) {
                doDownload = true;
            }

            if (doDownload) {
                var promise: Thenable<boolean> = new Promise((resolve, reject) => {
                    this.downloadRuntimes(this.insights, this.lastestVersionNumber, coreRootPath).then(() => {
                        // We will expect to now have coreRootPath/net5.0/Core_Root and coreRootPath/netcoreapp3.1/Core_Root
                        var runtimeDownloadSucceeded = false;
        
                        if (fs.existsSync(netcoreThreeCoreRootPath) &&
                            fs.existsSync(netcoreFiveCoreRootPath) &&
                            fs.existsSync(netcoreSixCoreRootPath) &&
                            fs.existsSync(netcoreSevenCoreRootPath) &&
                            fs.existsSync(ilDasmCoreRootPath)) {
                            runtimeDownloadSucceeded = true;
                        }

                        if (!runtimeDownloadSucceeded) {
                            vscode.window.showWarningMessage("Unable to download runtime successfully.");

                            console.assert(!runtimeDownloadSucceeded);
                            resolve(runtimeDownloadSucceeded);
                        }
        
                        if (ilDasmPath == undefined) {
                            ilDasmPath = ilDasmCoreRootPath;
                        }
        
                        if (os.platform() != "win32") {
                            // If on !windows set chmod +x to corerun and ildasm
                            fs.chmodSync(ilDasmPath, "0755");
        
                            var coreRunPath = path.join(netcoreThreeCoreRootPath, "corerun");
                            fs.chmodSync(coreRunPath, "0755");

                            coreRunPath = path.join(netcoreFiveCoreRootPath, "corerun");
                            fs.chmodSync(coreRunPath, "0755");

                            coreRunPath = path.join(netcoreSixCoreRootPath, "corerun");
                            fs.chmodSync(coreRunPath, "0755");

                            coreRunPath = path.join(netcoreSevenCoreRootPath, "corerun");
                            fs.chmodSync(coreRunPath, "0755");

                            if (customCoreRootPath != "") {
                                coreRunPath = path.join(customCoreRootPath, "corerun");
                                fs.chmodSync(coreRunPath, "0755");
                            }
                        }

                        this.insights.outputChannel.appendLine(`[Dependency Setup]: netcoreThreeCoreRootPath: ${netcoreThreeCoreRootPath}`);
                        this.insights.outputChannel.appendLine(`[Dependency Setup]: netcoreFiveCoreRootPath: ${netcoreFiveCoreRootPath}`);
                        this.insights.outputChannel.appendLine(`[Dependency Setup]: netcoreSixCoreRootPath: ${netcoreSixCoreRootPath}`);
                        this.insights.outputChannel.appendLine(`[Dependency Setup]: netcoreSevenCoreRootPath: ${netcoreSevenCoreRootPath}`);
                        this.insights.outputChannel.appendLine(`[Dependency Setup]: ilDasmCoreRootPath: ${ilDasmCoreRootPath}`);

                        resolve(runtimeDownloadSucceeded);
                    });
                });

                promises.push(promise);
            }
            else {
                const coreRootPath = path.join(outputPath, "coreRoot");
                netcoreThreeCoreRootPath = path.join(coreRootPath, "netcore3.1", "Core_Root");
                netcoreFiveCoreRootPath = path.join(coreRootPath, "net5.0", "Core_Root");
                netcoreSixCoreRootPath = path.join(coreRootPath, "net6.0", "Core_Root");
                netcoreSevenCoreRootPath = path.join(coreRootPath, "net7.0", "Core_Root");

                if (ilDasmPath == undefined) {
                    ilDasmPath = ilDasmCoreRootPath;
                }
            }
        }

        if (pmiPath == undefined) {
            const pmiExePath = path.join(outputPath, "pmiExe");

            const netcoreThreePmiPathDownload = path.join(pmiExePath, "netcore3.1", "netcoreapp3.1", "pmi.dll");
            const netcoreFivePmiPathDownload = path.join(pmiExePath, "net5.0", "net5.0", "pmi.dll");
            const netcoreSixPmiDownload = path.join(pmiExePath, "net6.0", "net6.0", "pmi.dll");
            const netcoreSevenPmiDownload = path.join(pmiExePath, "net7.0", "net7.0", "pmi.dll");

            var doDownload = false;
            if (forceDownload || 
                !fs.existsSync(netcoreThreePmiPathDownload) || 
                !fs.existsSync(netcoreFivePmiPathDownload) ||
                !fs.existsSync(netcoreSixPmiDownload) ||
                !fs.existsSync(netcoreSevenPmiDownload)) {
                doDownload = true;
            }

            if (doDownload) {
                var promise: Thenable<boolean> = new Promise((resolve, reject) => {
                    this.downloadPmiExe(this.insights, this.lastestVersionNumber, pmiExePath).then(() => {
                        // We will expect to now have net5.0/net5.0/pmi.dll and netcore/netcoreapp3.1/pmi.dll
                        var pmiDownloadSucceeded = false;

                        if (fs.existsSync(netcoreThreePmiPathDownload) &&
                            fs.existsSync(netcoreFivePmiPathDownload) && 
                            fs.existsSync(netcoreSixPmiDownload) &&
                            fs.existsSync(netcoreSevenPmiDownload)) {
                            pmiDownloadSucceeded = true;
                        }

                        netcoreThreePmiPath = netcoreThreePmiPathDownload;
                        netcoreFivePmiPath = netcoreFivePmiPathDownload;
                        netcoreSixPmiPath = netcoreSixPmiDownload;
                        netcoreSevenPmiPath = netcoreSevenPmiDownload;

                        if (!pmiDownloadSucceeded) {
                            vscode.window.showWarningMessage("Unable to download pmi successfully.");
                        }

                        this.insights.outputChannel.appendLine(`[Dependency Setup]: netcoreThreePmiPathDownload: ${netcoreThreePmiPathDownload}`);
                        this.insights.outputChannel.appendLine(`[Dependency Setup]: netcoreFivePmiPathDownload: ${netcoreFivePmiPathDownload}`);
                        this.insights.outputChannel.appendLine(`[Dependency Setup]: netcoreSixPmiDownload: ${netcoreSixPmiDownload}`);
                        this.insights.outputChannel.appendLine(`[Dependency Setup]: netcoreSevenPmiDownload: ${netcoreSevenPmiDownload}`);
                        resolve(pmiDownloadSucceeded);
                    });
                });

                promises.push(promise);
            }
            else {
                netcoreFivePmiPath = netcoreFivePmiPathDownload;
                netcoreThreePmiPath = netcoreThreePmiPathDownload;
                netcoreSixPmiPath = netcoreSixPmiDownload;
                netcoreSevenPmiPath = netcoreSevenPmiDownload;
            }
        }

        if (gcEventListenerPath == undefined) {
            const gcEventListenerTempDir = path.join(outputPath, "gcEventListener");

            if (os.platform() == "win32") {
                gcEventListenerPath = path.join(gcEventListenerTempDir, "gcEventListener", "gcEventListener.exe");
            }
            else {
                gcEventListenerPath = path.join(gcEventListenerTempDir, "gcEventListener", "gcEventListener");
            }

            this.insights.gcEventListenerPath = gcEventListenerPath;

            var doDownload = false;
            if (forceDownload || forceListenerDownload || !fs.existsSync(gcEventListenerTempDir) || !fs.existsSync(gcEventListenerPath)) {
                doDownload = true;
            }

            if (doDownload) {
                var promise: Thenable<boolean> = new Promise((resolve, reject) => {
                    this.downloadGcMonitorExe(this.insights, this.latestListenerVersionNumber, gcEventListenerTempDir).then(() => {
                        var downloadSucceeded = false;

                        if (fs.existsSync(gcEventListenerTempDir) && fs.existsSync(gcEventListenerPath)) {
                            downloadSucceeded = true;
                        }

                        if (!downloadSucceeded) {
                            vscode.window.showWarningMessage("Unable to download gcEventListenerPath successfully.");
                        }

                        this.insights.outputChannel.appendLine(`[Dependency Setup]: gcEventListenerPath: ${gcEventListenerPath}`);
                        resolve(downloadSucceeded);
                    });
                });

                promises.push(promise)
            }
            else {
                this.insights.outputChannel.appendLine(`gcEventListenerPath: ${gcEventListenerPath}`);
                this.insights.gcEventListenerPath = gcEventListenerPath;
            }
        }
        else {
            this.insights.outputChannel.appendLine(`gcEventListenerPath: ${gcEventListenerPath}`);
            this.insights.gcEventListenerPath = gcEventListenerPath;
        }

        if (roslynHelperPath == undefined) {
            const roslynHelperPathTempDir = path.join(outputPath, "roslynHelper");

            if (os.platform() == "win32") {
                roslynHelperPath = path.join(roslynHelperPathTempDir, "roslynHelper", "roslynHelper.exe");
            }
            else {
                roslynHelperPath = path.join(roslynHelperPathTempDir, "roslynHelper", "roslynHelper");
            }

            this.insights.roslynHelperPath = roslynHelperPath;

            var doDownload = false;
            if (forceDownload || forceListenerDownload || !fs.existsSync(roslynHelperPathTempDir) || !fs.existsSync(roslynHelperPath)) {
                doDownload = true;
            }

            if (doDownload) {
                var promise: Thenable<boolean> = new Promise((resolve, reject) => {
                    this.downloadRoslynHelper(this.insights, this.latestRoslynVersionNumber, roslynHelperPathTempDir).then(() => {
                        
                        var downloadSucceeded = false;

                        if (fs.existsSync(roslynHelperPathTempDir) && fs.existsSync(roslynHelperPath)) {
                            downloadSucceeded = true;
                        }

                        if (!downloadSucceeded) {
                            vscode.window.showWarningMessage("Unable to download roslynHelper successfully.");
                        }

                        this.insights.outputChannel.appendLine(`[Dependency Setup]: roslynHelperPath: ${roslynHelperPath}`);
                        resolve(downloadSucceeded);
                    });
                });

                promises.push(promise)
            }
            else {
                this.insights.outputChannel.appendLine(`roslynHelperPath: ${roslynHelperPath}`);
                this.insights.roslynHelperPath = roslynHelperPath;
            }
        }
        else {
            this.insights.outputChannel.appendLine(`roslynHelperPath: ${roslynHelperPath}`);
            this.insights.roslynHelperPath = roslynHelperPath;
        }

        if (promises.length > 0) {
            return new Promise((resolve, reject) => {
                Promise.all(promises).then((successes) => {
                    var didSucceed = true;
                    fs.writeFileSync(latestToolFile, this.lastestVersionNumber);
                    fs.writeFileSync(latestListenerFile, this.latestListenerVersionNumber);

                    for (var index = 0; index < successes.length; ++index) {
                        didSucceed = didSucceed && successes[index];
                    }

                    if (!didSucceed) {
                        resolve(false);
                    }
                    else {
                        resolve(this.continueSetup(this.insights, ilDasmPath, netcoreThreePmiPath, netcoreFivePmiPath, netcoreSixPmiPath, netcoreSevenPmiPath, pmiPath, netcoreThreeCoreRootPath, netcoreFiveCoreRootPath, netcoreSixCoreRootPath, netcoreSevenCoreRootPath, customCoreRootPath, outputPath, ilDasmOutputPath, pmiOutputPath, pmiTempDir));
                    }
                });
            });
        }
        else {
            return this.continueSetup(this.insights, ilDasmPath, netcoreThreePmiPath, netcoreFivePmiPath, netcoreSixPmiPath, netcoreSevenPmiPath, pmiPath, netcoreThreeCoreRootPath, netcoreFiveCoreRootPath, netcoreSixCoreRootPath, netcoreSevenCoreRootPath, customCoreRootPath, outputPath, ilDasmOutputPath, pmiOutputPath, pmiTempDir);
        }
    }

    ////////////////////////////////////////////////////////////////////////////
    // Private methods
    ////////////////////////////////////////////////////////////////////////////


    private downloadAndUnzip(insights: DotnetInsights, url: string, unzipFolder: string, outputPath: string, isCoreRoot: boolean) : Thenable<void> {
        const unzipName = path.join(unzipFolder, crypto.randomBytes(16).toString("hex") + ".tar.gz");

        var continueSetupAfterDownload = (resolve: any, reject: any) => {
            insights.outputChannel.appendLine(`[COMPLETE]: mkdir ${outputPath}`);

            insights.outputChannel.appendLine(`readDir ${outputPath}`);
            fs.readdir(outputPath, (err, filesInOutputPath) => {
                insights.outputChannel.appendLine(`[COMPLETE]: readDir ${outputPath}`);
                var deletePromises = [] as Promise<Error>[];

                if (filesInOutputPath.length > 0) {
                    for (var index = 0; index < filesInOutputPath.length; ++index) {
                        if (filesInOutputPath[index] == "temp") {
                            continue;
                        }

                        try {
                            var folderToDelete = path.join(outputPath, filesInOutputPath[index]);
                            insights.outputChannel.appendLine(`rm -r ${folderToDelete}`);
                            deletePromises.push(new Promise((deleteResolve, deleteReject) => {
                                rimraf(folderToDelete, (err) => {
                                    insights.outputChannel.appendLine(`[COMPLETED]: rm -r ${folderToDelete}`);

                                    if (err != undefined && err != null) { 
                                        insights.outputChannel.appendLine(`FAILED: ${err}`);
                                        deleteReject(err);
                                    }
                                    else {
                                        deleteResolve(new Error());
                                    }
                                });
                            }));
                        }
                        catch (e) {
                            console.log(e);
                        }
                    }
                }

                let allPromise = Promise.all(deletePromises);

                allPromise.then(() =>{
                    insights.outputChannel.appendLine(`untar ${unzipName} ${outputPath}`);
                    targz.decompress({
                        src: unzipName,
                        dest: outputPath
                    }, function(err){
                        if(err) {
                            insights.outputChannel.appendLine(`untar failed: ${unzipName}, ${err}`);
                            reject();
                        } else {
                            insights.outputChannel.appendLine(`[COMPLETE]: untar ${unzipName}`);
                            insights.outputChannel.appendLine(`readdir ${unzipName}`);
                            fs.readdir(outputPath, (err, files) => {
                                if (isCoreRoot) {
                                    if (files.length != 1) {
                                        var i = 0;
                                    }
                                }

                                insights.outputChannel.appendLine(`[COMPLETE]: readdir ${unzipName}`);
    
                                if (isCoreRoot && files[0] != "Core_Root") {
                                    // Rename the folder to Core_Root
    
                                    fs.rename(path.join(outputPath, files[0]), path.join(outputPath, "Core_Root"), (err) => {
                                        insights.outputChannel.appendLine(`[FULL-COMPLETE]: ${url}`);
                                        resolve();
                                    });
                                }
                                else {
                                    insights.outputChannel.appendLine(`[FULL-COMPLETE]: ${url}`);
                                    resolve();
                                }
                            });
                        }
                    });
                });
            });
        };

        try {
            const fileStream = fs.createWriteStream(unzipName);

            insights.outputChannel.appendLine(`[${url}] -> ${unzipName}`);

            var req = request(url).pipe(fileStream);

            return new Promise((resolve, reject) => {
                req.on("close", (response: any) => {
                    insights.outputChannel.appendLine(`Download completed: ${unzipName}`);
                    insights.outputChannel.appendLine(`mkdir ${outputPath}`);

                    try {
                        var dirExists = fs.existsSync(outputPath);

                        if (dirExists) {
                            continueSetupAfterDownload(resolve, reject);
                        }

                        else {
                            var mdPromise = fs.mkdir(outputPath, (err) => {
                                if (err?.code !== "EEXIST") {
                                    if (err != undefined) {
                                        insights.outputChannel.appendLine(err?.message);
                                    }
                                    reject();
                                }

                                continueSetupAfterDownload(resolve, reject);
                            });
                        }
                    }
                    catch(e: any) {
                        // If exists, continue.
                        if (e.code !== os.constants.errno.EEXIST) {
                            insights.outputChannel.appendLine(e);
                            reject();
                        }
                    }
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

    private downloadRuntimes(insights: DotnetInsights, versionNumber: string, unzipFolder: string) : Thenable<void[]> {
        const runtimes = ["netcore3.1", "net5.0", "net6.0", "net7.0"];
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
            promises.push(this.downloadAndUnzip(insights, runtimeUrl, unzipFolder, outputPath, true));
        }

        return Promise.all(promises);
    }

    private downloadPmiExe(insights: DotnetInsights, versionNumber: string, unzipFolder: string) : Thenable<void[]> {
        const runtimes = ["netcore3.1", "net5.0", "net6.0", "net7.0"];
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
            promises.push(this.downloadAndUnzip(insights, pmiUrl, unzipFolder, outputPath, false));
        }

        return Promise.all(promises);
    }

    private downloadGcMonitorExe(insights: DotnetInsights, versionNumber: string, unzipFolder: string) : Thenable<void[]> {
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

        promises.push(this.downloadAndUnzip(insights, baseUrl, unzipFolder, exeFolder, false));
        return Promise.all(promises);
    }

    private downloadRoslynHelper(insights: DotnetInsights, versionNumber: string, unzipFolder: string) : Thenable<void[]> {
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
        const baseUrl = `https://github.com/jashook/vscode-dotnet-insights/releases/download/${versionNumber}/roslynHelper-${osName}-${arch}.tar.gz`;

        promises.push(this.downloadAndUnzip(insights, baseUrl, unzipFolder, exeFolder, false));
        return Promise.all(promises);
    }

    private continueSetup(insights: DotnetInsights, ilDasmPath: any, netcoreThreePmiPath: any, netcoreFivePmiPath: any, netcoreSixPmiPath: any, netcoreSevenPmiPath: any,customPmiPath: any, netcoreThreeCoreRootPath: any, netcoreFiveCoreRoot: any, netcoreSixCoreRoot: any, netcoreSevenCoreRoot: any, customCoreRootPath: any, outputPath: any, ilDasmOutputPath: string, pmiOutputPath: string, pmiTempDir: string) : Thenable<boolean> {
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
        insights.netcoreSixCoreRootPath = netcoreSixCoreRoot;
        insights.netcoreSevenCoreRootPath = netcoreSevenCoreRoot;

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

        insights.netcoreThreeCoreRunPath = path.join(insights.netcoreThreeCoreRootPath, coreRunExe);
        insights.netcoreFiveCoreRunPath = path.join(insights.netcoreFiveCoreRootPath, coreRunExe);
        insights.netcoreSixCoreRunPath = path.join(insights.netcoreSixCoreRootPath, coreRunExe);
        insights.netcoreSevenCoreRunPath = path.join(insights.netcoreSevenCoreRootPath, coreRunExe);

        if (!fs.existsSync(insights.netcoreThreeCoreRunPath) ||
            !fs.existsSync(insights.netcoreFiveCoreRunPath) || 
            !fs.existsSync(insights.netcoreSixCoreRunPath) || 
            !fs.existsSync(insights.netcoreSevenCoreRunPath)) {
            vscode.window.showWarningMessage("Failed to set corerun path.");
            Promise.resolve(false);
        }

        insights.netcoreThreePmiPath = netcoreThreePmiPath;
        insights.netcoreFivePmiPath = netcoreFivePmiPath;
        insights.netcoreSixPmiPath = netcoreSixPmiPath;
        insights.netcoreSevenPmiPath = netcoreSevenPmiPath;

        // Change default here.
        if (insights.useNetCoreLts) {
            insights.pmiPath = insights.netcoreThreePmiPath;
        }
        else {
            insights.pmiPath = insights.netcoreFivePmiPath;
        }

        if (customPmiPath != "" && customPmiPath != undefined) {
            insights.pmiPath = customPmiPath;
        }

        if (insights.customCoreRootPath == "" || insights.customCoreRootPath == "") {
            if (insights.useNetCoreLts) {
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

        return this.setupIlDasm(insights, checkForDotnetSdk);
    }


    private setupIlDasm(insights: DotnetInsights, callback: (insights: DotnetInsights) => Thenable<boolean>): Thenable<boolean> {
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

    private setupSuperPmiForVersion(version: string, localRuntimeBuild: string | null) {
        if (localRuntimeBuild != null) {
            // We will not need to do a download
            var config = vscode.workspace.getConfiguration("defaultValue");

            console.log(config);
        }
        else {
            console.error("Local path currently is required the setup is incomplete.");
        }
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

    var killed = false;
    setTimeout((childProcess: child.ChildProcess) => {
        if (!childProcess.killed) {
            childProcess.kill();
            killed = true;
        }
    }, 5000, childProcess);

    return new Promise((resolve, reject) => {
        childProcess.addListener("close", (args: any) => {
            resolve(success);
        });
        childProcess.addListener("error", (args: any) => {
            reject(false);
        });
        childProcess.addListener("exit", (args: any) => {
            // Swallow a windows error hanging on pmi unload.
            if (killed) {
                resolve(true);
            }
        })
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