////////////////////////////////////////////////////////////////////////////////
// Module: extension.ts
//
// Notes:
// Entry point for Perf Insights. The extension has exactly one surface - a
// custom editor for `perf.data` captures - so activation registers that and
// nothing else.
////////////////////////////////////////////////////////////////////////////////

import * as vscode from 'vscode';

import { PerfInsightsEditorProvider } from './PerfInsightsEditor';

export function activate(context: vscode.ExtensionContext): void {
    context.subscriptions.push(PerfInsightsEditorProvider.register(context));
}

export function deactivate(): void {
}
