import { DotnetInsightsGcTreeDataProvider } from "./dotnetInsightsGc";

import { createServer } from "http";
import { IncomingMessage, ServerResponse } from 'node:http';
import { parse } from 'node:path';

export class GcData {
    public data: any;
    public timestamp: Date;

    constructor(data: any) {
        this.data = data;
        this.timestamp = new Date();
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
    public allocData: AllocData[];
    public processId: number;
    public processName: string;
    public processCommandLine: string;

    constructor(data: any, isAllocData: boolean) {
        const parsedJson = data;
        
        this.processId = parsedJson["ProcessID"];
        this.processName = parsedJson["ProcessName"];
        this.processCommandLine = parsedJson["processCommandLine"];
        this.data = [] as GcData[];
        this.allocData = [] as AllocData[];

        this.addData(parsedJson, isAllocData);
    }

    addData(parsedJson: any, isAllocData: boolean) {
        if (isAllocData) {
            this.allocData.push(new AllocData(parsedJson["data"]));
        }
        else {
            this.data.push(new GcData(parsedJson["data"]));
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
                request.on("data", (chunk) => {
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

                    var processById: ProcessInfo | undefined = this.processes.get(jsonData["ProcessID"]);

                    const isAllocData = request.url == "/gcAllocation";

                    if (processById != undefined) {
                        processById.addData(jsonData, isAllocData);
                    }
                    else {
                        processById = new ProcessInfo(jsonData, isAllocData);
                        this.processes.set(jsonData["ProcessID"], processById);
                    }

                    this.requests += 1;

                    this.treeView?.refresh();

                    console.log(`Add: ${jsonData['ProcessID']}, ${jsonData["ProcessName"]}`);

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