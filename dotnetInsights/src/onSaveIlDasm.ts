import * as child from 'child_process';
import * as crypto from 'crypto';
import * as fs from 'fs';
import * as path from 'path';
import * as vscode from 'vscode';
import * as os from "os";

import { DotnetInsightsTextEditorProvider } from "./DotnetInightsTextEditor";

import { DotnetInsights } from "./dotnetInsights";
import { ILDocument } from "./ILDocument";
import { AsmDocument } from "./AsmDocument";

import { ILDasmParser } from "./ilDamParser";
import { JitOrder } from "./JitOrder";
import { PmiCommand } from "./PmiCommand";
import { ILDasm } from './ILDasm';

export class OnSaveIlDasm {
    ////////////////////////////////////////////////////////////////////////////
    // Member variables
    ////////////////////////////////////////////////////////////////////////////

    private ilShown: ILDocument | undefined;
    private asmShown: AsmDocument | undefined;

    private method: string;
    private methodNoArgs: string;

    private insights: DotnetInsights;

    private roslynHelper: child.ChildProcess | undefined = undefined;
    private roslynHelperPath: string;
    private roslynHelperTempDir: string;
    private roslynHelperIlFile: string;
    private realtimeDasmFile: string;
    private roslynHelperCommand: string;

    private hasDocumentsOpen: boolean;

    ////////////////////////////////////////////////////////////////////////////
    // Constructor
    ////////////////////////////////////////////////////////////////////////////

    constructor(insights: DotnetInsights, cursorLocation: vscode.Position | undefined, symbols: vscode.DocumentSymbol[] | undefined) {
        this.hasDocumentsOpen = false;

        this.method = "";
        this.methodNoArgs = "";
        this.insights = insights;

        this.roslynHelper = undefined;

        this.roslynHelperPath = insights.roslynHelperPath;

        this.roslynHelperTempDir = insights.pmiTempDir;
        this.roslynHelperIlFile = path.join(this.roslynHelperTempDir, "generated.dll");
        this.realtimeDasmFile = path.join(this.roslynHelperTempDir, "generated.asm");
        this.roslynHelperCommand = `"${this.roslynHelperPath}" "${this.roslynHelperIlFile}"`;

        this.ilShown = new ILDocument();
        this.asmShown = new AsmDocument();

        let setupSuccess = this.setupActiveMethod(cursorLocation, symbols);
    }

    ////////////////////////////////////////////////////////////////////////////
    // Member methods
    ////////////////////////////////////////////////////////////////////////////

    public setupActiveMethod(cursorLocation: vscode.Position | undefined, symbols: vscode.DocumentSymbol[] | undefined) : boolean {
        if (cursorLocation == undefined || symbols == undefined) {
            return false;
        }

        let methodsReturned = this.getActiveMethod(cursorLocation, symbols);

        if (methodsReturned[0] == undefined || methodsReturned[1] == undefined) {
            // Not a valid method.
            return false;
        }

        let method = methodsReturned[0];
        let methodNoArgs = methodsReturned[1];

        // Check if the current method has changed. If so we will want to reset
        // all the differences that are being tracked.
        if (this.method != "") {
            if (this.method != method) {
                // The method has changed.
                // TODO remove differences.
            }
        }

        this.method = method;
        this.methodNoArgs = methodNoArgs;

        return true;
    }

    public runRoslynHelperForFile(file: string | undefined) : boolean {
        if (file == undefined) {
            return false;
        }

        this.insights.isInlineIL = true;

        this.insights.outputChannel.appendLine(`pmi for method: ${this.method}`);

        if (this.roslynHelper == undefined) {
            this.roslynHelper = child.exec(this.roslynHelperCommand, (error: any, stdout: string, stderr: string) => {
                // No op, should not finish

                console.log(error);
                console.log(stdout);
            });

            let boundObject = this;
            this.roslynHelper.stdout?.on('data', (data) => { boundObject.onRoslynStdOut(data); });
        }

        var success = false;

        while (!success) {
            try {
                this.roslynHelper?.stdin?.write(file);

                if (os.platform() == "win32") {
                    this.roslynHelper?.stdin?.write("\r\n");
                }
                else {
                    this.roslynHelper?.stdin?.write("\n");
                }
                success = true;
            }
            catch (e) {
                console.log(e);
            }
        }

        return success;
    }

    ////////////////////////////////////////////////////////////////////////////
    // Helper methods
    ////////////////////////////////////////////////////////////////////////////

    private areDocumentsOpen(): boolean {
        let editorsShown = vscode.window.visibleTextEditors;

        var documentsAreOpen = false;

        // TODO yes/no
        if (editorsShown.length >= 3) {
            // The last two editors should be the ildasm and asm files.
            var skipCount = 0;
            if (editorsShown[editorsShown.length - 1].document.fileName.indexOf("extension-output") != -1) {
                skipCount = 1;
            }

            let secondToLast = editorsShown.length - 2 - skipCount;
            let last = editorsShown.length - 1 - skipCount;

            console.assert(secondToLast > 0 && last > 0);

            if (editorsShown[secondToLast].document.fileName.indexOf(".ildasm") != -1 && editorsShown[last].document.fileName.indexOf(".asm") != -1) {
                documentsAreOpen = true;

                this.ilShown?.updateForPath(editorsShown[secondToLast].document.fileName);
                this.asmShown?.updateForPath(editorsShown[last].document.fileName);

                this.ilShown!.setWindow(editorsShown[secondToLast]);
                this.asmShown!.setWindow(editorsShown[last]);
            }
        }

        return documentsAreOpen;
    }

    private findSymbol(symbols: vscode.DocumentSymbol[], position: vscode.Position | undefined): [vscode.DocumentSymbol, vscode.DocumentSymbol] | undefined {
        // Get all the leaf nodes into one list
        if (position == undefined) return undefined;
    
        var leafNodes:  [vscode.DocumentSymbol, vscode.DocumentSymbol][] = this.getLeafNodesWithType(symbols, undefined, undefined);
    
        var returnValue : [vscode.DocumentSymbol, vscode.DocumentSymbol] | undefined;
        var found = false;
        for (var index = 0; index < leafNodes.length; ++index) {
            if (position?.line > leafNodes[index][1].range.start.line && position?.line < leafNodes[index][1].range.end.line) {
                returnValue = leafNodes[index];
                found = true;
                break;
            }
        }
    
        console.assert(found);
        return returnValue;
    }

    private getLeafNodesWithType(symbols: vscode.DocumentSymbol[], parent: vscode.DocumentSymbol | undefined, leafNodes: [vscode.DocumentSymbol, vscode.DocumentSymbol][] | undefined): [vscode.DocumentSymbol, vscode.DocumentSymbol][] {
        if (leafNodes == undefined) {
            leafNodes = [] as [vscode.DocumentSymbol, vscode.DocumentSymbol][];
        }
    
        for (var index = 0; index < symbols.length; ++index) {
            if (symbols[index].children.length > 0) {
                this.getLeafNodesWithType(symbols[index].children, symbols[index], leafNodes);
            }
            else {
                leafNodes.push([parent!, symbols[index]]);
            }
        }
    
        return leafNodes;
    }

    private getActiveMethod(cursorLocation: vscode.Position, symbols: vscode.DocumentSymbol[]) : [string | undefined, string | undefined] {
        // We will need the active method.
        var activeMethod: string | undefined;
        var activeMethodWithoutArgs: string | undefined;

        if (symbols !== undefined) {
            var symbol = this.findSymbol(symbols, cursorLocation);

            if (symbol != undefined) {
                var typeNameWithoutAssembly = symbol[0].name.split(".")[1];

                if (typeNameWithoutAssembly == undefined) {
                    typeNameWithoutAssembly = symbol[0].name;
                }

                var methodNameWithoutArgs = symbol[1].name.split("\(")[0];

                activeMethod = methodNameWithoutArgs;
                activeMethodWithoutArgs = methodNameWithoutArgs;
                if (symbol[1].kind == vscode.SymbolKind.Constructor) {
                    activeMethod = ".ctor";
                }

                if (symbol[1].kind == vscode.SymbolKind.Constructor) {
                    activeMethod = `${typeNameWithoutAssembly}:.ctor`;
                    methodNameWithoutArgs = ".ctor";
                    activeMethodWithoutArgs = methodNameWithoutArgs;
                }
                else {
                    activeMethod = `${typeNameWithoutAssembly}:${methodNameWithoutArgs}`;
                }
            }
            else {
                activeMethod = undefined;
                activeMethodWithoutArgs = undefined;
            }
        }
        else {
            vscode.window.showWarningMessage("Unable to determine method. Check that the C# extension is installed and Omnisharp has loaded this project.");
            return [undefined, undefined];
        }

        return [activeMethod, activeMethodWithoutArgs];
    }

    private inLineIlCallback(e: any) {
        console.assert(this.insights.ilDasmOutput != undefined);

        let ildasmParser = new ILDasmParser(this.insights.ilDasmOutput);
        ildasmParser.parse();

        let lineNumber = ildasmParser.methodMap.get(this.methodNoArgs);

        if (lineNumber == undefined) {
            // Name does not directly match. Look for a loose match

            let it = ildasmParser.methodMap.keys()
            let current = it.next();
            while(current.value != undefined) {
                let key = current.value;
                if (key.indexOf(this.methodNoArgs) != -1) {
                    lineNumber = ildasmParser.methodMap.get(key);
                    break;
                }

                current = it.next();
            }
        }

        if (lineNumber == undefined) {
            vscode.window.showWarningMessage("Unable to determine method. Check that the C# extension is installed and Omnisharp has loaded this project.");
            return;
        }

        const currentVisibleRange = e.visibleRanges[0];
        const size = currentVisibleRange.end.line - currentVisibleRange.start.line;

        e.revealRange(new vscode.Range(lineNumber, 0, lineNumber + size, 0));

        if (!this.hasDocumentsOpen) {
            this.ilShown?.updateForPath(this.roslynHelperIlFile);
        }
    }

    private inPlaceIlDasm() {
        let ilDasm = new ILDasm(this.insights);
        let output = ilDasm.execute(vscode.Uri.file(this.roslynHelperIlFile));

        this.insights.ilDasmOutput = output;

        let boundObject = this;
        this.ilShown?.change(output).then(success => {
            if (success) {
                this.inLineIlCallback(boundObject.ilShown?.getWindow());
            }
        });
    }

    private newEditorIlDasm() {
        let boundObject = this;
        this.insights.inlineIlCallback = (e: any) => { boundObject.inLineIlCallback(e); };

        vscode.commands.executeCommand("vscode.openWith", vscode.Uri.file(this.roslynHelperIlFile), DotnetInsightsTextEditorProvider.viewType, vscode.ViewColumn.Beside);
    }

    private onRoslynStdOut(data: string) {
        let response = data != null ? data.toString().trim() : "";
        this.insights.outputChannel.appendLine(response);

        if (response == "Compilation succeeded") {
            // We have written IL to roslynHelperIlFile

            // TODO: change decision based on whether documents are open.

            let haveIlAndAsmDocumentsOpen = this.areDocumentsOpen();
            this.hasDocumentsOpen = haveIlAndAsmDocumentsOpen;

            if (haveIlAndAsmDocumentsOpen) {
                // Do a manual call to ildasm and pmi instead of opening a new editor
                // for each.
                this.inPlaceIlDasm();
            }
            else {
                this.newEditorIlDasm();
            }

            // Also pmi the file.
            var jitOrder = new JitOrder(this.insights.coreRunPath, this.insights, this.roslynHelperIlFile);

            let boundObject = this;
            jitOrder.execute().then(output => {
                // Determine the method from the output

                // Split the output by newline
                var newLine = "\n";
                if (os.platform() == "win32") {
                    newLine = "\r\n";
                }

                var lines = output.split(newLine);
                var matchedMethod: any = undefined;
                for (var index = 0; index < lines.length; ++index) {
                    if (lines[index].indexOf(boundObject.method) != -1) {
                        let methodSplit = lines[index].split(' | ');

                        matchedMethod = methodSplit[methodSplit.length - 1].trim();
                        break;
                    }
                }

                console.assert(matchedMethod != undefined);
                
                var pmiMethod = new PmiCommand(boundObject.insights.coreRunPath, boundObject.insights, boundObject.roslynHelperIlFile);
                pmiMethod.execute(matchedMethod).then(value => {
                    let unique_id = value[0];
                    let output = value[1];

                    const outputFileName = path.join(boundObject.insights.pmiOutputPath, unique_id + ".asm");

                    fs.writeFile(outputFileName, output, (error) => {
                        if (error) {
                            return;
                        }
                        
                        let splitIndex = vscode.window.visibleTextEditors.length + 1;

                        if (!boundObject.hasDocumentsOpen) {
                            boundObject.asmShown?.updateForPath(outputFileName);

                            vscode.workspace.openTextDocument(outputFileName).then(doc => {
                                vscode.window.showTextDocument(doc, splitIndex);
                            });
                        }
                        else {
                            boundObject.asmShown?.change(output);
                        }

                        // As a background task JITDump the method as well
                        boundObject.jitDumpMethod(matchedMethod, unique_id);
                    });
                });
            });

        } else {
            // Failed. TODO, most likely references.
            vscode.window.showWarningMessage(`Failed to compile: ${response}`);
        }
    }

    private jitDumpMethod(matchedMethod: string, unique_id: string) {
        const boundObject = this;
        var pmiMethod = new PmiCommand(boundObject.insights.coreRunPath, boundObject.insights, boundObject.roslynHelperIlFile);

        pmiMethod.execute(matchedMethod, {"COMPlus_JitDump": matchedMethod}).then(value => {
            let output = value[1];

            const outputFileName = path.join(boundObject.insights.pmiOutputPath, unique_id + ".jitDump");

            fs.writeFile(outputFileName, output, (error) => {
                if (error) {
                    return;
                }
            });
        });
    }
}