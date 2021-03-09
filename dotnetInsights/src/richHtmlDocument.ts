import { assert } from "node:console";

class Token {
    previousToken?: Token;
    nextToken?: Token;
    value: string;

    constructor(value: string, previousToken?: Token) {
        this.previousToken = previousToken;
        this.nextToken = undefined;
        this.value = value;
    }
}

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

class TokenList {
    head: Token;
    tail: Token;

    unProcessedTokens: Token[];

    constructor(token: string, processed: boolean) {
        this.head = new Token(token);
        this.tail = this.head;

        this.unProcessedTokens = [] as Token[];

        if (!processed) {
            this.unProcessedTokens.push(this.head);
        }
    }

    addToken(token: string, processed: boolean) {
        this.tail.nextToken = new Token(token);
        this.tail.nextToken.previousToken = this.tail;

        this.tail = this.tail.nextToken;
        
        if (processed) {
            this.unProcessedTokens.push(this.tail);
        }
    }

    getUnprocessedTokens() {
        return this.unProcessedTokens;
    }

    popUnprocessedToken(): Token | undefined {
        return this.unProcessedTokens.pop();
    }

    processToken(token: Token, replacements?: UnprocessedToken[]) {
        if (replacements == undefined) {
            // No action
            return;
        }

        else {
            // Splice out this token
            var previousToken = token.previousToken;

            var nextToken = token.nextToken;

            const isHead = this.head == token;

            if (previousToken == undefined) {
                assert(isHead);
                previousToken = this.head;
            }

            // if this is the first token in the list, then we do not need to splice
            if (previousToken != undefined) {
                var movingToken = previousToken;
                for (var index = 0; index < replacements.length; ++index) {
                    movingToken.nextToken = new Token(replacements[index].value, movingToken);

                    if (replacements[index].keyword == false ||
                        replacements[index].literal == false || 
                        replacements[index].stringLiteral == false) {
                        this.unProcessedTokens.push(movingToken.nextToken);
                    }

                    movingToken = movingToken.nextToken;
                }

                movingToken.nextToken = nextToken;

                if (isHead && this.head.nextToken) {
                    this.head = this.head.nextToken;
                }
            }
        }
    }
}

export class RichHtmlDocument {
    private readonly lines: string[];

    private root: Node;

    constructor(lines: string[]) {
        this.lines = lines;
        this.root = new Node("div");

        this.create();
    }
    
    private create() {
        this.lines.forEach((line) => {
            var lineNode = new Node("div");
            
            var tokens = this.splitOutComments(line);

            var processed: boolean = tokens[0][0] == "\"" || tokens[0][0] == "\'";

            var tokenList = new TokenList(tokens[0], processed);
            for (var tokenIndex = 1; tokenIndex < tokens.length; ++tokenIndex) {
                processed = tokens[tokenIndex][0] == "\"" || tokens[tokenIndex][0] == "\'";
                tokenList.addToken(tokens[tokenIndex], processed);
            }

            while (tokenList.getUnprocessedTokens().length > 0) {
                var nextUnprocessedToken = tokenList.getUnprocessedTokens()[0];

                var replacements = this.process(nextUnprocessedToken);
                tokenList.processToken(nextUnprocessedToken, replacements);
            }

            // Add this as a child to the root node.
            this.root.children.push(lineNode);
        });
    }

    private process(token: Token): UnprocessedToken[] {
        return [];
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

    constructor(type: string, value?: string) {
        this.children = [] as Node[];
        this.type = type;
        this.value = value;
    }
}