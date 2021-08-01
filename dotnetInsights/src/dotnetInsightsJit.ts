////////////////////////////////////////////////////////////////////////////////
// Module: dotnetInsightsJit.ts
//
// Notes:
//
// Tree view for JIT Events published out of process.
////////////////////////////////////////////////////////////////////////////////

import * as vscode from 'vscode';
import * as path from 'path';

import { GcListener } from "./GcListener";

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

export class DotnetInsightsJitTreeDataProvider implements vscode.TreeDataProvider<JitDependency> {
    private _onDidChangeTreeData: vscode.EventEmitter<JitDependency | undefined | void> = new vscode.EventEmitter<JitDependency | undefined | void>();
    readonly onDidChangeTreeData: vscode.Event<JitDependency | undefined | void> = this._onDidChangeTreeData.event;

    public listener: GcListener;

    constructor(listener: GcListener) {
        this.listener = listener;
    }

    getTreeItem(element: JitDependency): vscode.TreeItem {
        return element;
    }

    refresh(): void {
        this._onDidChangeTreeData.fire();
    }

    getChildren(element?: JitDependency): Thenable<JitDependency[]> {
        if (this.listener?.processes?.size === 0) {
            return Promise.resolve([]);
        }
        else if (this.listener != undefined && this.listener.processes != undefined) {
            var keys = Array.from(this.listener.processes.keys());

            // Get the top level items
            if (element == undefined) {
                keys.sort((lhs: any, rhs: any) => {
                    return lhs - rhs;
                });

                var deps = [] as JitDependency[];
                for (var index = 0; index < keys.length; ++index)
                {
                    var processInfo = this.listener.processes.get(keys[index])!;
                    var jitMethodIds = Array.from(processInfo.jitData.keys());
                    const label = `${keys[index].toString()} - ${this.listener.processes.get(keys[index])?.processName}`;
                    const jitDep = new JitDependency(label, vscode.TreeItemCollapsibleState.Collapsed, undefined, this.listener, keys[index].toString(), jitMethodIds.length);

                    deps.push(jitDep);
                }

                return Promise.resolve(deps);
            }
            else {
                const value: number = parseInt(element.label.split(" - ")[0]);
                var deps = [] as JitDependency[];

                // Check this is a process by attempting to parse and lookup the
                // pid
                if (this.listener.processes.get(value) != undefined) {
                    try {
                        // Show all of the methods jitted for this process
                        var processInfo = this.listener.processes.get(value)!;

                        var jitMethodIds = Array.from(processInfo.jitData.keys());
                        for (var index = 0; index < jitMethodIds.length; ++index) {
                            const jitMethodId = jitMethodIds[index];

                            const jittedMethodCalls = processInfo.jitData.get(jitMethodId);
                            const firstJitInstance = jittedMethodCalls![0];

                            const methodName = firstJitInstance.methodName;
                            const totalLatencyFromJit = processInfo.jitDurationTotal.get(jitMethodId);

                            const label = `${jitMethodId} - ` + methodName;
                            const jitDep = new JitDependency(label, vscode.TreeItemCollapsibleState.Collapsed, undefined, this.listener, element.pid, totalLatencyFromJit, `${totalLatencyFromJit} ms`);

                            deps.push(jitDep);
                        }
                    }
                    catch (e) {
                        console.log("hello");
                    }
                    
                }
                // This has to be instances of each individual jit call
                else {
                    var processInfo = this.listener.processes.get(parseInt(element.pid!))!;

                    var jitMethodId = value;
                    const jittedMethodCalls = processInfo.jitData.get(jitMethodId);

                    for (var index = 0; index < jittedMethodCalls!.length; ++index) {
                        const tier = jittedMethodCalls![index].tier;
                        const duration = jittedMethodCalls![index].jitDuration;

                        var tierName = undefined;

                        // Unknown = 0,
                        // MinOptJitted = 1,
                        // Optimized = 2,
                        // QuickJitted = 3,
                        // OptimizedTier1 = 4,
                        // ReadyToRun = 5,
                        // PreJIT = 255

                        if (tier == 0) {
                            tierName = "Undefined";
                        }
                        else if (tier == 1) {
                            tierName = "MinOptJitted";
                        }
                        else if (tier == 2) {
                            tierName = "Optimized";
                        }
                        else if (tier == 3) {
                            tierName = "QuickJitted";
                        }
                        else if (tier == 4) {
                            tierName = "OptimizedTier1";
                        }
                        else if (tier == 5) {
                            tierName = "ReadyToRun";
                        }
                        else {
                            tierName = "PreJIT";
                        }

                        const label = `${tierName}`;
                        deps.push(new JitDependency(label, vscode.TreeItemCollapsibleState.None, undefined, this.listener, element.pid, duration, `${duration} ms`));
                    }
                }

                deps.sort((lhs: JitDependency, rhs: JitDependency) => {
                    return rhs.numValue! - lhs.numValue!;
                });

                return Promise.resolve(deps);

            }
        }
        else {
            return Promise.resolve([]);
        }
    }

}

export class JitDependency extends vscode.TreeItem {

    constructor(
        public readonly label: string,
        public readonly collapsibleState: vscode.TreeItemCollapsibleState,
        public command?: vscode.Command,
        public listener? : GcListener,
        public pid?: string,
        public numValue?: number,
        public description?: string
    ) {
        super(label, collapsibleState);
        const processInfo = listener?.processes.get(parseInt(pid!));
        var jitMethodIds = Array.from(processInfo!.jitData.keys());

        this.tooltip = `${this.label}`;

        this.numValue = numValue;

        if (description == undefined) {
            this.description = `Jitted Methods: ${jitMethodIds.length}`;
        }
        else {
            this.description = description;
        }
    }

    iconPath = {
        light: path.join(__filename, '..', '..', 'resources', 'light', 'dependency.svg'),
        dark: path.join(__filename, '..', '..', 'resources', 'dark', 'dependency.svg')
    };

    contextValue = 'dependency';
}