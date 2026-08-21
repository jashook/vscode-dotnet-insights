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
        return `<div class="detailTable"><p>No GC events to display.</p></div>`;
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
        // Raw string, not pre-formatted here - formatHumanDateTime calls
        // Intl.DateTimeFormat.formatToParts, which for a 1000-GC capture
        // means 1000 Intl calls on *every* extension-host render of this
        // webview, even though the table itself is injected lazily behind
        // the Detailed tab's first click (see detailTableHtml below).
        // snapshotGcStats.js's own formatHumanDateTime does the actual
        // formatting client-side, once, at that same lazy-inject point.
        const tdDateTimeRaw = gcData["DateTime"];
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

        // Read by snapshotGcStats.js's filterDetailTableToZoomRange to hide
        // rows outside the GC Charts tab's current zoom selection - the same
        // elapsed-ms value the charts themselves plot each GC at (see
        // buildAllPauseTimePulses), so a zoomed chart range and the filtered
        // table always agree on which GCs are "in view".
        const tdElapsedMsec = gcData["PauseStartRelativeMSec"];

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

        // data-gc-index (the row's own position in gcs, matching the
        // client-side `gcs` array parsed from hiddenData) lets a row-hide
        // toggle (snapshotGcStats.js's rebuildGcSummaryTiles) filter gcs
        // down to only visible entries before re-running the ported
        // GcStatsCalculations.ts math. rowHideBtn gets its own cell like
        // every other hide-enabled table on this page.
        rows += `<tr${severityClass} data-elapsed-msec="${tdElapsedMsec}" data-gc-index="${index}"><td class="rowHideColumn"><button class="rowHideBtn" type="button" title="Hide this row">&#10005;</button></td><td>${tdId}</td><td class="gcDateTimeCell" data-raw="${tdDateTimeRaw}"></td><td>${tdGen}</td><td>${tdType}</td><td>${tdPauseTime}</td><td>${tdReason}</td><td>${tdGen0Size}</td><td>${tdGen1Size}</td><td>${tdGen2Size}</td><td>${tdLohSize}</td><td>${tdPohSize}</td><td>${tdTotalHeapSize}</td><td>${tdGen0MinSize}</td><td>${tdTotalPromotedSize0}</td><td>${tdTotalPromotedSize1}</td><td>${tdTotalPromotedSize2}</td></tr>`;
    }

    // data-sort marks how snapshotGcStats.js's click-to-sort handler should
    // compare this column's cells: "number" parses textContent as a float,
    // "date" reads the DateTime cell's own data-raw attribute (see below -
    // the raw ISO/"+elapsed" string sorts correctly as plain text, the
    // human-formatted display text does not), anything else falls back to a
    // case-insensitive text compare. Each header's visible label is wrapped
    // in its own <span> so the click handler can append a sort-direction
    // arrow in a sibling <span> without having to re-derive or disturb the
    // label text on every click.
    const columns: [string, string][] = [
        ["GC Number", "number"],
        ["DateTime", "date"],
        ["Collection Generation", "number"],
        ["Type", "text"],
        ["Pause Time (mSec)", "number"],
        ["Reason", "text"],
        ["Generation 0 Size (mb)", "number"],
        ["Generation 1 Size (mb)", "number"],
        ["Generation 2 Size (mb)", "number"],
        ["LOH Size (mb)", "number"],
        ["POH Size (mb)", "number"],
        ["Total Heap Size (mb)", "number"],
        ["Gen 0 Min Budget (mb)", "number"],
        ["Promoted Gen0 (mb)", "number"],
        ["Promoted Gen1 (mb)", "number"],
        ["Promoted Gen2 (mb)", "number"],
    ];

    const headerWithHideColumn = renderRankedTableHeader(columns);

    // Hidden until at least one row is hidden - same allocationZoomStatus
    // idiom as every other hide-status bar on this page.
    const hideStatusHtml = `
        <div class="allocationZoomStatus" id="gcDetailHideStatus" style="display:none">
            <span class="allocationZoomStatusLabel" id="gcDetailHideStatusLabel"></span>
            <button class="resetZoomButton" id="gcDetailShowAllBtn">Show all</button>
        </div>`;

    return `${hideStatusHtml}<div class="detailTable"><table id="gcDetailTable">${headerWithHideColumn}${rows}</table></div>`;
}

// Shared header builder for every sortable detail table in these webviews
// (GC event table, CPU hot-methods table, .gcdump type census, etc.) - each
// column entry is [label, sortType] where sortType is "number", "date", or
// "text" (see sortDetailTableByColumn in media/rankedTable.js).
export function renderSortableTableHeader(columns: ReadonlyArray<[string, string]>): string {
    var headerCells = "";
    for (var columnIndex = 0; columnIndex < columns.length; ++columnIndex) {
        const [label, sortType] = columns[columnIndex];
        headerCells += `<th data-sort="${sortType}"><span class="thLabel">${label}</span><span class="sortIndicator"></span></th>`;
    }
    return `<tr class="tableHeader">${headerCells}</tr>`;
}

// The same header with the row-hide button's own leading column prepended - a
// bare, unlabeled <th> carrying no data-sort attribute, so
// wireSortableTableHeaders (media/rankedTable.js) skips it instead of trying to
// sort by a column that holds a button.
//
// Every ranked table in these webviews is built this way (CPU Methods,
// Contention Sites, Exception Types, the GC detail table, and the .gcdump type
// census/retained/references tables), and the column POSITIONS this produces
// are what snapshot.css's .cpuHotMethodsTable rules are written against:
// column 1 is the narrow hide column, column 2 is the left-aligned, wrapping
// name column that absorbs leftover width, and every column after it is
// numeric and right-aligned. A table that skips the hide column shifts all of
// that by one and gets its name column formatted as a number - which is
// exactly what the .gcdump census table did before it was built through here.
export function renderRankedTableHeader(columns: ReadonlyArray<[string, string]>): string {
    return renderSortableTableHeader(columns).replace(
        '<tr class="tableHeader">',
        '<tr class="tableHeader"><th class="rowHideColumn"></th>');
}
