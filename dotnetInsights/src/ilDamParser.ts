import * as os from "os";

import { DotnetInsights } from "./dotnetInsights";

export class ILDasmParser {
    ////////////////////////////////////////////////////////////////////////////
    // Member variables
    ////////////////////////////////////////////////////////////////////////////

    public fieldMap: Map<string, number>;
    public methodMap: Map<string, number>;
    public typeMap: Map<string, number>;

    private ildasmOutput: string;

    ////////////////////////////////////////////////////////////////////////////
    // Constructor
    ////////////////////////////////////////////////////////////////////////////

    constructor(ilDasmOutput: string) {
        this.ildasmOutput = ilDasmOutput;

        this.fieldMap = new Map<string, number>();
        this.methodMap = new Map<string, number>();
        this.typeMap = new Map<string, number>();
    }

    ////////////////////////////////////////////////////////////////////////////
    // Member methods
    ////////////////////////////////////////////////////////////////////////////

    public parse() {
        var eolChar = "\n";
        if (os.platform() == "win32") {
            eolChar = "\r\n";
        }
    
        const lines = this.ildasmOutput.split(eolChar);
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
    
                this.typeMap.set(className, index);
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
    
                this.fieldMap.set(className, index);
            }
            else if (currentLine.indexOf(".method") != -1) {
                // Split out the class name

                // Try the current line.
                var methodLine = lines[index];
                var methodNameSplit = methodLine.split(" ");

                if (methodNameSplit[methodNameSplit.length - 1] != "managed") {
                    methodLine = lines[index + 1];
                    methodNameSplit = methodLine.split(" ");
                }
    
                var methodName = "";
                for (var innerIndex = 0; innerIndex < methodNameSplit.length; ++innerIndex) {
                    if (methodNameSplit[innerIndex].indexOf("\(") != -1) {
                        methodName = methodNameSplit[innerIndex];
                        break;
                    }
                }

                methodName = methodName.split("\(")[0];
                this.methodMap.set(methodName, index);
            }
        }
    }
}