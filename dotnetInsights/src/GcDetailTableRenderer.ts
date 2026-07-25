// DateTime is a real calendar date/time (.nettrace source, in the parsing
// machine's local timezone - see GcJsonExporter.cs) or a "+elapsed since
// capture start" string (.gcinfo/XML source, which has no absolute time
// anchor - see gcDataFromXml). Formats the former as "18-Jul-2026 07:15:15
// AM PST"; the latter is already as compact/readable as it can get without
// a real date, so it's passed through unchanged.
export function formatHumanDateTime(dateTimeString: any): string {
    if (dateTimeString === undefined || dateTimeString === null) {
        return "";
    }

    if (typeof dateTimeString === "string" && dateTimeString.charAt(0) === '+') {
        return dateTimeString;
    }

    const parsed = new Date(dateTimeString);
    if (isNaN(parsed.getTime())) {
        return dateTimeString;
    }

    const parts = new Intl.DateTimeFormat('en-US', {
        day: '2-digit',
        month: 'short',
        year: 'numeric',
        hour: '2-digit',
        minute: '2-digit',
        second: '2-digit',
        hour12: true,
        timeZoneName: 'short'
    }).formatToParts(parsed);

    const partsByType: any = {};
    for (var partIndex = 0; partIndex < parts.length; ++partIndex) {
        partsByType[parts[partIndex].type] = parts[partIndex].value;
    }

    return `${partsByType["day"]}-${partsByType["month"]}-${partsByType["year"]} ${partsByType["hour"]}:${partsByType["minute"]}:${partsByType["second"]} ${partsByType["dayPeriod"]} ${partsByType["timeZoneName"]}`;
}

// Renders the same per-GC detail table as the live GC view
// (DotnetInsightsGcEditor.ts's getHtmlForWebview) but against the static
// gcData["gcData"] shape both .gcinfo and .nettrace sources already produce
// (see GcSnapshotRenderer.ts) - same columns, same pause-time severity
// color-coding, plus a DateTime column (not available on the live view,
// which predates that field).
export function renderGcDetailTable(gcs: any[]): string {
    if (gcs.length === 0) {
        return `<div id="detailTable"><p>No GC events to display.</p></div>`;
    }

    // 1024*1024 - i.e. this produces MB, not KB (the columns below are
    // labeled accordingly). Matches the unit the charts elsewhere on this
    // page already use for the same GenerationSize*/TotalHeapSize fields
    // (see snapshotGcStats.js's totalMb) - a true-KB divisor here would make
    // GB-scale heap totals unreadable, and disagreeing with the charts'
    // units on the same page would be its own source of confusion.
    const mb = 1024 * 1024;

    var rows = "";
    for (var index = 0; index < gcs.length; ++index) {
        const gcData = gcs[index]["data"];

        const pauseTime = parseFloat(gcData["PauseDurationMSec"]);

        const tdId = gcData["Id"];
        const tdDateTime = formatHumanDateTime(gcData["DateTime"]);
        const tdGen = gcData["generation"];
        const tdType = gcData["Type"];
        const tdPauseTime = pauseTime.toFixed(2);
        const tdReason = gcData["Reason"];
        const tdGen0Size = (parseInt(gcData["GenerationSize0"]) / mb).toFixed(2);
        const tdGen1Size = (parseInt(gcData["GenerationSize1"]) / mb).toFixed(2);
        const tdGen2Size = (parseInt(gcData["GenerationSize2"]) / mb).toFixed(2);
        const tdLohSize = (parseInt(gcData["GenerationSizeLOH"]) / mb).toFixed(2);
        const tdPohSize = (parseInt(gcData["GenerationSizePOH"]) / mb).toFixed(2);
        const tdTotalHeapSize = (parseInt(gcData["TotalHeapSize"]) / mb).toFixed(2);
        const tdGen0MinSize = (parseInt(gcData["Gen0MinSize"]) / mb).toFixed(2);
        const tdTotalPromotedSize0 = (parseInt(gcData["TotalPromotedSize0"]) / mb).toFixed(2);
        const tdTotalPromotedSize1 = (parseInt(gcData["TotalPromotedSize1"]) / mb).toFixed(2);
        const tdTotalPromotedSize2 = (parseInt(gcData["TotalPromotedSize2"]) / mb).toFixed(2);

        var severityClass = "";
        if (pauseTime > 200.0) {
            severityClass = ` class="expensiveGc"`;
        }
        else if (pauseTime > 100.0) {
            severityClass = ` class="warnGc"`;
        }
        else if (pauseTime > 50.0) {
            severityClass = ` class="interstingGc"`;
        }
        else if (pauseTime > 20.0) {
            severityClass = ` class="somewhatInterestingGc"`;
        }
        else if (pauseTime > 10.0) {
            severityClass = ` class="notSomewhatInterestingGc"`;
        }

        rows += `<tr${severityClass}><td>${tdId}</td><td>${tdDateTime}</td><td>${tdGen}</td><td>${tdType}</td><td>${tdPauseTime}</td><td>${tdReason}</td><td>${tdGen0Size}</td><td>${tdGen1Size}</td><td>${tdGen2Size}</td><td>${tdLohSize}</td><td>${tdPohSize}</td><td>${tdTotalHeapSize}</td><td>${tdGen0MinSize}</td><td>${tdTotalPromotedSize0}</td><td>${tdTotalPromotedSize1}</td><td>${tdTotalPromotedSize2}</td></tr>`;
    }

    const header = `<tr class="tableHeader"><th>GC Number</th><th>DateTime</th><th>Collection Generation</th><th>Type</th><th>Pause Time (mSec)</th><th>Reason</th><th>Generation 0 Size (mb)</th><th>Generation 1 Size (mb)</th><th>Generation 2 Size (mb)</th><th>LOH Size (mb)</th><th>POH Size (mb)</th><th>Total Heap Size (mb)</th><th>Gen 0 Min Budget (mb)</th><th>Promoted Gen0 (mb)</th><th>Promoted Gen1 (mb)</th><th>Promoted Gen2 (mb)</th></tr>`;

    return `<div id="detailTable"><table>${header}${rows}</table></div>`;
}
