import * as fs from "fs";
import * as os from "os";
import * as path from 'path';
import * as vscode from 'vscode';

import * as xml2js from 'xml2js';

import { DotnetInsights } from "./dotnetInsights";
import { GcListener, ProcessInfo, GcData, AllocData } from "./GcListener";

import { DotnetInsightsGcDocument } from "./DotnetInsightsGcEditor";
import { promises } from "dns";
import { rejects } from "assert";
import { exec } from "child_process";

export class DotnetInsightsGcSnapshotEditor implements vscode.CustomReadonlyEditorProvider {
    public static register(context: vscode.ExtensionContext, insights: DotnetInsights): vscode.Disposable {
        const provider = new DotnetInsightsGcSnapshotEditor(context, insights, null);
        const providerRegistration = vscode.window.registerCustomEditorProvider(DotnetInsightsGcSnapshotEditor.viewType, provider);
        return providerRegistration;
    }

    public static readonly viewType = 'dotnetInsightsGcSnapshot.edit';

    private timeInGc: number;
    private allocData: AllocData[] | undefined;
    
    constructor(
        private readonly context: vscode.ExtensionContext,
        private readonly insights: DotnetInsights,
        private gcData: any
    ) {
        this.timeInGc = 0;
        this.allocData = undefined;
    }

    openCustomDocument(uri: vscode.Uri, openContext: vscode.CustomDocumentOpenContext, token: vscode.CancellationToken): vscode.CustomDocument | Thenable<vscode.CustomDocument> {
        var filename = path.basename(uri.path);
        var endofLine = os.platform() == "win32" ? vscode.EndOfLine.CRLF : vscode.EndOfLine.LF;

        var processId = parseInt(filename.split(".gcstats")[0]);

        var document = new DotnetInsightsGcDocument(uri,
                                                    filename,
                                                    false,
                                                    "ildasm",
                                                    1,
                                                    false,
                                                    true,
                                                    endofLine,
                                                    0,
                                                    processId,
                                                    null);

        return document;
    }

    resolveCustomEditor(document: vscode.CustomDocument, webviewPanel: vscode.WebviewPanel, token: vscode.CancellationToken): void | Thenable<void> {
        webviewPanel.webview.options = {
            enableScripts: true,
            localResourceRoots: [
                vscode.Uri.joinPath(this.context.extensionUri, 'node_modules', 'chart.js', 'dist'),
                vscode.Uri.joinPath(this.context.extensionUri, 'media')
            ]
        }

        var gcDocument = document as DotnetInsightsGcDocument;
        const pid = gcDocument.processId;

        var gcEditor = this;

        function updateWebview() {
            // There are no updates possible for this view type.

            webviewPanel.webview.postMessage({
                type: 'update'
            });
        }

        this.getHtmlForWebviewWrapper(gcDocument, webviewPanel.webview).then((str) => {
            webviewPanel.webview.html = str;
        });

        updateWebview();
    }

    private gcDataFromXml(input: any): any {
        var gcData = [] as GcData[];

        var processName = "";
        var processCommandLine = "";

        try {
            const processInfo = input["GCProcess"];
            const gcEvents = processInfo["GCEvents"][0]["GCEvent"];

            processName = processInfo["$"]["Process"];
            processCommandLine = processInfo["$"]["CommandLine"];

            var gcDataToAdd = [] as any[];
            for (var index = 0; index < gcEvents.length; ++index) {
                const currentGc = gcEvents[index];
                const gen0MinSize = parseInt(currentGc["GlobalHeapHistory"][0]["$"]["FinalYoungestDesired"].replace(',',''));
                const generation = currentGc["$"]["GCGeneration"];
                const generationSize0 = parseInt(currentGc["HeapStats"][0]["$"]["GenerationSize0"].replace(',',''));
                const generationSize1 = parseInt(currentGc["HeapStats"][0]["$"]["GenerationSize1"].replace(',',''));
                const generationSize2 = parseInt(currentGc["HeapStats"][0]["$"]["GenerationSize2"].replace(',',''));
                const generationSizeLOH = parseInt(currentGc["HeapStats"][0]["$"]["GenerationSize3"].replace(',',''));

                var generationSizePOH = 0;

                try {
                    generationSizePOH = parseInt(currentGc["HeapStats"][0]["$"]["GenerationSize4"].replace(',',''));
                }
                catch (e) {
                    
                }

                const id = currentGc["$"]["GCNumber"];
                const kind = currentGc["$"]["Type"];

                const numHeaps = parseInt(currentGc["GlobalHeapHistory"][0]["$"]["NumHeaps"].replace(',',''));
                const pauseDurationMSec = parseInt(currentGc["$"]["PauseDurationMSec"].replace(',',''));
                const pauseStartRelativeMSec = parseInt(currentGc["$"]["PauseStartRelativeMSec"].replace(',',''));
                const pauseEndRelativeMSec = pauseStartRelativeMSec + pauseDurationMSec;
                const reason = currentGc["$"]["Reason"];
                const gcDurationMSec = parseInt(currentGc["$"]["GCDurationMSec"].replace(',',''));

                const totalHeapSize = generationSize0 + generationSize1 + generationSize2 + generationSizeLOH + generationSizePOH;

                const totalPromotedSize0 = parseInt(currentGc["HeapStats"][0]["$"]["TotalPromotedSize0"].replace(',',''));
                const totalPromotedSize1 = parseInt(currentGc["HeapStats"][0]["$"]["TotalPromotedSize1"].replace(',',''));
                const totalPromotedSize2 = parseInt(currentGc["HeapStats"][0]["$"]["TotalPromotedSize2"].replace(',',''));
                const totalPromotedSizeLoh = parseInt(currentGc["HeapStats"][0]["$"]["TotalPromotedSize3"].replace(',',''));

                var totalPromotedSizePoh = 0;
                try {
                    totalPromotedSizePoh = parseInt(currentGc["HeapStats"][0]["$"]["TotalPromotedSize3"].replace(',',''));
                }
                catch (e) {

                }

                let kb = 1024 * 1024;

                var data = {
                    "Gen0MinSize": gen0MinSize,
                    "generation": parseInt(generation),
                    "GenerationSize0": generationSize0 * kb,
                    "GenerationSize1": generationSize1 * kb,
                    "GenerationSize2": generationSize2 * kb,
                    "GenerationSizeLOH": generationSizeLOH * kb,
                    "Id": id,
                    "kind": kind,
                    "NumHeaps": numHeaps,
                    "PauseDurationMSec": pauseDurationMSec,
                    "PauseEndRelativeMSec": pauseEndRelativeMSec,
                    "PauseStartRelativeMSec": pauseStartRelativeMSec,
                    "Reason": reason,
                    "Heaps": [] as any[],
                    "TotalHeapSize": totalHeapSize * kb,
                    "TotalPromoted": totalPromotedSize0 * kb,
                    "TotalPromotedLOH": totalPromotedSizeLoh * kb,
                    "TotalPromotedSize0": totalPromotedSize0 * kb,
                    "TotalPromotedSize1": totalPromotedSize1 * kb,
                    "TotalPromotedSize2": totalPromotedSize2 * kb,
                    "Type": reason,
                    "GCDurationMSec": gcDurationMSec
                }

                var heaps = [] as any[];
                console.assert(currentGc["PerHeapHistories"][0]["PerHeapHistory"].length == numHeaps);

                var currentHeapData : any = {
                    "Generations": {
                        0: null,
                        1: null,
                        2: null,
                        3: null
                    }
                };

                var tryParse = (genData: any, key: string, isNumber?: boolean | null): any => {
                    try {
                        if (isNumber != null && isNumber == false) {
                            return genData[key];
                        }
                        else {
                            return parseInt(genData[key].replace(',',''));
                        }
                    }
                    catch (e) {
                        return 0.0;
                    }
                }

                for (var heapIndex = 0; heapIndex < currentGc["PerHeapHistories"][0]["PerHeapHistory"].length; ++heapIndex) {
                    var heapGenerations = [0, 1, 2, 3];
                    const currentHeap = currentGc["PerHeapHistories"][0]["PerHeapHistory"][heapIndex];

                    for (var generationIndex = 0; generationIndex < heapGenerations.length; ++generationIndex) {
                        const genNumber = generationIndex;
                        const currentGenData = currentHeap["GenData"][generationIndex]["$"];

                        const fragmentation = tryParse(currentGenData, "Fragmentation", true);
                        const freeListSpaceAfter = tryParse(currentGenData, "FreeListSpaceAfter", true);
                        const freeListSpaceBefore = tryParse(currentGenData, "FreeListSpaceBefore", true);
                        const freeObjSpaceAfter = tryParse(currentGenData, "FreeObjSpaceAfter", true);
                        const freeObjSpaceBefore = tryParse(currentGenData, "FreeObjSpaceBefore", true);
                        const genid = tryParse(currentGenData, "Name");
                        const genin = tryParse(currentGenData, "In", true);
                        const newAllocation = tryParse(currentGenData, "NewAllocation", true);
                        const nonePinnedSurv = tryParse(currentGenData, "NonePinnedSurv", true);
                        const objSizeAfter = tryParse(currentGenData, "ObjSizeAfter", true);
                        const objSpaceBefore = tryParse(currentGenData, "ObjSpaceBefore", true);

                        const out = tryParse(currentGenData, "Out", true);
                        const pinnedSurv = tryParse(currentGenData, "PinnedSurv", true);

                        const sizeAfter = tryParse(currentGenData, "SizeAfter", true);
                        const sizeBefore = tryParse(currentGenData, "SizeBefore", true);
                        const survRate = tryParse(currentGenData, "SurvRate", true);

                        currentHeapData["Generations"][generationIndex] = {
                            "Fragmentation": fragmentation,
                            "FreeListSpaceAfter": freeListSpaceAfter * kb,
                            "FreeListSpaceBefore" : freeListSpaceBefore * kb,
                            "FreeObjSpaceAfter" : freeObjSpaceAfter * kb,
                            "FreeObjSpaceBefore" : freeObjSpaceBefore * kb,
                            "Id" : genid,
                            "In" : genin * kb,
                            "NewAllocation" : newAllocation * kb,
                            "NonePinnedSurv" : nonePinnedSurv * kb,
                            "ObjSizeAfter" : objSizeAfter * kb,
                            "ObjSpaceBefore": objSpaceBefore * kb,
                            "Out" : out * kb,
                            "PinnedSurv" : pinnedSurv * kb,
                            "SizeAfter" : sizeAfter * kb,
                            "SizeBefore" : sizeBefore * kb,
                            "SurvRate" : survRate * kb
                        };
                    }

                    data["Heaps"].push(currentHeapData);
                }

                gcDataToAdd.push({"data": data});
            }

            return {
                "gcData": gcDataToAdd,
                "processName": processName
            };
        }
        catch (e) {
            return null;
        }
    }

    private parseFromXml(fileContents: Buffer): Thenable<any> {
        var returnValue = new Promise((resolve, reject) => {
            var parser = new xml2js.Parser();

            parser.parseString(fileContents, (_err: any, _result: any) => {
                if (_err) {
                    resolve(null);
                }
                else {
                    resolve(this.gcDataFromXml(_result));
                }
            });
        });

        return returnValue;
    }

    private getHtmlForWebview(document: DotnetInsightsGcDocument, webview: vscode.Webview, gcData: any) : string {
        const defaultHtmlReturn = /* html */`
        <!DOCTYPE html>
        <html lang="en">
        <head>
            <meta charset="UTF-8">
            <!--
            Use a content security policy to only allow loading images from https or from our extension directory,
            and only allow scripts that have a specific nonce.
            -->
            
            <meta http-equiv="Content-Security-Policy" 
            content="default-src * vscode-resource: https: 'unsafe-inline' 'unsafe-eval';
            script-src vscode-webview-resource: https: 'unsafe-inline' 'unsafe-eval';
            style-src vscode-webview-resource: https: 'unsafe-inline';
            img-src vscode-resource: https:;
            connect-src vscode-resource: https: http:;">

            <meta name="viewport" content="width=device-width, initial-scale=1.0">
        </head>
        <body>
            
        </body>
        </html>`;

        if (gcData == null) {
            // We should have the json representation of the gc stats
            try
            {
                const fileContents = fs.readFileSync(document.uri.fsPath);
                gcData = JSON.parse(fileContents.toString());
    
                if (gcData["allocations"] == null || gcData["gcData"] == null) {
                    throw new Error("Json error.");
                }
            }
            catch(e) {
                vscode.window.showWarningMessage(`${document.uri.fsPath} is corrupted or a incorrect type.`);
                return defaultHtmlReturn;
            }

        }

        // gc data has all of the allocations and gc events that occurred in the
        // window. We will now go through and calculate the interesting data we
        // want from what we were provided.

        const gcs = gcData["gcData"];
        
        let getAllocationAmount = (generationToUse?: number | undefined) => {
            try {
                for (var index = 0; index < gcs.length; ++index) {

                    if (generationToUse == undefined) {
                        for (var heapIndex = 0; heapIndex < currentGc["Heaps"].length; ++heapIndex) {
                            gcs[index]["data"]["Heaps"][heapIndex]["Generations"][0]["NewAllocation"] = parseInt(gcs[index]["data"]["TotalHeapSize"].replace(',', ''));
                            gcs[index]["data"]["Heaps"][heapIndex]["Generations"][1]["NewAllocation"] = parseInt(gcs[index]["data"]["TotalHeapSize"].replace(',', ''));
                            gcs[index]["data"]["Heaps"][heapIndex]["Generations"][2]["NewAllocation"] = parseInt(gcs[index]["data"]["TotalHeapSize"].replace(',', ''));
                            gcs[index]["data"]["Heaps"][heapIndex]["Generations"][3]["NewAllocation"] = parseInt(gcs[index]["data"]["TotalHeapSize"].replace(',', ''));
                        }
                    }
                    else {
                        for (var heapIndex = 0; heapIndex < currentGc["Heaps"].length; ++heapIndex) {
                            gcs[index]["data"]["Heaps"][heapIndex]["Generations"][generationToUse]["NewAllocation"] = parseInt(gcs[index]["data"]["TotalHeapSize"].replace(',', ''));
                        }
                    }
                }
            }
            catch (e) {

            }

            var kb = 1024 * 1024;
            var totalAllocations = 0;
            var allocationsBetweenGc = [];

            for (var index = 0; index < gcs.length; ++index ){
                var currentGc = gcs[index]["data"];

                var newAllocAmount = 0;

                if (generationToUse == undefined) {
                    for (var heapIndex = 0; heapIndex < currentGc["Heaps"].length; ++heapIndex) {
                        newAllocAmount += currentGc["Heaps"][heapIndex]["Generations"][0]["NewAllocation"] / kb;
                        newAllocAmount += currentGc["Heaps"][heapIndex]["Generations"][1]["NewAllocation"] / kb;
                        newAllocAmount += currentGc["Heaps"][heapIndex]["Generations"][2]["NewAllocation"] / kb;
                        newAllocAmount += currentGc["Heaps"][heapIndex]["Generations"][3]["NewAllocation"] / kb;
                    }
                }
                else {
                    for (var heapIndex = 0; heapIndex < currentGc["Heaps"].length; ++heapIndex) {
                        newAllocAmount += currentGc["Heaps"][heapIndex]["Generations"][generationToUse]["NewAllocation"] / kb;
                    }
                }

                totalAllocations += newAllocAmount;
                allocationsBetweenGc.push(newAllocAmount);
            }

            var maxAllocationAmountBetweenGcs = 0;
            var meanAllocationBetweenGcs = 0;
            var medianAllocationsBetweenGcs = 0;
            var lowestAllocationAmountBetweenGcs = allocationsBetweenGc[0];

            for (var index = 0; index < allocationsBetweenGc.length; ++index) {
                if (allocationsBetweenGc[index] > maxAllocationAmountBetweenGcs) {
                    maxAllocationAmountBetweenGcs = allocationsBetweenGc[index];
                }
                if (allocationsBetweenGc[index] < lowestAllocationAmountBetweenGcs) {
                    lowestAllocationAmountBetweenGcs = allocationsBetweenGc[index];
                }
            }

            allocationsBetweenGc.sort();
            var half = Math.floor(allocationsBetweenGc.length / 2);
            medianAllocationsBetweenGcs = allocationsBetweenGc[half];

            meanAllocationBetweenGcs = totalAllocations / allocationsBetweenGc.length;

            return [allocationsBetweenGc, [totalAllocations, meanAllocationBetweenGcs, medianAllocationsBetweenGcs, maxAllocationAmountBetweenGcs, lowestAllocationAmountBetweenGcs]];
        };

        let getValues = (generation?: number | undefined) : [number[], number[]] => {
            var totalTimeInGc: number = 0.0;
            var timesInEachGc = [];
            var averageTimeInGc: number = 0;
            var medianTimeInGc = 0;
            var highestTimeInGc = 0;
            var lowestTimeInGc = 0;

            for (var index = 0; index < gcs.length; ++index) {
                if (generation != undefined) {
                    if (gcs[index]["data"]["generation"] == generation) {
                        timesInEachGc.push(parseFloat(gcs[index]["data"]["PauseDurationMSec"]));
                    }
                }
                else {
                    timesInEachGc.push(parseFloat(gcs[index]["data"]["PauseDurationMSec"]));
                }
            }
    
            // Gcs over 50ms
            var expensiveGcs = [];
            for (var index = 0; index < timesInEachGc.length; ++index) {
                if (timesInEachGc[index] > 50) {
                    expensiveGcs.push(gcs[index]["data"]);
                }
            }
    
            lowestTimeInGc = timesInEachGc[0];
            for (var index = 0; index < timesInEachGc.length; ++index) {
                totalTimeInGc += timesInEachGc[index];
    
                if (timesInEachGc[index] < lowestTimeInGc) {
                    lowestTimeInGc = timesInEachGc[index];
                }
    
                if (timesInEachGc[index] > highestTimeInGc) {
                    highestTimeInGc = timesInEachGc[index];
                }
            }
    
            timesInEachGc.sort();
            var half = Math.floor(timesInEachGc.length / 2);
            medianTimeInGc = timesInEachGc[half];
    
            averageTimeInGc = totalTimeInGc / timesInEachGc.length;

            if (timesInEachGc.length == 0) {
                totalTimeInGc = 0;
                timesInEachGc = [];
                averageTimeInGc = 0;
                medianTimeInGc = 0;
                highestTimeInGc = 0;
                lowestTimeInGc = 0;
            }

            return [timesInEachGc, [totalTimeInGc, averageTimeInGc, medianTimeInGc, highestTimeInGc, lowestTimeInGc]];
        };

        var totalNumbers = getValues();

        let gen0Numbers = getValues(0);
        let gen1Numbers = getValues(1);
        let gen2Numbers = getValues(2);

        var allocationAmountTotal = getAllocationAmount();
        var allocationAmountGen0 = getAllocationAmount(0);
        var allocationAmountGen1 = getAllocationAmount(1);
        var allocationAmountGen2 = getAllocationAmount(2);
        var allocationAmountLOH = getAllocationAmount(3);

        var dataValue = "kb";

        var totalTotalValue = "mb";

        if (allocationAmountTotal[1][0].toFixed(2).length > 8) {
            dataValue = "mb";

            allocationAmountTotal[1][0] /= 1024;
            allocationAmountTotal[1][1] /= 1024;
            allocationAmountTotal[1][2] /= 1024;
            allocationAmountTotal[1][3] /= 1024;
            allocationAmountTotal[1][4] /= 1024;

            allocationAmountGen0[1][0] /= 1024;
            allocationAmountGen0[1][1] /= 1024;
            allocationAmountGen0[1][2] /= 1024;
            allocationAmountGen0[1][3] /= 1024;
            allocationAmountGen0[1][4] /= 1024;

            allocationAmountGen1[1][0] /= 1024;
            allocationAmountGen1[1][1] /= 1024;
            allocationAmountGen1[1][2] /= 1024;
            allocationAmountGen1[1][3] /= 1024;
            allocationAmountGen1[1][4] /= 1024;

            allocationAmountGen2[1][0] /= 1024;
            allocationAmountGen2[1][1] /= 1024;
            allocationAmountGen2[1][2] /= 1024;
            allocationAmountGen2[1][3] /= 1024;
            allocationAmountGen2[1][4] /= 1024;

            allocationAmountLOH[1][0] /= 1024;
            allocationAmountLOH[1][1] /= 1024;
            allocationAmountLOH[1][2] /= 1024;
            allocationAmountLOH[1][3] /= 1024;
            allocationAmountLOH[1][4] /= 1024;
        }
        
        if (allocationAmountTotal[1][0].toFixed(2).length > 8) {
            totalTotalValue = "gb";

            allocationAmountTotal[1][0] /= 1024;
            allocationAmountGen0[1][0] /= 1024;
            allocationAmountGen1[1][0] /= 1024;
            allocationAmountGen2[1][0] /= 1024;
            allocationAmountLOH[1][0] /= 1024;
        }

        var allocTotal = allocationAmountTotal[1][0].toFixed(2);
        var allocAverage = allocationAmountTotal[1][1].toFixed(2);
        var allocMedian = allocationAmountTotal[1][2].toFixed(2);
        var allocHighest = allocationAmountTotal[1][3].toFixed(2);
        var allocLowest = allocationAmountTotal[1][4].toFixed(2);
        var allocByGc = allocationAmountTotal[0];

        var allocGen0Total = allocationAmountGen0[1][0].toFixed(2);
        var allocGen0Average = allocationAmountGen0[1][1].toFixed(2);
        var allocGen0Median = allocationAmountGen0[1][2].toFixed(2);
        var allocGen0Highest = allocationAmountGen0[1][3].toFixed(2);
        var allocGen0Lowest = allocationAmountGen0[1][4].toFixed(2);
        var allocGen0ByGc = allocationAmountGen0[0];

        var allocGen1Total = allocationAmountGen1[1][0].toFixed(2);
        var allocGen1Average = allocationAmountGen1[1][1].toFixed(2);
        var allocGen1Median = allocationAmountGen1[1][2].toFixed(2);
        var allocGen1Highest = allocationAmountGen1[1][3].toFixed(2);
        var allocGen1Lowest = allocationAmountGen1[1][4].toFixed(2);
        var allocGen1ByGc = allocationAmountGen1[0];

        var allocGen2Total = allocationAmountGen2[1][0].toFixed(2);
        var allocGen2Average = allocationAmountGen2[1][1].toFixed(2);
        var allocGen2Median = allocationAmountGen2[1][2].toFixed(2);
        var allocGen2Highest = allocationAmountGen2[1][3].toFixed(2);
        var allocGen2Lowest = allocationAmountGen2[1][4].toFixed(2);
        var allocGen2ByGc = allocationAmountGen2[0];

        var allocLOHTotal = allocationAmountLOH[1][0].toFixed(2);
        var allocLOHAverage = allocationAmountLOH[1][1].toFixed(2);
        var allocLOHMedian = allocationAmountLOH[1][2].toFixed(2);
        var allocLOHHighest = allocationAmountLOH[1][3].toFixed(2);
        var allocLOHLowest = allocationAmountLOH[1][4].toFixed(2);
        var allocLOHByGc = allocationAmountLOH[0];

        // Time in GC.

        var totalTimeInGc = totalNumbers[1][0].toFixed(2);
        var averageTimeInGc = totalNumbers[1][1].toFixed(2);
        var medianTimeInGc = totalNumbers[1][2].toFixed(2);
        var highestTimeInGc = totalNumbers[1][3].toFixed(2);
        var lowestTimeInGc = totalNumbers[1][4].toFixed(2);
        var timeinsideEachGc = totalNumbers[0];

        var gen0TotalTimeInGc = gen0Numbers[1][0].toFixed(2);
        var gen0TimesInEachGc = gen0Numbers[0];
        var gen0AverageTimeInGc = gen0Numbers[1][1].toFixed(2);
        var gen0MedianTimeInGc = gen0Numbers[1][2].toFixed(2);
        var gen0HighestTimeInGc = gen0Numbers[1][3].toFixed(2);
        var gen0LowestTimeInGc = gen0Numbers[1][4].toFixed(2);

        var gen1TotalTimeInGc = gen1Numbers[1][0].toFixed(2);
        var gen1TimesInEachGc = gen1Numbers[0];
        var gen1AverageTimeInGc = gen1Numbers[1][1].toFixed(2);
        var gen1MedianTimeInGc = gen1Numbers[1][2].toFixed(2);
        var gen1HighestTimeInGc = gen1Numbers[1][3].toFixed(2);
        var gen1LowestTimeInGc = gen1Numbers[1][4].toFixed(2);

        var gen2TotalTimeInGc = gen2Numbers[1][0].toFixed(2);
        var gen2TimesInEachGc = gen2Numbers[0];
        var gen2AverageTimeInGc = gen2Numbers[1][1].toFixed(2);
        var gen2MedianTimeInGc = gen2Numbers[1][2].toFixed(2);
        var gen2HighestTimeInGc = gen2Numbers[1][3].toFixed(2);
        var gen2LowestTimeInGc = gen2Numbers[1][4].toFixed(2);

        const nonce = this.getNonce();

        const mainUri = webview.asWebviewUri(vscode.Uri.joinPath(this.context.extensionUri, 'media', 'snapshot.css'));
        const styleResetUri = webview.asWebviewUri(vscode.Uri.joinPath(this.context.extensionUri, 'media', 'reset.css'));
        const styleVSCodeUri = webview.asWebviewUri(vscode.Uri.joinPath(this.context.extensionUri, 'media', 'vscode.css'));

        const scriptUri = webview.asWebviewUri(vscode.Uri.joinPath(this.context.extensionUri, 'media', 'snapshotGcStats.js'));

        const chartjs = webview.asWebviewUri(vscode.Uri.joinPath(this.context.extensionUri, 'node_modules', 'chart.js', 'dist', 'Chart.min.js'));

        var canvasData = "";
        if (gcs.length > 0) {
            canvasData += `<div class="heapChartParentMultiple"><canvas class="gcStatsChart"></canvas></div>`;
            canvasData += `<div class="allocChartParent heapChartNextLine"><canvas class="gcStatsTimeChart"></canvas></div>`;
        }

        var totalCanvasData = "";
        if (gcs.length > 0) {
            totalCanvasData += `<div class="gcStats"><canvas id="totalGcStatsOverTime"></canvas></div>`;
        }

        var perHeapCanvasData = "";
        if (gcs.length > 0) {
            const gcData = gcs[0].data;

            for (var innerIndex = 0; innerIndex < gcData["Heaps"].length; ++innerIndex) {
                perHeapCanvasData += `<div class="heapChartParentMultiple"><canvas class="heapChart"></canvas></div>`;

                if (innerIndex + 1 != gcData["Heaps"].length) {
                    ++innerIndex;
                    perHeapCanvasData += `<div class="allocChartParent heapChartNextLine"><canvas class="heapChart"></canvas></div>`;
                }
            }
        }

        const gcCountsByGen = JSON.stringify([gen0TimesInEachGc.length, gen1TimesInEachGc.length, gen2TimesInEachGc.length]);

        var gcsToSerialize = [] as GcData[];
        for (var index = 0; index < gcs.length; ++index) {
            var gcDataNew = new GcData(gcs[index]);

            gcsToSerialize.push(gcDataNew);
        }
      
        var hiddenData = null;

        try {
            hiddenData = JSON.stringify(gcsToSerialize);
        }
        catch(e) {
            var i = 0;
        }

        var totalTimeInEachGc = [
            gen0TotalTimeInGc,
            gen1TotalTimeInGc,
            gen2TotalTimeInGc
        ];

        const totalTimeInEachGcJson = JSON.stringify(totalTimeInEachGc);

        // Allocations

        var htmlToReturn = /* html */`
        <!DOCTYPE html>
        <html lang="en">
            <head>
                <meta charset="UTF-8">
                <!--
                Use a content security policy to only allow loading images from https or from our extension directory,
                and only allow scripts that have a specific nonce.
                -->
                
                <!--<meta http-equiv="Content-Security-Policy" content="default-src 'none'; img-src ${webview.cspSource}; style-src ${webview.cspSource}; script-src 'nonce-${nonce}';">-->
                
                <meta http-equiv="Content-Security-Policy" 
                content="default-src * vscode-resource: https: 'unsafe-inline' 'unsafe-eval';
                script-src vscode-webview-resource: https: 'unsafe-inline' 'unsafe-eval';
                style-src vscode-webview-resource: https: 'unsafe-inline';
                img-src vscode-resource: https:;
                connect-src vscode-resource: https: http:;">

                <meta name="viewport" content="width=device-width, initial-scale=1.0">
                <link href="${styleResetUri}" rel="stylesheet" />
                <link href="${mainUri}" rel="stylesheet" />
                <link href="${styleVSCodeUri}" rel="stylesheet" />
            </head>
            <body>
                <span style="display:none" id="hiddenData"><!--${hiddenData}--></span>
                <span style="display:none" id="gcCountsByGen"><!--${gcCountsByGen}--></span>
                <span style="display:none" id="totalTimeInEachGcJson"><!--${totalTimeInEachGcJson}--></span>
                <h2 class="divider">${gcData["processName"]}</h2>

                <div id="timeSummary">Allocation Amount by Generation</div>

                <div class="summaryGcDiv">
                    <div class="total">
                        <div>Total</div>
                        <div>Total<span>${allocTotal} ${totalTotalValue}</span></div>
                        <div>Largest<span>${allocHighest} ${dataValue}</span></div>
                        <div>Smallest<span>${allocLowest} ${dataValue}</span></div>
                        <div>Average<span>${allocAverage} ${dataValue}</span></div>
                        <div>Median<span>${allocMedian} ${dataValue}</span></div>
                    </div>
                    <div class="gen0">
                        <div>Gen 0</div>
                        <div>Total<span>${allocGen0Total} ${totalTotalValue}</span></div>
                        <div>Largest<span>${allocGen0Highest} ${dataValue}</span></div>
                        <div>Smallest<span>${allocGen0Lowest} ${dataValue}</span></div>
                        <div>Average<span>${allocGen0Average} ${dataValue}</span></div>
                        <div>Median<span>${allocGen0Median} ${dataValue}</span></div>
                    </div>
                    <div class="gen1">
                        <div>Gen 1</div>
                        <div>Total<span>${allocGen1Total} ${totalTotalValue}</span></div>
                        <div>Largest<span>${allocGen1Highest} ${dataValue}</span></div>
                        <div>Smallest<span>${allocGen1Lowest} ${dataValue}</span></div>
                        <div>Average<span>${allocGen1Average} ${dataValue}</span></div>
                        <div>Median<span>${allocGen1Median} ${dataValue}</span></div>
                    </div>
                    <div class="gen2">
                        <div>Gen 2</div>
                        <div>Total<span>${allocGen2Total} ${totalTotalValue}</span></div>
                        <div>Largest<span>${allocGen2Highest} ${dataValue}</span></div>
                        <div>Smallest<span>${allocGen2Lowest} ${dataValue}</span></div>
                        <div>Average<span>${allocGen2Average} ${dataValue}</span></div>
                        <div>Median<span>${allocGen2Median} ${dataValue}</span></div>
                    </div>
                    <div class="loh">
                        <div>LOH</div>
                        <div>Total<span>${allocLOHTotal} ${totalTotalValue}</span></div>
                        <div>Largest<span>${allocLOHHighest} ${dataValue}</span></div>
                        <div>Smallest<span>${allocLOHLowest} ${dataValue}</span></div>
                        <div>Average<span>${allocLOHAverage} ${dataValue}</span></div>
                        <div>Median<span>${allocLOHMedian} ${dataValue}</span></div>
                    </div>
                </div>

                <div id="timeSummary">Time Spent by Generation</div>

                <div class="summaryGcDiv time">
                    <div class="total">
                        <div>Total</div>
                        <div>Count<span>${timeinsideEachGc.length}</span></div>
                        <div>Total<span>${totalTimeInGc} ms</span></div>
                        <div>Largest<span>${highestTimeInGc} ms</span></div>
                        <div>Smallest<span>${lowestTimeInGc} ms</span></div>
                        <div>Average<span>${averageTimeInGc} ms</span></div>
                        <div>Median<span>${medianTimeInGc} ms</span></div>
                    </div>
                    <div class="gen0">
                        <div>Gen 0</div>
                        <div>Count<span>${gen0TimesInEachGc.length}</span></div>
                        <div>Total<span>${gen0TotalTimeInGc} ms</span></div>
                        <div>Largest<span>${gen0HighestTimeInGc} ms</span></div>
                        <div>Smallest<span>${gen0LowestTimeInGc} ms</span></div>
                        <div>Average<span>${gen0AverageTimeInGc} ms</span></div>
                        <div>Median<span>${gen0MedianTimeInGc} ms</span></div>
                    </div>
                    <div class="gen1">
                        <div>Gen 1</div>
                        <div>Count<span>${gen1TimesInEachGc.length}</span></div>
                        <div>Total<span>${gen1TotalTimeInGc} ms</span></div>
                        <div>Largest<span>${gen1HighestTimeInGc} ms</span></div>
                        <div>Smallest<span>${gen1LowestTimeInGc} ms</span></div>
                        <div>Average<span>${gen1AverageTimeInGc} ms</span></div>
                        <div>Median<span>${gen1MedianTimeInGc} ms</span></div>
                    </div>
                    <div class="gen2">
                        <div>Gen 2</div>
                        <div>Count<span>${gen2TimesInEachGc.length}</span></div>
                        <div>Total<span>${gen2TotalTimeInGc} ms</span></div>
                        <div>Largest<span>${gen2HighestTimeInGc} ms</span></div>
                        <div>Smallest<span>${gen2LowestTimeInGc} ms</span></div>
                        <div>Average<span>${gen2AverageTimeInGc} ms</span></div>
                        <div>Median<span>${gen2MedianTimeInGc} ms</span></div>
                    </div>
                </div>

                <div class="spacer"></div>

                <div class="gcDataContainer">
                    ${canvasData}
                    <script src="${chartjs}"></script>
                </div>

                <h2 class="divider">GC Usage Over Time</h2>

                <div class="gcDataContainer" id="nextSpacer">
                    ${totalCanvasData}
                    <script src="${chartjs}"></script>
                </div>

                <h2 class="divider">Per Heap GC Usage Over Time</h2>

                <div class="gcDataContainer">
                    ${perHeapCanvasData}
                    <script src="${chartjs}"></script>
                </div>

                <script nonce="${nonce}" src="${scriptUri}"></script>
            </body>
        </html>`;

        return htmlToReturn;
    }

    private getHtmlForWebviewWrapper(document: DotnetInsightsGcDocument, webview: vscode.Webview): Thenable<string> {
        const fileContents = fs.readFileSync(document.uri.fsPath);

        var promiseToReturn = new Promise<string>((resolve, reject) => {
            this.parseFromXml(fileContents).then((gcData: any) => {
                resolve(this.getHtmlForWebview(document, webview, gcData));
            });
        })

        return promiseToReturn;
    }

    getNonce() {
        let text = '';
        const possible = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789';
        for (let i = 0; i < 32; i++) {
            text += possible.charAt(Math.floor(Math.random() * possible.length));
        }
        return text;
    }
}