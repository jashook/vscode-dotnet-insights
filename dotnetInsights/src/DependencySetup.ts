import * as child from 'child_process';
import * as fs from 'fs';
import * as path from 'path';
import * as vscode from 'vscode';
import * as os from "os";

import * as stream from "stream";

import fetch from 'node-fetch';

import * as crypto from "crypto";

import * as targz from "targz";

import * as rimraf from "rimraf";

import { DotnetInsightsTreeDataProvider, Dependency, DotnetInsights } from './dotnetInsights';
import { pipeline } from 'stream';
import { unzip } from 'zlib';

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

    public async setup() : Promise<boolean>  {
        this.insights.outputChannel.appendLine("Setting up dotnetInsights.");
        process.env["NODE_TLS_REJECT_UNAUTHORIZED"] = "0";

        const config = vscode.workspace.getConfiguration();
        var dotnetInsightsSettings: any = config.get("dotnet-insights");

        var ilDasmPath:any = undefined;
        var pmiPath: any = undefined;
        var netcoreSixPmiPath: any = undefined;
        var netcoreSevenPmiPath: any = undefined;
        var netcoreEightPmiPath: any = undefined;
        var netcoreNinePmiPath: any = undefined;
        var netcoreTenPmiPath: any = undefined;

        var customCoreRootPath: any  = undefined;

        var roslynHelperPath: any = undefined;
        var gcEventListenerPath: any = undefined;

        var osName: string = "osx";
        if (os.platform() === "win32") {
            osName = "win";
        }
        else if (os.platform() !== "darwin") {
            osName = "linux";
        }

        var gcStatsLocation: any = undefined;

        var outputPath: any = undefined;

        if (dotnetInsightsSettings !== undefined && dotnetInsightsSettings !== null) {
            ilDasmPath = dotnetInsightsSettings["ildasmPath"];
            pmiPath = dotnetInsightsSettings["pmiPath"];
            customCoreRootPath = dotnetInsightsSettings["coreRoot"];
            gcStatsLocation = dotnetInsightsSettings["gcDataPath"];
            outputPath = dotnetInsightsSettings["outputPath"];

            gcEventListenerPath = dotnetInsightsSettings["gcEventListenerPath"];
            roslynHelperPath = dotnetInsightsSettings["roslynHelperPath"];

            if ((dotnetInsightsSettings["useNetCoreLts"] !== undefined && dotnetInsightsSettings["useNetCoreLts"] !== null) && dotnetInsightsSettings["useNetCoreLts"] === true) {
                this.insights.useNetCoreLts = true;
            }
            else {
                this.insights.useNetCoreLts = false;
            }

            if (customCoreRootPath === undefined || customCoreRootPath === null) {
                customCoreRootPath = "";
            }

            if (gcStatsLocation === undefined || gcStatsLocation === null) {
                gcStatsLocation = "";
            }
        }
        else {
            customCoreRootPath = "";
        }

        if (outputPath === undefined || outputPath === null) {
            outputPath = this.context.globalStorageUri?.fsPath;
        }

        if (outputPath === "" || outputPath === undefined || outputPath === null) {
            this.insights.outputChannel.appendLine("outputPath must be set!");
        }

        if (!fs.existsSync(outputPath)) {
            // Create the folder
            fs.mkdirSync(outputPath);
        }

        if (gcStatsLocation === "") {
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

        var osVer = "osx";
        if (os.platform() === 'win32') {
            osVer = "win";
        }
        else if (os.platform() !== "darwin") {
            osVer = "linux";
        }

        const latestToolFile = path.join(outputPath, this.lastestVersionNumber + ".txt");
        const latestListenerFile = path.join(outputPath, this.latestListenerVersionNumber + ".txt");

        var forceDownload = false;
        if (!fs.existsSync(latestToolFile) || fs.readFileSync(latestToolFile).toString() !== this.lastestVersionNumber) {
            forceDownload = true;
        }

        var forceListenerDownload = false;
        if (!fs.existsSync(latestListenerFile) || fs.readFileSync(latestListenerFile).toString() !== this.latestListenerVersionNumber) {
            forceListenerDownload = true;
        }

        const osContainsArm64Downloads: { [id: string]: boolean } = {
            "osx": true,
            "win": false,
            "linux": false
        };

        var isArm64: boolean = process.arch === "arm64" && osContainsArm64Downloads[osName];
        const coreRootPath = path.join(outputPath, "coreRoot");

        var coreRootPaths: { [id:string]: { [id: string]: string } } = {
            "6.0": {
                "x64": path.join(coreRootPath, "net6.0", "Core_Root")
            },
            "7.0": {
                "x64": path.join(coreRootPath, "net7.0", "Core_Root")
            },
            "8.0": {
                "x64": path.join(coreRootPath, "net8.0", "Core_Root")
            },
            "9.0": {
                "x64": path.join(coreRootPath, "net9.0", "Core_Root")
            },
            "10.0": {
                "x64": path.join(coreRootPath, "net10.0", "Core_Root")
            },
        };

        if (isArm64) {
            coreRootPaths["6.0"]["arm64"] = path.join(coreRootPath, "net6.0", "Core_Root-arm64");
            coreRootPaths["7.0"]["arm64"] = path.join(coreRootPath, "net7.0", "Core_Root-arm64");
            coreRootPaths["8.0"]["arm64"] = path.join(coreRootPath, "net8.0", "Core_Root-arm64");
            coreRootPaths["9.0"]["arm64"] = path.join(coreRootPath, "net9.0", "Core_Root-arm64");
            coreRootPaths["10.0"]["arm64"] = path.join(coreRootPath, "net10.0", "Core_Root-arm64");
        }

        var didDownload = false;

        // ildasm comes with the core_root
        if (ilDasmPath === undefined || ilDasmPath === null) {

            const arm64Downloads: { [id: string]: string[] } = {
                "osx": ["net6.0", "net7.0", "net8.0", "net9.0", "net10.0"],
                "win": [],
                "linux": []
            };

            var tempCoreRootPath = isArm64 ? coreRootPaths["6.0"]["arm64"] : coreRootPaths["6.0"]["x64"];

            var ilDasmCoreRootPath = path.join(tempCoreRootPath, "ildasm.exe");
            if (os.platform() !== "win32") {
                ilDasmCoreRootPath = path.join(tempCoreRootPath, "ildasm");
            }

            var doDownload = false;
            var pathsToCheck = [coreRootPaths["6.0"]["x64"], coreRootPaths["7.0"]["x64"], coreRootPaths["8.0"]["x64"], coreRootPaths["9.0"]["x64"], coreRootPaths["10.0"]["x64"], ilDasmCoreRootPath];

            if (osContainsArm64Downloads[osName]) {
                const runtimesWithArm64 = arm64Downloads[osName];

                for (var runtimeIndex = 0; runtimeIndex < runtimesWithArm64.length; ++runtimeIndex) {
                    const runtimeName = runtimesWithArm64[runtimeIndex];

                    pathsToCheck.push(path.join(coreRootPath, runtimeName, "Core_Root-arm64"));
                }
            }

            if (forceDownload || !checkFoldersExist(pathsToCheck)) {
                doDownload = true;
                didDownload = true;
            }

            if (doDownload) {
                await this.downloadRuntimes(this.insights, this.lastestVersionNumber, coreRootPath);
                var runtimeDownloadSucceeded = false;
                if (checkFoldersExist(pathsToCheck)) {
                    runtimeDownloadSucceeded = true;
                }

                if (!runtimeDownloadSucceeded) {
                    vscode.window.showWarningMessage("Unable to download runtime successfully.");

                    this.insights.outputChannel.appendLine("runtimeDownload not successful");
                    return false;
                }

                if (ilDasmPath === undefined || ilDasmPath === null) {
                    ilDasmPath = ilDasmCoreRootPath;
                }

                if (os.platform() !== "win32") {
                    // If on !windows set chmod +x to corerun and ildasm
                    fs.chmodSync(ilDasmPath, "0755");

                    for (var corerunIndex = 0; corerunIndex < pathsToCheck.length; ++corerunIndex) {
                        var coreRunPath = path.join(pathsToCheck[corerunIndex], "corerun");

                        if (coreRunPath.indexOf("ildasm") === -1) {
                            fs.chmodSync(coreRunPath, "0755");
                        }
                    }

                    if (customCoreRootPath !== "") {
                        coreRunPath = path.join(customCoreRootPath, "corerun");
                        fs.chmodSync(coreRunPath, "0755");
                    }
                }

                this.insights.outputChannel.appendLine(`[Dependency Setup]: netcoreSixCoreRootPath: ${coreRootPaths["6.0"]["x64"]}`);
                this.insights.outputChannel.appendLine(`[Dependency Setup]: netcoreSevenCoreRootPath: ${coreRootPaths["7.0"]["x64"]}`);
                this.insights.outputChannel.appendLine(`[Dependency Setup]: netcoreSevenCoreRootPath: ${coreRootPaths["8.0"]["x64"]}`);
                this.insights.outputChannel.appendLine(`[Dependency Setup]: netcoreSevenCoreRootPath: ${coreRootPaths["9.0"]["x64"]}`);
                this.insights.outputChannel.appendLine(`[Dependency Setup]: netcoreSevenCoreRootPath: ${coreRootPaths["10.0"]["x64"]}`);
                this.insights.outputChannel.appendLine(`[Dependency Setup]: ilDasmCoreRootPath: ${ilDasmCoreRootPath}`);
            }
            else {
                const coreRootPath = path.join(outputPath, "coreRoot");
                coreRootPaths["6.0"]["x64"] = path.join(coreRootPath, "net6.0", "Core_Root");
                coreRootPaths["7.0"]["x64"] = path.join(coreRootPath, "net7.0", "Core_Root");
                coreRootPaths["8.0"]["x64"] = path.join(coreRootPath, "net8.0", "Core_Root");
                coreRootPaths["9.0"]["x64"] = path.join(coreRootPath, "net9.0", "Core_Root");
                coreRootPaths["10.0"]["x64"] = path.join(coreRootPath, "net10.0", "Core_Root");

                if (isArm64) {
                    coreRootPaths["6.0"]["arm64"] = path.join(coreRootPath, "net6.0", "Core_Root-arm64");
                    coreRootPaths["7.0"]["arm64"] = path.join(coreRootPath, "net7.0", "Core_Root-arm64");
                    coreRootPaths["8.0"]["arm64"] = path.join(coreRootPath, "net8.0", "Core_Root-arm64");
                    coreRootPaths["9.0"]["arm64"] = path.join(coreRootPath, "net9.0", "Core_Root-arm64");
                    coreRootPaths["10.0"]["arm64"] = path.join(coreRootPath, "net10.0", "Core_Root-arm64");
                }

                if (ilDasmPath === undefined || ilDasmPath === null) {
                    ilDasmPath = ilDasmCoreRootPath;
                }
            }
        }

        if (pmiPath === undefined || pmiPath === null) {
            const pmiExePath = path.join(outputPath, "pmiExe");

            const netcoreSixPmiDownload = path.join(pmiExePath, "net6.0", "net6.0", "pmi.dll");
            const netcoreSevenPmiDownload = path.join(pmiExePath, "net7.0", "net7.0", "pmi.dll");
            const netcoreEightPmiDownload = path.join(pmiExePath, "net8.0", "net8.0", "pmi.dll");
            const netcoreNinePmiDownload = path.join(pmiExePath, "net9.0", "net9.0", "pmi.dll");
            const netcoreTenPmiDownload = path.join(pmiExePath, "net10.0", "net10.0", "pmi.dll");

            const pathsToCheck = [netcoreSixPmiDownload, netcoreSevenPmiDownload, netcoreEightPmiDownload, netcoreNinePmiDownload, netcoreTenPmiDownload, pmiExePath];

            var doDownload = false;
            if (forceDownload || !checkFoldersExist(pathsToCheck)) {
                doDownload = true;
                didDownload = true;
            }

            if (doDownload) {
                await this.downloadPmiExe(this.insights, this.lastestVersionNumber, pmiExePath);
                var pmiDownloadSucceeded = false;

                if (checkFoldersExist(pathsToCheck)) {
                    pmiDownloadSucceeded = true;
                }

                netcoreSixPmiPath = netcoreSixPmiDownload;
                netcoreSevenPmiPath = netcoreSevenPmiDownload;

                if (!pmiDownloadSucceeded) {
                    vscode.window.showWarningMessage("Unable to download pmi successfully.");
                    return false;
                }

                this.insights.outputChannel.appendLine(`[Dependency Setup]: netcoreSixPmiDownload: ${netcoreSixPmiDownload}`);
                this.insights.outputChannel.appendLine(`[Dependency Setup]: netcoreSevenPmiDownload: ${netcoreSevenPmiDownload}`);
                this.insights.outputChannel.appendLine(`[Dependency Setup]: netcoreEightPmiDownload: ${netcoreEightPmiDownload}`);
                this.insights.outputChannel.appendLine(`[Dependency Setup]: netcoreNinePmiDownload: ${netcoreNinePmiDownload}`);
                this.insights.outputChannel.appendLine(`[Dependency Setup]: netcoreTenPmiDownload: ${netcoreTenPmiDownload}`);
            }
            else {
                netcoreSixPmiPath = netcoreSixPmiDownload;
                netcoreSevenPmiPath = netcoreSevenPmiDownload;
                netcoreEightPmiPath = netcoreEightPmiDownload;
                netcoreNinePmiPath = netcoreNinePmiDownload;
                netcoreTenPmiPath = netcoreTenPmiDownload;
            }
        }

        if (gcEventListenerPath === undefined || gcEventListenerPath === null) {
            const gcEventListenerTempDir = path.join(outputPath, "gcEventListener");

            if (os.platform() === "win32") {
                gcEventListenerPath = path.join(gcEventListenerTempDir, "gcEventListener", "gcEventListener.exe");
            }
            else {
                gcEventListenerPath = path.join(gcEventListenerTempDir, "gcEventListener", "gcEventListener");
            }

            this.insights.gcEventListenerPath = gcEventListenerPath;

            var doDownload = false;
            if (forceDownload || forceListenerDownload || !fs.existsSync(gcEventListenerTempDir) || !fs.existsSync(gcEventListenerPath)) {
                doDownload = true;
                didDownload = true;
            }

            if (doDownload) {
                await this.downloadGcMonitorExe(this.insights, this.latestListenerVersionNumber, gcEventListenerTempDir);
                var downloadSucceeded = false;

                if (fs.existsSync(gcEventListenerTempDir) && fs.existsSync(gcEventListenerPath)) {
                    downloadSucceeded = true;
                }

                if (!downloadSucceeded) {
                    vscode.window.showWarningMessage("Unable to download gcEventListenerPath successfully.");
                    return false;
                }

                this.insights.outputChannel.appendLine(`[Dependency Setup]: gcEventListenerPath: ${gcEventListenerPath}`);
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

        if (roslynHelperPath === undefined || roslynHelperPath === null) {
            const roslynHelperPathTempDir = path.join(outputPath, "roslynHelper");

            if (os.platform() === "win32") {
                roslynHelperPath = path.join(roslynHelperPathTempDir, "roslynHelper", "roslynHelper.exe");
            }
            else {
                roslynHelperPath = path.join(roslynHelperPathTempDir, "roslynHelper", "roslynHelper");
            }

            this.insights.roslynHelperPath = roslynHelperPath;

            var doDownload = false;
            if (forceDownload || forceListenerDownload || !fs.existsSync(roslynHelperPathTempDir) || !fs.existsSync(roslynHelperPath)) {
                doDownload = true;
                didDownload = true;
            }

            if (doDownload) {
                await this.downloadRoslynHelper(this.insights, this.latestRoslynVersionNumber, roslynHelperPathTempDir);
                
                var downloadSucceeded = false;
                if (fs.existsSync(roslynHelperPathTempDir) && fs.existsSync(roslynHelperPath)) {
                    downloadSucceeded = true;
                }

                if (!downloadSucceeded) {
                    vscode.window.showWarningMessage("Unable to download roslynHelper successfully.");
                }

                this.insights.outputChannel.appendLine(`[Dependency Setup]: roslynHelperPath: ${roslynHelperPath}`);
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

        if (didDownload) {
            fs.writeFileSync(latestToolFile, this.lastestVersionNumber);
            fs.writeFileSync(latestListenerFile, this.latestListenerVersionNumber);
        }

        return this.continueSetup(this.insights, ilDasmPath, netcoreSixPmiPath, netcoreSevenPmiPath, netcoreEightPmiPath, netcoreNinePmiPath, netcoreTenPmiPath, pmiPath, coreRootPaths, customCoreRootPath, outputPath, ilDasmOutputPath, pmiOutputPath, pmiTempDir);
    }

    ////////////////////////////////////////////////////////////////////////////
    // Private methods
    ////////////////////////////////////////////////////////////////////////////

    private async unzipDownloadedFile(insights: DotnetInsights, url: string, unzipFolder: string, outputPath: string, isCoreRoot: boolean, unzipName: string, arch?: string|null) : Promise<boolean> {
        insights.outputChannel.appendLine(`[COMPLETE]: mkdir ${outputPath}`);

        insights.outputChannel.appendLine(`readDir ${outputPath}`);

        var promise = new Promise<boolean>((resolve, reject) => {
            fs.readdir(outputPath, (err, filesInOutputPath) => {
                insights.outputChannel.appendLine(`[COMPLETE]: readDir ${outputPath}`);
                
                if (filesInOutputPath.length > 0) {
                    for (var index = 0; index < filesInOutputPath.length; ++index) {
                        if (filesInOutputPath[index] = "temp") {
                            continue;
                        }

                        try {
                            var folderToDelete = path.join(outputPath, filesInOutputPath[index]);
                            insights.outputChannel.appendLine(`rm -r ${folderToDelete}`);
                            rimraf(folderToDelete, (err) => {
                                insights.outputChannel.appendLine(`[COMPLETED]: rm -r ${folderToDelete}`);

                                if (err !== undefined && err !== null) { 
                                    insights.outputChannel.appendLine(`FAILED: ${err}`);
                                    reject();
                                }
                            });
                        }
                        catch (e) {
                            console.log(e);
                        }
                    }
                }

                insights.outputChannel.appendLine(`untar ${unzipName} ${outputPath}`);
                targz.decompress({
                    src: unzipName,
                    dest: outputPath
                }, function(err){
                    if(err) {
                        insights.outputChannel.appendLine(`untar failed: ${unzipName}, ${err}`);
                        resolve(false);
                        return;
                    } else {
                        insights.outputChannel.appendLine(`[COMPLETE]: untar ${unzipName}`);
                        insights.outputChannel.appendLine(`readdir ${unzipName}`);
                        fs.readdir(outputPath, (err, files) => {
                            var fileName = "";
                            if (isCoreRoot) {
                                for (var scanIndex = 0; scanIndex < files.length; ++scanIndex) {
                                    if (files[scanIndex][0] !== "." && files[scanIndex].indexOf("Core_Root") === -1 && files[scanIndex].indexOf(arch!) !== -1) {
                                        fileName = files[scanIndex];
                                        break;
                                    }
                                }
                            }

                            insights.outputChannel.appendLine(`[COMPLETE]: readdir ${unzipName}`);

                            if (isCoreRoot && fileName !== "Core_Root" && fileName !== "Core_Root-arm64") {
                                // Rename the folder to Core_Root

                                if (arch === null || arch === undefined) {
                                    reject();
                                }

                                if (arch !== "x64") {
                                    fs.rename(path.join(outputPath, fileName), path.join(outputPath, "Core_Root-" + arch), (err) => {
                                        insights.outputChannel.appendLine(`[FULL-COMPLETE]: ${url}`);
                                        resolve(true);
                                    });
                                }
                                else {
                                    fs.rename(path.join(outputPath, fileName), path.join(outputPath, "Core_Root"), (err) => {
                                        insights.outputChannel.appendLine(`[FULL-COMPLETE]: ${url}`);
                                        resolve(true);
                                    });
                                }
                            }
                            else {
                                insights.outputChannel.appendLine(`[FULL-COMPLETE]: ${url}`);
                                resolve(true);
                            }
                        });
                    }
                });
            });
        });

        return await promise;
    }

    private async downloadAndUnzip(insights: DotnetInsights, url: string, unzipFolder: string, outputPath: string, isCoreRoot: boolean, arch?: string|null) : Promise<boolean> {
        const unzipName = path.join(unzipFolder, crypto.randomBytes(16).toString("hex") + ".tar.gz");

        var retryCount = 3;
        var success = true;
        do
        {
            try {
                const fileStream = fs.createWriteStream(unzipName);

                insights.outputChannel.appendLine(`[${url}] -> ${unzipName}`);

                const response = await fetch(url);
                if (!response.ok) {
                    insights.outputChannel.append(response.statusText);
                }

                if (response.body !== null) {
                    await fs.promises.writeFile(unzipName, stream.Readable.from(response.body));
                }

                insights.outputChannel.appendLine(`Download completed: ${unzipName}`);
                insights.outputChannel.appendLine(`mkdir ${outputPath}`);

                try {
                    var dirExists = fs.existsSync(outputPath);

                    if (dirExists) {
                        success = await this.unzipDownloadedFile(insights, url, unzipFolder, outputPath, isCoreRoot, unzipName, arch);
                        if (success === false) {
                            continue;
                        }
                    }

                    else {
                        var mdPromise = await fs.promises.mkdir(outputPath);
                        dirExists = fs.existsSync(outputPath);
                        if (dirExists) {
                            success = await this.unzipDownloadedFile(insights, url, unzipFolder, outputPath, isCoreRoot, unzipName, arch);
                            if (success === false) {
                                continue;
                            }
                        }
                        else  {
                            success = false;
                            continue;
                        }
                    }
                }
                catch(e: any) {
                    // If exists, continue.
                    if (e.code !== os.constants.errno.EEXIST) {
                        insights.outputChannel.appendLine(e);
                        success = true;
                    }
                }
            }
            catch (e) {
                // Clean up temp zip file, which is large
                if (fs.existsSync(unzipName)) {
                    fs.unlinkSync(unzipName);
                }
                success = false;

                insights.outputChannel.appendLine("Failed to download, retrying.");
            }
        } while (!success && retryCount-- > 0);

        if (retryCount === 0) {
            insights.outputChannel.appendLine("Failed with retries.");
        }

        return success;
    }

    private sleep(ms: number): Promise<void> {
        return new Promise(resolve => setTimeout(resolve, ms));
    }

    private async downloadRuntimes(insights: DotnetInsights, versionNumber: string, unzipFolder: string) : Promise<boolean> {
        const runtimes: string[] = ["net6.0", "net7.0", "net8.0", "net9.0", "net10.0"];
        const coreRootFolder = unzipFolder;

        const runtimeArches: { [id: string]: { [id: string]: string[] } } = {
            "net6.0": {
                "win": ["x64"],
                "linux": ["x64"],
                "osx": ["arm64", "x64"]
            },
            "net7.0": {
                "win": ["x64"],
                "linux": ["x64"],
                "osx": ["arm64", "x64"]
            },
            "net8.0": {
                "win": ["x64"],
                "linux": ["x64"],
                "osx": ["arm64", "x64"]
            },
            "net9.0": {
                "win": ["x64"],
                "linux": ["x64"],
                "osx": ["arm64", "x64"]
            },
            "net10.0": {
                "win": ["x64"],
                "linux": ["x64"],
                "osx": ["arm64", "x64"]
            },
        };

        unzipFolder = path.join(unzipFolder, "temp");

        if (!fs.existsSync(coreRootFolder)) {
            fs.mkdirSync(coreRootFolder);
        }

        if (!fs.existsSync(unzipFolder)) {
            fs.mkdirSync(unzipFolder);
        }

        const baseRuntimeUrl = `https://github.com/jashook/vscode-dotnet-insights/releases/download/${versionNumber}/`;

        var success = true;
        var promises:Promise<boolean>[] = [];

        for (var index = 0; index < runtimes.length; ++index) {
            var osName: string = "osx";
            if (os.platform() === "win32") {
                osName = "win";
            }
            else if (os.platform() !== "darwin") {
                osName = "linux";
            }

            const outputPath = path.join(coreRootFolder, runtimes[index]);

            const runtimeLookup: string = runtimes[index];

            var runtimeArchForOsAndRuntime = runtimeArches[runtimeLookup][osName];
            for (var archIndex = 0; archIndex < runtimeArchForOsAndRuntime.length; ++archIndex) {
                let arch = runtimeArchForOsAndRuntime[archIndex];

                const runtimeUrl = baseRuntimeUrl + `${osName}-${arch}-${runtimes[index]}.tar.gz`;
                promises.push(this.downloadAndUnzip(insights, runtimeUrl, unzipFolder, outputPath, true, arch));
            }
        }

        var multipleDownloadPromise = Promise.all(promises);

        await multipleDownloadPromise;
        return success;
    }

    private async downloadPmiExe(insights: DotnetInsights, versionNumber: string, unzipFolder: string) : Promise<boolean> {
        const runtimes = ["net6.0", "net7.0", "net8.0", "net9.0", "net10.0"];
        const pmiExeFolder = unzipFolder;

        unzipFolder = path.join(unzipFolder, "temp");

        if (!fs.existsSync(pmiExeFolder)) {
            fs.mkdirSync(pmiExeFolder);
        }

        if (!fs.existsSync(unzipFolder)) {
            fs.mkdirSync(unzipFolder);
        }

        var success = true;

        const arch = "x64";
        const baseUrl = `https://github.com/jashook/vscode-dotnet-insights/releases/download/${versionNumber}/`;

        for (var index = 0; index < runtimes.length; ++index) {
            // Always download the windows built binaries for pmi
            var osName = "win";

            const outputPath = path.join(pmiExeFolder, runtimes[index]);

            const pmiUrl = baseUrl + `${osName}-${arch}-${runtimes[index]}-pmi.tar.gz`;
            success = success && await this.downloadAndUnzip(insights, pmiUrl, unzipFolder, outputPath, false);
        }

        return success;
    }

    private async downloadGcMonitorExe(insights: DotnetInsights, versionNumber: string, unzipFolder: string) : Promise<boolean> {
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
        if (os.platform() === "win32") {
            osName = "win";
        }
        else if (os.platform() !== "darwin") {
            osName = "linux";
        }

        const arch = "x64";
        const baseUrl = `https://github.com/jashook/vscode-dotnet-insights/releases/download/${versionNumber}/gcEventListener-${osName}.tar.gz`;

        var success = await this.downloadAndUnzip(insights, baseUrl, unzipFolder, exeFolder, false);
        return success;
    }

    private async downloadRoslynHelper(insights: DotnetInsights, versionNumber: string, unzipFolder: string) : Promise<boolean> {
        const exeFolder = unzipFolder;

        unzipFolder = path.join(unzipFolder, "temp");

        if (!fs.existsSync(exeFolder)) {
            fs.mkdirSync(exeFolder);
        }

        if (!fs.existsSync(unzipFolder)) {
            fs.mkdirSync(unzipFolder);
        }

        var osName = "osx";
        if (os.platform() === "win32") {
            osName = "win";
        }
        else if (os.platform() !== "darwin") {
            osName = "linux";
        }

        const arch = "x64";
        const baseUrl = `https://github.com/jashook/vscode-dotnet-insights/releases/download/${versionNumber}/roslynHelper-${osName}-${arch}.tar.gz`;

        var success = await this.downloadAndUnzip(insights, baseUrl, unzipFolder, exeFolder, false);
        return success;
    }

    private continueSetup(insights: DotnetInsights, ilDasmPath: any, netcoreSixPmiPath: any, netcoreSevenPmiPath: any, netcoreEightPmiPath: any, netcoreNinePmiPath: any, netcoreTenPmiPath: any, customPmiPath: any, coreRootPaths: { [id:string]: { [id: string]: string } }, customCoreRootPath: any, outputPath: any, ilDasmOutputPath: string, pmiOutputPath: string, pmiTempDir: string) : Thenable<boolean> {
        if (typeof(ilDasmPath) !== "string") {
            if (os.platform() === "darwin") {
                ilDasmPath = ilDasmPath["osx"];
            }
            else if (os.platform() === "linux") {
                ilDasmPath = ilDasmPath["linux"];
            }
            else {
                ilDasmPath = ilDasmPath["windows"];
            }
        }

        if (typeof(customPmiPath) !== "string" && (customPmiPath !== undefined && customPmiPath !== null)) {
            if (os.platform() === "darwin") {
                customPmiPath = customPmiPath["osx"];
            }
            else if (os.platform() === "linux") {
                customPmiPath = customPmiPath["linux"];
            }
            else {
                customPmiPath = customPmiPath["windows"];
            }
        }

        if (typeof(customCoreRootPath) !== "string") {
            if (os.platform() === "darwin") {
                customCoreRootPath = customCoreRootPath["osx"];
            }
            else if (os.platform() === "linux") {
                customCoreRootPath = customCoreRootPath["linux"];
            }
            else {
                customCoreRootPath = customCoreRootPath["windows"];
            }
        }

        if (typeof(outputPath) !== "string") {
            if (os.platform() === "darwin") {
                outputPath = outputPath["osx"];
            }
            else if (os.platform() === "linux") {
                outputPath = outputPath["linux"];
            }
            else {
                outputPath = outputPath["windows"];
            }
        }

        insights.ilDasmPath = ilDasmPath;

        insights.netcoreSixX64CoreRootPath = coreRootPaths["6.0"]["x64"];
        insights.netcoreSevenX64CoreRootPath = coreRootPaths["7.0"]["x64"];
        insights.netcoreEightX64CoreRootPath = coreRootPaths["8.0"]["x64"];
        insights.netcoreNineX64CoreRootPath = coreRootPaths["9.0"]["x64"];
        insights.netcoreTenX64CoreRootPath = coreRootPaths["10.0"]["x64"];

        insights.netcoreSixArm64CoreRootPath = coreRootPaths["6.0"]["arm64"];
        insights.netcoreSevenArm64CoreRootPath = coreRootPaths["7.0"]["arm64"];
        insights.netcoreEightArm64CoreRootPath = coreRootPaths["8.0"]["arm64"];
        insights.netcoreNineArm64CoreRootPath = coreRootPaths["9.0"]["arm64"];
        insights.netcoreTenArm64CoreRootPath = coreRootPaths["10.0"]["arm64"];

        insights.customCoreRootPath = customCoreRootPath;

        insights.ilDasmOutputPath = ilDasmOutputPath;
        insights.pmiOutputPath = pmiOutputPath;

        insights.pmiTempDir = pmiTempDir;

        // Setup mostly will involve making sure we have the dotnet tools required
        // to provide insights.

        var coreRunExe = "";
        if (os.platform() === "win32") {
            coreRunExe = "CoreRun.exe";
        }
        else {
            coreRunExe = "corerun";
        }

        insights.netcoreSixX64CoreRunPath = path.join(insights.netcoreSixX64CoreRootPath, coreRunExe);
        insights.netcoreSevenX64CoreRunPath = path.join(insights.netcoreSevenX64CoreRootPath, coreRunExe);
        insights.netcoreEightX64CoreRunPath = path.join(insights.netcoreEightX64CoreRootPath, coreRunExe);
        insights.netcoreNineX64CoreRunPath = path.join(insights.netcoreNineX64CoreRootPath, coreRunExe);
        insights.netcoreTenX64CoreRunPath = path.join(insights.netcoreTenX64CoreRootPath, coreRunExe);

        insights.netcoreSixArm64CoreRunPath = insights.netcoreSixArm64CoreRootPath !== undefined ? path.join(insights.netcoreSixArm64CoreRootPath, coreRunExe) : "";
        insights.netcoreSevenArm64CoreRunPath = insights.netcoreSevenArm64CoreRootPath !== undefined ? path.join(insights.netcoreSevenArm64CoreRootPath, coreRunExe) : "";
        insights.netcoreEightArm64CoreRunPath = insights.netcoreEightArm64CoreRootPath !== undefined ? path.join(insights.netcoreEightArm64CoreRootPath, coreRunExe) : "";
        insights.netcoreNineArm64CoreRunPath = insights.netcoreNineArm64CoreRootPath !== undefined ? path.join(insights.netcoreNineArm64CoreRootPath, coreRunExe) : "";
        insights.netcoreTenArm64CoreRunPath = insights.netcoreTenArm64CoreRootPath !== undefined ? path.join(insights.netcoreTenArm64CoreRootPath, coreRunExe) : "";

        if (process.arch === "arm64") {
            if (!fs.existsSync(insights.netcoreSixArm64CoreRootPath) ||
                !fs.existsSync(insights.netcoreSevenArm64CoreRootPath) ||
                !fs.existsSync(insights.netcoreEightArm64CoreRootPath)  ||
                !fs.existsSync(insights.netcoreNineArm64CoreRootPath)  ||
                !fs.existsSync(insights.netcoreTenArm64CoreRootPath) ) {
                vscode.window.showWarningMessage("Failed to set corerun path.");
                Promise.resolve(false);
            }
        }

        if (!fs.existsSync(insights.netcoreSixX64CoreRunPath) || 
            !fs.existsSync(insights.netcoreSevenX64CoreRunPath) || 
            !fs.existsSync(insights.netcoreEightX64CoreRunPath) || 
            !fs.existsSync(insights.netcoreNineX64CoreRunPath) || 
            !fs.existsSync(insights.netcoreTenX64CoreRunPath)) {
            vscode.window.showWarningMessage("Failed to set corerun path.");
            Promise.resolve(false);
        }

        insights.netcoreSixPmiPath = netcoreSixPmiPath;
        insights.netcoreSevenPmiPath = netcoreSevenPmiPath;
        insights.netcoreEightPmiPath = netcoreEightPmiPath;
        insights.netcoreNinePmiPath = netcoreNinePmiPath;
        insights.netcoreTenPmiPath = netcoreTenPmiPath;

        // Change default here.
        if (insights.useNetCoreLts) {
            insights.pmiPath = insights.netcoreEightPmiPath;
        }
        else {
            insights.pmiPath = insights.netcoreTenPmiPath;
        }

        if (customPmiPath !== "" && (customPmiPath !== undefined && customPmiPath !== null)) {
            insights.pmiPath = customPmiPath;
        }

        const isArm64 = process.arch === "arm64";

        if (insights.customCoreRootPath === "" || insights.customCoreRootPath === "") {
            if (insights.useNetCoreLts) {
                insights.coreRunPath = isArm64 ? insights.netcoreEightArm64CoreRunPath : insights.netcoreEightX64CoreRunPath;
            }
            else {
                insights.coreRunPath = isArm64 ? insights.netcoreEightArm64CoreRunPath : insights.netcoreEightX64CoreRunPath;
            }
        }
        else {
            insights.customCoreRunPath = path.join(insights.customCoreRootPath, coreRunExe);
            insights.coreRunPath = insights.customCoreRunPath;
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
            
            if (error !== null) {
                insights.outputChannel.appendLine(stderr);
                if (os.platform() !== "win32") {
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
                    this.insights.outputChannel.appendLine("Failed to setup .NET ILDasm.");
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
        if (localRuntimeBuild !== null) {
            // We will not need to do a download
            var config = vscode.workspace.getConfiguration("defaultValue");
        }
        else {
            this.insights.outputChannel.appendLine("Local path currently is required the setup is incomplete.");
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
        if (error !== null && stdout === undefined) {
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
        });
    });
}

function checkForDotnetSdk(insights: DotnetInsights) : Thenable<boolean> {
    // Check for the dotnet sdk. This is not necessary, it is possible to look
    // at managed pe files as well. In addition, we can hijack the jit dropped 
    // into the publish folder for superPMI.

    var dotnetCommand = "dotnet --list-sdks";

    var childProcess: child.ChildProcess = child.exec(dotnetCommand, (error: any, stdout: string, stderr: string) => {
        if (error !== null) {
            // The dotnet sdk is not installed.
            // We do not currently know what version to install.

            return;
        }
        else {
            var lines = stdout.split("\n");

            var installedSdks = [];

            insights.outputChannel.appendLine("Installed SDK Versions:");
            for (var index = 0; index < lines.length; ++index) {
                var sdkVersion = lines[index].split(" ")[0];

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

function checkFoldersExist(folders: string[]) {
    for (var index = 0; index < folders.length; ++index) {
        if (!fs.existsSync(folders[index])) {
            return false;
        }
    }

    return true;
}