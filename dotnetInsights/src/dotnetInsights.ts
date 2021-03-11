import * as child from 'child_process';
import * as vscode from 'vscode';
import * as fs from 'fs';
import * as os from "os";
import * as path from 'path';
import * as assert from "assert"

import { Method } from "./DotnetInightsTextEditor"

export class DotnetInsightsTreeDataProvider implements vscode.TreeDataProvider<Dependency> {
    private _onDidChangeTreeData: vscode.EventEmitter<Dependency | undefined | void> = new vscode.EventEmitter<Dependency | undefined | void>();
    readonly onDidChangeTreeData: vscode.Event<Dependency | undefined | void> = this._onDidChangeTreeData.event;

    public insights: DotnetInsights | undefined;

    constructor(insights: DotnetInsights) {
        this.insights = insights;
        this.insights.treeView = this;
    }

    getTreeItem(element: Dependency): vscode.TreeItem {
        return element;
    }

    refresh(): void {
        this._onDidChangeTreeData.fire();
    }

    getChildren(element?: Dependency): Thenable<Dependency[]> {
        if (this.insights?.methods?.size == 0) {
            return Promise.resolve([]);
        }
        else if (this.insights != undefined && this.insights.methods != undefined && this.insights.methods.size > 0) {
            var dependencies = [] as Dependency[];

            assert(this.insights != undefined);
            assert(this.insights?.methods != undefined);

            const userMethods = this.insights?.methods.get("user");
            if (userMethods == undefined) {
                return Promise.resolve(dependencies);
            }

            for (var index = 0; index < userMethods?.length; ++index) {
                const currentMethod = userMethods[index];
                dependencies.push(new Dependency(currentMethod.name, currentMethod.ilBytes, vscode.TreeItemCollapsibleState.None));
            }

            return Promise.resolve(dependencies);
        }
        else {
            return Promise.resolve([]);
        }
    }

}

export class Dependency extends vscode.TreeItem {

    constructor(
        public readonly label: string,
        private readonly ilBytes: number,
        public readonly collapsibleState: vscode.TreeItemCollapsibleState,
        public readonly command?: vscode.Command
    ) {
        super(label, collapsibleState);

        this.tooltip = `${this.label}`;
        this.description = this.ilBytes.toString();
    }

    iconPath = {
        light: path.join(__filename, '..', '..', 'resources', 'light', 'dependency.svg'),
        dark: path.join(__filename, '..', '..', 'resources', 'dark', 'dependency.svg')
    };

    contextValue = 'dependency';
}

export class DotnetInsights {
    public ilDasmPath: string;
    public ilDasmVersion: string;

    public pmiPath: string;
    public coreRoot: string;
    public coreRunPath: string;

    public sdkVersions: string[];

    public ilDasmOutputPath: string;
    public pmiOutputPath: string;

    public pmiTempDir: string;

    public useIldasm: boolean;
    public usePmi: boolean

    public methods: Map<string, Method[]> | undefined;

    public treeView: DotnetInsightsTreeDataProvider | undefined;

    constructor() {
        this.ilDasmPath = "";
        this.ilDasmVersion = "";

        this.pmiPath = "";
        this.coreRoot = "";
        this.coreRunPath = "";

        this.ilDasmOutputPath = "";
        this.pmiOutputPath = "";

        this.pmiTempDir = "";

        this.useIldasm = true;
        this.usePmi = false;

        this.treeView = undefined;
        this.methods = undefined;

        this.sdkVersions = [] as string[];
    }

    public setUseIldasm() {
        this.useIldasm = true;
        this.usePmi = false;
    }

    public setUsePmi() {
        this.useIldasm = false;
        this.usePmi = true;
    }

    public updateForPath(path: string) {
        var pmiCommand = this.coreRunPath + " " + this.pmiPath + " " + "DRIVEALL-QUIET" + " " + path;
        console.log(pmiCommand);

        var mb = 1024 * 1024;
        var maxBufferSize = 512 * mb;
        
        // Used by pmi as it need FS access
        const cwd: string =  this.pmiTempDir;
        const endofLine = os.platform() == "win32" ? vscode.EndOfLine.CRLF : vscode.EndOfLine.LF;


        var childProcess = child.exec(pmiCommand, {
            maxBuffer: maxBufferSize,
            "cwd": cwd,
            "shell": "bash",
            "env": {
                "COMPlus_JitOrder": "1",
                "COMPlus_TieredCompilation": "0"
            }
        }, (error: any, output: string, stderr: string) => {
            if (error) {
                return;
            }

            var methods = this.parseJitOrderOutput(output, endofLine);

            this.methods = methods;
            this.treeView?.refresh();
        });
    }

    private parseJitOrderOutput(jitOrder: string, eol: vscode.EndOfLine) {
        var eolChar = "\n";
        if (eol == vscode.EndOfLine.CRLF) {
            eolChar = "\r\n";
        }

        var methods = [] as Method[];

        var lines = jitOrder.split(eolChar);
        
        for (var index = 0; index < lines.length; ++index) {
            const currentLine = lines[index];
            if (currentLine.indexOf("-------") != -1 ||
                currentLine.indexOf("Method has") != -1 ||
                currentLine.indexOf("method name") != -1) {
                continue;
            }

            var lineSplit = lines[index].split("| ");

            try {
                // '         |  Profiled   | Method   |   Method has    |   calls   | Num |LclV |AProp| CSE |   Perf  |bytes | x64 codesize| 
                // mdToken |  CNT |  RGN |    Hash  | EH | FRM | LOOP | NRM | IND | BBs | Cnt | Cnt | Cnt |  Score  |  IL  |   HOT | CLD | method name 
                if (lineSplit.length > 0) {
                    var splitIndex = 0;

                    var methodDesc = lineSplit[splitIndex++].trim();
                    var unused = lineSplit[splitIndex++].trim();
                    var region = lineSplit[splitIndex++].trim();
                    var hash = lineSplit[splitIndex++].trim();
                    var hashEh = lineSplit[splitIndex++].indexOf("EH") != -1;
                    var frame = lineSplit[splitIndex++].trim();
                    var hasLoop = lineSplit[splitIndex++].indexOf("LOOP") != -1;
                    var directCallCount = parseInt(lineSplit[splitIndex++].trim());
                    var indirectCallCount = parseInt(lineSplit[splitIndex++].trim());
                    var basicBlockCount = parseInt(lineSplit[splitIndex++].trim());
                    var localVarCount = parseInt(lineSplit[splitIndex++].trim());

                    var assertionPropCount = undefined;
                    var cseCount = undefined;
                    var isMinOpts = false;

                    if (lineSplit[splitIndex].trim() == "MinOpts") {
                        isMinOpts = true;
                        ++splitIndex;

                        if (splitIndex != 12) {
                            throw new Error("unhandled");
                        }
                    }
                    else {
                        assertionPropCount = parseInt(lineSplit[splitIndex++].trim());
                        cseCount = parseInt(lineSplit[splitIndex++].trim());

                        if (splitIndex != 13) {
                            throw new Error("unhandled");
                        }
                    }

                    var perfScore = undefined;
                    const perfScoreSize = isMinOpts ? 17 : 18;

                    if (lineSplit.length == perfScoreSize) {
                        perfScore = parseFloat(lineSplit[splitIndex++].trim());
                    }

                    var ilBytes = parseInt(lineSplit[splitIndex++].trim());
                    var hotCodeSize = parseInt(lineSplit[splitIndex++].trim());
                    var coldCodeSize = parseInt(lineSplit[splitIndex++].trim());
                    var totalCodeSize = hotCodeSize + coldCodeSize;

                    var name = lineSplit[splitIndex++].trim();

                    methods.push(new Method(methodDesc, isMinOpts, region, hash, hashEh, frame, hasLoop, directCallCount, indirectCallCount, basicBlockCount, localVarCount, assertionPropCount, cseCount, perfScore, ilBytes, hotCodeSize, coldCodeSize, totalCodeSize, name));
                }
            }
            catch (error: any) {
                console.log(`Failed to parse line: ${index}`);
            }
        }

        var sortedMethods = new Map<string, Method[]>([
            ["system", []],
            ["user", []]
        ]);

        for (var index = 0; index < methods.length; ++index) {
            const currentMethod = methods[index];

            if (currentMethod.name.indexOf("System.") == -1 && 
                currentMethod.name.indexOf("ILStubClass") == -1 &&
                currentMethod.name.indexOf(".cctor") == -1 &&
                currentMethod.name.indexOf("<>c:.ctor") == -1 && 
                currentMethod.name.indexOf("Microsoft.Win32.") == -1 && 
                currentMethod.name.indexOf("Sys:") == -1 && 
                currentMethod.name.indexOf("PrepareMethodinator:") == -1 &&
                currentMethod.name.indexOf("c:<Canonicalize>") == -1 &&
                currentMethod.name.indexOf("PMI") == -1 &&
                currentMethod.name.indexOf("<ReadBufferAsync>") == -1 &&
                currentMethod.name.indexOf("UnixConsoleStream") == -1 && 
                currentMethod.name.indexOf("PrepareAll") == -1) {
                var userList = sortedMethods.get("user");

                assert(userList != undefined);
                userList.push(currentMethod);
            }
            else {
                var systemList = sortedMethods.get("system");
                
                assert(systemList != undefined);
                systemList.push(currentMethod);
            }
        }

        return sortedMethods;
    }
}