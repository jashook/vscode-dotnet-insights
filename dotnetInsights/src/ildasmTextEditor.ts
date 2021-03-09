import * as child from 'child_process';
import * as fs from 'fs';
import * as os from "os";
import * as path from 'path';
import * as vscode from 'vscode';

import { DotnetInsights } from './dotnetInsights';
import { RichHtmlDocument } from "./richHtmlDocument";

export class IlDasmTextEditorProvider implements vscode.CustomReadonlyEditorProvider {
    public static register(context: vscode.ExtensionContext, insights: DotnetInsights): vscode.Disposable {
        const provider = new IlDasmTextEditorProvider(context, insights);
        const providerRegistration = vscode.window.registerCustomEditorProvider(IlDasmTextEditorProvider.viewType, provider);
        return providerRegistration;
    }

    private static readonly viewType = 'ildasm.edit';
    
    constructor(
        private readonly context: vscode.ExtensionContext,
        private readonly insights: DotnetInsights
    ) { }

    openCustomDocument(uri: vscode.Uri, openContext: vscode.CustomDocumentOpenContext, token: vscode.CancellationToken): vscode.CustomDocument | Thenable<vscode.CustomDocument> {
        if (this.insights.ilDasmPath == "") {
            throw new Error("ILDasm not setup correctly.");
        }

        // We will run ILDasm then render those contents
        var ildasmCommand = this.insights.ilDasmPath + " " + uri.path;
        console.log(ildasmCommand);

        var mb = 1024 * 1024;
        var maxBufferSize = 512 * mb;

        var output = child.execSync(ildasmCommand, {
            maxBuffer: maxBufferSize
        }).toString();

        var filename = path.basename(uri.path);
        var endofLine = os.platform() == "win32" ? vscode.EndOfLine.CRLF : vscode.EndOfLine.LF;

        var document = new ILDasmDocument(uri,
                                          filename,
                                          false,
                                          "ildasm",
                                          1,
                                          false,
                                          true,
                                          endofLine,
                                          output.length,
                                          output);

        return document;
    }

    resolveCustomEditor(document: vscode.CustomDocument, webviewPanel: vscode.WebviewPanel, token: vscode.CancellationToken): void | Thenable<void> {
        webviewPanel.webview.options = {
            enableScripts: false
        }

        var ildasmDocument = document as ILDasmDocument;
        webviewPanel.webview.html = this.getHtmlForWebview(ildasmDocument, webviewPanel.webview);
    }

    private getHtmlForWebview(document: ILDasmDocument, webview: vscode.Webview): string {
        var fileName = document.fileName;

        var linesArray = [];
        for (var index = 0; index < document.lines.length; ++index) {
            linesArray.push("<div>" + document.lines[index] + "</div>");
        }

        var lines = linesArray.join(document.eol == vscode.EndOfLine.LF ? "\n" : "\r\n");
        var nonce = this.getNonce();

        const styleVsCodeUri = webview.asWebviewUri(vscode.Uri.joinPath(this.context.extensionUri, 'media', 'main.css'));

        var html = new RichHtmlDocument(document.lines);

        var returnValue = /* html */`
            <!DOCTYPE html>
            <html lang="en">
            <head>
                <meta charset="UTF-8">

                <!--
                Use a content security policy to only allow loading images from https or from our extension directory,
                and only allow scripts that have a specific nonce.
                -->
                <meta http-equiv="Content-Security-Policy" content="default-src 'none'; img-src ${webview.cspSource}; style-src ${webview.cspSource}; script-src 'nonce-${nonce}';">
                <meta name="viewport" content="width=device-width, initial-scale=1.0">

                <link href="${styleVsCodeUri}" rel="stylesheet" />

                <title>${fileName}</title>
            </head>
            <body>
                ${lines}
            </body>
            </html>`;

        return returnValue;
    }

    getNonce() {
        let text = '';
        const possible = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789';
        for (let i = 0; i < 32; i++) {
            text += possible.charAt(Math.floor(Math.random() * possible.length));
        }
        return text;
    }
}

class ILDasmDocument extends vscode.Disposable implements vscode.TextDocument {
    uri: vscode.Uri;
    fileName: string;
    isUntitled: boolean;
    languageId: string;
    version: number;
    isDirty: boolean;
    isClosed: boolean;
    eol: vscode.EndOfLine;
    lineCount: number;

    text: string;
    lines: string[];

    constructor(
        uri: vscode.Uri,
        fileName: string,
        isUntitled: boolean,
        languageId: string,
        version: number,
        isDirty: boolean,
        isClosed: boolean,
        eol: vscode.EndOfLine,
        lineCount: number,
        text: string
    ) {
        super(() => {
            console.log("Tearing down ILDasmDocument");
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
        this.text = text

        this.lines = this.text.split(eol == vscode.EndOfLine.LF ? '\n' : "\r\n");
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
            return this.text;
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