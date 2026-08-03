import * as fs from "fs";
import * as os from "os";
import * as path from 'path';
import * as vscode from 'vscode';

import * as xml2js from 'xml2js';

import { DotnetInsights } from "./dotnetInsights";
import { GcListener, ProcessInfo, GcData, AllocData } from "./GcListener";

import { DotnetInsightsGcDocument } from "./DotnetInsightsGcEditor";
import { renderGcSnapshotWebview } from "./GcSnapshotRenderer";
import { promises } from "dns";
import { rejects } from "assert";
import { exec } from "child_process";

export class DotnetInsightsGcSnapshotEditor implements vscode.CustomReadonlyEditorProvider {
    public static register(context: vscode.ExtensionContext, insights: DotnetInsights): vscode.Disposable {
        const provider = new DotnetInsightsGcSnapshotEditor(context, insights, null);
        // Without this, switching to another editor tab and back tears down
        // and reloads the webview's whole DOM/JS state from scratch (VS
        // Code's default) - losing the Detailed tab's injected table, any
        // sort applied to it, and the GC charts' zoom range (see
        // snapshotGcStats.js's gcChartsZoomRange/heapContentsZoomRange).
        const providerRegistration = vscode.window.registerCustomEditorProvider(DotnetInsightsGcSnapshotEditor.viewType, provider, { webviewOptions: { retainContextWhenHidden: true } });
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
                const gen0MinSize = parseInt(currentGc["GlobalHeapHistory"][0]["$"]["FinalYoungestDesired"].replaceAll(',',''));
                const generation = currentGc["$"]["GCGeneration"];
                const generationSize0 = parseInt(currentGc["HeapStats"][0]["$"]["GenerationSize0"].replaceAll(',',''));
                const generationSize1 = parseInt(currentGc["HeapStats"][0]["$"]["GenerationSize1"].replaceAll(',',''));
                const generationSize2 = parseInt(currentGc["HeapStats"][0]["$"]["GenerationSize2"].replaceAll(',',''));
                const generationSizeLOH = parseInt(currentGc["HeapStats"][0]["$"]["GenerationSize3"].replaceAll(',',''));

                var generationSizePOH = 0;

                try {
                    generationSizePOH = parseInt(currentGc["HeapStats"][0]["$"]["GenerationSize4"].replaceAll(',',''));
                }
                catch (e) {
                    
                }

                const id = currentGc["$"]["GCNumber"];
                const kind = currentGc["$"]["Type"];

                const numHeaps = parseInt(currentGc["GlobalHeapHistory"][0]["$"]["NumHeaps"].replaceAll(',',''));
                const pauseDurationMSec = parseInt(currentGc["$"]["PauseDurationMSec"].replaceAll(',',''));
                const pauseStartRelativeMSec = parseInt(currentGc["$"]["PauseStartRelativeMSec"].replaceAll(',',''));
                const pauseEndRelativeMSec = pauseStartRelativeMSec + pauseDurationMSec;
                const reason = currentGc["$"]["Reason"];
                const gcDurationMSec = parseInt(currentGc["$"]["GCDurationMSec"].replaceAll(',',''));

                const totalHeapSize = generationSize0 + generationSize1 + generationSize2 + generationSizeLOH + generationSizePOH;

                const totalPromotedSize0 = parseInt(currentGc["HeapStats"][0]["$"]["TotalPromotedSize0"].replaceAll(',',''));
                const totalPromotedSize1 = parseInt(currentGc["HeapStats"][0]["$"]["TotalPromotedSize1"].replaceAll(',',''));
                const totalPromotedSize2 = parseInt(currentGc["HeapStats"][0]["$"]["TotalPromotedSize2"].replaceAll(',',''));
                const totalPromotedSizeLoh = parseInt(currentGc["HeapStats"][0]["$"]["TotalPromotedSize3"].replaceAll(',',''));

                var totalPromotedSizePoh = 0;
                try {
                    totalPromotedSizePoh = parseInt(currentGc["HeapStats"][0]["$"]["TotalPromotedSize4"].replaceAll(',',''));
                }
                catch (e) {

                }

                // Perfview's XML export has no absolute wall-clock anchor for the
                // capture (unlike .nettrace, which has SyncTimeUTC) - only
                // relative-to-capture-start numbers. Format PauseStartRelativeMSec
                // as an elapsed-time string so there's still something meaningful
                // to show alongside the GC number, clearly distinguished from a
                // real calendar date by the leading "+".
                const elapsedMs = pauseStartRelativeMSec;
                const elapsedDate = new Date(elapsedMs);
                const elapsedHours = Math.floor(elapsedMs / 3600000);
                const dateTime = `+${elapsedHours.toString().padStart(2, '0')}:${elapsedDate.getUTCMinutes().toString().padStart(2, '0')}:${elapsedDate.getUTCSeconds().toString().padStart(2, '0')}.${elapsedDate.getUTCMilliseconds().toString().padStart(3, '0')}`;

                var data = {
                    "Gen0MinSize": gen0MinSize,
                    "generation": parseInt(generation),
                    "GenerationSize0": generationSize0,
                    "GenerationSize1": generationSize1,
                    "GenerationSize2": generationSize2,
                    "GenerationSizeLOH": generationSizeLOH,
                    "GenerationSizePOH": generationSizePOH,
                    "Id": id,
                    "DateTime": dateTime,
                    "kind": kind,
                    "NumHeaps": numHeaps,
                    "PauseDurationMSec": pauseDurationMSec,
                    "PauseEndRelativeMSec": pauseEndRelativeMSec,
                    "PauseStartRelativeMSec": pauseStartRelativeMSec,
                    "Reason": reason,
                    "Heaps": [] as any[],
                    "TotalHeapSize": totalHeapSize,
                    "TotalPromoted": totalPromotedSize0,
                    "TotalPromotedLOH": totalPromotedSizeLoh,
                    "TotalPromotedPOH": totalPromotedSizePoh,
                    "TotalPromotedSize0": totalPromotedSize0,
                    "TotalPromotedSize1": totalPromotedSize1,
                    "TotalPromotedSize2": totalPromotedSize2,
                    "Type": reason,
                    "GCDurationMSec": gcDurationMSec
                }

                var heaps = [] as any[];
                console.assert(currentGc["PerHeapHistories"][0]["PerHeapHistory"].length == numHeaps);

                var tryParse = (genData: any, key: string, isNumber?: boolean | null): any => {
                    try {
                        if (isNumber != null && isNumber == false) {
                            return genData[key];
                        }
                        else {
                            return parseInt(genData[key].replaceAll(',',''));
                        }
                    }
                    catch (e) {
                        return 0.0;
                    }
                }

                for (var heapIndex = 0; heapIndex < currentGc["PerHeapHistories"][0]["PerHeapHistory"].length; ++heapIndex) {
                    var heapGenerations = [0, 1, 2, 3];
                    const currentHeap = currentGc["PerHeapHistories"][0]["PerHeapHistory"][heapIndex];

                    // Declared fresh per heap - previously this object was
                    // declared once outside the loop and mutated+pushed on
                    // every iteration, so every entry in data["Heaps"] ended
                    // up as the same reference holding only the last heap's
                    // data (multi-heap/server GC captures only, invisible on
                    // single-heap workstation GC captures).
                    var currentHeapData : any = {
                        "HeapIndex": heapIndex,
                        "Generations": {
                            0: null,
                            1: null,
                            2: null,
                            3: null
                        }
                    };

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
                            "FreeListSpaceAfter": freeListSpaceAfter,
                            "FreeListSpaceBefore" : freeListSpaceBefore,
                            "FreeObjSpaceAfter" : freeObjSpaceAfter,
                            "FreeObjSpaceBefore" : freeObjSpaceBefore,
                            "Id" : genid,
                            "In" : genin,
                            "NewAllocation" : newAllocation,
                            "NonePinnedSurv" : nonePinnedSurv,
                            "ObjSizeAfter" : objSizeAfter,
                            "ObjSpaceBefore": objSpaceBefore,
                            "Out" : out,
                            "PinnedSurv" : pinnedSurv,
                            "SizeAfter" : sizeAfter,
                            "SizeBefore" : sizeBefore,
                            "SurvRate" : survRate
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

    private getHtmlForWebviewWrapper(document: DotnetInsightsGcDocument, webview: vscode.Webview): Thenable<string> {
        const fileContents = fs.readFileSync(document.uri.fsPath);

        var promiseToReturn = new Promise<string>((resolve, reject) => {
            this.parseFromXml(fileContents).then((gcData: any) => {
                if (gcData == null) {
                    // Not valid XML - .gcinfo files can also just be the raw JSON shape directly.
                    try {
                        gcData = JSON.parse(fileContents.toString());
                    }
                    catch (e) {
                        gcData = null;
                    }
                }

                resolve(renderGcSnapshotWebview(document, webview, this.context.extensionUri, gcData, "gcinfo"));
            });
        })

        return promiseToReturn;
    }

}