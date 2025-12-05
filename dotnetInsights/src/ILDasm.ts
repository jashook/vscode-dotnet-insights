import * as child from 'child_process';
import * as crypto from 'crypto';
import * as fs from 'fs';
import * as path from 'path';
import * as vscode from 'vscode';
import * as os from "os";

import { DotnetInsights } from "./dotnetInsights";

export class ILDasm {
    ////////////////////////////////////////////////////////////////////////////
    // Member variables
    ////////////////////////////////////////////////////////////////////////////

    private ildasm: string;
    private insights : DotnetInsights;

    ////////////////////////////////////////////////////////////////////////////
    // Constructor
    ////////////////////////////////////////////////////////////////////////////

    constructor(insights: DotnetInsights) {
        this.insights = insights;

        this.ildasm = this.insights.ilDasmPath;
    }

    ////////////////////////////////////////////////////////////////////////////
    // Member methods
    ////////////////////////////////////////////////////////////////////////////

    public execute(uri: vscode.Uri): string {
        if (this.insights.ilDasmPath === "") {
            throw new Error("DotnetInsights not setup correctly.");
        }

        var output: string;

        var mb = 1024 * 1024;
        var maxBufferSize = 512 * mb;

        // We will run ildasm then render those contents
        var ildasmCommand = `\"${this.insights.ilDasmPath}\"` + " " + `\"${uri.fsPath}\"`;
        this.insights.outputChannel.appendLine(ildasmCommand);

        var output = child.execSync(ildasmCommand, {
            maxBuffer: maxBufferSize
        }).toString();

        return output;
    }
}