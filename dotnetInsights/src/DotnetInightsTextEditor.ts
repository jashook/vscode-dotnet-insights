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

    public static readonly viewType = 'dotnetInsights.edit';
    
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

        var mb = 1024 * 1024;
        var maxBufferSize = 512 * mb;
        
        // Used by pmi as it need FS access
        const cwd: string =  this.insights.pmiTempDir;
        const endofLine = os.platform() == "win32" ? vscode.EndOfLine.CRLF : vscode.EndOfLine.LF;

        // We will run ildasm then render those contents
        var ildasmCommand = `\"${this.insights.ilDasmPath}\"` + " " + `\"${uri.fsPath}\"`;
        this.insights.outputChannel.appendLine(ildasmCommand);

        var output = child.execSync(ildasmCommand, {
            maxBuffer: maxBufferSize
        }).toString();

        var filename = path.basename(uri.fsPath);

        var extensionOutputPath = this.insights.ilDasmOutputPath;
        assert(fs.existsSync(extensionOutputPath));

        // Hijack the URI by saving the created file to a temporary location
        var filename = path.basename(uri.fsPath);

        // This must be a managed .dll file
        assert (path.extname(filename) == ".dll");

        var filenameWithoutExt = filename.split(".dll")[0]
        filename = filenameWithoutExt + ".ildasm";
        outputFilePath = path.join(extensionOutputPath, filename);

        this.insights.ilDasmOutput = output;

        fs.writeFileSync(outputFilePath, output);

        var openPath = vscode.Uri.file(outputFilePath);

        if (vscode.window.visibleTextEditors.length == 1 && !this.insights.isInlineIL) {
            vscode.commands.executeCommand('workbench.action.closeActiveEditor').then(() => {
                vscode.workspace.openTextDocument(openPath).then(doc => {
                    this.insights.isInlineIL = false;
                    vscode.window.showTextDocument(doc)
                });
            });
        }
        else {
            this.insights.outputChannel.appendLine("closeEditorsAndGroup");
            vscode.commands.executeCommand('workbench.action.closeEditorsAndGroup').then(() => {
                vscode.workspace.openTextDocument(openPath).then(doc => {
                    if (this.insights.inlineIlCallback != undefined) {
                        vscode.window.showTextDocument(doc, {
                            viewColumn: vscode.ViewColumn.Beside
                        }).then(this.insights.inlineIlCallback);
                    }
                    else {
                        vscode.window.showTextDocument(doc, {
                            viewColumn: vscode.ViewColumn.Beside
                        });
                    }

                    this.insights.inlineIlCallback = false;
                });
            });
        }

        // After the text editor has loaded we will want to update the tree view
        if (this.insights.useIldasm && !this.insights.isInlineIL) {
            this.insights.updateForPath(outputFilePath, uri.fsPath, output);
        }
        
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

export class Method {
    public desc: string;
    public isMinOpts: boolean;
    public region: string;
    public hash: string;
    public hasEh: boolean;
    public frame: string;
    public hasLoop: boolean;
    public directCallCount: number;
    public indirectCallCount: number;
    public basicBlockCount: number;
    public localVarCount: number;
    public assertionPropCount: number | undefined;
    public cseCount: number | undefined;
    public perfScore: number | undefined;
    public ilBytes: number;
    public hotCodeSize: number;
    public coldCodeSize: number;
    public totalCodeSize: number;
    public name: string;

    constructor(desc: string,
                isMinOpts: boolean,
                region: string,
                hash: string,
                hasEh: boolean,
                frame: string,
                hasLoop: boolean,
                directCallCount: number,
                indirectCallCount: number,
                basicBlockCount: number,
                localVarCount: number,
                assertionPropCount: number | undefined,
                cseCount: number | undefined,
                perfScore: number | undefined,
                ilBytes: number,
                hotCodeSize: number,
                coldCodeSize: number,
                totalCodeSize: number,
                name: string) {
        this.desc = desc;
        this.isMinOpts = isMinOpts;
        this.region = region;
        this.hash = hash;
        this.hasEh = hasEh;
        this.frame = frame;
        this.hasLoop = hasLoop;
        this.directCallCount = directCallCount;
        this.indirectCallCount = indirectCallCount;
        this.basicBlockCount = basicBlockCount;
        this.localVarCount = localVarCount;
        this.assertionPropCount = assertionPropCount;
        this.cseCount = cseCount;
        this.perfScore = perfScore;
        this.ilBytes = ilBytes;
        this.hotCodeSize = hotCodeSize;
        this.coldCodeSize = coldCodeSize;
        this.totalCodeSize = totalCodeSize;
        this.name = name;
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