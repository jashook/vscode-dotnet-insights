import { DotnetInsightsGcTreeDataProvider } from "./dotnetInsightsGc";

import { createServer } from "http";
import { IncomingMessage, ServerResponse } from 'http';
import { DotnetInsightsJitTreeDataProvider } from "./dotnetInsightsJit";
import { Profiler } from "./Profiler";

export class GcData {
    public data: any;
    public timestamp: Date;
    public percentInGc: string | undefined;
    public privateBytes: number;
    public pagedMemory: number;
    public nonPagedSystemMemory: number;
    public pagedSystemMemory: number;
    public virtualMemory: number;
    public workingSet: number;
    public allocData: AllocData[];
    public filteredAllocData: any;

    constructor(data: any) {
        if (data["workingSet"] != undefined) {
            this.data = data.data;
            this.timestamp = data.timestamp;
            this.percentInGc = data.percentInGc;
            this.privateBytes = data.privateBytes;
            this.pagedMemory = data.pagedMemory;
            this.nonPagedSystemMemory = data.nonPagedSystemMemory;
            this.pagedSystemMemory = data.pagedSystemMemory;
            this.virtualMemory = data.virtualMemory;
            this.workingSet = data.workingSet;
            this.filteredAllocData = data.filteredAllocData;
            this.allocData = [];

            if (this.timestamp == undefined) {
                this.timestamp = new Date();
            }

            return;
        }

        this.data = data["data"];
        this.timestamp = new Date();
        this.percentInGc = undefined;

        this.privateBytes = parseInt(data["privateBytes"]);
        this.pagedMemory = parseInt(data["pagedMemory"]);
        this.nonPagedSystemMemory = parseInt(data["nonPagedSystemMemory"]);
        this.pagedSystemMemory = parseInt(data["pagedSystemMemory"]);
        this.virtualMemory = parseInt(data["virtualMemory"]);
        this.workingSet = parseInt(data["workingSet"]);

        this.allocData = [];
    }
}

export class AllocData {
    public data: any;
    public timestamp: Date;

    constructor(data: any) {
        this.data = data;
        this.timestamp = new Date();
    }
}

export class JitMethodInfo {
    public methodName: string;
    public loadDuration: number;
    public eventTick: Date;
    public tier: number;
    public methodId: number;
    
    constructor(methodName: string, loadDuration: number, tier: number, methodId: number) {
        this.methodName = methodName;
        this.loadDuration = loadDuration;
        this.tier = tier;
        this.eventTick = new Date();
        this.methodId = methodId;
    }
}

export class ProcessInfo { 
    public data: GcData[];
    public jitData: Map<number, JitMethodInfo[]>;
    public loadDurationTotal: Map<number, number>;
    public processId: number;
    public processName: string;
    public processCommandLine: string;
    public processStartTime: Date;

    constructor(data: any, isAllocData: boolean, isJitInfo: boolean) {
        const parsedJson = data;
        
        this.processId = parsedJson["ProcessID"];
        this.processName = parsedJson["ProcessName"];
        this.processCommandLine = parsedJson["processCommandLine"];
        this.data = [] as GcData[];
        this.jitData = new Map<number, JitMethodInfo[]>();
        this.loadDurationTotal = new Map<number, number>();
        this.processStartTime = new Date(parsedJson["processStartTime"]);

        this.addData(parsedJson, isAllocData, isJitInfo);
    }

    addData(parsedJson: any, isAllocData: boolean, isJitInfo: boolean) {
        if (isAllocData) {
            for (var index = 0; index < parsedJson.length; ++index) {
                this.data[this.data.length - 1].allocData.push(new AllocData(parsedJson[index]));
            }
        }
        else if (isJitInfo) {
            var data = parsedJson["data"];

            var methodId = parseInt(data["methodId"]);
            var methodInfos = this.jitData.get(methodId);

            var isNewItem = false;
            if (methodInfos == null) {
                methodInfos = [] as JitMethodInfo[];
                this.loadDurationTotal.set(methodId, 0);
                isNewItem = true;
            }
            else {
                console.log("found");
            }

            var tier = parseInt(data["tier"]);
            var loadDurationMs = parseFloat(data["loadTimeMs"]);
            var methodName = data["methodName"];

            var methodNameSplit = methodName.split(":");
            const methodNameValue = methodNameSplit[methodNameSplit.length - 1];
            const methodSignature = methodNameSplit.slice(0, methodNameSplit.length - 1).join(":");

            const methodSignatureSplit = methodSignature.split(" ");
            var emptyIndex = 0;
            for (var index = 0; index < methodSignatureSplit.length; ++index) {
                if (methodSignatureSplit[index] == "") {
                    emptyIndex = index + 1;
                    break;
                }
            }

            if (emptyIndex < methodSignatureSplit.length) {
                methodName = methodSignatureSplit.slice(0, emptyIndex).join(" ") + methodNameValue + methodSignatureSplit.slice(emptyIndex).join(" ");
            }
            else {
                methodName = methodSignatureSplit.slice(0, emptyIndex).join(" ") + methodNameValue;
            }
            
            var jitMethodInfo = new JitMethodInfo(methodName, loadDurationMs, tier, methodId);
            methodInfos.push(jitMethodInfo);

            var loadDurationTotal = this.loadDurationTotal.get(methodId);
            if (loadDurationTotal == undefined) {
                console.assert(loadDurationTotal != undefined);
            }
            else {
                this.loadDurationTotal.set(methodId, loadDurationTotal + loadDurationMs);
            }

            if (isNewItem) {
                this.jitData.set(methodId, methodInfos);
            }
        }
        else {
            this.data.push(new GcData(parsedJson));
        }
    }
}

export class GcListener {

    public treeView: DotnetInsightsGcTreeDataProvider | undefined;
    public jitTreeView: DotnetInsightsJitTreeDataProvider | undefined;
    public processes: Map<number, ProcessInfo>;
    public httpServer: any;

    public sendShutdown: boolean;

    public profiler: Profiler | undefined;

    public requests: number;
    public secondTimer: any;
    
    constructor(
    ) {
        this.jitTreeView = undefined;
        this.treeView = undefined;
        this.processes = new Map<number, ProcessInfo>();
        this.sendShutdown = false;
        this.httpServer = undefined;

        this.requests = 0;
        setInterval(() => {
            console.log(`Requests per second: ${this.requests}`);
            this.requests = 0;
        }, 1000);

        this.profiler = Profiler.getInstance(10);
    }

    start() {
        this.httpServer = createServer((request: IncomingMessage, response: ServerResponse) => {
            if (request.method == "GET") {
                if (this.sendShutdown) {
                    response.statusCode = 400;
                }

                response.end("eol");
            }
            else if (request.method == "POST") {
                var data = "";
                request.on("data", (chunk: any) => {
                    data += chunk;
                });

                request.on("end", () => {
                    var jsonData:any = undefined;
                    try {
                        jsonData = JSON.parse(data);
                    }
                    catch(e) {
                        response.end("eol");
                        return;
                    }

                    if (jsonData == undefined) {
                        response.end("eol");
                        return;
                    }

                    console.log(request.url);
                    const isAllocData = request.url == "/gcAllocation";
                    const isJitEvent = request.url == "/jitEvent";
                    const isProfilerEvent = request.url == "/profiler";

                    if (isProfilerEvent) {
                        const profilerData = jsonData;
                        console.assert(this.profiler != undefined);

                        this.profiler?.addData(profilerData);

                        response.end("eol");
                        return;
                    }

                    var processById: ProcessInfo | undefined = this.processes.get(jsonData["ProcessID"]);

                    if (isAllocData) {
                        processById = this.processes.get(jsonData[0]["ProcessID"]);
                        console.assert(processById != undefined);
                    }

                    if (processById != undefined) {
                        processById.addData(jsonData, isAllocData, isJitEvent);
                    }
                    else {
                        processById = new ProcessInfo(jsonData, isAllocData, isJitEvent);
                        this.processes.set(jsonData["ProcessID"], processById);
                    }

                    this.requests += 1;

                    if (isAllocData) {
                        this.treeView?.refresh();
                        console.log(`Add: ${processById["processId"]}, ${processById["processName"]}`);
                    }

                    if (isJitEvent) {
                        this.jitTreeView?.refresh();
                    }

                    if (this.sendShutdown) {
                        response.statusCode = 400;
                    }

                    response.end("eol");
                });

            }
        });

        const port = 2143;
        this.httpServer.listen(port);
    }
}