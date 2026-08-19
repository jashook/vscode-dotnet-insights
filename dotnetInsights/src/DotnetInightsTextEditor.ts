import * as child from 'child_process';
import * as fs from 'fs';
import * as os from "os";
import * as path from 'path';
import * as vscode from 'vscode';
import * as assert from "assert";

import { Uri } from 'vscode';

import { DotnetInsights } from './dotnetInsights';
import { ILDasm } from './ILDasm';

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
        var ilDasmCommand = new ILDasm(this.insights);
        var output = ilDasmCommand.execute(uri);

        var outputFilePath: string;

        // Used by pmi as it need FS access
        const cwd: string =  this.insights.pmiTempDir;
        const endofLine = os.platform() === "win32" ? vscode.EndOfLine.CRLF : vscode.EndOfLine.LF;
        
        var filename = path.basename(uri.fsPath);

        var extensionOutputPath = this.insights.ilDasmOutputPath;

        // Hijack the URI by saving the created file to a temporary location
        var filename = path.basename(uri.fsPath);

        var filenameWithoutExt = filename.split(".dll")[0];
        filename = filenameWithoutExt + ".ildasm";
        outputFilePath = path.join(extensionOutputPath, filename);

        this.insights.ilDasmOutput = output;

        fs.writeFileSync(outputFilePath, output);

        // The generated .ildasm file is shown from resolveCustomEditor, which is
        // where this provider's own placeholder editor can be closed without
        // touching any of the user's other editors.

        // Update the tree view for the disassembly that was just generated
        if (this.insights.useIldasm && !this.insights.isInlineIL) {
            this.insights.updateForPath(outputFilePath, uri.fsPath, output);
        }
        else if (this.insights.isInlineIL)
        {
            this.insights.isInlineIL = false;
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
        // This provider never renders anything into its own webview. It only
        // exists to hijack opening a *.dll, so the panel it is handed here is a
        // placeholder that gets swapped for the ildasm text document.
        var insightsDocument = document as DotnetInsightsDocument;

        var inlineIlCallback = this.insights.inlineIlCallback;
        this.insights.inlineIlCallback = undefined;

        showIlDasmInPlaceOfPanel(webviewPanel, insightsDocument.fileName).then(editor => {
            if (inlineIlCallback !== undefined) {
                inlineIlCallback(editor);
            }
        });
    }
}

/**
 * Swaps this provider's placeholder editor for the ildasm text document it
 * generated, in the view column that placeholder occupied.
 *
 * Disposing the panel closes exactly that one editor. This used to run
 * workbench.action.closeActiveEditor (or, with more than one editor visible,
 * closeEditorsAndGroup), which took whatever else the user had open down with
 * it - the whole editor group in the second case (issue #99).
 *
 * The panel must NOT be disposed while VS Code is still inside
 * resolveCustomEditor: it goes on setting the editor up after that call
 * returns, and disposing underneath it fails the whole open with a modal
 * "Unable to open '<name>.dll': OverlayWebview has been disposed" - even though
 * the ildasm document itself opened fine. So the swap is deferred by a turn of
 * the event loop, and the panel goes away only once its replacement is actually
 * on screen, which is several async round trips later again.
 *
 * The inline IL flow opens the .dll with ViewColumn.Beside, so its placeholder
 * is already beside and the ildasm document still lands there.
 */
export function showIlDasmInPlaceOfPanel(webviewPanel: vscode.WebviewPanel, ilDasmFilePath: string): Thenable<vscode.TextEditor> {
    var panelDisposed = false;
    var disposeListener = webviewPanel.onDidDispose(() => { panelDisposed = true; });

    return new Promise<vscode.TextEditor>((resolve, reject) => {
        setTimeout(() => {
            // Read after the deferral, not before: a panel has no viewColumn
            // until it has been laid out, which has happened by now.
            var viewColumn = (panelDisposed || webviewPanel.viewColumn === undefined)
                             ? vscode.ViewColumn.Active
                             : webviewPanel.viewColumn;

            vscode.workspace.openTextDocument(vscode.Uri.file(ilDasmFilePath)).then(doc => {
                // preview: false, so this generated document takes a tab of its
                // own rather than evicting whatever the user is previewing.
                return vscode.window.showTextDocument(doc, {
                    viewColumn: viewColumn,
                    preview: false
                });
            }).then(editor => {
                webviewPanel.dispose();
                disposeListener.dispose();

                resolve(editor);
            }, error => {
                disposeListener.dispose();

                reject(error);
            });
        }, 0);
    });
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
    encoding: string;
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
        this.encoding = "utf8";
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