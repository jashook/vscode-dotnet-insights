import * as child from 'child_process';
import * as crypto from 'crypto';
import * as fs from 'fs';
import * as path from 'path';
import * as vscode from 'vscode';
import * as os from "os";

import { DotnetInsights } from "./dotnetInsights";

export class AsmDocument {
    ////////////////////////////////////////////////////////////////////////////
    // Member variables
    ////////////////////////////////////////////////////////////////////////////

    private path: string;
    private text: string;

    private window: vscode.TextEditor | undefined;

    ////////////////////////////////////////////////////////////////////////////
    // Constructor
    ////////////////////////////////////////////////////////////////////////////

    constructor(path?: string, text?: string) {
        if (path !== undefined) {
            this.path = path;
        }
        else {
            this.path = "";
        }

        if (text !== undefined) {
            this.text = text;
        }

        else {
            this.text = "";
        }
    }

    ////////////////////////////////////////////////////////////////////////////
    // Member methods
    ////////////////////////////////////////////////////////////////////////////

    public change(contents: string) : Thenable<boolean> {
        let boundObject = this;
        let promise = new Promise<boolean>((resolve, reject) => {
            fs.writeFile(boundObject.path, contents, (err) => {
                if (err !== null) {
                    reject(false);
                }

                resolve(true);
            });
        });

        return promise;
    }

    public getPath(): string {
        return this.path;
    }

    public getWindow(): vscode.TextEditor {
        return this.window!;
    }

    public setWindow(window: vscode.TextEditor) {
        this.window = window;
    }

    public updateForPath(filePath: string) {
        this.path = filePath;
        this.text = fs.readFileSync(filePath).toString();
    }

}