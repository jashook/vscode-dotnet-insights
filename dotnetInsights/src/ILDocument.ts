import * as child from 'child_process';
import * as crypto from 'crypto';
import * as fs from 'fs';
import * as path from 'path';
import * as vscode from 'vscode';
import * as os from "os";

import { DotnetInsights } from "./dotnetInsights";

export class ILDocument {
    ////////////////////////////////////////////////////////////////////////////
    // Member variables
    ////////////////////////////////////////////////////////////////////////////

    private path: string;
    private text: string;

    ////////////////////////////////////////////////////////////////////////////
    // Constructor
    ////////////////////////////////////////////////////////////////////////////

    constructor(path?: string, text?: string) {
        if (path != undefined) {
            this.path = path;
        }
        else {
            this.path = "";
        }

        if (text != undefined) {
            this.text = text;
        }
        else {
            this.text = "";
        }
    }

    ////////////////////////////////////////////////////////////////////////////
    // Member methods
    ////////////////////////////////////////////////////////////////////////////

}