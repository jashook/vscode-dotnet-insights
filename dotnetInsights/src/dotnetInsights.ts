import * as child from 'child_process';
import * as vscode from 'vscode';
import * as fs from 'fs';
import * as os from "os";
import * as path from 'path';
import * as assert from "assert"

import { Method } from "./DotnetInightsTextEditor"
import { type } from 'node:os';

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
            if (element == undefined) {
                // Get the top level items
                var topLevelDeps = [] as Dependency[];

                topLevelDeps.push(new Dependency("Types", undefined, undefined, vscode.TreeItemCollapsibleState.Collapsed));
                topLevelDeps.push(new Dependency("Methods", undefined, undefined, vscode.TreeItemCollapsibleState.Collapsed));

                return Promise.resolve(topLevelDeps);
            }
            else if (element.label == "Methods") {
                var dependencies = [] as Dependency[];

                assert(this.insights != undefined);
                assert(this.insights?.methods != undefined);

                const userMethods = this.insights?.methods.get("user");
                if (userMethods == undefined) {
                    return Promise.resolve(dependencies);
                }

                for (var index = 0; index < userMethods?.length; ++index) {
                    const currentMethod = userMethods[index];
                    dependencies.push(new Dependency(currentMethod.name, currentMethod.ilBytes, currentMethod.totalCodeSize, vscode.TreeItemCollapsibleState.None));
                }

                return Promise.resolve(dependencies);
            }
            else if (element.label == "Types") {
                var dependencies = [] as Dependency[];

                assert(this.insights != undefined);
                assert(this.insights?.types != undefined);

                for (var index = 0; index < this.insights.types.length; ++index) {
                    const currentType = this.insights.types[index];

                    dependencies.push(new Dependency(currentType.name, undefined, currentType.size, vscode.TreeItemCollapsibleState.None, undefined, currentType.typeName));
                }
                
                return Promise.resolve(dependencies);
            }
            else {
                return Promise.resolve([]);
            }
        }
        else {
            return Promise.resolve([]);
        }
    }

}

export class Dependency extends vscode.TreeItem {

    constructor(
        public readonly label: string,
        private readonly ilBytes: number | undefined,
        private readonly bytes: number | undefined,
        public readonly collapsibleState: vscode.TreeItemCollapsibleState,
        public readonly command?: vscode.Command,
        public readonly typeName? : string
    ) {
        super(label, collapsibleState);

        this.tooltip = `${this.label}`;

        if (typeName != undefined) {
            this.description = `size: ${this.bytes} type: ${this.typeName}`;
        }
        else {
            if (this.ilBytes == undefined || this.bytes == undefined) {
                this.description = "";
            }
            else {
                this.description = `ilBytes: ${this.ilBytes.toString()} codeSize: ${this.bytes.toString()}`;
            }
        }
    }

    iconPath = {
        light: path.join(__filename, '..', '..', 'resources', 'light', 'dependency.svg'),
        dark: path.join(__filename, '..', '..', 'resources', 'dark', 'dependency.svg')
    };

    contextValue = 'dependency';
}

class Type {
    public name: string;
    public baseType: string;
    public typeName: string;
    public size: number;
    public nestedTypeCount: number;
    public nestedType: Type[];

    constructor(name: string, baseType: string, typeName: string, size: number, nestedTypeCount: number) {
        this.name = name;
        this.baseType = baseType;
        this.typeName = typeName;
        this.size = size;
        this.nestedTypeCount = nestedTypeCount;
        this.nestedType = [] as Type[];
    }

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
    public types: Type[] | undefined;

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
        this.types = undefined;

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

    public updateForPath(path: string, ilDasmOutput: string) {
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

        pmiCommand = this.coreRunPath + " " + this.pmiPath + " " + "DRIVEALL-QUIET-DUMPTYPES" + " " + path;
        var typeChildProcess = child.exec(pmiCommand, {
            maxBuffer: maxBufferSize,
            "cwd": cwd
        }, (error: any, output: string, stderr: string) => {
            if (error) {
                return;
            }

            var types = this.parseTypes(output, endofLine);
            this.types = types;
        });
    }

    private parseTypes(output: string, endofLine: vscode.EndOfLine) {
        var eolChar = "\n";
        if (endofLine == vscode.EndOfLine.CRLF) {
            eolChar = "\r\n";
        }

        var types = [] as Type[];

        // [<<Main>g__HelloWorldSync|0>d (class)]: [RuntimeType] Size: 20, nested types: 5
        // - CHILD [<>1__state (struct)]: [System.Int32, System.Private.CoreLib, Version=5.0.0.0, Culture=neutral, PublicKeyToken=7cec85d7bea7798e] Size: 4, nested types: 0
        // - CHILD [<>t__builder (struct)]: [System.Runtime.CompilerServices.AsyncTaskMethodBuilder, System.Private.CoreLib, Version=5.0.0.0, Culture=neutral, PublicKeyToken=7cec85d7bea7798e] Size: 0, nested types: 0
        // - CHILD [<>4__this (class)]: [hello_world.Program+<>c__DisplayClass0_0, hello-world, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null] Size: 8, nested types: 0
        // - CHILD [<client>5__1 (class)]: [System.Net.Http.HttpClient, System.Net.Http, Version=5.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a] Size: 8, nested types: 0
        // - CHILD [<>u__1 (struct)]: [System.Runtime.CompilerServices.TaskAwaiter, System.Private.CoreLib, Version=5.0.0.0, Culture=neutral, PublicKeyToken=7cec85d7bea7798e] Size: 0, nested types: 0

        var lines = output.split(eolChar);

        var currentType: Type | undefined = undefined;
        for (var index = 0; index < lines.length; ++index) {
            var regex = /\[(.*) \((.*)\)\]: \[(.*)\] Size: (.*), nested types: (.*)/g;
            const currentLine = lines[index];

            if (currentLine.length == 0) {
                continue;
            }

            if (currentLine.indexOf("- CHILD") == -1 && currentLine.indexOf("Completed assembly ") == -1) {
                var splitLine = regex.exec(currentLine);

                if (splitLine?.length != 6) {
                    throw new Error("Unable to parse type");
                }

                var name = splitLine[1];
                var baseType = splitLine[2];
                var typeName = splitLine[3];
                var size = parseInt(splitLine[4]);
                var nestedTypeCount = parseInt(splitLine[5]);

                if (currentType != undefined) {
                    assert(currentType.nestedTypeCount == currentType.nestedType.length);
                    types.push(currentType);
                }

                currentType = new Type(name, baseType, typeName, size, nestedTypeCount);
            }
            else if (currentLine.indexOf("- CHILD") != -1) {
                var startTypeLine = currentLine.indexOf("- CHILD ");

                var splitLine = regex.exec(currentLine.substring(startTypeLine, currentLine.length));

                if (splitLine?.length != 6) {
                    throw new Error("Unable to parse type");
                }

                var name = splitLine[1];
                var baseType = splitLine[2];
                var typeName = splitLine[3];
                var size = parseInt(splitLine[4]);
                var nestedTypeCount = parseInt(splitLine[5]);

                var nestedType = new Type(name, baseType, typeName, size, nestedTypeCount);
                currentType?.nestedType.push(nestedType);
            }
            else {
                continue;
            }
        }

        if (currentType != undefined) {
            types.push(currentType);
        }

        return types;
    }

    private parseJitOrderOutput(jitOrder: string, eol: vscode.EndOfLine) {
        var eolChar = "\n";
        if (eol == vscode.EndOfLine.CRLF) {
            eolChar = "\r\n";
        }

        var methods = [] as Method[];

        var lines = jitOrder.split(eolChar);
        
        for (var index = 0; index < lines.length; ++index) {
            var currentLine = lines[index];
            if (currentLine.length == 0 ||
                currentLine.indexOf("-------") != -1 ||
                currentLine.indexOf("Method has") != -1 ||
                currentLine.indexOf("method name") != -1 ) {
                continue;
            }

            if (currentLine.indexOf("Completed assembly jit-dasm ") != -1) {
                const newCurrentLine = currentLine.split("skipped methods: ")[1];

                const firstValue = newCurrentLine.split("| ")[0].trim();
                const newValue = firstValue.substring(firstValue.length - 8, firstValue.length);

                const changeValue = newCurrentLine.substring(newCurrentLine.indexOf(newValue), newCurrentLine.length);
                currentLine = changeValue;
            }

            var lineSplit = currentLine.split("| ");

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
                currentMethod.name.indexOf("PrepareAll") == -1 &&
                currentMethod.name.indexOf("WindowsConsoleStream") == -1) {
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