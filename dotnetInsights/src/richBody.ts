import * as assert from "assert";

class UnprocessedToken {
    literal: boolean;
    stringLiteral: boolean;
    keyword: boolean;

    value: string;

    constructor(value: string) {
        this.literal = false;
        this.stringLiteral = false;
        this.keyword = false;

        this.value = value;
    }
}

export class RichBody {
    private readonly lines: string[];

    private root: Node;

    keywords: Set<string>;

    constructor(lines: string[], keywords: Set<string>) {
        this.lines = lines;
        this.root = new Node("body");

        this.keywords = keywords;

        this.create();
    }
    
    public create() {
        var delims = new Map();
        delims.set("(", true);
        delims.set(")", true);
        delims.set("[", true);
        delims.set("]", true);
        delims.set("\\", true);
        delims.set("/", true);
        delims.set(" ", true);
        
        for (var lineIndex = 0; lineIndex < this.lines.length; ++lineIndex) {
            const line = this.lines[lineIndex];
            var lineNode = new Node("div");

            if (line.length == 0) {
                // Add this as a child to the root node.
                this.root.children.push(lineNode);

                continue;
            }
            
            var tokens = this.splitOutComments(line);

            // If the whole line is a comment then we do not need to do more processing.
            if (tokens[0] == '') {
                assert(tokens.length == 2);

                // Add the node to the line
                lineNode.children.push(new Node("span", "//" + tokens[1], "comment"));
            }
            else {
                // There was a comment this will become the last node
                var commentNode: Node | undefined;
                if (tokens.length > 1) {
                    commentNode = new Node("span", "//" + tokens[tokens.length - 1], "comment");
                }

                // The first token will always be the rest of the line to parse
                assert (tokens.length <= 2);
                var lineToProcess = tokens[0];

                tokens = this.splitWithDelim(lineToProcess, delims);

                var containedInStringLiteral = [] as string[];
                for (var index = 0; index < tokens.length; ++index) {
                    var token = tokens[index];
                    const firstChar = token[0];
                    const lastChar = token[token.length - 1];

                    if (containedInStringLiteral.length > 0) {
                        if (lastChar == "\"" || lastChar == "'") {
                            const stringLiteral = containedInStringLiteral.join("");

                            lineNode.children.push(new Node("span", stringLiteral, "stringLiteral"));
                            continue;
                        }
                        else {
                            containedInStringLiteral.push(token);
                            continue;
                        }
                    }
                    else if (firstChar == "\"" || firstChar == "'") {
                        containedInStringLiteral.push(token);
                        continue;
                    }

                    if (delims.has(token)) {
                        var newItem = [] as string[];
                        newItem.push(token);

                        while (index + 1 < tokens.length) {
                            token = tokens[index + 1];

                            if (!delims.has(token)) {
                                break;
                            }

                            newItem.push(token);
                            ++index;
                        }

                        continue;
                    }

                    // Check if this is a number
                    if (this.isInt(token)) {
                        lineNode.children.push(new Node("span", token, "number"));
                    }
                    else if (this.isFloat(token)) {
                        lineNode.children.push(new Node("span", token, "number"));
                    }
                    else if (this.keywords.has(token)) {
                        lineNode.children.push(new Node("span", token, "keyword"));
                    }
                    else {
                        lineNode.children.push(new Node("span", token));
                    }
                }

                if (commentNode != undefined) {
                    lineNode.children.push(commentNode);
                }
            }

            // Add this as a child to the root node.
            this.root.children.push(lineNode);
        }
    }

    public getHtml(): string {
        var returnHtml = this.root.toHtml();
        return returnHtml;
    }

    private isFloat(value: string) {
        var floatRegex = /^-?\d+(?:[.,]\d*?)?$/;
        if (!floatRegex.test(value)) {
            return false;
        }
    
        const floatValue = parseFloat(value);
        if (isNaN(floatValue)) {
            return false;
        }

        return true;
    }
    
    private isInt(value: string) {
        var intRegex = /^-?\d+$/;
        if (!intRegex.test(value))
            return false;
    
        const intVal = parseInt(value, 10);
        
        return parseFloat(value) == intVal && !isNaN(intVal);
    }

    private splitWithDelim(value: string, delims: Map<string, boolean>) {
        var splitValue = [] as string[];

        var beginIndex = 0;

        for (var index = 0; index < value.length; ++index) {
            const currentChar = value[index];

            if (delims.has(currentChar)) {
                if (beginIndex != index) {
                    splitValue.push(value.substring(beginIndex, index));
                }

                splitValue.push(value.substring(index, index + 1));
                beginIndex = index + 1;
            }
        }

        if (beginIndex != value.length) {
            splitValue.push(value.substring(beginIndex, value.length));
        }

        return splitValue;
    }

    private splitOutComments(line: string) {
        var tokens = [] as string[];

        var foundComment = false;

        tokens = line.split("//");
        return tokens;
    }

    private splitOutStringLiterals(line: string) {
        var tokens = [] as string[];

        var foundSingle = false;
        var foundDouble = false;

        var beginIndex = 0;

        for (var index = 0; index < line.length; ++index) {
            if (line[index] == '\'' && !foundDouble) {
                if (foundSingle) {
                    tokens.push(line.substring(beginIndex, index + 1));
                    foundSingle = false;

                    beginIndex = index + 1;
                }
                else {
                    tokens.push(line.substring(beginIndex, index));

                    beginIndex = index;
                    foundSingle = true;
                }
            }
            else if (line[index] == '\"' && !foundSingle) {
                if (foundDouble) {
                    tokens.push(line.substring(beginIndex, index + 1));
                    foundDouble = false;

                    beginIndex = index + 1;
                }
                else {
                    tokens.push(line.substring(beginIndex, index));

                    beginIndex = index;
                    foundSingle = true;
                }
            }
        }

        if (beginIndex != line.length) {
            tokens.push(line.substring(beginIndex, line.length));
        }

        return tokens
    }

}

class Node {
    children: Node[];
    type: string;
    value?: string;
    className?: string;

    constructor(type: string, value?: string, className?: string) {
        this.children = [] as Node[];
        this.type = type;
        this.value = value;
        this.className = className;
    }

    public toHtml(): string {
        var value = "";

        if (this.children.length > 0) {
            assert(this.value == undefined);

            var childHtmls = [] as string[];
            for (var index = 0; index < this.children.length; ++index) {
                const node = this.children[index];
                childHtmls.push(node.toHtml());
            }

            var innerHtml = childHtmls.join(" ");
            var newLine = this.type == "body" ? "\n" : "";
            var indent = this.type != "body" ? "    " : "";

            value = `${indent}<${this.type}>${newLine}${innerHtml}</${this.type}>\n`
        }
        else {
            // This is a leaf node.
            var innerHtml = this.value == undefined ? "" : this.value;
            var className = this.className == undefined ? "" : ` class="${this.className}"`;

            var newLine = this.type == "div" ? "\n" : "";
            var indent = this.type != "body" ? "    " : "";

            value = `${indent}<${this.type}${className}>${innerHtml}</${this.type}>${newLine}`

            return value;
        }

        return value;
    }
}