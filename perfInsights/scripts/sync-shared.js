////////////////////////////////////////////////////////////////////////////////
// Module: sync-shared.js
//
// Notes:
// Copies the webview code this extension SHARES with dotnetInsights into
// generated directories here, immediately before every compile.
//
// Both extensions render the same JSON: perfParser produces the same
// `cpuProfile` object nettraceParser does, through the same exporter, so the
// same renderer and the same client scripts display it. Copying rather than
// duplicating keeps one source of truth - the copies are gitignored and
// regenerated on every build, so an edit made here is lost on the next
// compile, which is the intended signal that dotnetInsights is where these
// files live.
//
// This is the interim arrangement, not the end state. The end state is an
// insightsShared package both extensions depend on. It is deliberately NOT
// done yet: promoting these files means moving them out of dotnetInsights and
// rewriting its imports, which is a change to a shipping extension made for
// the benefit of one that does not exist yet. Copying proves the sharing works
// first, and costs nothing to undo.
////////////////////////////////////////////////////////////////////////////////

const fs = require("fs");
const path = require("path");

const sourceExtensionRoot = path.resolve(__dirname, "..", "..", "dotnetInsights");
const targetExtensionRoot = path.resolve(__dirname, "..");

// Renderer modules. CpuProfileRenderer's renderCpuProfileView takes the
// cpuProfile object and nothing else - it has no knowledge of where the
// capture came from - which is what makes it reusable verbatim.
const sharedSourceFiles = [
    "CpuProfileRenderer.ts",
    "ContentionRenderer.ts",
    "GcDetailTableRenderer.ts"
];

// Client-side assets the rendered HTML references.
const sharedMediaFiles = [
    "snapshot.css",
    "rankedTable.js",
    "cpuDrillDownStats.js",
    "contentionDrillDownStats.js"
];

function copyInto(relativeSourceDir, files, relativeTargetDir) {
    const targetDir = path.join(targetExtensionRoot, relativeTargetDir);
    fs.mkdirSync(targetDir, { recursive: true });

    for (const fileName of files) {
        const sourcePath = path.join(sourceExtensionRoot, relativeSourceDir, fileName);
        if (!fs.existsSync(sourcePath)) {
            throw new Error("sync-shared: missing shared file " + sourcePath);
        }

        fs.copyFileSync(sourcePath, path.join(targetDir, fileName));
    }

    return files.length;
}

const sourceCount = copyInto("src", sharedSourceFiles, path.join("src", "shared"));
const mediaCount = copyInto("media", sharedMediaFiles, path.join("media", "shared"));

console.log("sync-shared: copied " + sourceCount + " renderer modules and " + mediaCount + " media files from dotnetInsights");
