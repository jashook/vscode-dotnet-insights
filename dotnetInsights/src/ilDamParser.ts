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
        if (os.platform() === "win32") {
            eolChar = "\r\n";
        }
    
        const lines = this.ildasmOutput.split(eolChar);
        for (var index = 0; index < lines.length; ++index) {
            const currentLine = lines[index];
    
            if (currentLine.indexOf(".class") !== -1) {
                // Split out the class name
                const classNameSplit = currentLine.split(" ");
    
                var className = classNameSplit[classNameSplit.length - 1];
    
                if (className[0] === "'" || className[0] === "\"") {
                    const quoteChar = className[0] === "'" ? "'" : "\"";
    
                    var lastIndex = className[className.length - 1] === quoteChar ? className.length - 1 : className.length;
                    className = className.substring(1, lastIndex);
                }
    
                if (className.indexOf('.') !== -1) {
                    className = className.substring(className.indexOf('.') + 1, className.length);
                }
    
                this.typeMap.set(className, index);
            }
            else if (currentLine.indexOf(".field") !== -1) {
                // Split out the class name
                const classNameSplit = currentLine.split(" ");
    
                var className = classNameSplit[classNameSplit.length - 1];
    
                if (className[0] === "'" || className[0] === "\"") {
                    const quoteChar = className[0] === "'" ? "'" : "\"";
    
                    var lastIndex = className[className.length - 1] === quoteChar ? className.length - 1 : className.length;
                    className = className.substring(1, lastIndex);
                }
    
                this.fieldMap.set(className, index);
            }
            else if (currentLine.indexOf(".method") !== -1) {
                // Split out the class name

                var methodLine = lines[index].trim();
                // Check to see if the next line has the brace
                if (lines[index + 1].indexOf("{") !== 1) {
                    // Method is on this line.
                    
                }
                else {
                    // combine lines removing whitespace until we get to a brace
                    for (var forwardIndex = index + 1; forwardIndex < lines.length; ++forwardIndex) {
                        methodLine += lines[forwardIndex].trim();
                    }
                }

                var methodName = methodLine.split("\(")[0];

                var methodNameSplit = methodName.split(" ");
                methodName = methodNameSplit[methodNameSplit.length - 1];

                console.log(methodName);

                this.methodMap.set(methodName, index);
            }
        }
    }
}