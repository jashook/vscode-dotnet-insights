import * as child from 'child_process';
import * as vscode from 'vscode';
import * as fs from 'fs';
import * as os from "os";
import * as path from 'path';

import { GcListener } from "./GcListener";

export class DotnetInsightsGcTreeDataProvider implements vscode.TreeDataProvider<GcDependency> {
    private _onDidChangeTreeData: vscode.EventEmitter<GcDependency | undefined | void> = new vscode.EventEmitter<GcDependency | undefined | void>();
    readonly onDidChangeTreeData: vscode.Event<GcDependency | undefined | void> = this._onDidChangeTreeData.event;

    public listener: GcListener;

    constructor(listener: GcListener) {
        this.listener = listener;
    }

    getTreeItem(element: GcDependency): vscode.TreeItem {
        return element;
    }

    refresh(): void {
        this._onDidChangeTreeData.fire();
    }

    getChildren(element?: GcDependency): Thenable<GcDependency[]> {
        if (this.listener?.processes?.size === 0) {
            return Promise.resolve([]);
        }
        else if (this.listener !== undefined && this.listener.processes !== undefined) {
            var keys = Array.from(this.listener.processes.keys());

            keys.sort((lhs, rhs) => {
                return lhs - rhs;
            });

            var deps = [] as GcDependency[];
            for (var index = 0; index < keys.length; ++index)
            {
                const label = `${keys[index].toString()} - ${this.listener.processes.get(keys[index])?.processName}`;
                const gcDep = new GcDependency(label, vscode.TreeItemCollapsibleState.None, undefined, this.listener, keys[index].toString());
                gcDep.command = {
                    command: "dotnetInsightsGc.selectPid",
                    title: "View Type",
                    arguments: [gcDep]
                };

                deps.push(gcDep);
            }

            return Promise.resolve(deps);
        }
        else {
            return Promise.resolve([]);
        }
    }

}

export class GcDependency extends vscode.TreeItem {

    constructor(
        public readonly label: string,
        public readonly collapsibleState: vscode.TreeItemCollapsibleState,
        public command?: vscode.Command,
        public listener? : GcListener,
        public pid?: string
    ) {
        super(label, collapsibleState);

        this.tooltip = `${this.label}`;
        this.description = `GC Count: ${this.listener?.processes.get(parseInt(this.label))?.data.length}`;
    }

    iconPath?: string | vscode.IconPath | undefined;
    contextValue = 'dependency';
}