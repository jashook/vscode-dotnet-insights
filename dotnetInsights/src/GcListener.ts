import * as child from 'child_process';
import * as fs from 'fs';
import * as os from "os";
import * as path from 'path';
import * as vscode from 'vscode';
import * as assert from "assert"

import { DotnetInsightsGcTreeDataProvider } from "./dotnetInsightsGc";

import { createServer } from "http";
import { IncomingMessage, ServerResponse } from 'node:http';

export class GcData {
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

    constructor(data: any) {
        const parsedJson = data;
        
        this.processId = parsedJson["ProcessID"];
        this.data = [] as GcData[];

        this.addData(parsedJson);
    }

    addData(parsedJson: any) {
        this.data.push(new GcData(parsedJson["data"]));
    }
}

export class GcListener {

    public treeView: DotnetInsightsGcTreeDataProvider | undefined;
    public processes: Map<number, ProcessInfo>;
    
    constructor(
    ) {
        this.treeView = undefined;
        this.processes = new Map<number, ProcessInfo>();
        // For now only support windows.
        if (os.platform() == "win32") {
            const httpServer = createServer((request: IncomingMessage, response: ServerResponse) => {
                if (request.method == "POST") {
                    var data = "";
                    request.on("data", (chunk) => {
                        data += chunk;
                    });

                    request.on("end", () => {
                        const jsonData = JSON.parse(data);
                        var processById: ProcessInfo | undefined = this.processes.get(jsonData["ProcessID"]);

                        if (processById != undefined) {
                            processById.addData(jsonData);
                        }
                        else {
                            const processReturned = new ProcessInfo(jsonData);
                            this.processes.set(jsonData["ProcessID"], processReturned);
                        }

                        this.treeView?.refresh();

                        console.log(`Add: ${jsonData['ProcessID']}`);
                        response.end("eol");
                    });

                }
            });

            const port = 2143;
            httpServer.listen(port);
        }
    }
}