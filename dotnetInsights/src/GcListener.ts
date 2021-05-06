import { DotnetInsightsGcTreeDataProvider } from "./dotnetInsightsGc";

import { createServer } from "http";
import { IncomingMessage, ServerResponse } from 'http';

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

export class ProcessInfo { 
    public data: GcData[];
    public processId: number;
    public processName: string;
    public processCommandLine: string;
    public processStartTime: Date;

    constructor(data: any, isAllocData: boolean) {
        const parsedJson = data;
        
        this.processId = parsedJson["ProcessID"];
        this.processName = parsedJson["ProcessName"];
        this.processCommandLine = parsedJson["processCommandLine"];
        this.data = [] as GcData[];
        this.processStartTime = new Date(parsedJson["processStartTime"]);

        this.addData(parsedJson, isAllocData);
    }

    addData(parsedJson: any, isAllocData: boolean) {
        if (isAllocData) {
            for (var index = 0; index < parsedJson.length; ++index) {
                this.data[this.data.length - 1].allocData.push(new AllocData(parsedJson[index]));
            }
        }
        else {
            this.data.push(new GcData(parsedJson));
        }
    }
}

export class GcListener {

    public treeView: DotnetInsightsGcTreeDataProvider | undefined;
    public processes: Map<number, ProcessInfo>;
    public httpServer: any;

    public sendShutdown: boolean;

    public requests: number;
    public secondTimer: any;
    
    constructor(
    ) {
        this.treeView = undefined;
        this.processes = new Map<number, ProcessInfo>();
        this.sendShutdown = false;
        this.httpServer = undefined;

        this.requests = 0;
        setInterval(() => {
            console.log(`Requests per second: ${this.requests}`);
            this.requests = 0;
        }, 1000);
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
                    }

                    console.log(request.url);
                    const isAllocData = request.url == "/gcAllocation";

                    var processById: ProcessInfo | undefined = this.processes.get(jsonData["ProcessID"]);

                    if (isAllocData) {
                        processById = this.processes.get(jsonData[0]["ProcessID"]);
                        console.assert(processById != undefined);
                    }

                    if (processById != undefined) {
                        processById.addData(jsonData, isAllocData);
                    }
                    else {
                        processById = new ProcessInfo(jsonData, isAllocData);
                        this.processes.set(jsonData["ProcessID"], processById);
                    }

                    this.requests += 1;

                    if (isAllocData) {
                        this.treeView?.refresh();
                        console.log(`Add: ${jsonData['ProcessID']}, ${jsonData["ProcessName"]}`);
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