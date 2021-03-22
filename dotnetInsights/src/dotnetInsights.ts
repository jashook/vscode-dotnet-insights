import * as child from 'child_process';
import * as vscode from 'vscode';
import * as fs from 'fs';
import * as os from "os";
import * as path from 'path';
import * as assert from "assert";

import { Method } from "./DotnetInightsTextEditor";

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
        else if (this.insights != undefined && this.insights.methods != undefined && this.insights.ilAsmVsCodePath != undefined && this.insights.dllPath != undefined && this.insights.methods.size > 0) {
            const dllPath = this.insights.dllPath;
            if (element == undefined) {
                // Get the top level items
                var topLevelDeps = [] as Dependency[];

                const ilAsmVsCodePath = this.insights.ilAsmVsCodePath;
                assert(ilAsmVsCodePath != undefined);

                topLevelDeps.push(new Dependency("Types", false, false, ilAsmVsCodePath, dllPath, undefined, undefined, vscode.TreeItemCollapsibleState.Collapsed));
                topLevelDeps.push(new Dependency("Methods", false, false, ilAsmVsCodePath, dllPath, undefined, undefined, vscode.TreeItemCollapsibleState.Collapsed));

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

                const ilAsmVsCodePath = this.insights.ilAsmVsCodePath;
                assert(ilAsmVsCodePath != undefined);

                for (var index = 0; index < userMethods?.length; ++index) {
                    const currentMethod = userMethods[index];
                    var dep = new Dependency(currentMethod.name, true, false, ilAsmVsCodePath, dllPath, currentMethod.ilBytes, currentMethod.totalCodeSize, vscode.TreeItemCollapsibleState.None);

                    dep.command = {
                        command: "dotnetInsights.tier1",
                        title: "View DASM",
                        arguments: [dep, this.insights]
                    }

                    dependencies.push(dep);
                }

                return Promise.resolve(dependencies);
            }
            else if (element.label == "Types") {
                var dependencies = [] as Dependency[];

                assert(this.insights != undefined);
                assert(this.insights?.types != undefined);

                const ilAsmVsCodePath = this.insights.ilAsmVsCodePath;
                assert(ilAsmVsCodePath != undefined);

                for (var index = 0; index < this.insights.types.length; ++index) {
                    const currentType = this.insights.types[index];

                    const collapsedState = currentType.nestedType.length == 0 ? vscode.TreeItemCollapsibleState.None : vscode.TreeItemCollapsibleState.Collapsed;

                    var dep = new Dependency(currentType.name, false, true, ilAsmVsCodePath, dllPath, undefined, currentType.size, collapsedState, undefined, currentType.typeName, currentType);

                    if (collapsedState == vscode.TreeItemCollapsibleState.None) {
                        // We can add a command this has to be a type.

                        if (this.insights.typeMap != undefined) {
                            if (this.insights.typeMap.get(currentType.name) != undefined) {
                                dep.command = {
                                    command: "dotnetInsights.selectNode",
                                    title: "View Type",
                                    arguments: [dep]
                                }

                                dep.lineNumber = this.insights.typeMap.get(currentType.name);
                            }
                        }
                        
                    }

                    dependencies.push(dep);
                }
                
                return Promise.resolve(dependencies);
            }
            else if (element.type != undefined) {
                var dependencies = [] as Dependency[];

                for (var index = 0; index < element.type.nestedType.length; ++index) {
                    const currentType = element.type.nestedType[index];

                    const ilAsmVsCodePath = this.insights.ilAsmVsCodePath;
                    const dllPath = this.insights.dllPath;
                    assert(ilAsmVsCodePath != undefined);

                    const isType = element.isType;

                    const collapsedState = currentType.nestedType.length == 0 ? vscode.TreeItemCollapsibleState.None : vscode.TreeItemCollapsibleState.Collapsed;
                    const dep = new Dependency(currentType.name, !isType, isType, ilAsmVsCodePath, dllPath, undefined, currentType.size, collapsedState, undefined, currentType.typeName, currentType);
        
                    if (collapsedState == vscode.TreeItemCollapsibleState.None) {
                        // We can add a command

                        if (this.insights.fieldMap != undefined) {
                            if (this.insights.fieldMap.get(currentType.name) != undefined) {
                                dep.command = {
                                    command: "dotnetInsights.selectNode",
                                    title: "View Type",
                                    arguments: [dep]
                                }

                                dep.lineNumber = this.insights.fieldMap.get(currentType.name);
                            }
                        }
                    }

                    dependencies.push(dep);
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
        public readonly isMethod: boolean,
        public readonly isType: boolean,
        public readonly fsPath: string,
        public readonly dllPath: string,
        private readonly ilBytes: number | undefined,
        private readonly bytes: number | undefined,
        public readonly collapsibleState: vscode.TreeItemCollapsibleState,
        public command?: vscode.Command,
        public readonly typeName? : string,
        public readonly type? : Type,
        public lineNumber?: number
    ) {
        super(label, collapsibleState);

        this.tooltip = `${this.label}`;

        if (typeName != undefined) {
            this.description = `size: ${this.bytes} type: ${this.typeName}`;
            this.type = type;
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

    public netcoreThreePmiPath : string;
    public netcoreFivePmiPath : string;

    public pmiPath: string;

    public netcoreThreeCoreRootPath: string;
    public netcoreFiveCoreRootPath: string;

    public customCoreRootPath: string;

    public netcoreThreeCoreRunPath: string;
    public netcoreFiveCoreRunPath: string;

    public customCoreRunPath: string

    public coreRunPath: string;

    public useNetCoreThree: boolean;

    public sdkVersions: string[];

    public ilDasmOutputPath: string;
    public pmiOutputPath: string;

    public pmiTempDir: string;

    public useIldasm: boolean;
    public usePmi: boolean

    public methods: Map<string, Method[]> | undefined;
    public types: Type[] | undefined;

    public ilAsmVsCodePath: string | undefined;
    public dllPath: string | undefined;

    public typeMap: Map<string, number> | undefined;
    public fieldMap: Map<string, number> | undefined;

    public outputChannel: vscode.OutputChannel;

    public treeView: DotnetInsightsTreeDataProvider | undefined;

    constructor(outputChannel: vscode.OutputChannel) {
        this.ilDasmPath = "";
        this.ilDasmVersion = "";

        this.outputChannel = outputChannel;

        this.netcoreFivePmiPath = "";
        this.netcoreThreePmiPath = "";

        this.pmiPath = "";

        this.useNetCoreThree = false;

        this.netcoreThreeCoreRootPath = "";
        this.netcoreFiveCoreRootPath = "";

        this.customCoreRootPath = "";

        this.netcoreThreeCoreRunPath = "";
        this.netcoreFiveCoreRunPath = "";

        this.customCoreRunPath = "";

        this.coreRunPath = "";

        this.ilDasmOutputPath = "";
        this.pmiOutputPath = "";

        this.pmiTempDir = "";

        this.useIldasm = true;
        this.usePmi = false;

        this.treeView = undefined;
        this.methods = undefined;
        this.types = undefined;

        this.typeMap = undefined;
        this.fieldMap = undefined;

        this.ilAsmVsCodePath = undefined;
        this.dllPath = undefined;

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

    public updateForPath(ilAsmPath: string, fsPath: string, ilDasmOutput: string) {
        var pmiCommand = `"${this.coreRunPath}"` + " " + `"${this.pmiPath}"` + " " + "PREPALL-QUIET" + " " + `"${fsPath}"`;
        this.outputChannel.appendLine(pmiCommand);

        var mb = 1024 * 1024;
        var maxBufferSize = 512 * mb;
        
        const jitOrderCwd = path.join(this.pmiOutputPath, "jitOrder");
        const typeCwd = path.join(this.pmiOutputPath, "types");

        if  (!fs.existsSync(jitOrderCwd)) {
            fs.mkdirSync(jitOrderCwd);
        }

        if (!fs.existsSync(typeCwd)) {
            fs.mkdirSync(typeCwd);
        }

        // Used by pmi as it need FS access
        const cwd: string =  this.pmiTempDir;
        const endofLine = os.platform() == "win32" ? vscode.EndOfLine.CRLF : vscode.EndOfLine.LF;

        var methodPromise = undefined;
        var typePromise = undefined;
        
        var childProcess = child.exec(pmiCommand, {
            maxBuffer: maxBufferSize,
            "cwd": jitOrderCwd,
            "env": {
                "COMPlus_JitOrder": "1",
                "COMPlus_TieredCompilation": "0"
            }
        }, (error: any, output: string, stderr: string) => {
            if (error) {
                console.error("Failed to execute pmi.");
                console.error(error);
            }

            var methods = this.parseJitOrderOutput(output, endofLine);

            this.methods = methods;
            this.treeView?.refresh();
        });

        pmiCommand = `"${this.coreRunPath}"` + " " + `"${this.pmiPath}"` + " " + "PREPALL-QUIET-DUMPTYPES" + " " + `"${fsPath}"`;
        this.outputChannel.appendLine(pmiCommand);
        var typeChildProcess = child.exec(pmiCommand, {
            maxBuffer: maxBufferSize,
            "cwd": typeCwd
        }, (error: any, output: string, stderr: string) => {
            if (error) {
                console.error("Failed to execute pmi for types.");
                console.error(error);
            }

            var types = this.parseTypes(output, endofLine);
            this.types = types;
            this.treeView?.refresh();
        });

        const maps = this.parseIlDasmOutput(ilDasmOutput, endofLine);
        this.typeMap = maps[0];
        this.fieldMap = maps[1];

        this.ilAsmVsCodePath = ilAsmPath;
        this.dllPath = fsPath;
    }

    private parseIlDasmOutput(output: string, eol: vscode.EndOfLine) {
        var eolChar = "\n";
        if (eol == vscode.EndOfLine.CRLF) {
            eolChar = "\r\n";
        }

        var typeNumbers = new Map<string, number>();
        var fieldNumbers = new Map<string, number>();

        const lines = output.split(eolChar);
        for (var index = 0; index < lines.length; ++index) {
            const currentLine = lines[index];

            if (currentLine.indexOf(".class") != -1) {
                // Split out the class name
                const classNameSplit = currentLine.split(" ")

                var className = classNameSplit[classNameSplit.length - 1];

                if (className[0] == "'" || className[0] == "\"") {
                    const quoteChar = className[0] == "'" ? "'" : "\"";

                    var lastIndex = className[className.length - 1] == quoteChar ? className.length - 1 : className.length;
                    className = className.substring(1, lastIndex);
                }

                if (className.indexOf('.') != -1) {
                    className = className.substring(className.indexOf('.') + 1, className.length);
                }

                typeNumbers.set(className, index);
            }
            else if (currentLine.indexOf(".field") != -1) {
                // Split out the class name
                const classNameSplit = currentLine.split(" ")

                var className = classNameSplit[classNameSplit.length - 1];

                if (className[0] == "'" || className[0] == "\"") {
                    const quoteChar = className[0] == "'" ? "'" : "\"";

                    var lastIndex = className[className.length - 1] == quoteChar ? className.length - 1 : className.length;
                    className = className.substring(1, lastIndex);
                }

                fieldNumbers.set(className, index);
            }
        }

        return [typeNumbers, fieldNumbers];
    }

    private parseTypes(output: string, endofLine: vscode.EndOfLine) {
        var eolChar = "\n";
        if (endofLine == vscode.EndOfLine.CRLF) {
            eolChar = "\r\n";
        }

        output.replace(eolChar, "\n");

        var types = [] as Type[];

        var lines = output.split("\n");

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
                    if(currentType.nestedTypeCount != currentType.nestedType.length) {
                        throw new Error("Error");
                    }
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
                this.outputChannel.appendLine(`Failed to parse line: ${index}`);
            }
        }

        var sortedMethods = new Map<string, Method[]>([
            ["system", []],
            ["user", []]
        ]);

        for (var index = 0; index < methods.length; ++index) {
            const currentMethod = methods[index];
            const methodName = currentMethod.name.split("\(")[0];

            if (methodName.indexOf("System.") == -1 && 
                methodName.indexOf("ILStubClass") == -1 &&
                methodName.indexOf(".cctor") == -1 &&
                methodName.indexOf("<>c:.ctor") == -1 && 
                methodName.indexOf("Microsoft.Win32.") == -1 && 
                methodName.indexOf("Sys:") == -1 && 
                methodName.indexOf("PrepareMethodinator:") == -1 &&
                methodName.indexOf("c:<Canonicalize>") == -1 &&
                methodName.indexOf("PMI") == -1 &&
                methodName.indexOf("<ReadBufferAsync>") == -1 &&
                methodName.indexOf("UnixConsoleStream") == -1 && 
                methodName.indexOf("PrepareAll") == -1 &&
                methodName.indexOf("Worker:") == -1 &&
                methodName.indexOf("PrepareBase:") == -1 &&
                methodName.indexOf("CounterBase:") == -1 &&
                methodName.indexOf("CustomLoadContext:") == -1 &&
                methodName.indexOf("Visitor:") == -1 &&
                methodName.indexOf("Resolver:") == -1 &&
                methodName.indexOf("Util:") == -1 &&
                methodName.indexOf("WindowsConsoleStream") == -1) {
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