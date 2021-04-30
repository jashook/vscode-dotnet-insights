import * as child from 'child_process';
import * as crypto from 'crypto';
import * as fs from 'fs';
import * as path from 'path';
import * as vscode from 'vscode';
import * as os from "os";

import { DotnetInsights } from "./dotnetInsights";

export class PmiCommand {
    ////////////////////////////////////////////////////////////////////////////
    // Member variables
    ////////////////////////////////////////////////////////////////////////////

    private coreRun: string;
    private peFile: string;
    private pmiPath: string;
    private pmiWd: string;

    private outputChannel: vscode.OutputChannel;

    ////////////////////////////////////////////////////////////////////////////
    // Constructor
    ////////////////////////////////////////////////////////////////////////////

    constructor(coreRun: string, insights: DotnetInsights, peFile: string) {
        let pmiPath = insights.pmiPath;

        console.assert(fs.existsSync(coreRun));
        console.assert(fs.existsSync(pmiPath));
        console.assert(fs.existsSync(peFile));

        this.coreRun = coreRun;
        this.pmiPath = pmiPath;
        this.peFile = peFile;

        this.outputChannel = insights.outputChannel;
        this.pmiWd = path.join(insights.pmiOutputPath, "selectMethod");

        if  (!fs.existsSync(this.pmiWd)) {
            fs.mkdirSync(this.pmiWd);
        }
    }

    ////////////////////////////////////////////////////////////////////////////
    // Member methods
    ////////////////////////////////////////////////////////////////////////////

    public execute(methodName?: string, extraOptions?: any): Thenable<[string, string]> {
        var pmiCommand = `"${this.coreRun}"` + " " + `"${this.pmiPath}"` + " " + "PREPALL-QUIET" + " " + `"${this.peFile}"`;
        this.outputChannel.appendLine(pmiCommand);

        var mb = 1024 * 1024;
        var maxBufferSize = 512 * mb;

        const id = crypto.randomBytes(16).toString("hex");

        var env: any;

        env = {
            "COMPlus_TieredCompilation": "0",
            "COMPlus_TC_QuickJit": "0"
        }

        if (methodName != undefined) {
            extraOptions = {
                "COMPlus_JitDisasm": `${methodName}`,
                "COMPlus_JitGCDump": `${methodName}`
            }
        }

        if (extraOptions != undefined) {
            let keys = Object.keys(extraOptions);
            for (var index = 0; index < keys.length; ++index) {
                env[keys[index]] = extraOptions[keys[index]];
            }
        }

        var promise: Thenable<[string, string]> = new Promise((resolve, reject) => {
            var childProcess = child.exec(pmiCommand, {
                maxBuffer: maxBufferSize,
                "cwd": this.pmiWd,
                "env": env
            }, (error: any, output: string, stderr: string) => {
                if (error) {
                    console.error("Failed to execute pmi.");
                    console.error(error);

                    reject(["", error]);
                }

                var replaceRegex = /completed assembly.*\n/i;
                if (os.platform() == "win32") {
                    replaceRegex = /completed assembly.*\r\n/i;
                }

                output = output.replace(replaceRegex, "");
                resolve([id, output]);
            });
        });

        return promise;
    }
}