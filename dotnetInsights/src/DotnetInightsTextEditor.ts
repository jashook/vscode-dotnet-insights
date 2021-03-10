import * as child from 'child_process';
import * as fs from 'fs';
import * as os from "os";
import * as path from 'path';
import * as vscode from 'vscode';
import * as assert from "assert"

import { Uri } from 'vscode'

import { DotnetInsights } from './dotnetInsights';

export class DotnetInsightsTextEditorProvider implements vscode.CustomReadonlyEditorProvider {
    public static register(context: vscode.ExtensionContext, insights: DotnetInsights): vscode.Disposable {
        const provider = new DotnetInsightsTextEditorProvider(context, insights);
        const providerRegistration = vscode.window.registerCustomEditorProvider(DotnetInsightsTextEditorProvider.viewType, provider);
        return providerRegistration;
    }

    private static readonly viewType = 'dotnetInsights.edit';
    
    constructor(
        private readonly context: vscode.ExtensionContext,
        private readonly insights: DotnetInsights
    ) { }

    openCustomDocument(uri: vscode.Uri, openContext: vscode.CustomDocumentOpenContext, token: vscode.CancellationToken): vscode.CustomDocument | Thenable<vscode.CustomDocument> {
        if (this.insights.ilDasmPath == "") {
            throw new Error("DotnetInsights not setup correctly.");
        }

        var output: string;
        var outputFilePath: string;

        if (this.insights.useIldasm) {
            // We will run ildasm then render those contents
            var ildasmCommand = this.insights.ilDasmPath + " " + uri.path;
            console.log(ildasmCommand);

            var mb = 1024 * 1024;
            var maxBufferSize = 512 * mb;

            var output = child.execSync(ildasmCommand, {
                maxBuffer: maxBufferSize
            }).toString();

            var filename = path.basename(uri.path);

            var extensionOutputPath = this.insights.ilDasmOutputPath;
            assert(fs.existsSync(extensionOutputPath));

            // Hijack the URI by saving the created file to a temporary location
            var filename = path.basename(uri.path);

            // This must be a managed .dll file
            assert (path.extname(filename) == ".dll");

            var filenameWithoutExt = filename.split(".dll")[0]
            filename = filenameWithoutExt + ".ildasm";

            outputFilePath = path.join(extensionOutputPath, filename);
        }
        else {
            // We will run pmi then render those contents
            var pmiCommand = this.insights.coreRunPath + " " + this.insights.pmiPath + " " + "DRIVEALL" + " " + uri.path;
            console.log(pmiCommand);

            var mb = 1024 * 1024;
            var maxBufferSize = 512 * mb;

            var output = child.execSync(pmiCommand, {
                maxBuffer: maxBufferSize,
                "env": {
                    "COMPlus_JitDisasm": "*"
                }
            }).toString();

            var filename = path.basename(uri.path);

            var extensionOutputPath = this.insights.pmiOutputPath;
            assert(fs.existsSync(extensionOutputPath));

            // Hijack the URI by saving the created file to a temporary location
            var filename = path.basename(uri.path);

            // This must be a managed .dll file
            assert (path.extname(filename) == ".dll");

            var filenameWithoutExt = filename.split(".dll")[0]
            filename = filenameWithoutExt + ".asm";

            outputFilePath = path.join(extensionOutputPath, filename);
        }

        fs.writeFileSync(outputFilePath, output);

        var openPath = vscode.Uri.file(outputFilePath);
        vscode.workspace.openTextDocument(openPath).then(doc => {
            vscode.window.showTextDocument(doc)
        });

        const endofLine = os.platform() == "win32" ? vscode.EndOfLine.CRLF : vscode.EndOfLine.LF;
        
        var document = new DotnetInsightsDocument(uri,
                                          outputFilePath,
                                          false,
                                          "DotnetInsights",
                                          1,
                                          false,
                                          true,
                                          endofLine,
                                          output.length);

        return document;
    }

    resolveCustomEditor(document: vscode.CustomDocument, webviewPanel: vscode.WebviewPanel, token: vscode.CancellationToken): void | Thenable<void> {
        // unused
    }
}

class DotnetInsightsDocument extends vscode.Disposable implements vscode.TextDocument {
    uri: vscode.Uri;
    fileName: string;
    isUntitled: boolean;
    languageId: string;
    version: number;
    isDirty: boolean;
    isClosed: boolean;
    eol: vscode.EndOfLine;
    lineCount: number;

    constructor(
        uri: vscode.Uri,
        fileName: string,
        isUntitled: boolean,
        languageId: string,
        version: number,
        isDirty: boolean,
        isClosed: boolean,
        eol: vscode.EndOfLine,
        lineCount: number
    ) {
        super(() => {
            console.log("Tearing down DotnetInsightsDocument");
        });

        this.uri = uri;
        this.fileName = fileName;
        this.isUntitled = isUntitled;
        this.languageId = languageId;
        this.version = version;
        this.isDirty = isDirty;
        this.isClosed = isClosed;
        this.eol = eol,
        this.lineCount = lineCount;
    }

    lineAt(position: any): vscode.TextLine {
        throw new Error('Method not implemented.');
    }
    
    save(): Thenable<boolean> {
        // Do nothing.

        return Promise.resolve(true);
    }

    offsetAt(position: vscode.Position): number {
        throw new Error('Method not implemented.');
    }

    positionAt(offset: number): vscode.Position {
        throw new Error('Method not implemented.');
    }

    getText(range?: vscode.Range): string {
        // if (range == undefined) {
            return "";
        // }


        //return this.text.substring(range.start, range.end);
    }

    getWordRangeAtPosition(position: vscode.Position, regex?: RegExp): vscode.Range | undefined {
        throw new Error('Method not implemented.');
    }

    validateRange(range: vscode.Range): vscode.Range {
        throw new Error('Method not implemented.');
    }
    
    validatePosition(position: vscode.Position): vscode.Position {
        throw new Error('Method not implemented.');
    }
}