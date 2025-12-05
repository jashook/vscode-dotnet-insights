import * as child from 'child_process';
import * as crypto from 'crypto';
import * as fs from 'fs';
import * as path from 'path';
import * as vscode from 'vscode';
import * as os from "os";

import { DotnetInsights } from "./dotnetInsights";
import { PmiCommand } from './PmiCommand';

export class JitOrder {
    ////////////////////////////////////////////////////////////////////////////
    // Member variables
    ////////////////////////////////////////////////////////////////////////////

    private coreRun: string;
    private peFile: string;
    private insights : DotnetInsights;

    ////////////////////////////////////////////////////////////////////////////
    // Constructor
    ////////////////////////////////////////////////////////////////////////////

    constructor(coreRun: string, insights: DotnetInsights, peFile: string) {
        console.assert(fs.existsSync(coreRun));
        console.assert(fs.existsSync(peFile));

        this.coreRun = coreRun;
        this.peFile = peFile;

        this.insights = insights;
    }

    ////////////////////////////////////////////////////////////////////////////
    // Member methods
    ////////////////////////////////////////////////////////////////////////////

    public execute(): Thenable<string> {
        var pmiCommand = new PmiCommand(this.coreRun, this.insights, this.peFile);
        var promise: Thenable<string> = new Promise((resolve, reject) => {
            // eslint-disable-next-line @typescript-eslint/naming-convention
            pmiCommand.execute(undefined, {"COMPlus_JitOrder": "1"}).then(value => {
                resolve(value[1]);
            });
        });

        return promise;
    }
}