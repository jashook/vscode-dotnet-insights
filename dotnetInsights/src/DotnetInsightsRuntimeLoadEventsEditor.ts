import * as fs from "fs";
import * as os from "os";
import * as path from 'path';
import * as vscode from 'vscode';

import { DotnetInsights } from "./dotnetInsights";
import { JitMethodInfo, ProcessInfo, GcListener } from "./GcListener";

import { DotnetInsightsGcDocument } from "./DotnetInsightsGcEditor";

export class DotnetInsightsRuntimeLoadEventsEditor implements vscode.CustomReadonlyEditorProvider {
    public static register(context: vscode.ExtensionContext, insights: DotnetInsights): vscode.Disposable {
        const provider = new DotnetInsightsRuntimeLoadEventsEditor(context, insights, null, insights.listener!);
        const providerRegistration = vscode.window.registerCustomEditorProvider(DotnetInsightsRuntimeLoadEventsEditor.viewType, provider);
        return providerRegistration;
    }

    public static readonly viewType = 'dotnetInsightsRuntimeEditor.edit';

    private timeInJit: number;
    private loadData: [any] | undefined;
    private listener: GcListener;
    private processName: string;

    constructor(
        private readonly context: vscode.ExtensionContext,
        private readonly insights: DotnetInsights,
        private jitData: any,
        private gcListener: GcListener
    ) {
        this.loadData = undefined;
        this.timeInJit = 0;
        this.listener = gcListener;
        this.processName = "";
    }

    openCustomDocument(uri: vscode.Uri, openContext: vscode.CustomDocumentOpenContext, token: vscode.CancellationToken): vscode.CustomDocument | Thenable<vscode.CustomDocument> {
        var filename = path.basename(uri.path);
        var endofLine = os.platform() === "win32" ? vscode.EndOfLine.CRLF : vscode.EndOfLine.LF;

        var processId = parseInt(filename.split("---")[0]);

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
                                                    this.listener);

        return document;
    }

    resolveCustomEditor(document: vscode.CustomDocument, webviewPanel: vscode.WebviewPanel, token: vscode.CancellationToken): void | Thenable<void> {
        webviewPanel.webview.options = {
            enableScripts: true,
            localResourceRoots: [
                vscode.Uri.joinPath(this.context.extensionUri, 'node_modules', 'chart.js', 'dist'),
                vscode.Uri.joinPath(this.context.extensionUri, 'media')
            ]
        };

        var gcDocument = document as DotnetInsightsGcDocument;
        const pid = gcDocument.processId;

        var gcEditor = this;

        function updateWebview() {
            // There are no updates possible for this view type.

            webviewPanel.webview.postMessage({
                type: 'update'
            });
        }

        webviewPanel.webview.html = this.getHtmlForWebview(gcDocument, webviewPanel.webview);

        updateWebview();
    }

    
    private getHtmlForWebview(document: DotnetInsightsGcDocument, webview: vscode.Webview) : string {
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

        // We should have the json representation of the gc stats
        const fileContents = fs.readFileSync(document.uri.fsPath);
        try
        {
            var loadData = JSON.parse(fileContents.toString());

            if (loadData === null || 
                loadData === undefined) {
                throw new Error("Json error.");
            }

            this.processName = loadData[0];
            this.loadData = loadData[1];

            if (this.loadData === null || 
                this.loadData === undefined) {
                throw new Error("Json error.");
            }

        }
        catch(e) {
            vscode.window.showWarningMessage(`${document.uri.fsPath} is corrupted or a incorrect type.`);
            return defaultHtmlReturn;
        }

        // All the load events are nicely parsed. All we need to do is
        // setup a timeline chart. We will have a line for each method load
        // by method id

        const nonce = this.getNonce();

        const mainUri = webview.asWebviewUri(vscode.Uri.joinPath(this.context.extensionUri, 'media', 'jit.css'));
        const styleResetUri = webview.asWebviewUri(vscode.Uri.joinPath(this.context.extensionUri, 'media', 'reset.css'));
        const styleVSCodeUri = webview.asWebviewUri(vscode.Uri.joinPath(this.context.extensionUri, 'media', 'vscode.css'));

        const scriptUri = webview.asWebviewUri(vscode.Uri.joinPath(this.context.extensionUri, 'media', 'jit.js'));

        const chartjs = webview.asWebviewUri(vscode.Uri.joinPath(this.context.extensionUri, 'node_modules', 'chart.js', 'dist', 'Chart.min.js'));

        var canvasData = "";
        if (this.loadData.length > 0) {
            canvasData = `<div class="jitStats"><canvas id="totalJitStatsOverTime"></canvas></div>`;
        }

        var loadTimes = [];
        var r2rLoadTimes = [];
        var tierZeroLoadTimes = [];
        var tierOneLoadTimes = [];

        var numberOfMethods = 0;
        var totalLoadTime = 0;
        var highestLoadTime = 0;
        var lowestLoadTime = 0;
        var averageLoadTime = 0;
        var medianLoadTime = 0;

        var r2rNumberOfMethods = 0;
        var r2rTotalLoadTime = 0;
        var r2rHighestLoadTime = 0;
        var r2rLowestLoadTime = 0;
        var r2rAverageLoadTime = 0;
        var r2rMedianLoadTime = 0;

        var tierZeroNumberOfMethods = 0;
        var tierZeroTotalLoadTime = 0;
        var tierZeroHighestLoadTime = 0;
        var tierZeroLowestLoadTime = 0;
        var tierZeroAverageLoadTime = 0;
        var tierZeroMedianLoadTime = 0;

        var tierOneNumberOfMethods = 0;
        var tierOneTotalLoadTime = 0;
        var tierOneHighestLoadTime = 0;
        var tierOneLowestLoadTime = 0;
        var tierOneAverageLoadTime = 0;
        var tierOneMedianLoadTime = 0;

        var percentMethodsInR2R = 0;
        var percentMethodsInTier0 = 0;
        var percentMethodsInTier1 = 0;
        var r2rNumberOfTrappedMethods = 0;
        var tierZeroNumberOfTrappedMethods = 0;
        var tierOneNumberOfTrappedMethods = 0;

        // Unknown = 0,
        // MinOptJitted = 1,
        // Optimized = 2,
        // QuickJitted = 3,
        // OptimizedTier1 = 4,
        // ReadyToRun = 5,
        // PreJIT = 255

        for (var index = 0; index < this.loadData.length; ++index) {
            const currentLoadMethodData = this.loadData[index];

            for (var methodIndex = 0; methodIndex < currentLoadMethodData[1].length; ++methodIndex) {
                const methodId = currentLoadMethodData[0];
                const currentLoadData = currentLoadMethodData[1][methodIndex];

                ++numberOfMethods;
                totalLoadTime += currentLoadData.loadDuration;

                if (index === 0) {
                    lowestLoadTime = currentLoadData.loadDuration;
                }
                else if (currentLoadData.loadDuration < lowestLoadTime) {
                    lowestLoadTime = currentLoadData.loadDuration;
                }

                if (currentLoadData.loadDuration > highestLoadTime) {
                    highestLoadTime = currentLoadData.loadDuration;
                }
    
                loadTimes.push(currentLoadData);
    
                if (currentLoadData.tier === 1 || currentLoadData.tier === 3) {
                    tierZeroLoadTimes.push(currentLoadData);
                } 
                else if (currentLoadData.tier === 2 || currentLoadData.tier === 4) {
                    tierOneLoadTimes.push(currentLoadData);
                }
                else if (currentLoadData.tier === 5) {
                    r2rLoadTimes.push(currentLoadData);
                }
                else {
                    console.log("Unknown op tier");
                }
            }
        }

        loadTimes.sort((a, b) => {
            return a.loadDuration - b.loadDuration;
        });

        var half = Math.ceil(loadTimes.length / 2) === loadTimes.length ? loadTimes.length - 1 : Math.ceil(loadTimes.length / 2);
        medianLoadTime = loadTimes[half].loadDuration;
        averageLoadTime = numberOfMethods > 0 ? totalLoadTime / numberOfMethods : 0;

        // R2R

        r2rNumberOfMethods = r2rLoadTimes.length;

        for (var index = 0; index < r2rLoadTimes.length; ++index) {
            const r2rData = r2rLoadTimes[index];
            r2rTotalLoadTime += r2rData.loadDuration;

            if (index === 0) {
                r2rLowestLoadTime = r2rData.loadDuration;
            }
            else if (r2rData.loadDuration < r2rLowestLoadTime) {
                r2rLowestLoadTime = r2rData.loadDuration;
            }

            if (r2rData.loadDuration > r2rHighestLoadTime) {
                r2rHighestLoadTime = r2rData.loadDuration;
            }
        }

        r2rLoadTimes.sort((a, b) => { return a.loadDuration - b.loadDuration; } );

        half = Math.ceil(r2rLoadTimes.length / 2) === r2rLoadTimes.length ? r2rLoadTimes.length - 1 : Math.ceil(r2rLoadTimes.length / 2);
        r2rMedianLoadTime = r2rNumberOfMethods > 0 ? r2rLoadTimes[half].loadDuration : 0;
        r2rAverageLoadTime = r2rNumberOfMethods > 0 ? r2rTotalLoadTime / r2rNumberOfMethods : 0;

        // Tier 0

        tierZeroNumberOfMethods = tierZeroLoadTimes.length;

        for (var index = 0; index < tierZeroLoadTimes.length; ++index) {
            const tierZeroData = tierZeroLoadTimes[index];
            tierZeroTotalLoadTime += tierZeroData.loadDuration;
            this.timeInJit += tierZeroData.loadDuration;

            if (index === 0) {
                tierZeroLowestLoadTime = tierZeroData.loadDuration;
            }
            else if (tierZeroData.loadDuration < tierZeroLowestLoadTime) {
                tierZeroLowestLoadTime = tierZeroData.loadDuration;
            }

            if (tierZeroData.loadDuration > tierZeroHighestLoadTime) {
                tierZeroHighestLoadTime = tierZeroData.loadDuration;
            }
        }

        half = Math.ceil(tierZeroLoadTimes.length / 2) === tierZeroLoadTimes.length ? tierZeroLoadTimes.length - 1 : Math.ceil(tierZeroLoadTimes.length / 2);
        tierZeroMedianLoadTime = tierZeroNumberOfMethods > 0 ? tierZeroLoadTimes[half].loadDuration : 0;
        tierZeroAverageLoadTime = tierZeroTotalLoadTime > 0 ? tierZeroTotalLoadTime / tierZeroNumberOfMethods : 0;

        // Tier 1

        tierOneNumberOfMethods = tierOneLoadTimes.length;

        for (var index = 0; index < tierOneLoadTimes.length; ++index) {
            const tierOneData = tierOneLoadTimes[index];
            tierOneTotalLoadTime += tierOneData.loadDuration;
            this.timeInJit += tierOneData.loadDuration;

            if (index === 0) {
                tierOneLowestLoadTime = tierOneData.loadDuration;
            }
            else if (tierOneData.loadDuration < tierOneLowestLoadTime) {
                tierOneLowestLoadTime = tierOneData.loadDuration;
            }

            if (tierOneData.loadDuration > tierOneHighestLoadTime) {
                tierOneHighestLoadTime = tierOneData.loadDuration;
            }
        }

        half = Math.ceil(tierOneLoadTimes.length / 2) === tierOneLoadTimes.length ? tierOneLoadTimes.length - 1 : Math.ceil(tierOneLoadTimes.length / 2);
        tierOneMedianLoadTime = tierOneLoadTimes.length > 0 ? tierOneLoadTimes[half].loadDuration : 0;
        tierOneAverageLoadTime = tierOneNumberOfMethods > 0 ? tierOneTotalLoadTime / tierOneNumberOfMethods : tierOneNumberOfMethods;

        // Total stats

        var numberOfActiveR2RMethods = r2rNumberOfMethods;
        var numberOfActiveTier0Methods = tierZeroNumberOfMethods;
        var numberOfActiveTier1Methods = tierOneNumberOfMethods;

        var r2rMap = new Map<number, JitMethodInfo[]>();
        var tier0Map = new Map<number, JitMethodInfo[]>();
        var tier1Map = new Map<number, JitMethodInfo[]>();

        for (var index = 0; index < loadTimes.length; ++index) {
            const dataPoint = loadTimes[index];
            var dataLookedUp = undefined;

            if (dataPoint.tier === 1 || dataPoint.tier === 3) {
                dataLookedUp = tier0Map.get(dataPoint.methodId);

                if (dataLookedUp !== undefined) {
                    dataLookedUp.push(dataPoint);
                }
                else {
                    tier0Map.set(dataPoint.methodId, [dataPoint]);
                }
            } 
            else if (dataPoint.tier === 2 || dataPoint.tier === 4) {
                dataLookedUp = tier1Map.get(dataPoint.methodId);

                if (dataLookedUp !== undefined) {
                    dataLookedUp.push(dataPoint);
                }
                else {
                    tier1Map.set(dataPoint.methodId, [dataPoint]);
                }
            }
            else if (dataPoint.tier === 5) {
                dataLookedUp = r2rMap.get(dataPoint.methodId);

                if (dataLookedUp !== undefined) {
                    dataLookedUp.push(dataPoint);
                }
                else {
                    r2rMap.set(dataPoint.methodId, [dataPoint]);
                }
            }
            else {
                continue;
            }
        }

        var r2rKeys = Array.from(r2rMap.keys());
        var tier0Keys = Array.from(tier0Map.keys());
        var tier1Keys = Array.from(tier1Map.keys());

        for (var index = 0; index < r2rKeys.length; ++index) {
            const currentKey = r2rKeys[index];

            if (tier0Map.has(currentKey) || tier1Map.has(currentKey)) {
                // This has been tiered up
                numberOfActiveR2RMethods -= 1;
            }
        }

        for (var index = 0; index < tier0Keys.length; ++index) {
            const currentKey = tier0Keys[index];

            if (tier1Map.has(currentKey)) {
                numberOfActiveTier0Methods -= 1;
            }
        }

        percentMethodsInR2R = (numberOfActiveR2RMethods / numberOfMethods) * 100;
        percentMethodsInTier0 = (numberOfActiveTier0Methods / numberOfMethods) * 100;
        percentMethodsInTier1 = (numberOfActiveTier1Methods / numberOfMethods) * 100;

        r2rNumberOfTrappedMethods = numberOfActiveR2RMethods;
        tierZeroNumberOfTrappedMethods = numberOfActiveTier0Methods;
        tierOneNumberOfTrappedMethods = numberOfActiveTier1Methods;

        const dataValue = "ms";

        var labelsByMs = false;
        var labelsBy500Ms = false;
        var labelsBySecond = false;
        var labelsByMinute = false;
        var labelsByHour = false;

        loadTimes.sort((a: JitMethodInfo, b: JitMethodInfo) => {
            return Date.parse(a.eventTick.toString())- Date.parse(b.eventTick.toString());
        });

        var beginTime = Date.parse(loadTimes[0].eventTick);
        var endTime = Date.parse(loadTimes[loadTimes.length > 0 ? loadTimes.length - 1 : 0].eventTick);

        var intervalTimeIn100Ms = endTime - beginTime;

        const intervalTimeIn500Ms = intervalTimeIn100Ms / 5;
        const intervalTimeInSeconds = intervalTimeIn100Ms / 1000;

        const intervalTimeInMinutes = intervalTimeInSeconds / 60;
        const intervalTimeInHours = intervalTimeInMinutes / 60;

        // 100 ms intervals
        var intervalCount = intervalTimeIn100Ms / 100;

        // If there are too many buckets use 500 ms
        if (intervalCount > 100) {
            intervalCount = intervalTimeIn500Ms / 100;
            if (intervalCount <= 100) {
                labelsBy500Ms = true;
            }
            else {
                // Every 10 seconds. Seconds is a large bucket.
                intervalCount = intervalTimeInSeconds / 10;

                if (intervalCount <= 200) {
                    labelsBySecond = true;
                }
            }
        }
        else {
            labelsByMs = true;
        }

        if (labelsByMs !== true && labelsBySecond !== true && labelsBy500Ms !== true) {
            intervalCount = intervalTimeInMinutes;

            if (intervalCount <= 1440) {
                labelsByMinute = true;
            }
            else {
                labelsByHour = true;
            }
        }

        var labelsToPass = null;
        var intervalInMs = 100;

        if (labelsByMs === true) {
            console.assert(labelsBy500Ms === false);
            console.assert(labelsBySecond === false);
            console.assert(labelsByMinute === false);
            console.assert(labelsByHour === false);

            labelsToPass = labelsByMs;
        }
        else if (labelsBy500Ms === true) {
            console.assert(labelsByMs === false);
            console.assert(labelsByMinute === false);
            console.assert(labelsByHour === false);
            console.assert(labelsByHour === false);

            intervalInMs *= 5;
        }
        else if (labelsBySecond === true) {
            console.assert(labelsByMs === false);
            console.assert(labelsBy500Ms === false);
            console.assert(labelsByMinute === false);
            console.assert(labelsByHour === false);

            intervalInMs *= 100;
        }
        else if (labelsByMinute === true) {
            console.assert(labelsByMs === false);
            console.assert(labelsBy500Ms === false);
            console.assert(labelsBySecond === false);
            console.assert(labelsByHour === false);

            intervalInMs *= (10 * 60);
        }
        else {
            console.assert(labelsByMs === false);
            console.assert(labelsBy500Ms === false);
            console.assert(labelsBySecond === false);
            console.assert(labelsByMinute === false);

            intervalInMs *= (10 * 60 * 60);
        }

        var intervalToWorkOn = (endTime - beginTime);
        labelsToPass = [];

        intervalCount = Math.ceil(intervalCount);

        for (var index = 0; index < intervalCount; ++index) {
            const labelToUse = new Date(intervalToWorkOn).toUTCString();

            labelsToPass.push(labelToUse);
            intervalToWorkOn += intervalInMs;
        }

        console.assert(labelsToPass.length === intervalCount);

        var r2rTimeByBucket = [];
        var tier0TimeByBucket = [];
        var tier1TimeByBucket = [];
        var tier0TierUpTimeByBucket = [];
        var tier1TierUpTimeByBucket = [];

        for (var index = 0 ; index < labelsToPass.length; ++index) {
            r2rTimeByBucket.push(0);
            tier0TimeByBucket.push(0);
            tier1TimeByBucket.push(0);
            tier0TierUpTimeByBucket.push(0);
            tier1TierUpTimeByBucket.push(0);
        }

        let getBucketIndex = (currentLoadData: JitMethodInfo) => {
            var timeEncountered = Date.parse(currentLoadData.eventTick.toString());
            
            var bucketIndex = Math.floor((timeEncountered - beginTime) / intervalInMs);
            if (bucketIndex === intervalCount) {
                --bucketIndex;
            }

            console.assert(bucketIndex >= 0 && bucketIndex < intervalCount);

            return bucketIndex;
        };

        // Unknown = 0,
        // MinOptJitted = 1,
        // Optimized = 2,
        // QuickJitted = 3,
        // OptimizedTier1 = 4,
        // ReadyToRun = 5,
        // PreJIT = 255

        for (var index = 0; index < loadTimes.length; ++index) {
            const currentLoadData : JitMethodInfo = loadTimes[index];
            const bucketIndex = getBucketIndex(currentLoadData);

            if (currentLoadData.tier === 5) {
                r2rTimeByBucket[bucketIndex] += currentLoadData.loadDuration;
            }
            else if (currentLoadData.tier === 1 || currentLoadData.tier === 3) {
                tier0TimeByBucket[bucketIndex] += currentLoadData.loadDuration;
            }
            else if (currentLoadData.tier === 2 || currentLoadData.tier === 4) {
                tier1TimeByBucket[bucketIndex] += currentLoadData.loadDuration;
            }
            else {
                continue;
            }
        }

        // Go through all Tier0 methods and add to buckets
        for (var index = 0; index < tier0Keys.length; ++index) {
            const currentKey = tier0Keys[index];

            if (r2rMap.has(currentKey)) {
                const itemsForKey = tier0Map.get(currentKey)!;
                for (var innerIndex = 0; innerIndex < itemsForKey.length; ++innerIndex) {
                    const itemForKeyFound = itemsForKey[innerIndex];

                    const bucketIndex = getBucketIndex(itemForKeyFound);
                    
                    // Tier 0 tier up time is any tier 0 method that has also a
                    // R2R load for the same method id
                    tier0TierUpTimeByBucket[bucketIndex] += itemForKeyFound.loadDuration;
                }
            }
        }

        // Go through Tier 1 methods and add to buckets
        for (var index = 0; index < tier1Keys.length; ++index) {
            const currentKey = tier1Keys[index];

            if (tier0Map.has(currentKey) || r2rMap.has(currentKey)) {
                const itemsForKey = tier1Map.get(currentKey)!;
                for (var innerIndex = 0; innerIndex < itemsForKey.length; ++innerIndex) {
                    const itemForKeyFound = itemsForKey[innerIndex];

                    const bucketIndex = getBucketIndex(itemForKeyFound);
                    tier1TierUpTimeByBucket[bucketIndex] += itemForKeyFound.loadDuration;
                }
            }
        }

        var hiddenData: any = [
            labelsToPass,
            r2rTimeByBucket,
            tier0TimeByBucket,
            tier1TimeByBucket,
            tier0TierUpTimeByBucket,
            tier1TierUpTimeByBucket
        ];

        console.log(hiddenData);

        var data = "";
        var outerData = "";

        var addedSpacer = false;

        hiddenData = JSON.stringify(hiddenData);
        if (r2rLoadTimes.length > 0) {
            r2rLoadTimes.sort((a, b) => {
                return b.loadDuration - a.loadDuration;
            });

            data += `<div class="spacer"></div>`;
            data += `<div class="spacer"></div>`;
            data += `<div class="spacer"></div>`;
            data += `<div class="spacer"></div>`;

            data += `<h3 class="table-title">R2R Methods</h3>`;

            addedSpacer = true;

            data = `<table id="r2r_table">`;
            data += `<tr class="tableHeader"><th>Method Name</th><<th>Load Time (ms)</th></tr>`;

            for (var index = 0; index < r2rLoadTimes.length && index < 100; ++index) {
                var dataAtIndex : JitMethodInfo = r2rLoadTimes[index];

                var evenOdd = index % 2 == 0 ? "even" : "odd";
                
                data += `<tr class=${evenOdd}><td class="left-align">${dataAtIndex.methodName}</td><td>${dataAtIndex.loadDuration}</td>`;
            }

            data += `</table>`;
            data += `<div class="spacer"></div>`;
            data += `<div class="spacer"></div>`;
        }

        if (tierZeroLoadTimes.length > 0) {
            tierZeroLoadTimes.sort((a, b) => {
                return b.loadDuration - a.loadDuration;
            });

            if (addedSpacer === false) {
                data += `<div class="spacer"></div>`;
                data += `<div class="spacer"></div>`;
                data += `<div class="spacer"></div>`;
                data += `<div class="spacer"></div>`;

                addedSpacer = true;
            }
            data += `<h3 class="table-title">Tier 0 Methods</h3>`;

            data += `<table id="t0_table">`;
            data += `<tr class="tableHeader"><th>Method Name</th><th>Load Time (ms)</th></tr>`;

            for (var index = 0; index < tierZeroLoadTimes.length && index < 100; ++index) {
                var dataAtIndex : JitMethodInfo = tierZeroLoadTimes[index];
                var evenOdd = index % 2 == 0 ? "even" : "odd";
                
                data += `<tr class=${evenOdd}><td class="left-align">${dataAtIndex.methodName}</td><td>${dataAtIndex.loadDuration}</td>`;
            }

            data += `</table>`;
            data += `<div class="spacer"></div>`;
            data += `<div class="spacer"></div>`;
        }

        if (tierOneLoadTimes.length > 0) {
            tierOneLoadTimes.sort((a, b) => {
                return b.loadDuration - a.loadDuration;
            });

            if (addedSpacer === false) {
                data += `<div class="spacer"></div>`;
                data += `<div class="spacer"></div>`;
                data += `<div class="spacer"></div>`;
                data += `<div class="spacer"></div>`;

                addedSpacer = true;
            }
            data += `<h3 class="table-title">Tier 1 Methods</h3>`;

            data += `<table id="t1_table">`;
            data += `<tr class="tableHeader"><th>Method Name</th><th>Load Time (ms)</th></tr>`;

            for (var index = 0; index < tierOneLoadTimes.length && index < 100; ++index) {
                var dataAtIndex : JitMethodInfo = tierOneLoadTimes[index];
                var evenOdd = index % 2 == 0 ? "even" : "odd";
                
                data += `<tr class=${evenOdd}><td class="left-align">${dataAtIndex.methodName}</td><td>${dataAtIndex.loadDuration}</td>`;
            }

            data += `</table>`;
        }

        if (data !== "") {
            outerData = `<div id="jitData">${data}</div>`;
        }

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

                <h2 class="divider">${this.processName}</h2>
                <div id="timeSummary">Jit Events (Time to JIT) and Load Events for R2R</div>

                <div class="summaryGcDiv">
                    <div class="total">
                        <div>Total Time (JIT + R2R)</div>
                        <div>Method Count<span>${numberOfMethods}</span></div>
                        <div>Total<span>${totalLoadTime.toFixed(2)} ${dataValue}</span></div>
                        <div>Largest<span>${highestLoadTime.toFixed(2)} ${dataValue}</span></div>
                        <div>Smallest<span>${lowestLoadTime.toFixed(2)} ${dataValue}</span></div>
                        <div>Average<span>${averageLoadTime.toFixed(2)} ${dataValue}</span></div>
                        <div>Median<span>${medianLoadTime.toFixed(2)} ${dataValue}</span></div>
                    </div>
                    <div class="gen0">
                        <div>R2R</div>
                        <div>Method Count<span>${r2rNumberOfMethods}</span></div>
                        <div>Total<span>${r2rTotalLoadTime.toFixed(2)} ${dataValue}</span></div>
                        <div>Largest<span>${r2rHighestLoadTime.toFixed(2)} ${dataValue}</span></div>
                        <div>Smallest<span>${r2rLowestLoadTime.toFixed(2)} ${dataValue}</span></div>
                        <div>Average<span>${r2rAverageLoadTime.toFixed(2)} ${dataValue}</span></div>
                        <div>Median<span>${r2rMedianLoadTime.toFixed(2)} ${dataValue}</span></div>
                    </div>
                    <div class="gen1">
                        <div>Tier 0</div>
                        <div>Method Count<span>${tierZeroNumberOfMethods}</span></div>
                        <div>Total<span>${tierZeroTotalLoadTime.toFixed(2)} ${dataValue}</span></div>
                        <div>Largest<span>${tierZeroHighestLoadTime.toFixed(2)} ${dataValue}</span></div>
                        <div>Smallest<span>${tierZeroLowestLoadTime.toFixed(2)} ${dataValue}</span></div>
                        <div>Average<span>${tierZeroAverageLoadTime.toFixed(2)} ${dataValue}</span></div>
                        <div>Median<span>${tierZeroMedianLoadTime.toFixed(2)} ${dataValue}</span></div>
                    </div>
                    <div class="gen2">
                        <div>Tier 1</div>
                        <div>Method Count<span>${tierOneNumberOfMethods}</span></div>
                        <div>Total<span>${tierOneTotalLoadTime.toFixed(2)} ${dataValue}</span></div>
                        <div>Largest<span>${tierOneHighestLoadTime.toFixed(2)} ${dataValue}</span></div>
                        <div>Smallest<span>${tierOneLowestLoadTime.toFixed(2)} ${dataValue}</span></div>
                        <div>Average<span>${tierOneAverageLoadTime.toFixed(2)} ${dataValue}</span></div>
                        <div>Median<span>${tierOneMedianLoadTime.toFixed(2)} ${dataValue}</span></div>
                    </div>
                    <div class="loh">
                        <div>Method Information</div>
                        <div>R2R Methods<span>${r2rNumberOfTrappedMethods}</span></div>
                        <div>Tier 0 Methods<span>${tierZeroNumberOfTrappedMethods}</span></div>
                        <div>Tier 1 Methods<span>${tierOneNumberOfTrappedMethods}</span></div>
                        <div>R2R %<span>${percentMethodsInR2R.toFixed(2)}%</span></div>
                        <div>Tier 0 %<span>${percentMethodsInTier0.toFixed(2)}%</span></div>
                        <div>Tier 1 %<span>${percentMethodsInTier1.toFixed(2)}%</span></div>
                    </div>
                </div>

                <div class="spacer"></div>
                <div class="spacer"></div>

                <div class="gcDataContainer">
                    ${canvasData}
                    <script src="${chartjs}"></script>
                </div>

                ${outerData}
                <script nonce="${nonce}" src="${scriptUri}"></script>
            </body>
        </html>`;

        return htmlToReturn;
    }

    getNonce() {
        let text = '';
        const possible = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789';
        for (let i = 0; i < 32; i++) {
            text += possible.charAt(Math.ceil(Math.random() * possible.length));
        }
        return text;
    }
}