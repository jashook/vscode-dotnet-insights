// Script run within the webview itself.

var allocationDatasets = {};

(function () {

    // Get a reference to the VS Code webview api.
    // We use this API to post messages back to our extension.

    // @ts-ignore
    const vscode = acquireVsCodeApi();
  
    console.time("gcParsing");
    var gcs = JSON.parse(document.getElementById("hiddenData").textContent);
    console.timeEnd("gcParsing");

    console.time("gcCountsByGenParsing");
    var gcCountsByGen = JSON.parse(document.getElementById("gcCountsByGen").textContent);
    console.timeEnd("gcCountsByGenParsing");

    var totalTimeInEachGcJson = JSON.parse(document.getElementById("totalTimeInEachGcJson").textContent);

    // null when sourceFormat !== "nettrace" or the capture had zero
    // allocation ticks - see GcSnapshotRenderer.ts's hasHeapContents.
    // Includes every raw allocation tick (see AllocationJsonExporter.cs) -
    // potentially tens of thousands for a busy capture - but JSON.parse
    // itself is cheap regardless (the earlier perf work on this page found
    // DOM construction, not JSON parsing, to be the actual cost - see
    // detailTableHtml below), so this is still parsed eagerly like
    // gcCountsByGen; only the chart/table DOM built from it is deferred to
    // the "Heap Contents" nav button's first click.
    var allocationSummaryJson = JSON.parse(document.getElementById("allocationSummaryJson").textContent);

    // null when sourceFormat !== "nettrace" or the capture had zero
    // exception events - see GcSnapshotRenderer.ts's hasExceptions. Eagerly
    // parsed for the same reason allocationSummaryJson is (JSON.parse
    // itself is cheap; only the DOM built from it is deferred to the
    // "Exceptions" nav button's first click).
    var exceptionSummaryJson = JSON.parse(document.getElementById("exceptionSummaryJson").textContent);

    // null when sourceFormat !== "nettrace" or the capture had zero CPU
    // samples - see GcSnapshotRenderer.ts's hasCpuProfile. Eagerly parsed
    // for the same reason allocationSummaryJson/exceptionSummaryJson are -
    // only the flame graph DOM built from it (media/flameGraph.js) is
    // deferred to the "Profile" nav button's first click.
    var cpuProfileJson = JSON.parse(document.getElementById("cpuProfileJson").textContent);

    // null when sourceFormat !== "nettrace" or the capture had zero
    // contention events - see GcSnapshotRenderer.ts's hasContention. Eagerly
    // parsed for the same reason the others are.
    var contentionSummaryJson = JSON.parse(document.getElementById("contentionSummaryJson").textContent);
    var threadingSummaryJson = JSON.parse(document.getElementById("threadingSummaryJson").textContent);

    // Generic "hide this row, recompute everything else" controller shared
    // by every ranked/percent table on this page (CPU Methods, Contention
    // Top Sites, Allocation ranked types, Exceptions, GC Detailed) - one
    // instance per table (not a shared registry) since each table's own
    // onChange callback does entirely different recompute work (rebuild
    // rows + maybe a timeline chart vs. rebuild summary-tile blocks).
    // Hidden state is in-memory only, same as every other piece of
    // interactive UI state on this page (zoom range, expand/collapse, sort
    // column) - none of which persist via vscode.getState()/setState()
    // today, so a webview reload resets it like everything else.
    function createRowHideController(statusBarId, statusLabelId, onChange) {
        var hiddenIndices = new Set();

        function updateStatusBarUi() {
            var statusBar = document.getElementById(statusBarId);
            var statusLabel = document.getElementById(statusLabelId);
            if (!statusBar || !statusLabel) {
                return;
            }

            if (hiddenIndices.size === 0) {
                statusBar.style.display = 'none';
                return;
            }

            statusBar.style.display = '';
            statusLabel.textContent = 'Hidden rows (' + hiddenIndices.size + ') — Show all';
        }

        return {
            toggle: function (index) {
                if (hiddenIndices.has(index)) {
                    hiddenIndices.delete(index);
                } else {
                    hiddenIndices.add(index);
                }

                updateStatusBarUi();
                onChange();
            },
            isHidden: function (index) {
                return hiddenIndices.has(index);
            },
            count: function () {
                return hiddenIndices.size;
            },
            reset: function () {
                if (hiddenIndices.size === 0) {
                    return;
                }

                hiddenIndices.clear();
                updateStatusBarUi();
                onChange();
            },
            // Bulk-hide, used by "Hide IO-Bound Methods" - adds every index
            // in one pass and fires onChange (a full table/tile/timeline
            // rebuild) once at the end, rather than once per row the way a
            // loop of individual toggle() calls would. Idempotent per index
            // (already-hidden ones are skipped) and a no-op (never calls
            // onChange at all) if nothing in the given set was newly hidden -
            // clicking the bulk button twice in a row shouldn't force a
            // pointless rebuild the second time.
            hideMany: function (indices) {
                var changed = false;
                for (var hideIndex = 0; hideIndex < indices.length; ++hideIndex) {
                    if (!hiddenIndices.has(indices[hideIndex])) {
                        hiddenIndices.add(indices[hideIndex]);
                        changed = true;
                    }
                }

                if (!changed) {
                    return;
                }

                updateStatusBarUi();
                onChange();
            }
        };
    }

    // Full human-readable form for tooltips (space isn't constrained there
    // the way it is on an axis tick) - mirrors GcDetailTableRenderer.ts's
    // formatHumanDateTime exactly, e.g. "21-Jul-2026 03:42:13 PM PDT".
    var formatHumanDateTime = function (dateTimeString) {
        if (dateTimeString === undefined || dateTimeString === null) {
            return "";
        }

        if (dateTimeString.charAt(0) === '+') {
            return dateTimeString;
        }

        var parsed = new Date(dateTimeString);
        if (isNaN(parsed.getTime())) {
            return dateTimeString;
        }

        var parts = new Intl.DateTimeFormat('en-US', {
            day: '2-digit',
            month: 'short',
            year: 'numeric',
            hour: '2-digit',
            minute: '2-digit',
            second: '2-digit',
            hour12: true,
            timeZoneName: 'short'
        }).formatToParts(parsed);

        var partsByType = {};
        for (var partIndex = 0; partIndex < parts.length; ++partIndex) {
            partsByType[parts[partIndex].type] = parts[partIndex].value;
        }

        return `${partsByType["day"]}-${partsByType["month"]}-${partsByType["year"]} ${partsByType["hour"]}:${partsByType["minute"]}:${partsByType["second"]} ${partsByType["dayPeriod"]} ${partsByType["timeZoneName"]}`;
    };

    // Tooltip title for every GC-over-time chart - each point carries its
    // own gcId/dateTime (see buildGcPointSeries), since all these charts are
    // now on a shared linear (elapsed-ms) x-axis rather than a category axis
    // keyed by array index.
    var linearGcTooltipTitle = function (tooltipItems, tooltipData) {
        var lines = [];
        for (var itemIndex = 0; itemIndex < tooltipItems.length; ++itemIndex) {
            var tooltipItem = tooltipItems[itemIndex];
            var point = tooltipData.datasets[tooltipItem.datasetIndex].data[tooltipItem.index];
            lines.push(`GC #${point.gcId} — ${formatHumanDateTime(point.dateTime)}`);
        }
        return lines;
    };

    // One point per GC: {x: elapsed ms, y: value, gcId, dateTime} - shared
    // shape for every GC-over-time chart (Usage, Fragmentation, per-Heap) now
    // that they're all on the same linear x-axis as the Pause Time chart
    // (see buildAllPauseTimePulses below). valueFn(gcData) computes the y
    // value for one GC.
    var buildGcPointSeries = function (valueFn) {
        var points = [];
        for (var index = 0; index < gcs.length; ++index) {
            var gcData = gcs[index]["data"];
            points.push({
                x: gcData["PauseStartRelativeMSec"],
                y: valueFn(gcData),
                gcId: gcData["Id"],
                dateTime: gcData["DateTime"]
            });
        }
        return points;
    };

    // ── Shared drag-to-zoom, across the entire webview ────────────────────
    // One zoom range applies across every time-series chart on the page -
    // the GC view's Pause Time/Usage/Fragmentation/per-Heap charts *and* the
    // Heap Contents view's allocation-rate/type-timeline charts (see
    // applySharedZoom below) - plus filters the Detailed tab's rows to the
    // same range. null means "full capture, not zoomed". Declared once here
    // (rather than as two independent per-view variables) so dragging a
    // selection on any chart, in either view, moves every other chart to
    // match - a GC-charts zoom and a Heap-Contents zoom used to be entirely
    // separate state.
    var sharedZoomRange = null;

    // The zoom range most recently cleared by a reset (Backspace, swipe-
    // back, or either Reset Zoom button) - lets a single forward gesture/
    // action restore it, mirroring browser back/forward. Set inside
    // applySharedZoom itself (see below) rather than at each reset call
    // site, so every existing way of resetting picks it up automatically.
    // A fresh manual zoom (drag-select) invalidates it, same as a new
    // navigation clearing forward history in a browser.
    var zoomRangeForForward = null;

    // {chart, zoomHandle} for the always-rebuilt charts (Pause Time, Usage,
    // Fragmentation once built) - destroyed/recreated on every zoom change.
    var gcChartHandles = [];

    // heapIndex -> {chart, zoomHandle}, only for heap charts actually built
    // so far (they're lazily constructed via IntersectionObserver - see
    // below) - a zoom change rebuilds only the ones already on screen, and
    // any heap chart built for the first time afterward picks up the
    // then-current sharedZoomRange directly.
    var heapChartHandlesByIndex = {};

    function destroyGcCharts() {
        for (var handleIndex = 0; handleIndex < gcChartHandles.length; ++handleIndex) {
            var handle = gcChartHandles[handleIndex];
            if (handle.zoomHandle) {
                handle.zoomHandle.detach();
            }
            handle.chart.destroy();
        }
        gcChartHandles = [];

        for (var heapIndexKey in heapChartHandlesByIndex) {
            var heapHandle = heapChartHandlesByIndex[heapIndexKey];
            if (heapHandle.zoomHandle) {
                heapHandle.zoomHandle.detach();
            }
            heapHandle.chart.destroy();
        }
        heapChartHandlesByIndex = {};
    }

    function onGcChartsRangeSelected(startMSec, endMSec) {
        applySharedZoom({ startMSec: startMSec, endMSec: endMSec });
    }

    function updateGcZoomStatusUi(zoomRange) {
        var statusBar = document.getElementById("gcZoomStatus");
        if (!statusBar) {
            return;
        }

        if (!zoomRange) {
            statusBar.style.display = "none";
            return;
        }

        statusBar.style.display = "block";
        var label = statusBar.getElementsByClassName("allocationZoomStatusLabel")[0];
        if (label) {
            label.textContent = "Zoomed: " + formatElapsedMs(zoomRange.startMSec) +
                " – " + formatElapsedMs(zoomRange.endMSec) + " (Backspace to reset)";
        }
    }

    // Manually-hidden GC Detailed table rows (data-gc-index, the row's own
    // position in gcs). onChange rebuilds the two Charts-tab summary-tile
    // blocks (Allocation Amount by Generation / Time Spent by Generation)
    // from a JS port of GcStatsCalculations.ts - see rebuildGcSummaryTiles
    // below - and re-applies the zoom filter so the two visibility
    // conditions stay composed.
    var gcRowHider = createRowHideController('gcDetailHideStatus', 'gcDetailHideStatusLabel', function () {
        rebuildGcSummaryTiles();
        filterDetailTableToZoomRange();
    });

    // Hides Detailed-tab rows whose GC falls outside sharedZoomRange, OR
    // that the user hid manually via gcRowHider - a no-op until both the
    // Detailed tab has been opened at least once (see detailTableInjected
    // below) and either condition is active. Called on every zoom change,
    // the first time the Detailed tab opens (in case a zoom was already
    // applied on a chart beforehand), and every gcRowHider change.
    function filterDetailTableToZoomRange() {
        if (!detailTableInjected) {
            return;
        }

        var detailTable = document.querySelector('#tab-detailed .detailTable table');
        if (!detailTable) {
            return;
        }

        for (var rowIndex = 1; rowIndex < detailTable.rows.length; ++rowIndex) {
            var row = detailTable.rows[rowIndex];
            var elapsedMsec = parseFloat(row.getAttribute('data-elapsed-msec'));
            var isVisible = !sharedZoomRange || (elapsedMsec >= sharedZoomRange.startMSec && elapsedMsec <= sharedZoomRange.endMSec);
            if (isVisible) {
                var gcIndex = parseInt(row.getAttribute('data-gc-index'), 10);
                if (!isNaN(gcIndex) && gcRowHider.isHidden(gcIndex)) {
                    isVisible = false;
                }
            }
            row.style.display = isVisible ? "" : "none";
        }
    }

    // Direct JS ports of GcStatsCalculations.ts's computeAllocationAmountStats/
    // computePauseTimeStats, used to recompute the GC view's own summary
    // tiles after a Detailed-table row is hidden - kept as their own copy
    // here (server-side TypeScript vs. client-side JS) rather than shared,
    // matching how CpuProfileRenderer.ts's formatMethodNameHtml already has
    // its own independent client-side copy in drillDownStats.js. Both
    // functions call .sort() with NO comparator, exactly matching
    // GcStatsCalculations.ts's own pre-existing (documented, deliberately
    // not fixed) lexicographic-sort median quirk - see that file's own
    // header comment. Preserved as-is here for the same reason: this port
    // should compute the exact same (possibly "wrong") median the
    // server-rendered initial tiles already show, not a corrected one -
    // "hide a row, recompute" would otherwise silently change what "median"
    // means depending on whether anything is currently hidden.
    function computeAllocationAmountStatsJs(visibleGcs, generationToUse) {
        var kb = 1024;
        var totalAllocations = 0;
        var allocationsBetweenGc = [];

        for (var index = 0; index < visibleGcs.length; ++index) {
            var currentGc = visibleGcs[index]["data"];
            var newAllocAmount = 0;

            if (generationToUse === undefined) {
                for (var heapIndex = 0; heapIndex < currentGc["Heaps"].length; ++heapIndex) {
                    newAllocAmount += currentGc["Heaps"][heapIndex]["Generations"][0]["NewAllocation"] / kb;
                    newAllocAmount += currentGc["Heaps"][heapIndex]["Generations"][1]["NewAllocation"] / kb;
                    newAllocAmount += currentGc["Heaps"][heapIndex]["Generations"][2]["NewAllocation"] / kb;
                    newAllocAmount += currentGc["Heaps"][heapIndex]["Generations"][3]["NewAllocation"] / kb;
                }
            } else {
                for (var heapIndex2 = 0; heapIndex2 < currentGc["Heaps"].length; ++heapIndex2) {
                    newAllocAmount += currentGc["Heaps"][heapIndex2]["Generations"][generationToUse]["NewAllocation"] / kb;
                }
            }

            totalAllocations += newAllocAmount;
            allocationsBetweenGc.push(newAllocAmount);
        }

        if (allocationsBetweenGc.length === 0) {
            return [[], [0, 0, 0, 0, 0]];
        }

        var maxAllocationAmountBetweenGcs = 0;
        var lowestAllocationAmountBetweenGcs = allocationsBetweenGc[0];

        for (var sumIndex = 0; sumIndex < allocationsBetweenGc.length; ++sumIndex) {
            if (allocationsBetweenGc[sumIndex] > maxAllocationAmountBetweenGcs) {
                maxAllocationAmountBetweenGcs = allocationsBetweenGc[sumIndex];
            }
            if (allocationsBetweenGc[sumIndex] < lowestAllocationAmountBetweenGcs) {
                lowestAllocationAmountBetweenGcs = allocationsBetweenGc[sumIndex];
            }
        }

        allocationsBetweenGc.sort();
        var half = Math.floor(allocationsBetweenGc.length / 2);
        var medianAllocationsBetweenGcs = allocationsBetweenGc[half];
        var meanAllocationBetweenGcs = totalAllocations / allocationsBetweenGc.length;

        return [allocationsBetweenGc, [totalAllocations, meanAllocationBetweenGcs, medianAllocationsBetweenGcs, maxAllocationAmountBetweenGcs, lowestAllocationAmountBetweenGcs]];
    }

    function computePauseTimeStatsJs(visibleGcs, generation) {
        var totalTimeInGc = 0.0;
        var timesInEachGc = [];
        var highestTimeInGc = 0;
        var lowestTimeInGc = 0;

        for (var index = 0; index < visibleGcs.length; ++index) {
            if (generation !== undefined) {
                if (visibleGcs[index]["data"]["generation"] === generation) {
                    timesInEachGc.push(parseFloat(visibleGcs[index]["data"]["PauseDurationMSec"]));
                }
            } else {
                timesInEachGc.push(parseFloat(visibleGcs[index]["data"]["PauseDurationMSec"]));
            }
        }

        if (timesInEachGc.length === 0) {
            return [[], [0, 0, 0, 0, 0]];
        }

        lowestTimeInGc = timesInEachGc[0];
        for (var timeSumIndex = 0; timeSumIndex < timesInEachGc.length; ++timeSumIndex) {
            totalTimeInGc += timesInEachGc[timeSumIndex];

            if (timesInEachGc[timeSumIndex] < lowestTimeInGc) {
                lowestTimeInGc = timesInEachGc[timeSumIndex];
            }

            if (timesInEachGc[timeSumIndex] > highestTimeInGc) {
                highestTimeInGc = timesInEachGc[timeSumIndex];
            }
        }

        timesInEachGc.sort();
        var half = Math.floor(timesInEachGc.length / 2);
        var medianTimeInGc = timesInEachGc[half];
        var averageTimeInGc = totalTimeInGc / timesInEachGc.length;

        return [timesInEachGc, [totalTimeInGc, averageTimeInGc, medianTimeInGc, highestTimeInGc, lowestTimeInGc]];
    }

    // Port of GcSnapshotRenderer.ts's dynamic kb/mb/gb unit-rescaling logic
    // (the block right after computeAllocationAmountStats's own call sites)
    // - all five groups (Total/Gen0/Gen1/Gen2/LOH) always share ONE unit,
    // chosen from the Total group's own magnitude, exactly as server-side:
    // a large enough capture could otherwise show "Total: 1417.77 gb" next
    // to "Average: 425.75 mb" despite both being individually correct.
    // statsResults is an array of computeAllocationAmountStatsJs's raw
    // return values ([byGcArray, summaryTuple]) - mutated in place, same as
    // the TS original mutates its own local variables.
    function scaleAllocationAmountStatsGroups(statsResults) {
        var dataValue = 'kb';
        var totalSummary = statsResults[0][1];

        if (totalSummary[0].toFixed(2).length > 8) {
            dataValue = 'mb';
            for (var groupIndex = 0; groupIndex < statsResults.length; ++groupIndex) {
                for (var fieldIndex = 0; fieldIndex < 5; ++fieldIndex) {
                    statsResults[groupIndex][1][fieldIndex] /= 1024;
                }
            }
        }

        if (totalSummary[0].toFixed(2).length > 8) {
            dataValue = 'gb';
            for (var groupIndex2 = 0; groupIndex2 < statsResults.length; ++groupIndex2) {
                for (var fieldIndex2 = 0; fieldIndex2 < 5; ++fieldIndex2) {
                    statsResults[groupIndex2][1][fieldIndex2] /= 1024;
                }
            }
        }

        return dataValue;
    }

    // Mirrors the exact <div class="total|gen0|gen1|gen2|loh"> markup
    // GcSnapshotRenderer.ts's allocationAmountSummaryGcDiv template emits
    // server-side (Total/Largest/Smallest/Average/Median rows).
    function buildAllocationTileGroupHtml(className, label, statsSummary, unitLabel) {
        var total = statsSummary[0].toFixed(2);
        var mean = statsSummary[1].toFixed(2);
        var median = statsSummary[2].toFixed(2);
        var max = statsSummary[3].toFixed(2);
        var min = statsSummary[4].toFixed(2);

        return '<div class="' + className + '">' +
            '<div>' + label + '</div>' +
            '<div>Total<span>' + total + ' ' + unitLabel + '</span></div>' +
            '<div>Largest<span>' + max + ' ' + unitLabel + '</span></div>' +
            '<div>Smallest<span>' + min + ' ' + unitLabel + '</span></div>' +
            '<div>Average<span>' + mean + ' ' + unitLabel + '</span></div>' +
            '<div>Median<span>' + median + ' ' + unitLabel + '</span></div>' +
            '</div>';
    }

    // Mirrors timeSpentSummaryGcDiv's template (Count/Total/Largest/
    // Smallest/Average/Median rows, always "ms" - pause times never go
    // through the kb/mb/gb rescale above).
    function buildTimeTileGroupHtml(className, label, byGcArray, statsSummary) {
        var total = statsSummary[0].toFixed(2);
        var mean = statsSummary[1].toFixed(2);
        var median = statsSummary[2].toFixed(2);
        var max = statsSummary[3].toFixed(2);
        var min = statsSummary[4].toFixed(2);

        return '<div class="' + className + '">' +
            '<div>' + label + '</div>' +
            '<div>Count<span>' + byGcArray.length + '</span></div>' +
            '<div>Total<span>' + total + ' ms</span></div>' +
            '<div>Largest<span>' + max + ' ms</span></div>' +
            '<div>Smallest<span>' + min + ' ms</span></div>' +
            '<div>Average<span>' + mean + ' ms</span></div>' +
            '<div>Median<span>' + median + ' ms</span></div>' +
            '</div>';
    }

    function rebuildAllocationAmountTiles(visibleGcs) {
        var container = document.getElementById('allocationAmountSummaryGcDiv');
        if (!container) {
            return;
        }

        var totalStats = computeAllocationAmountStatsJs(visibleGcs, undefined);
        var gen0Stats = computeAllocationAmountStatsJs(visibleGcs, 0);
        var gen1Stats = computeAllocationAmountStatsJs(visibleGcs, 1);
        var gen2Stats = computeAllocationAmountStatsJs(visibleGcs, 2);
        var lohStats = computeAllocationAmountStatsJs(visibleGcs, 3);

        var unitLabel = scaleAllocationAmountStatsGroups([totalStats, gen0Stats, gen1Stats, gen2Stats, lohStats]);

        container.innerHTML =
            buildAllocationTileGroupHtml('total', 'Total', totalStats[1], unitLabel) +
            buildAllocationTileGroupHtml('gen0', 'Gen 0', gen0Stats[1], unitLabel) +
            buildAllocationTileGroupHtml('gen1', 'Gen 1', gen1Stats[1], unitLabel) +
            buildAllocationTileGroupHtml('gen2', 'Gen 2', gen2Stats[1], unitLabel) +
            buildAllocationTileGroupHtml('loh', 'LOH', lohStats[1], unitLabel);
    }

    function rebuildTimeSpentTiles(visibleGcs) {
        var container = document.getElementById('timeSpentSummaryGcDiv');
        if (!container) {
            return;
        }

        var totalStats = computePauseTimeStatsJs(visibleGcs, undefined);
        var gen0Stats = computePauseTimeStatsJs(visibleGcs, 0);
        var gen1Stats = computePauseTimeStatsJs(visibleGcs, 1);
        var gen2Stats = computePauseTimeStatsJs(visibleGcs, 2);

        container.innerHTML =
            buildTimeTileGroupHtml('total', 'Total', totalStats[0], totalStats[1]) +
            buildTimeTileGroupHtml('gen0', 'Gen 0', gen0Stats[0], gen0Stats[1]) +
            buildTimeTileGroupHtml('gen1', 'Gen 1', gen1Stats[0], gen1Stats[1]) +
            buildTimeTileGroupHtml('gen2', 'Gen 2', gen2Stats[0], gen2Stats[1]);
    }

    // Filters gcs down to non-hidden entries and rebuilds both Charts-tab
    // summary-tile blocks from that filtered list - called from gcRowHider's
    // own onChange (declared above filterDetailTableToZoomRange).
    function rebuildGcSummaryTiles() {
        var visibleGcs = [];
        for (var index = 0; index < gcs.length; ++index) {
            if (!gcRowHider.isHidden(index)) {
                visibleGcs.push(gcs[index]);
            }
        }

        rebuildAllocationAmountTiles(visibleGcs);
        rebuildTimeSpentTiles(visibleGcs);
    }

    // Rebuilds only the GC view's own charts (Pause Time/Usage/Fragmentation
    // + any per-heap chart already built) plus the Detailed table's row
    // filter - called by applySharedZoom below, which also rebuilds the Heap
    // Contents charts, so the two views' charts stay in sync regardless of
    // which one a drag-select actually happened on. zoomRange is null for
    // the full, unzoomed capture. A heap chart built for the first time
    // afterward (scrolled into view) just reads sharedZoomRange directly.
    function renderGcCharts(zoomRange) {
        var previouslyBuiltHeapIndexes = [];
        for (var existingHeapIndexKey in heapChartHandlesByIndex) {
            previouslyBuiltHeapIndexes.push(parseInt(existingHeapIndexKey, 10));
        }

        destroyGcCharts();
        updateGcZoomStatusUi(zoomRange);

        var pauseHandle = renderTotalGcPauseTimeChart(zoomRange);
        if (pauseHandle) {
            gcChartHandles.push(pauseHandle);
        }

        var statsHandle = renderTotalGcStatsChart(zoomRange);
        if (statsHandle) {
            gcChartHandles.push(statsHandle);
        }

        if (document.getElementById("gcFragmentationOverTime")) {
            requestAnimationFrame(function () {
                var fragHandle = renderGcFragmentationChart(zoomRange);
                if (fragHandle) {
                    gcChartHandles.push(fragHandle);
                }
            });
        }

        for (var rebuildIndex = 0; rebuildIndex < previouslyBuiltHeapIndexes.length; ++rebuildIndex) {
            var heapIndexToRebuild = previouslyBuiltHeapIndexes[rebuildIndex];
            heapChartHandlesByIndex[heapIndexToRebuild] = renderHeapChart(heapIndexToRebuild, zoomRange);
        }

        filterDetailTableToZoomRange();
    }

    // Single entry point for every zoom change after page load (drag-select
    // on ANY chart in either view, either Reset Zoom button, or Backspace) -
    // updates sharedZoomRange once, then rebuilds both views' charts so a
    // zoom applied while looking at the GC charts is already in place if the
    // user switches to Heap Contents, and vice versa. renderHeapContentsCharts
    // (defined further below) is a no-op for canvases that don't exist yet
    // (the Heap Contents view is injected lazily on first open - see
    // AllocationSummaryRenderer.ts/renderAllocationTimelineChart's own
    // canvasElement-null guard), so calling it here even before that view
    // has ever been opened is harmless.
    // isForwardRestore is true only when performGoForwardAction is replaying
    // zoomRangeForForward - every other caller (drag-select, either Reset
    // Zoom button, Backspace, swipe-back) omits it. That's enough to
    // maintain single-level undo/redo without touching any of those other
    // call sites: clearing an active zoom (zoomRange === null while one was
    // set) stashes it for forward, and applying any *other* new range
    // (a real drag-select, not a forward replay) drops the stashed one,
    // same as a browser dropping forward history on a fresh navigation.
    function applySharedZoom(zoomRange, isForwardRestore) {
        if (zoomRange === null && sharedZoomRange !== null) {
            zoomRangeForForward = sharedZoomRange;
        } else if (!isForwardRestore) {
            zoomRangeForForward = null;
        }

        sharedZoomRange = zoomRange;
        renderGcCharts(zoomRange);
        renderHeapContentsCharts(zoomRange);
    }

    // Both canvases (and hence both charts) are absent entirely when this
    // capture has zero GCs - see GcSnapshotRenderer.ts's own `if (gcs.length
    // > 0)` guard around canvasData. A real capture with no GCs (e.g. an
    // exceptions-only nettrace) is now a real, reachable case (the GC tab
    // itself stays visible-but-disabled rather than omitted - see
    // GcSnapshotRenderer.ts's viewTabBar), so these can no longer assume
    // the canvas exists unconditionally the way they used to.
    var gcStatsChart = document.getElementsByClassName("gcStatsChart")[0];
    var gcCountChart = null;

    if (gcStatsChart) {
        const gcStatsChartChartContext = gcStatsChart;
        const context = gcStatsChartChartContext.getContext('2d');

        gcCountChart = new Chart(context, {
            "type": 'bar',
            data: {
                labels: [
                    "0",
                    "1",
                    "2"
                ],
                datasets: [{
                    label: "GC Count By Generation",
                    data: gcCountsByGen,
                    backgroundColor: [
                        "rgba(72, 83, 136, 0.2)",
                        "rgba(96, 165, 69, 0.2)",
                        "rgba(141, 31, 95, 0.2)"
                    ]
                }]
            },
            options: {
                animation: { duration: 0 },
                "maintainAspectRatio": false
            }
        });
    }

    var gcStatsTimeChart = document.getElementsByClassName("gcStatsTimeChart")[0];
    var gcTimeCountChart = null;

    if (gcStatsTimeChart) {
        const gcStatsTimeChartChartContext = gcStatsTimeChart;
        const newContext = gcStatsTimeChartChartContext.getContext('2d');

        gcTimeCountChart = new Chart(newContext, {
            "type": 'bar',
            data: {
                labels: [
                    "0",
                    "1",
                    "2"
                ],
                datasets: [{
                    label: "Total Time In GC By Generation",
                    data: totalTimeInEachGcJson,
                    backgroundColor: [
                        "rgba(72, 83, 136, 0.2)",
                        "rgba(96, 165, 69, 0.2)",
                        "rgba(141, 31, 95, 0.2)"
                    ]
                }]
            },
            options: {
                scales: {
                    yAxes: [{
                        ticks: {
                            beginAtZero: true
                        },
                        scaleLabel: {
                            display: true,
                            labelString: "Time in ms"
                        }
                    }],
                },
                animation: { duration: 0 },
                "maintainAspectRatio": false,
            }
        });
    }

    // LOH isn't a distinct GC generation - LOH is swept as part of Gen 2
    // (full) GCs, so a GC's "generation" field alone can't identify it. A GC
    // counts toward the LOH line only when its trigger Reason indicates LOH
    // allocation pressure specifically.
    var lohTriggerReasons = { "AllocLarge": true, "OutOfSpaceLOH": true };

    // Real captures can have many seconds of true idle time between GCs. A
    // plain category axis (one evenly-spaced tick per GC, like the memory
    // charts use) hides that entirely - and a line chart with one point per
    // GC and no explicit zero in between draws a straight line directly from
    // one GC's pause value to the next, which reads as continuous blocking
    // across the whole capture. Both problems are fixed by putting this
    // chart on a real *linear* (elapsed-ms) x-axis and emitting each GC as a
    // zero-flanked pulse: (start, 0) -> (start, pauseMs) -> (end, pauseMs)
    // -> (end, 0). Consecutive pulses' flanking zeros are equal (both 0), so
    // Chart.js draws a flat baseline between them that's exactly as wide as
    // the real gap.
    // Single pass over gcs builds all five pause-time pulse arrays at once,
    // replacing five separate O(n) passes through buildPauseTimePulses.
    // Point objects are shared across arrays (they're never mutated).
    // Returns [total, gen0, gen1, gen2, loh].
    var buildAllPauseTimePulses = function () {
        var totalPoints = [];
        var gen0Points = [];
        var gen1Points = [];
        var gen2Points = [];
        var lohPoints = [];

        for (var pulseIndex = 0; pulseIndex < gcs.length; ++pulseIndex) {
            var pulseGcData = gcs[pulseIndex]["data"];
            var startMs = pulseGcData["PauseStartRelativeMSec"];
            var endMs = pulseGcData["PauseEndRelativeMSec"];
            var pauseMs = pulseGcData["PauseDurationMSec"];
            var pulseGcId = pulseGcData["Id"];
            var pulseDateTime = pulseGcData["DateTime"];

            var ptRise  = { x: startMs, y: 0,       gcId: pulseGcId, dateTime: pulseDateTime, isGap: true };
            var ptTop0  = { x: startMs, y: pauseMs,  gcId: pulseGcId, dateTime: pulseDateTime, isGap: false };
            var ptTop1  = { x: endMs,   y: pauseMs,  gcId: pulseGcId, dateTime: pulseDateTime, isGap: false };
            var ptFall  = { x: endMs,   y: 0,        gcId: pulseGcId, dateTime: pulseDateTime, isGap: true };

            totalPoints.push(ptRise, ptTop0, ptTop1, ptFall);

            var generation = pulseGcData["generation"];
            if (generation === 0) {
                gen0Points.push(ptRise, ptTop0, ptTop1, ptFall);
            } else if (generation === 1) {
                gen1Points.push(ptRise, ptTop0, ptTop1, ptFall);
            } else if (generation === 2) {
                gen2Points.push(ptRise, ptTop0, ptTop1, ptFall);
            }

            if (lohTriggerReasons[pulseGcData["Reason"]] === true) {
                lohPoints.push(ptRise, ptTop0, ptTop1, ptFall);
            }
        }

        return [totalPoints, gen0Points, gen1Points, gen2Points, lohPoints];
    };

    var formatElapsedMs = function (ms) {
        if (ms < 1000) {
            return `${Math.round(ms)}ms`;
        }

        var totalSeconds = ms / 1000;
        if (totalSeconds < 60) {
            return `${totalSeconds.toFixed(1)}s`;
        }

        var minutes = Math.floor(totalSeconds / 60);
        var seconds = Math.round(totalSeconds % 60);
        return `${minutes}m ${seconds}s`;
    };

    var pauseTimeTooltipTitle = function (tooltipItems, tooltipData) {
        var lines = [];
        for (var itemIndex = 0; itemIndex < tooltipItems.length; ++itemIndex) {
            var tooltipItem = tooltipItems[itemIndex];
            var point = tooltipData.datasets[tooltipItem.datasetIndex].data[tooltipItem.index];
            if (point.isGap) {
                lines.push(`${formatElapsedMs(point.x)} elapsed — idle`);
            } else {
                lines.push(`GC #${point.gcId} — ${formatHumanDateTime(point.dateTime)}`);
            }
        }
        return lines;
    };

    var pauseTimePulses = buildAllPauseTimePulses();

    function renderTotalGcPauseTimeChart(zoomRange) {
        var canvasElement = document.getElementById("totalGcPauseTimeOverTime");
        if (!canvasElement) {
            return null;
        }

        var xAxisTicks = { callback: formatElapsedMs };
        if (zoomRange) {
            xAxisTicks.min = zoomRange.startMSec;
            xAxisTicks.max = zoomRange.endMSec;
        }

        var dragStateHolder = { current: null };

        var chart = new Chart(canvasElement.getContext('2d'), {
            type: 'line',
                data: {
                    datasets: [{
                        label: 'Total Blocking Time',
                        data: pauseTimePulses[0],
                        backgroundColor: [
                            "rgba(220, 53, 69, 0.2)",
                        ],
                        borderColor: "rgba(220, 53, 69, 1)",
                        borderWidth: 1,
                        lineTension: 0,
                        pointRadius: 2,
                        pointHoverRadius: 4
                    },
                    {
                        label: 'Gen 0',
                        data: pauseTimePulses[1],
                        backgroundColor: [
                            "rgba(72, 83, 136, 0.2)",
                        ],
                        borderWidth: 1,
                        lineTension: 0,
                        pointRadius: 2,
                        pointHoverRadius: 4
                    },
                    {
                        label: "Gen 1",
                        data: pauseTimePulses[2],
                        backgroundColor: [
                            "rgba(96, 165, 69, 0.2)",
                        ],
                        borderWidth: 1,
                        lineTension: 0,
                        pointRadius: 2,
                        pointHoverRadius: 4
                    },
                    {
                        label: "Gen 2",
                        data: pauseTimePulses[3],
                        backgroundColor: [
                            "rgba(141, 31, 95, 0.2)",
                        ],
                        borderWidth: 1,
                        lineTension: 0,
                        pointRadius: 2,
                        pointHoverRadius: 4
                    },
                    {
                        label: "LOH",
                        data: pauseTimePulses[4],
                        backgroundColor: [
                            "rgba(201, 221, 84, 0.2)"
                        ],
                        borderWidth: 1,
                        lineTension: 0,
                        pointRadius: 2,
                        pointHoverRadius: 4
                    }
                ]},
                plugins: [createZoomSelectionPlugin(dragStateHolder)],
                options: {
                    title: {
                        display: true,
                        text: `GC Pause Time by Generation`
                    },
                    // Sharp vertical pulses (x,0)->(x,pauseMs) confuse Chart.js's
                    // default bezier curve fitting (lineTension) - it overshoots
                    // through the vertical segments and loops back on itself,
                    // producing the self-crossing "figure eight" artifact. Force
                    // straight-line segments instead, both per-dataset above and
                    // here as a chart-wide default so nothing falls back to it.
                    elements: {
                        line: {
                            tension: 0
                        }
                    },
                    scales: {
                        xAxes: [{
                            type: 'linear',
                            position: 'bottom',
                            ticks: xAxisTicks,
                            scaleLabel: {
                                display: true,
                                labelString: "Capture Time Elapsed"
                            }
                        }],
                        yAxes: [{
                            ticks: {
                                beginAtZero: true
                            },
                            scaleLabel: {
                                display: true,
                                labelString: "Time in ms"
                            }
                        }],
                    },
                    tooltips: {
                        callbacks: {
                            title: pauseTimeTooltipTitle
                        }
                    },
                    animation: { duration: 0 },
                    "maintainAspectRatio": false,
                }
        });

        var zoomHandle = attachDragToZoom(chart, canvasElement, dragStateHolder, pixelToMSecLinear, onGcChartsRangeSelected);
        return { chart: chart, zoomHandle: zoomHandle };
    }

    var totalMb = 1024 * 1024;

    var totalGenSizeSeries = [
        buildGcPointSeries(function (gcData) { return gcData["GenerationSize0"] / totalMb; }),
        buildGcPointSeries(function (gcData) { return gcData["GenerationSize1"] / totalMb; }),
        buildGcPointSeries(function (gcData) { return gcData["GenerationSize2"] / totalMb; }),
        buildGcPointSeries(function (gcData) { return gcData["GenerationSizeLOH"] / totalMb; })
    ];

    function renderTotalGcStatsChart(zoomRange) {
        var canvasElement = document.getElementById("totalGcStatsOverTime");
        if (!canvasElement) {
            return null;
        }

        var xAxisTicks = { callback: formatElapsedMs };
        if (zoomRange) {
            xAxisTicks.min = zoomRange.startMSec;
            xAxisTicks.max = zoomRange.endMSec;
        }

        var dragStateHolder = { current: null };

        var chart = new Chart(canvasElement.getContext('2d'), {
            type: 'line',
                data: {
                    datasets: [{
                        label: 'Gen 0',
                        data: totalGenSizeSeries[0],
                        backgroundColor: [
                            "rgba(72, 83, 136, 0.2)",
                        ],
                        borderWidth: 1,
                        pointRadius: 2,
                        pointHoverRadius: 4
                    },
                    {
                        label: "Gen 1",
                        data: totalGenSizeSeries[1],
                        backgroundColor: [
                            "rgba(96, 165, 69, 0.2)",
                        ],
                        borderWidth: 1,
                        pointRadius: 2,
                        pointHoverRadius: 4
                    },
                    {
                        label: "Gen 2",
                        data: totalGenSizeSeries[2],
                        backgroundColor: [
                            "rgba(141, 31, 95, 0.2)",
                        ],
                        borderWidth: 1,
                        pointRadius: 2,
                        pointHoverRadius: 4
                    },
                    {
                        label: "LOH",
                        data: totalGenSizeSeries[3],
                        backgroundColor: [
                            "rgba(201, 221, 84, 0.2)"
                        ],
                        borderWidth: 1,
                        pointRadius: 2,
                        pointHoverRadius: 4
                    }
                ]},
                plugins: [createZoomSelectionPlugin(dragStateHolder)],
                options: {
                    title: {
                        display: true,
                        text: `Total GC Usage by Generation`
                    },
                    scales: {
                        xAxes: [{
                            type: 'linear',
                            position: 'bottom',
                            ticks: xAxisTicks,
                            scaleLabel: {
                                display: true,
                                labelString: "Capture Time Elapsed"
                            }
                        }],
                        yAxes: [{
                            ticks: {
                                beginAtZero: true
                            },
                            scaleLabel: {
                                display: true,
                                labelString: "Memory Usage in MB"
                            }
                        }],
                    },
                    tooltips: {
                        callbacks: {
                            title: linearGcTooltipTitle
                        }
                    },
                    animation: { duration: 0 },
                    "maintainAspectRatio": false,
                }
        });

        var zoomHandle = attachDragToZoom(chart, canvasElement, dragStateHolder, pixelToMSecLinear, onGcChartsRangeSelected);
        return { chart: chart, zoomHandle: zoomHandle };
    }

    // Built once, on first render, and cached - the GC x heap x gen summing
    // below is the expensive part, not chart construction, so every
    // subsequent zoom change (renderGcCharts) reuses these same point arrays
    // and just re-clips the x-axis range (see renderGcFragmentationChart).
    var fragmentationSeriesCache = null;

    function buildFragmentationSeries() {
        var fragGen0Points = [];
        var fragGen1Points = [];
        var fragGen2Points = [];
        var fragLohPoints = [];
        var fragTotalPoints = [];
        var pinnedCountPoints = [];
        var compactionMarkerPoints = [];

        for (var fragIndex = 0; fragIndex < gcs.length; ++fragIndex) {
            var fragGcData = gcs[fragIndex]["data"];
            var fragHeaps = fragGcData["Heaps"];
            var fragX = fragGcData["PauseStartRelativeMSec"];
            var fragGcId = fragGcData["Id"];
            var fragDateTime = fragGcData["DateTime"];

            var fragByGen = [0, 0, 0, 0];
            var sizeAfterByGen = [0, 0, 0, 0];

            for (var fragHeapIndex = 0; fragHeapIndex < fragHeaps.length; ++fragHeapIndex) {
                var fragGens = fragHeaps[fragHeapIndex]["Generations"];
                for (var genIdx = 0; genIdx < 4; ++genIdx) {
                    var fragGen = fragGens[genIdx];
                    if (fragGen) {
                        fragByGen[genIdx] += parseFloat(fragGen["Fragmentation"]) || 0;
                        sizeAfterByGen[genIdx] += parseFloat(fragGen["SizeAfter"]) || 0;
                    }
                }
            }

            var totalHeapSizeBytes = parseFloat(fragGcData["TotalHeapSize"]) || 0;
            var totalFragBytes = fragByGen[0] + fragByGen[1] + fragByGen[2] + fragByGen[3];

            fragGen0Points.push({ x: fragX, y: sizeAfterByGen[0] > 0 ? (fragByGen[0] / sizeAfterByGen[0]) * 100 : 0, gcId: fragGcId, dateTime: fragDateTime });
            fragGen1Points.push({ x: fragX, y: sizeAfterByGen[1] > 0 ? (fragByGen[1] / sizeAfterByGen[1]) * 100 : 0, gcId: fragGcId, dateTime: fragDateTime });
            fragGen2Points.push({ x: fragX, y: sizeAfterByGen[2] > 0 ? (fragByGen[2] / sizeAfterByGen[2]) * 100 : 0, gcId: fragGcId, dateTime: fragDateTime });
            fragLohPoints.push({ x: fragX, y: sizeAfterByGen[3] > 0 ? (fragByGen[3] / sizeAfterByGen[3]) * 100 : 0, gcId: fragGcId, dateTime: fragDateTime });
            fragTotalPoints.push({ x: fragX, y: totalHeapSizeBytes > 0 ? (totalFragBytes / totalHeapSizeBytes) * 100 : 0, gcId: fragGcId, dateTime: fragDateTime });

            pinnedCountPoints.push({ x: fragX, y: parseInt(fragGcData["PinnedObjectCount"]) || 0, gcId: fragGcId, dateTime: fragDateTime });

            var mechanisms = parseInt(fragGcData["GlobalMechanisms"]) || 0;
            // GCGlobalMechanisms.Compaction = 0x2
            compactionMarkerPoints.push((mechanisms & 0x2) !== 0 ? { x: fragX, y: 2, gcId: fragGcId, dateTime: fragDateTime } : null);
        }

        return {
            gen0: fragGen0Points,
            gen1: fragGen1Points,
            gen2: fragGen2Points,
            loh: fragLohPoints,
            total: fragTotalPoints,
            pinnedCount: pinnedCountPoints,
            compactionMarker: compactionMarkerPoints
        };
    }

    function renderGcFragmentationChart(zoomRange) {
        var canvasElement = document.getElementById("gcFragmentationOverTime");
        if (!canvasElement) {
            return null;
        }

        if (!fragmentationSeriesCache) {
            fragmentationSeriesCache = buildFragmentationSeries();
        }

        var xAxisTicks = { callback: formatElapsedMs };
        if (zoomRange) {
            xAxisTicks.min = zoomRange.startMSec;
            xAxisTicks.max = zoomRange.endMSec;
        }

        var dragStateHolder = { current: null };

        var chart = new Chart(canvasElement.getContext('2d'), {
            type: 'line',
            data: {
                datasets: [
                    {
                        label: 'Total',
                        data: fragmentationSeriesCache.total,
                        borderColor: "rgba(220, 53, 69, 1)",
                        backgroundColor: "rgba(220, 53, 69, 0.05)",
                        borderWidth: 2,
                        yAxisID: 'fragPct',
                        fill: false,
                        pointRadius: 2,
                        pointHoverRadius: 4
                    },
                    {
                        label: 'Gen 0',
                        data: fragmentationSeriesCache.gen0,
                        borderColor: "rgba(72, 83, 136, 1)",
                        backgroundColor: "rgba(72, 83, 136, 0.05)",
                        borderWidth: 1,
                        yAxisID: 'fragPct',
                        fill: false,
                        pointRadius: 2,
                        pointHoverRadius: 4
                    },
                    {
                        label: 'Gen 1',
                        data: fragmentationSeriesCache.gen1,
                        borderColor: "rgba(96, 165, 69, 1)",
                        backgroundColor: "rgba(96, 165, 69, 0.05)",
                        borderWidth: 1,
                        yAxisID: 'fragPct',
                        fill: false,
                        pointRadius: 2,
                        pointHoverRadius: 4
                    },
                    {
                        label: 'Gen 2',
                        data: fragmentationSeriesCache.gen2,
                        borderColor: "rgba(141, 31, 95, 1)",
                        backgroundColor: "rgba(141, 31, 95, 0.05)",
                        borderWidth: 1,
                        yAxisID: 'fragPct',
                        fill: false,
                        pointRadius: 2,
                        pointHoverRadius: 4
                    },
                    {
                        label: 'LOH',
                        data: fragmentationSeriesCache.loh,
                        borderColor: "rgba(201, 221, 84, 1)",
                        backgroundColor: "rgba(201, 221, 84, 0.05)",
                        borderWidth: 1,
                        yAxisID: 'fragPct',
                        fill: false,
                        pointRadius: 2,
                        pointHoverRadius: 4
                    },
                    {
                        label: 'Pinned Objects',
                        data: fragmentationSeriesCache.pinnedCount,
                        borderColor: "rgba(255, 165, 0, 0.8)",
                        backgroundColor: "rgba(255, 165, 0, 0.05)",
                        borderWidth: 1,
                        borderDash: [4, 4],
                        yAxisID: 'pinnedCount',
                        fill: false,
                        pointRadius: 2,
                        pointHoverRadius: 4
                    },
                    {
                        label: 'Compaction',
                        data: fragmentationSeriesCache.compactionMarker,
                        backgroundColor: "rgba(255, 165, 0, 0.9)",
                        borderColor: "rgba(255, 165, 0, 1)",
                        pointRadius: 8,
                        pointStyle: 'triangle',
                        showLine: false,
                        yAxisID: 'fragPct',
                        fill: false
                    }
                ]
            },
            plugins: [createZoomSelectionPlugin(dragStateHolder)],
            options: {
                title: {
                    display: true,
                    text: 'Heap Fragmentation by Generation'
                },
                scales: {
                    xAxes: [{
                        type: 'linear',
                        position: 'bottom',
                        ticks: xAxisTicks,
                        scaleLabel: {
                            display: true,
                            labelString: "Capture Time Elapsed"
                        }
                    }],
                    yAxes: [
                        {
                            id: 'fragPct',
                            position: 'left',
                            ticks: {
                                beginAtZero: true,
                                max: 100,
                                callback: function (value) { return value + '%'; }
                            },
                            scaleLabel: {
                                display: true,
                                labelString: 'Fragmentation %'
                            }
                        },
                        {
                            id: 'pinnedCount',
                            position: 'right',
                            ticks: {
                                beginAtZero: true
                            },
                            scaleLabel: {
                                display: true,
                                labelString: 'Pinned Object Count'
                            },
                            gridLines: {
                                drawOnChartArea: false
                            }
                        }
                    ]
                },
                tooltips: {
                    callbacks: {
                        title: linearGcTooltipTitle
                    }
                },
                animation: { duration: 0 },
                maintainAspectRatio: false
            }
        });

        var zoomHandle = attachDragToZoom(chart, canvasElement, dragStateHolder, pixelToMSecLinear, onGcChartsRangeSelected);
        return { chart: chart, zoomHandle: zoomHandle };
    }

    var lohTypesSection = document.getElementById("lohTypesSection");
    if (lohTypesSection && allocationSummaryJson && allocationSummaryJson["topTypes"]) {
        var lohTypes = [];
        var topTypes = allocationSummaryJson["topTypes"];

        for (var topTypeIndex = 0; topTypeIndex < topTypes.length; ++topTypeIndex) {
            if (topTypes[topTypeIndex]["LargeCount"] > 0) {
                lohTypes.push(topTypes[topTypeIndex]);
            }
        }

        lohTypes.sort(function (leftType, rightType) { return rightType["LargeCount"] - leftType["LargeCount"]; });

        if (lohTypes.length === 0) {
            lohTypesSection.innerHTML = '<p>No LOH allocations detected in this capture.</p>';
        } else {
            var lohMb = 1024 * 1024;
            var lohKb = 1024;

            var lohHeaderCells = '<th>Type</th><th>LOH Ticks</th><th>Small Ticks</th><th>Pinned Ticks</th><th>Total Ticks</th><th>Total Sampled Bytes (all kinds)</th>';
            var lohRows = '';

            for (var lohIndex = 0; lohIndex < lohTypes.length; ++lohIndex) {
                var lohType = lohTypes[lohIndex];
                var totalBytes = lohType["TotalBytes"];
                var bytesLabel = totalBytes >= lohMb
                    ? (totalBytes / lohMb).toFixed(2) + ' MB'
                    : (totalBytes / lohKb).toFixed(2) + ' KB';

                lohRows += '<tr>' +
                    '<td>' + lohType["TypeName"] + '</td>' +
                    '<td>' + lohType["LargeCount"] + '</td>' +
                    '<td>' + lohType["SmallCount"] + '</td>' +
                    '<td>' + lohType["PinnedCount"] + '</td>' +
                    '<td>' + lohType["TickCount"] + '</td>' +
                    '<td>' + bytesLabel + '</td>' +
                    '</tr>';
            }

            lohTypesSection.innerHTML = '<div class="detailTable"><table>' +
                '<tr class="tableHeader">' + lohHeaderCells + '</tr>' +
                lohRows +
                '</table></div>';
        }
    }

    // heapIndex -> [gen0, gen1, gen2, loh] point series - built once per
    // heap, the first time that heap's chart is actually rendered (see
    // renderHeapChart), then reused by every later zoom-triggered rebuild.
    var heapChartSeriesCache = {};

    function buildHeapSeries(passedHeapIndex) {
        var mb = 1024;

        return [
            buildGcPointSeries(function (gcData) { return gcData["Heaps"][passedHeapIndex]["Generations"][0]["SizeAfter"] / mb; }),
            buildGcPointSeries(function (gcData) { return gcData["Heaps"][passedHeapIndex]["Generations"][1]["SizeAfter"] / mb; }),
            buildGcPointSeries(function (gcData) { return gcData["Heaps"][passedHeapIndex]["Generations"][2]["SizeAfter"] / mb; }),
            buildGcPointSeries(function (gcData) { return gcData["Heaps"][passedHeapIndex]["Generations"][3]["SizeAfter"] / mb; })
        ];
    }

    function renderHeapChart(passedHeapIndex, zoomRange) {
        if (!heapChartSeriesCache[passedHeapIndex]) {
            heapChartSeriesCache[passedHeapIndex] = buildHeapSeries(passedHeapIndex);
        }
        var series = heapChartSeriesCache[passedHeapIndex];

        var canvasElement = heapCharts[passedHeapIndex];

        var xAxisTicks = { callback: formatElapsedMs };
        if (zoomRange) {
            xAxisTicks.min = zoomRange.startMSec;
            xAxisTicks.max = zoomRange.endMSec;
        }

        var dragStateHolder = { current: null };

        var chart = new Chart(canvasElement.getContext('2d'), {
            type: 'line',
            data: {
                datasets: [{
                    label: 'Gen 0',
                    data: series[0],
                    backgroundColor: [
                        'rgba(54, 162, 235, 0.2)',
                    ],
                    borderWidth: 1,
                    pointRadius: 2,
                    pointHoverRadius: 4
                },
                {
                    label: "Gen 1",
                    data: series[1],
                    backgroundColor: [
                        'rgba(75, 192, 192, 0.2)'
                    ],
                    borderWidth: 1,
                    pointRadius: 2,
                    pointHoverRadius: 4
                },
                {
                    label: "Gen 2",
                    data: series[2],
                    backgroundColor: [
                        'rgba(153, 102, 255, 0.2)'
                    ],
                    borderWidth: 1,
                    pointRadius: 2,
                    pointHoverRadius: 4
                },
                {
                    label: "LOH",
                    data: series[3],
                    backgroundColor: [
                        'rgba(255, 206, 86, 0.2)'
                    ],
                    borderWidth: 1,
                    pointRadius: 2,
                    pointHoverRadius: 4
                }
            ]},
            plugins: [createZoomSelectionPlugin(dragStateHolder)],
            options: {
                title: {
                    display: true,
                    text: `Heap: ${passedHeapIndex}`
                },
                scales: {
                    xAxes: [{
                        type: 'linear',
                        position: 'bottom',
                        ticks: xAxisTicks,
                        scaleLabel: {
                            display: true,
                            labelString: "Capture Time Elapsed"
                        }
                    }],
                    yAxes: [{
                        ticks: {
                            beginAtZero: true
                        },
                        scaleLabel: {
                            display: true,
                            labelString: "Memory Usage in MB"
                        }
                    }],
                },
                tooltips: {
                    callbacks: {
                        title: linearGcTooltipTitle
                    }
                },
                animation: { duration: 0 },
                "maintainAspectRatio": false,
            }
        });

        var zoomHandle = attachDragToZoom(chart, canvasElement, dragStateHolder, pixelToMSecLinear, onGcChartsRangeSelected);
        return { chart: chart, zoomHandle: zoomHandle };
    }

    var heapCharts = document.getElementsByClassName("heapChart");

    // Initial (unzoomed) build of the Pause Time / Usage / Fragmentation
    // charts - mirrors renderHeapContentsCharts(null)'s role for the Heap
    // Contents view. Runs before the per-heap setup below so sharedZoomRange
    // is settled (still null, nothing to zoom yet) before any heap chart
    // reads it. Calls renderGcCharts directly, not applySharedZoom - the
    // Heap Contents view has nothing to rebuild yet at page load (it's
    // injected lazily on first open - see the viewNavButton handler below).
    renderGcCharts(null);

    // Click equivalent of the Backspace zoom-reset above - for anyone who
    // doesn't know/want to use the keyboard shortcut. Only visible while a
    // zoom is actually applied (see updateGcZoomStatusUi). The button is
    // part of the GC view's static markup (not lazily injected), so this can
    // be wired up right away.
    var resetGcZoomButton = document.getElementById('resetGcZoomButton');
    if (resetGcZoomButton) {
        resetGcZoomButton.addEventListener('click', function () {
            applySharedZoom(null);
        });
    }

    // Server GC traces can have 16+ heaps, each getting its own Chart.js
    // instance - building all of them synchronously on load is expensive and
    // most users never scroll down to see every heap. Defer each chart's
    // construction until its canvas actually scrolls into view. Whatever
    // zoom is currently applied (sharedZoomRange) is used immediately, so a
    // heap chart that first appears while already zoomed doesn't flash the
    // full range before narrowing.
    if ('IntersectionObserver' in window) {
        var heapChartObserver = new IntersectionObserver(function (entries, observer) {
            for (var entryIndex = 0; entryIndex < entries.length; ++entryIndex) {
                var entry = entries[entryIndex];
                if (entry.isIntersecting) {
                    var heapIndex = parseInt(entry.target.getAttribute('data-heap-index'), 10);
                    heapChartHandlesByIndex[heapIndex] = renderHeapChart(heapIndex, sharedZoomRange);
                    observer.unobserve(entry.target);
                }
            }
        }, { rootMargin: '200px' });

        for (var heapObserveIndex = 0; heapObserveIndex < heapCharts.length; ++heapObserveIndex) {
            heapCharts[heapObserveIndex].setAttribute('data-heap-index', heapObserveIndex);
            heapChartObserver.observe(heapCharts[heapObserveIndex]);
        }
    } else {
        for (var index = 0; index < heapCharts.length; ++index) {
            heapChartHandlesByIndex[index] = renderHeapChart(index, sharedZoomRange);
        }
    }

    // The Detailed tab's markup arrives as inert commented-out text (see
    // GcSnapshotRenderer.ts) rather than live HTML, so the browser doesn't
    // have to parse/build a DOM node for every GC's row (or, for the
    // generation-breakdown tables appended below it, every heap x
    // generation x field combination) on page load - only do that once,
    // the first time the tab is actually opened.
    var detailTableInjected = false;

    var MB = 1024 * 1024;

    // Mirrors GcDetailTableRenderer.ts's severity thresholds exactly, so the
    // GC Number column on the generation-breakdown tables below can carry
    // the same at-a-glance coloring the GC summary table already
    // establishes for that GC, without duplicating whole-row logic here.
    var getSeverityClass = function (pauseTime) {
        if (pauseTime > 200.0) {
            return "expensiveGc";
        }
        if (pauseTime > 100.0) {
            return "warnGc";
        }
        if (pauseTime > 50.0) {
            return "interstingGc";
        }
        if (pauseTime > 20.0) {
            return "somewhatInterestingGc";
        }
        if (pauseTime > 10.0) {
            return "notSomewhatInterestingGc";
        }
        return "";
    };

    // Per-generation fields available on Heaps[].Generations[genIndex] (see
    // GcJsonExporter.cs / gcDataFromXml - both sources produce this same
    // shape). "Common" is the default view; the full list is one click away
    // via #genFieldsToggle rather than always rendering all 56 (14 fields x
    // 4 generations) columns.
    var COMMON_GEN_FIELDS = [
        { key: "NewAllocation", label: "New Allocation" },
        { key: "SurvRate", label: "Surv Rate", isPercent: true },
        { key: "In", label: "In" },
        { key: "Out", label: "Out" },
        { key: "Fragmentation", label: "Fragmentation" }
    ];

    var ALL_GEN_FIELDS = [
        { key: "SizeBefore", label: "Size Before" },
        { key: "ObjSpaceBefore", label: "Obj Space Before" },
        { key: "Fragmentation", label: "Fragmentation" },
        { key: "FreeListSpaceBefore", label: "Free List Space Before" },
        { key: "FreeListSpaceAfter", label: "Free List Space After" },
        { key: "FreeObjSpaceBefore", label: "Free Obj Space Before" },
        { key: "FreeObjSpaceAfter", label: "Free Obj Space After" },
        { key: "ObjSizeAfter", label: "Obj Size After" },
        { key: "In", label: "In" },
        { key: "Out", label: "Out" },
        { key: "NewAllocation", label: "New Allocation" },
        { key: "SurvRate", label: "Surv Rate", isPercent: true },
        { key: "PinnedSurv", label: "Pinned Surv" },
        { key: "NonePinnedSurv", label: "Non-Pinned Surv" }
    ];

    var GENERATION_LABELS = ["Gen0", "Gen1", "Gen2", "LOH"];

    // heapIndex === -1 means "All Heaps": byte fields summed across every
    // heap the GC reports, SurvRate averaged across heaps. There's no
    // separately-decoded GC-level total for these fields the way there is
    // for GenerationSize0-3/LOH/POH (those come from a distinct GCHeapStats
    // event) - summing/averaging the per-heap breakdown is the only way to
    // get one number per generation across the whole GC.
    function computeGenFieldValue(heaps, heapIndex, genIndex, field) {
        if (heapIndex === -1) {
            if (!heaps || heaps.length === 0) {
                return null;
            }

            var total = 0;
            var count = 0;
            for (var h = 0; h < heaps.length; ++h) {
                var gen = heaps[h]["Generations"][genIndex];
                if (gen === undefined || gen === null) {
                    continue;
                }
                total += parseFloat(gen[field.key]) || 0;
                ++count;
            }

            if (count === 0) {
                return null;
            }

            return field.isPercent ? (total / count) : total;
        }

        if (!heaps || heapIndex >= heaps.length) {
            return null;
        }

        var heapGen = heaps[heapIndex]["Generations"][genIndex];
        if (heapGen === undefined || heapGen === null) {
            return null;
        }

        return parseFloat(heapGen[field.key]) || 0;
    }

    function formatGenFieldValue(value, field) {
        if (value === null) {
            return "&ndash;";
        }

        return field.isPercent ? value.toFixed(1) : (value / MB).toFixed(2);
    }

    // Built entirely from data already in `gcs` (hiddenData) - there's no
    // separate server-rendered payload for these at all, so this costs
    // nothing until the Detailed tab is actually opened. Uses the
    // .detailTable *class* (matching renderGcDetailTable's own wrapper), not
    // an id - multiple tables share the Detailed panel now, so an id would
    // collide.
    function buildGenerationBreakdownTable(heapIndex, showAllFields) {
        if (gcs.length === 0) {
            return "<p>No GC events to display.</p>";
        }

        var fields = showAllFields ? ALL_GEN_FIELDS : COMMON_GEN_FIELDS;

        var headerCells = "<th>GC Number</th><th>DateTime</th>";
        for (var genIndex = 0; genIndex < 4; ++genIndex) {
            for (var fieldIndex = 0; fieldIndex < fields.length; ++fieldIndex) {
                var field = fields[fieldIndex];
                var unit = field.isPercent ? "%" : "mb";
                headerCells += `<th>${field.label} ${GENERATION_LABELS[genIndex]} (${unit})</th>`;
            }
        }

        var rows = "";
        for (var index = 0; index < gcs.length; ++index) {
            var gcEntry = gcs[index]["data"];
            var heaps = gcEntry["Heaps"];

            var severityClass = getSeverityClass(parseFloat(gcEntry["PauseDurationMSec"]));
            var rowClass = severityClass ? ` class="${severityClass}"` : "";
            var rowCells = `<td>${gcEntry["Id"]}</td><td>${formatHumanDateTime(gcEntry["DateTime"])}</td>`;
            for (var rowGenIndex = 0; rowGenIndex < 4; ++rowGenIndex) {
                for (var rowFieldIndex = 0; rowFieldIndex < fields.length; ++rowFieldIndex) {
                    var rowField = fields[rowFieldIndex];
                    var value = computeGenFieldValue(heaps, heapIndex, rowGenIndex, rowField);
                    rowCells += `<td>${formatGenFieldValue(value, rowField)}</td>`;
                }
            }

            rows += `<tr${rowClass}>${rowCells}</tr>`;
        }

        return `<div class="detailTable"><table><tr class="tableHeader">${headerCells}</tr>${rows}</table></div>`;
    }

    // "All Heaps" first, then one table per heap in heap-number order, all
    // stacked in the same view rather than split across separate tabs.
    function buildAllGenerationBreakdownTables(showAllFields) {
        var maxHeapCount = 0;
        for (var index = 0; index < gcs.length; ++index) {
            var heaps = gcs[index]["data"]["Heaps"];
            if (heaps && heaps.length > maxHeapCount) {
                maxHeapCount = heaps.length;
            }
        }

        var html = `<h3 class="detailTableHeading">Generations: All Heaps</h3>` + buildGenerationBreakdownTable(-1, showAllFields);
        for (var heapIndex = 0; heapIndex < maxHeapCount; ++heapIndex) {
            html += `<h3 class="detailTableHeading">Generations: Heap ${heapIndex}</h3>` + buildGenerationBreakdownTable(heapIndex, showAllFields);
        }

        return html;
    }

    var showAllGenFields = false;

    function renderGenerationBreakdownSection() {
        document.getElementById("generationBreakdownSection").innerHTML = buildAllGenerationBreakdownTables(showAllGenFields);
    }

    // Click-to-sort, shared by every lazily-injected table on this page
    // (the Detailed tab's per-GC table, and the Profile tab's Hot Methods
    // table - see CpuProfileRenderer.ts). Each table is only ever built
    // once per webview session, so this reorders the already-rendered <tr>
    // elements in place rather than re-deriving values from the original
    // source array - matching the render-once/mutate-the-DOM approach the
    // rest of this lazy-inject path already uses.
    function detailTableSortValue(cell, sortType) {
        if (sortType === 'date') {
            // The DateTime cell's own data-raw attribute (an ISO-8601
            // timestamp or a zero-padded "+elapsed" string - see
            // GcDetailTableRenderer.ts) sorts correctly as plain text; the
            // human-formatted display text (e.g. "21-Jul-2026 03:42:13 PM
            // PDT") does not.
            return cell.getAttribute('data-raw') || '';
        }

        if (sortType === 'number') {
            var parsed = parseFloat(cell.textContent);
            return isNaN(parsed) ? -Infinity : parsed;
        }

        return cell.textContent.toLowerCase();
    }

    function sortDetailTableByColumn(table, columnIndex, sortType, ascending) {
        var tbody = table.tBodies[0] || table;
        // Snapshots the live HTMLCollection before any row gets moved -
        // table.rows[0] is the header row, left untouched. Skip .callPathsDetail
        // rows: they're paired with their method/data row and moved along
        // with it below, not sorted independently (they'd sort to a random
        // position relative to their method row otherwise).
        var allRows = Array.prototype.slice.call(table.rows, 1);
        var dataRows = [];
        for (var filterIndex = 0; filterIndex < allRows.length; ++filterIndex) {
            if (!allRows[filterIndex].classList.contains('callPathsDetail')) {
                dataRows.push(allRows[filterIndex]);
            }
        }

        dataRows.sort(function (rowA, rowB) {
            var valueA = detailTableSortValue(rowA.cells[columnIndex], sortType);
            var valueB = detailTableSortValue(rowB.cells[columnIndex], sortType);

            var comparison = 0;
            if (valueA < valueB) {
                comparison = -1;
            } else if (valueA > valueB) {
                comparison = 1;
            }

            return ascending ? comparison : -comparison;
        });

        // appendChild on a node already in the tree moves it - iterating in
        // the desired final order and re-appending each row leaves the
        // header (never touched) first and every data row following in
        // sorted order. For expandable rows (CPU hot-methods table), the
        // paired callPathsDetail row is moved immediately after its method row
        // so it stays correctly associated after the sort.
        for (var rowIndex = 0; rowIndex < dataRows.length; ++rowIndex) {
            tbody.appendChild(dataRows[rowIndex]);
            var pairedDetailId = dataRows[rowIndex].getAttribute('data-cpu-method-target') || dataRows[rowIndex].getAttribute('data-contention-target');
            if (pairedDetailId) {
                var pairedDetailRow = document.getElementById(pairedDetailId);
                if (pairedDetailRow) {
                    tbody.appendChild(pairedDetailRow);
                }
            }
        }
    }

    function setupDetailTableSortHandlers(container) {
        var table = container.querySelector(".detailTable table");
        if (!table) {
            return;
        }

        // Scoped to this one call/table (not module-level) - two distinct
        // tables (Detailed tab, Profile tab's Hot Methods table) each get
        // their own independent "which column, which direction" state, so
        // sorting one table's column 2 doesn't leave a stale ascending/
        // descending toggle for an unrelated table's own column 2.
        var currentSortColumnIndex = -1;
        var currentSortAscending = true;

        var headerCells = table.rows[0].cells;
        for (var headerIndex = 0; headerIndex < headerCells.length; ++headerIndex) {
            var headerCell = headerCells[headerIndex];

            // The row-hide button column's own <th> is a bare, unlabeled
            // cell with no data-sort attribute (see e.g.
            // CpuProfileRenderer.ts's headerWithHideColumn) - skip it here
            // rather than wiring a click handler that would call
            // sortDetailTableByColumn with a null sortType and then throw
            // reaching for a sortIndicator span this cell doesn't have.
            if (!headerCell.hasAttribute('data-sort')) {
                continue;
            }

            (function (columnIndex, headerCell) {
                headerCell.addEventListener('click', function () {
                    var ascending = (currentSortColumnIndex === columnIndex) ? !currentSortAscending : true;
                    sortDetailTableByColumn(table, columnIndex, headerCell.getAttribute('data-sort'), ascending);

                    // The row-hide column's own blank <th> (skipped above,
                    // never gets a click listener of its own) has no
                    // .sortIndicator span at all - guard against it here too,
                    // since this loop walks every header cell unconditionally
                    // regardless of which one was actually clicked.
                    for (var clearIndex = 0; clearIndex < headerCells.length; ++clearIndex) {
                        var indicatorToClear = headerCells[clearIndex].getElementsByClassName('sortIndicator')[0];
                        if (indicatorToClear) {
                            indicatorToClear.textContent = '';
                        }
                    }
                    headerCell.getElementsByClassName('sortIndicator')[0].textContent = ascending ? ' ▲' : ' ▼';

                    currentSortColumnIndex = columnIndex;
                    currentSortAscending = ascending;
                });
            })(headerIndex, headerCell);
        }
    }

    var genFieldsToggle = document.getElementById("genFieldsToggle");
    genFieldsToggle.addEventListener('click', function () {
        showAllGenFields = !showAllGenFields;
        genFieldsToggle.textContent = showAllGenFields ? "Show Common Fields" : "Show All Fields";

        // Only the generation-breakdown tables have a curated/full mode -
        // the GC summary table above them is unaffected, so only rebuild
        // this section, and only if the Detailed tab has actually been
        // opened at least once already.
        if (detailTableInjected) {
            renderGenerationBreakdownSection();
        }
    });

    var tabButtons = document.getElementsByClassName("tabButton");
    for (var tabIndex = 0; tabIndex < tabButtons.length; ++tabIndex) {
        tabButtons[tabIndex].addEventListener('click', function (event) {
            var targetTab = event.currentTarget.getAttribute('data-tab');

            var buttons = document.getElementsByClassName("tabButton");
            for (var buttonIndex = 0; buttonIndex < buttons.length; ++buttonIndex) {
                buttons[buttonIndex].classList.remove('active');
            }

            var panels = document.getElementsByClassName("tabPanel");
            for (var panelIndex = 0; panelIndex < panels.length; ++panelIndex) {
                panels[panelIndex].classList.remove('active');
            }

            event.currentTarget.classList.add('active');
            document.getElementById('tab-' + targetTab).classList.add('active');

            // Only the Detailed tab's generation-breakdown tables have a
            // curated/full field mode - no point showing the toggle while
            // looking at Charts.
            genFieldsToggle.style.display = (targetTab === 'detailed') ? 'inline-block' : 'none';

            if (targetTab === 'detailed' && !detailTableInjected) {
                var holder = document.getElementById("detailTableHtml");
                var detailTableHtml = holder.innerHTML.slice(4, holder.innerHTML.length - 3);
                var detailedPanel = document.getElementById('tab-detailed');
                detailedPanel.innerHTML = detailTableHtml + '<div id="generationBreakdownSection"></div>';

                // GcDetailTableRenderer.ts emits the raw DateTime string on
                // each cell rather than pre-formatting it - formatting 1000+
                // rows via Intl.DateTimeFormat is cheap once, here, on first
                // open, but was costing that same work on every
                // extension-host render when done server-side.
                var dateTimeCells = detailedPanel.getElementsByClassName("gcDateTimeCell");
                for (var dateTimeCellIndex = 0; dateTimeCellIndex < dateTimeCells.length; ++dateTimeCellIndex) {
                    var dateTimeCell = dateTimeCells[dateTimeCellIndex];
                    dateTimeCell.textContent = formatHumanDateTime(dateTimeCell.getAttribute('data-raw'));
                }

                setupDetailTableSortHandlers(detailedPanel);

                // Row-hide button + "Show all" - delegated on detailedPanel
                // (a stable ancestor) since it's only ever injected once,
                // same reasoning as the Allocation/Exceptions tables' own
                // direct-listener-on-container choice.
                detailedPanel.addEventListener('click', function (event) {
                    if (event.target.closest('#gcDetailShowAllBtn')) {
                        gcRowHider.reset();
                        return;
                    }

                    // Whole cell is the click target, not just the ✕ glyph
                    // itself - a small icon-only hit target is easy to miss.
                    var hideCell = event.target.closest('.rowHideColumn');
                    if (!hideCell) {
                        return;
                    }

                    var hideRow = hideCell.closest('[data-gc-index]');
                    if (hideRow) {
                        gcRowHider.toggle(parseInt(hideRow.getAttribute('data-gc-index'), 10));
                    }
                });

                renderGenerationBreakdownSection();
                detailTableInjected = true;

                // A GC chart zoom may already have been applied on the
                // Charts tab before this tab was ever opened - filter to it
                // immediately rather than showing every row until the next
                // zoom change.
                filterDetailTableToZoomRange();
            }
        });
    }

    // Left-side view switcher (GC / Heap Contents / eventually Profile) -
    // an axis orthogonal to the tabButton/tabPanel handling above: the GC
    // view's own Charts/Detailed tabs are unaffected and live one level
    // deeper, inside #view-gc. Same show/hide-via-active-class mechanism,
    // keyed on data-view/id="view-*" instead of data-tab/id="tab-*".
    var allocationSummaryInjected = false;
    var exceptionSummaryInjected = false;
    var cpuProfileInjected = false;
    var contentionInjected = false;
    var threadingInjected = false;

    // { chart, zoomHandle } for every currently-rendered Heap Contents
    // chart - detached/destroyed and rebuilt on every zoom change (renderHeapContentsCharts
    // below), since the same <canvas> elements persist across zoom changes
    // and stale listeners/instances would otherwise pile up on them.
    var heapContentsChartHandles = [];

    // Computed once, the first time the Heap Contents view is opened - gcs
    // doesn't change after that, so there's no need to recompute this on
    // every zoom change.
    var gen0GcTimesMSecForCharts = null;
    var gen1GcTimesMSecForCharts = null;

    function destroyHeapContentsCharts() {
        for (var handleIndex = 0; handleIndex < heapContentsChartHandles.length; ++handleIndex) {
            var handle = heapContentsChartHandles[handleIndex];
            if (handle.zoomHandle) {
                handle.zoomHandle.detach();
            }
            handle.chart.destroy();
        }
        heapContentsChartHandles = [];
    }

    function onHeapContentsRangeSelected(startMSec, endMSec) {
        applySharedZoom({ startMSec: startMSec, endMSec: endMSec });
    }

    function updateZoomStatusUi(zoomRange) {
        var statusBar = document.getElementById("allocationZoomStatus");
        if (!statusBar) {
            return;
        }

        if (!zoomRange) {
            statusBar.style.display = "none";
            return;
        }

        statusBar.style.display = "block";
        var label = statusBar.getElementsByClassName("allocationZoomStatusLabel")[0];
        if (label) {
            label.textContent = "Zoomed: " + formatElapsedMsForAllocationChart(zoomRange.startMSec) +
                " – " + formatElapsedMsForAllocationChart(zoomRange.endMSec) + " (Backspace to reset)";
        }
    }

    // Manually-hidden Allocation ranked-type rows, one controller per scope
    // ("all"/"loh") since each scope is its own independent table/tiles.
    // getAllocationTypeHider resolves which instance a given scope string
    // means - onChange re-invokes the existing zoom-recompute path
    // (updateRankedTypesTables) rather than a separate rebuild function, so
    // hiding composes with whatever zoom is currently active for free.
    var allocationTypeHiderAll = createRowHideController('allocationTypeHideStatus-all', 'allocationTypeHideStatusLabel-all', function () {
        updateRankedTypesTables(sharedZoomRange);
    });
    var allocationTypeHiderLoh = createRowHideController('allocationTypeHideStatus-loh', 'allocationTypeHideStatusLabel-loh', function () {
        updateRankedTypesTables(sharedZoomRange);
    });

    function getAllocationTypeHider(scope) {
        return scope === 'loh' ? allocationTypeHiderLoh : allocationTypeHiderAll;
    }

    // Recomputes each type's byte total (and a matching grand total to
    // percentage against) for zoomRange - or, when null, just wraps
    // summaryScope's own server-computed topTypes/totalSampledBytes
    // unchanged. Both branches return the same shape: an array of
    // {typeIndex, TypeName, TotalBytes} sorted by TotalBytes descending
    // (typeIndex is always the *original* position in topTypes/typeDrillDown,
    // preserved through the re-sort so a zoomed row still resolves the right
    // drill-down entry), plus grandTotalBytes to percentage each row against.
    //
    // Only possible because typeTimeline.buckets carries a real per-type
    // byte breakdown per time bucket (see allocationStats.js's
    // renderAllocationTypeTimelineChart, which reads the same field) -
    // there's no equivalent per-bucket breakdown for tick/small/large/
    // pinned counts, so those stay whole-capture-only (see
    // updateRankedTypesTables's ticksOnlyColumn hiding).
    //
    // hider's hidden types are dropped from the returned types array
    // entirely (not display:none'd - this function's own caller,
    // updateOneRankedTypesTable, already fully wipes and rebuilds the
    // table's rows from its return value on every call, so there's no row
    // index stability to preserve the way the CPU Methods table has to)
    // and subtracted from grandTotalBytes, so both the ranked list AND the
    // % of Sampled denominator reflect only what's still visible.
    function computeZoomedTypeStats(summaryScope, zoomRange, hider) {
        var topTypes = summaryScope["topTypes"];

        if (!zoomRange) {
            var wholeCaptureTypes = [];
            var wholeGrandTotalBytes = summaryScope["totalSampledBytes"];
            for (var wholeIndex = 0; wholeIndex < topTypes.length; ++wholeIndex) {
                if (hider.isHidden(wholeIndex)) {
                    wholeGrandTotalBytes -= topTypes[wholeIndex]["TotalBytes"];
                    continue;
                }

                // Tick/Small/Large/Pinned counts carried straight through
                // (unlike the zoomed branch below, the real whole-capture
                // values are available here - see renderRankedTypesTableRows,
                // which renders them only when present on typeStats).
                wholeCaptureTypes.push({
                    typeIndex: wholeIndex,
                    TypeName: topTypes[wholeIndex]["TypeName"],
                    TotalBytes: topTypes[wholeIndex]["TotalBytes"],
                    TickCount: topTypes[wholeIndex]["TickCount"],
                    SmallCount: topTypes[wholeIndex]["SmallCount"],
                    LargeCount: topTypes[wholeIndex]["LargeCount"],
                    PinnedCount: topTypes[wholeIndex]["PinnedCount"]
                });
            }
            // Already sorted server-side by TotalBytes descending - no
            // re-sort needed (dropping hidden entries doesn't disturb the
            // relative order of the rest).
            return { types: wholeCaptureTypes, grandTotalBytes: wholeGrandTotalBytes };
        }

        var typeTimeline = summaryScope["typeTimeline"];
        var buckets = typeTimeline["buckets"];
        var bucketWidthMSec = typeTimeline["bucketWidthMSec"] || 0;

        // One extra slot beyond topTypes.length for "Other" (typeTimeline's
        // own last type - see allocationStats.js) - not itself rendered as
        // a ranked row (topTypes never includes it either), but its bytes
        // still belong in grandTotalBytes so the zoomed "% of Sampled"
        // figure means the same thing the unzoomed one does: share of every
        // sampled byte in range, not just the ranked subset of it.
        var bytesByTypeIndex = new Array(typeTimeline["types"].length).fill(0);
        for (var bucketIndex = 0; bucketIndex < buckets.length; ++bucketIndex) {
            var bucket = buckets[bucketIndex];
            var bucketStartMSec = bucket["bucketStartMSec"];
            var bucketEndMSec = bucketStartMSec + bucketWidthMSec;
            // Same overlap test as renderAllocationTypeTimelineChart's own
            // bucket filter (allocationStats.js) - a bucket qualifies if it
            // overlaps the zoom window at all, not just if its own start
            // falls inside it.
            if (bucketEndMSec <= zoomRange.startMSec || bucketStartMSec >= zoomRange.endMSec) {
                continue;
            }

            var bytesByType = bucket["bytesByType"];
            for (var typeIdx = 0; typeIdx < bytesByType.length; ++typeIdx) {
                bytesByTypeIndex[typeIdx] += bytesByType[typeIdx];
            }
        }

        var grandTotalBytes = 0;
        for (var sumIndex = 0; sumIndex < bytesByTypeIndex.length; ++sumIndex) {
            grandTotalBytes += bytesByTypeIndex[sumIndex];
        }

        var zoomedTypes = [];
        for (var topIndex = 0; topIndex < topTypes.length; ++topIndex) {
            if (hider.isHidden(topIndex)) {
                grandTotalBytes -= (bytesByTypeIndex[topIndex] || 0);
                continue;
            }

            zoomedTypes.push({
                typeIndex: topIndex,
                TypeName: topTypes[topIndex]["TypeName"],
                TotalBytes: bytesByTypeIndex[topIndex] || 0
            });
        }
        zoomedTypes.sort(function (left, right) { return right.TotalBytes - left.TotalBytes; });

        return { types: zoomedTypes, grandTotalBytes: grandTotalBytes };
    }

    // Rebuilds one ranked-types table's <tr> rows (everything after its own
    // header row) from zoomedStats, and toggles allocationTypeTableZoomed on
    // the table itself so the CSS-hidden Tick/Small/Large/Pinned columns
    // (ticksOnlyColumn - see AllocationSummaryRenderer.ts) show or hide to
    // match. Matches AllocationSummaryRenderer.ts's renderTypeBreakdownPanel
    // row markup exactly (same classes/attributes), just built client-side
    // from possibly-recomputed data instead of the server-rendered original.
    function renderRankedTypesTableRows(zoomedStats, scope) {
        var mb = 1024 * 1024;
        var rowsHtml = "";

        for (var rowIndex = 0; rowIndex < zoomedStats.types.length; ++rowIndex) {
            var typeStats = zoomedStats.types[rowIndex];
            var totalBytes = typeStats["TotalBytes"];
            var percentOfSampled = zoomedStats.grandTotalBytes > 0 ? (totalBytes * 100.0) / zoomedStats.grandTotalBytes : 0;

            var tdTotalBytes = (totalBytes / mb).toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 });
            var tdPercent = percentOfSampled.toFixed(2);

            // Only the unzoomed branch of computeZoomedTypeStats carries
            // these through (the zoomed branch has no per-bucket data for
            // them - see computeZoomedTypeStats's own comment) - undefined
            // here means "leave the CSS-hidden cells empty", matching the
            // ticksOnlyColumn hide rule these same cells already carry.
            var tdTickCount = typeStats.TickCount !== undefined ? typeStats.TickCount : "";
            var tdSmallCount = typeStats.SmallCount !== undefined ? typeStats.SmallCount : "";
            var tdLargeCount = typeStats.LargeCount !== undefined ? typeStats.LargeCount : "";
            var tdPinnedCount = typeStats.PinnedCount !== undefined ? typeStats.PinnedCount : "";

            rowsHtml += `<tr class="typeRow" data-type-index="${typeStats.typeIndex}" data-scope="${scope}">` +
                `<td class="rowHideColumn"><button class="rowHideBtn" type="button" title="Hide this row">&#10005;</button></td>` +
                `<td>${typeStats.TypeName}</td>` +
                `<td>${tdTotalBytes}</td>` +
                `<td>${tdPercent}</td>` +
                `<td class="ticksOnlyColumn">${tdTickCount}</td><td class="ticksOnlyColumn">${tdSmallCount}</td><td class="ticksOnlyColumn">${tdLargeCount}</td><td class="ticksOnlyColumn">${tdPinnedCount}</td>` +
                `</tr>`;
        }

        return rowsHtml;
    }

    function updateOneRankedTypesTable(summaryScope, scope, zoomRange) {
        var table = document.getElementById("allocationTypeTable-" + scope);
        if (!table) {
            return;
        }

        var hider = getAllocationTypeHider(scope);
        var zoomedStats = computeZoomedTypeStats(summaryScope, zoomRange, hider);
        var headerRow = table.getElementsByClassName("tableHeader")[0];
        table.innerHTML = "";
        table.appendChild(headerRow);
        table.insertAdjacentHTML("beforeend", renderRankedTypesTableRows(zoomedStats, scope));
        table.classList.toggle("allocationTypeTableZoomed", !!zoomRange);

        // Total/Distinct Types tiles are deliberately kept zoom-INDEPENDENT
        // (a plain zoom change has always left them frozen at whole-capture
        // values - see this function's callers) but DO get adjusted for
        // hidden rows, using the whole-capture topTypes/totalSampledBytes
        // rather than zoomedStats.grandTotalBytes (which is the zoomed
        // window's own total, a different number). This mirrors the CPU
        // Methods table's own tiles, which are likewise always against the
        // full totalSampleCount regardless of the CPU timeline's zoom.
        var mb = 1024 * 1024;
        var topTypesForTiles = summaryScope["topTypes"];
        var hiddenWholeCaptureBytes = 0;
        var visibleWholeCaptureTypeCount = 0;
        for (var tileIndex = 0; tileIndex < topTypesForTiles.length; ++tileIndex) {
            if (hider.isHidden(tileIndex)) {
                hiddenWholeCaptureBytes += topTypesForTiles[tileIndex]["TotalBytes"];
            } else {
                ++visibleWholeCaptureTypeCount;
            }
        }

        var totalTile = document.getElementById("allocationTotalTile-" + scope);
        if (totalTile) {
            var adjustedTotalBytes = summaryScope["totalSampledBytes"] - hiddenWholeCaptureBytes;
            totalTile.textContent = (adjustedTotalBytes / mb).toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 }) + " mb";
        }

        var distinctTypesTile = document.getElementById("allocationDistinctTypesTile-" + scope);
        if (distinctTypesTile) {
            distinctTypesTile.textContent = visibleWholeCaptureTypeCount.toLocaleString();
        }
    }

    // Called on every zoom change (see renderHeapContentsCharts below) -
    // updates both the "all" and "loh" ranked types tables (whichever
    // exist), not just whichever is currently visible behind the All
    // Types/LOH Only toggle, so switching that toggle later shows
    // already-correct data instead of needing its own separate trigger.
    function updateRankedTypesTables(zoomRange) {
        updateOneRankedTypesTable(allocationSummaryJson, "all", zoomRange);

        var lohSummaryForTable = allocationSummaryJson["loh"];
        if (lohSummaryForTable && lohSummaryForTable["topTypes"] && lohSummaryForTable["topTypes"].length > 0) {
            updateOneRankedTypesTable(lohSummaryForTable, "loh", zoomRange);
        }
    }

    // Rebuilds only the Heap Contents view's own charts - called both by the
    // initial Heap Contents open (with the then-current sharedZoomRange, in
    // case a GC chart was already zoomed first) and by applySharedZoom above
    // (which also rebuilds the GC charts, keeping both views in sync
    // regardless of which one a drag-select actually happened on). zoomRange
    // is null for the full, unzoomed capture.
    function renderHeapContentsCharts(zoomRange) {
        destroyHeapContentsCharts();
        updateZoomStatusUi(zoomRange);

        var zoomOptionsForRate = { range: zoomRange, onRangeSelected: onHeapContentsRangeSelected };
        var rateChartHandle = renderAllocationTimelineChart(
            document.getElementById("allocationTimelineChart"),
            allocationSummaryJson["ticks"],
            gen0GcTimesMSecForCharts,
            gen1GcTimesMSecForCharts,
            zoomOptionsForRate);
        if (rateChartHandle) {
            heapContentsChartHandles.push(rateChartHandle);
        }

        // "all" and (if present) "loh" stacked charts are both built up
        // front, not lazily on toggle - see AllocationSummaryRenderer.ts's
        // renderTypeBreakdownPanel. Each gets its own onSegmentClick
        // closure bound to its own scope object so onDrillDownSegmentClick
        // doesn't need any global "which view is active" state to resolve
        // the right drillDown/typeTimeline data.
        var allChartHandle = renderAllocationTypeTimelineChart(
            document.getElementById("allocationTypeTimelineChart-all"),
            allocationSummaryJson["typeTimeline"],
            function (typeIndex, bucketIndex) {
                onDrillDownSegmentClick(typeIndex, bucketIndex, allocationSummaryJson, "All Types");
            },
            { range: zoomRange, onRangeSelected: onHeapContentsRangeSelected });
        if (allChartHandle) {
            heapContentsChartHandles.push(allChartHandle);
        }

        var lohSummary = allocationSummaryJson["loh"];
        if (lohSummary && lohSummary["topTypes"] && lohSummary["topTypes"].length > 0) {
            var lohChartHandle = renderAllocationTypeTimelineChart(
                document.getElementById("allocationTypeTimelineChart-loh"),
                lohSummary["typeTimeline"],
                function (typeIndex, bucketIndex) {
                    onDrillDownSegmentClick(typeIndex, bucketIndex, lohSummary, "LOH Only");
                },
                { range: zoomRange, onRangeSelected: onHeapContentsRangeSelected });
            if (lohChartHandle) {
                heapContentsChartHandles.push(lohChartHandle);
            }
        }

        updateRankedTypesTables(zoomRange);
    }

    // Overview is now the default active view for nettrace input (see
    // GcSnapshotRenderer.ts) - #view-gc, and every chart built into it, can
    // now start out `display:none` (.viewPanel's own CSS) rather than
    // always being the visible default the way it used to be. Chart.js
    // measures a canvas's size once at construction time and does not
    // recover on its own once its container later becomes visible - a
    // real, confirmed pitfall, not hypothetical - so every GC-view chart
    // built so far (gcCountChart/gcTimeCountChart above, plus
    // renderGcCharts's own gcChartHandles/heapChartHandlesByIndex, all
    // built eagerly at page load regardless of which tab is active) needs
    // an explicit resize() once the GC tab actually becomes visible.
    // Cheap/idempotent to call even when the charts were already visible
    // (e.g. gcinfo format, where GC still starts active) - Chart.js's own
    // resize() is a no-op-ish recompute, not a rebuild.
    function resizeGcViewCharts() {
        if (gcCountChart) {
            gcCountChart.resize();
        }

        if (gcTimeCountChart) {
            gcTimeCountChart.resize();
        }

        for (var handleIndex = 0; handleIndex < gcChartHandles.length; ++handleIndex) {
            gcChartHandles[handleIndex].chart.resize();
        }

        for (var heapIndexKey in heapChartHandlesByIndex) {
            heapChartHandlesByIndex[heapIndexKey].chart.resize();
        }
    }

    var viewNavButtons = document.getElementsByClassName("viewNavButton");
    for (var viewButtonIndex = 0; viewButtonIndex < viewNavButtons.length; ++viewButtonIndex) {
        viewNavButtons[viewButtonIndex].addEventListener('click', function (event) {
            var targetView = event.currentTarget.getAttribute('data-view');

            var buttons = document.getElementsByClassName("viewNavButton");
            for (var buttonIndex = 0; buttonIndex < buttons.length; ++buttonIndex) {
                buttons[buttonIndex].classList.remove('active');
            }

            var panels = document.getElementsByClassName("viewPanel");
            for (var panelIndex = 0; panelIndex < panels.length; ++panelIndex) {
                panels[panelIndex].classList.remove('active');
            }

            event.currentTarget.classList.add('active');
            document.getElementById('view-' + targetView).classList.add('active');

            if (targetView === 'gc') {
                resizeGcViewCharts();
            } else if (targetView === 'heapContents' && !allocationSummaryInjected) {
                var holder = document.getElementById("allocationSummaryHtml");
                var allocationSummaryHtml = holder.innerHTML.slice(4, holder.innerHTML.length - 3);

                // Tiles -> chart canvas -> table order comes from
                // AllocationSummaryRenderer.ts's own markup now (single
                // source of truth) - this just injects it and wires up the
                // chart once the canvas element actually exists in the DOM.
                document.getElementById('view-heapContents').innerHTML = allocationSummaryHtml;

                // "Allocated before this GC" reference line needs each
                // Gen0/Gen1 GC's own start time - gcs is this file's own
                // parsed data (allocationStats.js has no access to it), so
                // it's extracted here and passed down as plain arrays.
                gen0GcTimesMSecForCharts = [];
                gen1GcTimesMSecForCharts = [];
                for (var gcIndex = 0; gcIndex < gcs.length; ++gcIndex) {
                    var gcEntry = gcs[gcIndex]["data"];
                    if (gcEntry["generation"] === 0) {
                        gen0GcTimesMSecForCharts.push(gcEntry["PauseStartRelativeMSec"]);
                    } else if (gcEntry["generation"] === 1) {
                        gen1GcTimesMSecForCharts.push(gcEntry["PauseStartRelativeMSec"]);
                    }
                }
                gen0GcTimesMSecForCharts.sort(function (left, right) { return left - right; });
                gen1GcTimesMSecForCharts.sort(function (left, right) { return left - right; });

                // Use whatever zoom is already applied (e.g. dragged on a GC
                // chart before Heap Contents was ever opened) rather than
                // always starting unzoomed.
                renderHeapContentsCharts(sharedZoomRange);

                wireHeapContentsInnerTabs();
                allocationSummaryInjected = true;
            } else if (targetView === 'exceptions' && !exceptionSummaryInjected) {
                var exceptionHolder = document.getElementById("exceptionSummaryHtml");
                var exceptionSummaryHtml = exceptionHolder.innerHTML.slice(4, exceptionHolder.innerHTML.length - 3);

                document.getElementById('view-exceptions').innerHTML = exceptionSummaryHtml;

                wireExceptionsPanel();
                setupDetailTableSortHandlers(document.getElementById('view-exceptions'));
                exceptionSummaryInjected = true;
            } else if (targetView === 'contention' && !contentionInjected) {
                var contentionHolder = document.getElementById("contentionHtml");
                var contentionHtmlContent = contentionHolder.innerHTML.slice(4, contentionHolder.innerHTML.length - 3);

                document.getElementById('view-contention').innerHTML = contentionHtmlContent;

                wireContentionTab();
                setupDetailTableSortHandlers(document.getElementById('view-contention'));
                contentionInjected = true;
            } else if (targetView === 'threading' && !threadingInjected) {
                var threadingHolder = document.getElementById("threadingHtml");
                var threadingHtmlContent = threadingHolder.innerHTML.slice(4, threadingHolder.innerHTML.length - 3);

                document.getElementById('view-threading').innerHTML = threadingHtmlContent;

                wireThreadingTab();
                setupDetailTableSortHandlers(document.getElementById('view-threading'));
                threadingInjected = true;
            } else if (targetView === 'profile' && !cpuProfileInjected) {
                var cpuProfileHolder = document.getElementById("cpuProfileHtml");
                var cpuProfileHtml = cpuProfileHolder.innerHTML.slice(4, cpuProfileHolder.innerHTML.length - 3);

                document.getElementById('view-profile').innerHTML = cpuProfileHtml;

                wireProfileInnerTabs();
                setupDetailTableSortHandlers(document.getElementById('profile-tab-hotmethods'));

                // Flame Graph is the default-active inner tab (see
                // CpuProfileRenderer.ts), so it's built immediately here
                // rather than deferred to a further click - cpuProfileJson
                // is already parsed eagerly above (see this file's own
                // header), so this is just the client-side DOM build.
                renderFlameGraph(
                    document.getElementById('flameGraphContainer'),
                    document.getElementById('flameGraphBreadcrumb'),
                    document.getElementById('flameGraphResetZoomBtn'),
                    document.getElementById('flameGraphTooltip'),
                    cpuProfileJson);

                cpuProfileInjected = true;
            }
        });
    }

    // "Charts"/"Drill Down" inner tabs within the Heap Contents view - a
    // third, distinct navigational axis from the GC view's own
    // Charts/Detailed tabs (.tabButton/.tabPanel) and the top-level
    // GC/Heap Contents view switcher (.viewNavButton/.viewPanel) just
    // above, so it's keyed on its own heapContentsTabButton/
    // heapContentsTabPanel classes and "heapContents-tab-*" ids to avoid
    // colliding with either. Wired once, right after
    // AllocationSummaryRenderer.ts's markup is injected above (the buttons
    // don't exist in the DOM before that).
    function switchHeapContentsTab(targetTab) {
        // Scoped to #view-heapContents, NOT queried globally. The
        // heapContentsTabButton/heapContentsTabPanel classes are shared for
        // styling by the Profile and Contention views' own tab bars (see
        // switchProfileTab/switchContentionTab, which have always scoped
        // their own queries), so a global query here reached into those
        // views and deactivated their panels - after which the
        // 'heapContents-tab-<null>' lookup below threw, leaving whichever
        // view the user was actually in blank. It presented as "the table
        // sometimes isn't populated", intermittent because it depended on
        // which view got wired first and therefore whose click listener ran
        // last.
        var heapContentsView = document.getElementById('view-heapContents');
        if (!heapContentsView) {
            return;
        }

        var buttons = heapContentsView.getElementsByClassName("heapContentsTabButton");
        for (var buttonIndex = 0; buttonIndex < buttons.length; ++buttonIndex) {
            buttons[buttonIndex].classList.remove('active');
            if (buttons[buttonIndex].getAttribute('data-heaptab') === targetTab) {
                buttons[buttonIndex].classList.add('active');
            }
        }

        var panels = heapContentsView.getElementsByClassName("heapContentsTabPanel");
        for (var panelIndex = 0; panelIndex < panels.length; ++panelIndex) {
            panels[panelIndex].classList.remove('active');
        }

        var targetPanel = document.getElementById('heapContents-tab-' + targetTab);
        if (!targetPanel) {
            return;
        }

        targetPanel.classList.add('active');

        var backButton = document.getElementById('backToChartsButton');
        if (backButton) {
            backButton.style.display = (targetTab === 'drilldown') ? 'inline-block' : 'none';
        }
    }

    // The Charts panel has summary tiles and the allocation-rate line chart
    // above the stacked type-timeline (bar) chart a drill-down was reached
    // from - just switching tabs back leaves that scrolled out of view
    // above the fold. Scrolls it back into view so "go back" actually
    // returns to what you were just looking at, not just the top of the
    // panel.
    function goBackToChartsView() {
        switchHeapContentsTab('charts');

        var activeViewButton = document.querySelector('.allocationViewButton.active');
        var activeScope = activeViewButton ? activeViewButton.getAttribute('data-allocview') : 'all';
        var stackedBarChart = document.getElementById('allocationTypeTimelineChart-' + activeScope);
        if (stackedBarChart) {
            stackedBarChart.scrollIntoView({ behavior: 'smooth', block: 'center' });
        }
    }

    // Builds this row's own subtree the first time it's expanded -
    // drillDownStats.js's renderCallerRow/renderDrillDownTable only emit an
    // empty data-lazy="true" placeholder for any row with children, so the
    // nested-table HTML (the expensive part) only ever gets built for a
    // subtree a user actually asked to see, not eagerly for all of them on
    // every drill-down click. Safe to call on an already-built detailRow -
    // it's a no-op (data-lazy is removed once built, so this just falls
    // through).
    function buildDrillDownRowIfLazy(detailRow) {
        if (!detailRow || detailRow.getAttribute('data-lazy') !== 'true') {
            return;
        }

        var builtHtml = buildLazyDrillDownSubtree(detailRow.id);
        if (builtHtml !== null) {
            detailRow.querySelector('.callerTreeCell').innerHTML = builtHtml;
        }
        detailRow.removeAttribute('data-lazy');
    }

    // Expands one row, then keeps going for as long as there was no real
    // choice to make.
    //
    // A row with exactly one child presents no decision - the chain simply
    // continues - so stopping there just charges the reader another click
    // to learn nothing. This follows that single-child run down, building
    // and expanding each hop, and stops at the first node that actually
    // branches (2+ children, a genuine fork worth choosing at) or at the
    // end of the chain. On a real request-pipeline stack that collapses
    // 20-odd mechanical clicks into one while still stopping exactly where
    // the reader has something to decide.
    //
    // Note this deliberately does NOT recurse into a branch's children: if
    // the row being expanded has several children, they're revealed and
    // that's it - auto-following each of their runs is what made the old
    // "expand everything" behavior an unreadable flat dump.
    function expandDrillDownRowFollowingLinearRun(toggleRow, detailRow) {
        buildDrillDownRowIfLazy(detailRow);
        toggleRow.classList.add('expanded');
        detailRow.classList.add('expanded');

        var currentDetailRow = detailRow;
        for (;;) {
            // The <table class="callerTreeInner"> holding this level's rows.
            // Rows of deeper levels live inside their own nested tables, so
            // innerTable.rows is exactly this one level.
            var innerTable = currentDetailRow.querySelector('table.callerTreeInner');
            if (!innerTable) {
                return;
            }

            var childRows = [];
            for (var rowIndex = 0; rowIndex < innerTable.rows.length; ++rowIndex) {
                if (innerTable.rows[rowIndex].classList.contains('callerRow')) {
                    childRows.push(innerTable.rows[rowIndex]);
                }
            }

            // 0 children: chain ended. 2+: a real branch - stop and let the
            // reader pick which one to follow.
            if (childRows.length !== 1) {
                return;
            }

            var onlyChildRow = childRows[0];
            if (onlyChildRow.getAttribute('data-expandable') !== 'true') {
                return;
            }

            var onlyChildDetailRow = document.getElementById(onlyChildRow.getAttribute('data-target'));
            if (!onlyChildDetailRow) {
                return;
            }

            buildDrillDownRowIfLazy(onlyChildDetailRow);
            onlyChildRow.classList.add('expanded');
            onlyChildDetailRow.classList.add('expanded');
            currentDetailRow = onlyChildDetailRow;
        }
    }

    // Bulk-toggles every collapsible row in the drill-down panel at once
    // (see the Expand All/Collapse All buttons in drillDownStats.js's
    // renderDrillDownTable, and the leafMethodRow "reveal the whole story"
    // click below) - every node with at least one child is now individually
    // collapsible (not just real branch points), so a deep tree can take
    // many individual clicks to fully open or close by hand.
    //
    // When expanding, this also has to *build* every still-lazy row under
    // container first - building one level can introduce new
    // still-collapsed-and-unbuilt rows one level deeper (a child that
    // itself has children), so every newly-built row's own lazy children
    // get queued up too, ensuring "expand everything under here" really
    // does reach every depth, not just whatever happened to already exist.
    // Collapsing never needs this - hiding already-built content via CSS is
    // still cheap, and there's no reason to throw already-built DOM away.
    //
    // Deliberately an explicit worklist, not "re-run container.querySelector
    // for the next lazy row until none are left" - re-querying the *whole*
    // container on every single row is a real perf bug that shipped here
    // once already: each call re-scans everything already built too, so a
    // tree with N rows did on the order of N container-wide scans (each one
    // itself O(container size)), which measurably stalled/froze the webview
    // on a large capture's deep or wide call stacks. Scoping each lazy scan
    // to only the subtree that specific build just produced keeps the total
    // work proportional to N, not N^2.
    function setAllDrillDownRowsExpanded(container, expanded) {
        if (expanded) {
            var lazyQueue = [];

            // container is sometimes itself a still-lazy .callPathsDetail row
            // (a leafMethodRow's own detail row, the first time it's
            // expanded) - Element.querySelector[All] only searches
            // *descendants*, never the element it's called on, so container
            // itself has to be queued explicitly rather than only its
            // descendants (e.g. the whole drillDownPanel, for Expand All,
            // which is never itself a lazy row).
            if (container.classList && container.classList.contains('callPathsDetail')) {
                lazyQueue.push(container);
            }

            var initialLazyRows = container.querySelectorAll('.callPathsDetail[data-lazy="true"]');
            for (var initialIndex = 0; initialIndex < initialLazyRows.length; ++initialIndex) {
                lazyQueue.push(initialLazyRows[initialIndex]);
            }

            while (lazyQueue.length > 0) {
                var lazyRow = lazyQueue.pop();
                buildDrillDownRowIfLazy(lazyRow);

                // Scoped to the row just built, not the whole container -
                // this is what keeps the total work linear (see above).
                var newlyLazyRows = lazyRow.querySelectorAll('.callPathsDetail[data-lazy="true"]');
                for (var newIndex = 0; newIndex < newlyLazyRows.length; ++newIndex) {
                    lazyQueue.push(newlyLazyRows[newIndex]);
                }
            }
        }

        var toggleRows = container.querySelectorAll('[data-expandable="true"]');
        for (var rowIndex = 0; rowIndex < toggleRows.length; ++rowIndex) {
            var toggleRow = toggleRows[rowIndex];
            var detailRow = document.getElementById(toggleRow.getAttribute('data-target'));

            if (expanded) {
                toggleRow.classList.add('expanded');
                if (detailRow) {
                    detailRow.classList.add('expanded');
                }
            } else {
                toggleRow.classList.remove('expanded');
                if (detailRow) {
                    detailRow.classList.remove('expanded');
                }
            }
        }
    }

    // Shared by both ways into the Drill Down tab (a chart-segment click and
    // a global-table row click below) - injects the rendered table, reveals
    // the tab button (hidden until there's actually something to show), and
    // switches to it.
    function showDrillDownTab(drillDownHtml) {
        document.getElementById('heapContents-tab-drilldown').innerHTML = drillDownHtml;

        var drillDownTabButton = document.getElementById('drillDownTabButton');
        if (drillDownTabButton) {
            drillDownTabButton.style.display = 'inline-block';
        }

        switchHeapContentsTab('drilldown');
    }

    // Called from allocationStats.js's onClick handler on the type-timeline
    // chart when a real (non-"Other") stacked segment is clicked. Scoped to
    // that one (type, 1-second bucket) cell. summaryScope is either
    // allocationSummaryJson (the "all" chart) or allocationSummaryJson.loh
    // (the "LOH Only" chart) - each chart's onSegmentClick closure binds its
    // own scope (see the renderAllocationTypeTimelineChart calls above), so
    // this never has to guess which toggle state is currently active.
    function onDrillDownSegmentClick(typeIndex, bucketIndex, summaryScope, filterLabel) {
        var drillDown = summaryScope["drillDown"];
        var cellEntry = (drillDown && drillDown["cells"]) ? drillDown["cells"][typeIndex + ":" + bucketIndex] : null;

        var typeTimeline = summaryScope["typeTimeline"];
        var typeName = typeTimeline["types"][typeIndex];
        var bucketLabel = formatElapsedMsForAllocationChart(typeTimeline["buckets"][bucketIndex]["bucketStartMSec"]);

        showDrillDownTab(renderDrillDownTable(cellEntry, typeName, bucketLabel, filterLabel, allocationSummaryJson["methodNames"], summaryScope["totalSampledBytes"]));
    }

    // Called from the click delegation in wireHeapContentsInnerTabs below
    // when a row in a ranked types table (either the "all" or "loh" one -
    // see summaryScope) is clicked. Scoped to that type across the *whole*
    // capture (AllocationJsonExporter.cs's typeDrillDown - a parallel array
    // to topTypes), not one chart cell - merges every bucket's stacks for
    // this type into one view.
    function onTypeDrillDownClick(typeIndex, summaryScope, filterLabel) {
        var typeDrillDown = summaryScope["typeDrillDown"];
        var typeEntry = typeDrillDown ? typeDrillDown[typeIndex] : null;
        var typeName = summaryScope["topTypes"][typeIndex]["TypeName"];

        showDrillDownTab(renderDrillDownTable(typeEntry, typeName, "Whole Capture", filterLabel, allocationSummaryJson["methodNames"], summaryScope["totalSampledBytes"]));
    }

    function wireHeapContentsInnerTabs() {
        // Scoped for the same reason switchHeapContentsTab is - an unscoped
        // query bound this handler to the Profile and Contention views' tab
        // buttons too, so clicking those ran the heap-contents switcher with
        // a null target.
        var heapContentsTabButtons = document.querySelectorAll('#view-heapContents .heapContentsTabButton');
        for (var tabButtonIndex = 0; tabButtonIndex < heapContentsTabButtons.length; ++tabButtonIndex) {
            heapContentsTabButtons[tabButtonIndex].addEventListener('click', function (event) {
                switchHeapContentsTab(event.currentTarget.getAttribute('data-heaptab'));
            });
        }

        var backToChartsButton = document.getElementById('backToChartsButton');
        if (backToChartsButton) {
            backToChartsButton.addEventListener('click', goBackToChartsView);
        }

        // Click equivalent of the Backspace zoom-reset (see the keydown
        // listener further below) - for anyone who doesn't know/want to use
        // the keyboard shortcut. Only visible while a zoom is actually
        // applied (see updateZoomStatusUi).
        var resetZoomButton = document.getElementById('resetZoomButton');
        if (resetZoomButton) {
            resetZoomButton.addEventListener('click', function () {
                applySharedZoom(null);
            });
        }

        // Ranked types table rows (AllocationSummaryRenderer.ts) - both the
        // "all" and "loh" tables are only ever injected once (not rebuilt
        // per click like the drill-down panel itself), so a direct listener
        // on their shared container is fine here rather than needing
        // delegation on something more stable. data-scope on each row (see
        // renderTypeBreakdownPanel) picks which summary object
        // (allocationSummaryJson or its .loh) the click resolves against.
        var chartsPanel = document.getElementById('heapContents-tab-charts');
        if (chartsPanel) {
            chartsPanel.addEventListener('click', function (event) {
                // "Show all" for either scope's hide-status bar - checked
                // first since these buttons live inside chartsPanel too
                // (see AllocationSummaryRenderer.ts's hideStatusHtml).
                var showAllBtn = event.target.closest('[data-alloc-showall-scope]');
                if (showAllBtn) {
                    getAllocationTypeHider(showAllBtn.getAttribute('data-alloc-showall-scope')).reset();
                    return;
                }

                // Row-hide cell - checked before .typeRow below so a click
                // anywhere in it never also fires that row's drill-down
                // navigation (the whole row is otherwise one big click
                // target - see onTypeDrillDownClick). Whole cell is the
                // click target, not just the ✕ glyph itself.
                var hideCell = event.target.closest('.rowHideColumn');
                if (hideCell) {
                    var hideRow = hideCell.closest('.typeRow');
                    if (hideRow) {
                        getAllocationTypeHider(hideRow.getAttribute('data-scope')).toggle(parseInt(hideRow.getAttribute('data-type-index'), 10));
                    }
                    return;
                }

                var typeRow = event.target.closest('.typeRow');
                if (!typeRow) {
                    return;
                }

                var isLohRow = typeRow.getAttribute('data-scope') === 'loh';
                var rowScope = isLohRow ? allocationSummaryJson["loh"] : allocationSummaryJson;
                onTypeDrillDownClick(parseInt(typeRow.getAttribute('data-type-index'), 10), rowScope, isLohRow ? "LOH Only" : "All Types");
            });
        }

        // "All Types" / "LOH Only" filter toggle (only rendered at all when
        // allocationSummary.loh has data - see AllocationSummaryRenderer.ts).
        // A pure CSS show/hide between the two pre-rendered panels/charts,
        // not a re-render - both charts already exist by the time this is
        // wired up.
        var allocationViewButtons = document.getElementsByClassName("allocationViewButton");
        for (var allocViewIdx = 0; allocViewIdx < allocationViewButtons.length; ++allocViewIdx) {
            allocationViewButtons[allocViewIdx].addEventListener('click', function (event) {
                var targetScope = event.currentTarget.getAttribute('data-allocview');

                var buttons = document.getElementsByClassName("allocationViewButton");
                for (var buttonIndex = 0; buttonIndex < buttons.length; ++buttonIndex) {
                    buttons[buttonIndex].classList.remove('active');
                }
                event.currentTarget.classList.add('active');

                var panels = document.getElementsByClassName("allocationViewPanel");
                for (var panelIndex = 0; panelIndex < panels.length; ++panelIndex) {
                    panels[panelIndex].classList.remove('active');
                }
                document.getElementById('allocView-' + targetScope).classList.add('active');
            });
        }

        // Event delegation, attached once to the panel itself rather than
        // per-row - drillDownStats.js's renderDrillDownTable rebuilds this
        // panel's entire innerHTML on every chart-segment click, which
        // would otherwise silently drop any listeners attached directly to
        // its rows. [data-expandable="true"] marks a toggleable row at any
        // depth - both the outer leafMethodRow and any deeper callerRow
        // branch point use the same attribute/behavior (see
        // drillDownStats.js's renderCallerChainRows).
        var drillDownPanel = document.getElementById('heapContents-tab-drilldown');
        if (drillDownPanel) {
            drillDownPanel.addEventListener('click', function (event) {
                if (event.target.closest('.drillDownExpandAllBtn')) {
                    setAllDrillDownRowsExpanded(drillDownPanel, true);
                    return;
                }

                if (event.target.closest('.drillDownCollapseAllBtn')) {
                    setAllDrillDownRowsExpanded(drillDownPanel, false);
                    return;
                }

                var leafRow = event.target.closest('[data-expandable="true"]');
                if (!leafRow) {
                    return;
                }

                var detailRow = document.getElementById(leafRow.getAttribute('data-target'));
                if (!detailRow) {
                    return;
                }

                // EVERY row - the leaf/allocation-site row included - reveals
                // only its own *immediate* children, lazily building just
                // that one level the first time (see drillDownStats.js's
                // renderCallerRow/buildLazyDrillDownSubtree); deeper levels
                // stay collapsed and unbuilt until expanded themselves.
                //
                // A leaf row used to special-case into
                // setAllDrillDownRowsExpanded, expanding its ENTIRE chain in
                // one click on the theory that "show me the whole story" is
                // what a user wants first. On real data that turned out to
                // actively obscure the story: a request-pipeline stack is
                // largely one non-branching chain 20+ frames deep, so one
                // click dumped 20+ rows that all repeat the same bytes/
                // percentage (correctly - a caller of a caller accounts for
                // exactly the same allocations until the paths diverge),
                // burying the handful of rows where something actually
                // branches. Expanding one hop at a time keeps each click's
                // output readable and makes a real branch visible as soon as
                // it's reached. "Expand All" is still there for anyone who
                // does want the whole tree at once.
                if (leafRow.classList.contains('expanded')) {
                    leafRow.classList.remove('expanded');
                    detailRow.classList.remove('expanded');
                    return;
                }

                expandDrillDownRowFollowingLinearRun(leafRow, detailRow);
            });
        }
    }

    // "Flame Graph"/"Methods" inner tabs within the Profile view - two tabs
    // only now (no separate Drill Down tab; caller trees expand inline within
    // the Methods table), keyed on data-profiletab/id="profile-tab-*" so it
    // never collides with the Heap Contents view's own tab bar. Builds the
    // CPU timeline chart lazily the first time the Methods tab is activated
    // (same as the flame graph for the Flame Graph tab).
    var cpuTimelineBuilt = false;

    function switchProfileTab(targetTab) {
        var buttons = document.querySelectorAll('#view-profile .heapContentsTabButton');
        for (var buttonIndex = 0; buttonIndex < buttons.length; ++buttonIndex) {
            buttons[buttonIndex].classList.remove('active');
            if (buttons[buttonIndex].getAttribute('data-profiletab') === targetTab) {
                buttons[buttonIndex].classList.add('active');
            }
        }

        var panels = document.querySelectorAll('#view-profile .heapContentsTabPanel');
        for (var panelIndex = 0; panelIndex < panels.length; ++panelIndex) {
            panels[panelIndex].classList.remove('active');
        }
        document.getElementById('profile-tab-' + targetTab).classList.add('active');

        if (targetTab === 'hotmethods' && !cpuTimelineBuilt) {
            cpuTimelineBuilt = true;
            renderCpuTimeline(null);
        }
    }

    // Mirrors buildExceptionDrillDownRowIfLazy, against cpuDrillDownStats.js's
    // buildLazyCpuDrillDownSubtree and the data-cpu-lazy attribute.
    function buildCpuDrillDownRowIfLazy(detailRow) {
        if (!detailRow || detailRow.getAttribute('data-cpu-lazy') !== 'true') {
            return;
        }

        var builtHtml = buildLazyCpuDrillDownSubtree(detailRow.id);
        if (builtHtml !== null) {
            detailRow.querySelector('.callerTreeCell').innerHTML = builtHtml;
        }
        detailRow.removeAttribute('data-cpu-lazy');
    }

    // An explicit worklist rather than a container-wide re-scan per row.
    // container is typically one method's own .callPathsDetail row (see
    // buildInlineCpuMethodCallerTree's Expand All/Collapse All buttons,
    // wired up in wireProfileInnerTabs) - scoped to just that method's own
    // caller tree, not every expanded method on the page.
    function setAllCpuDrillDownRowsExpanded(container, expanded) {
        if (expanded) {
            var lazyQueue = [];

            if (container.classList && container.classList.contains('callPathsDetail')) {
                lazyQueue.push(container);
            }

            var initialLazyRows = container.querySelectorAll('.callPathsDetail[data-cpu-lazy="true"]');
            for (var initialIndex = 0; initialIndex < initialLazyRows.length; ++initialIndex) {
                lazyQueue.push(initialLazyRows[initialIndex]);
            }

            while (lazyQueue.length > 0) {
                var lazyRow = lazyQueue.pop();
                buildCpuDrillDownRowIfLazy(lazyRow);

                var newlyLazyRows = lazyRow.querySelectorAll('.callPathsDetail[data-cpu-lazy="true"]');
                for (var newIndex = 0; newIndex < newlyLazyRows.length; ++newIndex) {
                    lazyQueue.push(newlyLazyRows[newIndex]);
                }
            }
        }

        var toggleRows = container.querySelectorAll('[data-cpu-expandable="true"]');
        for (var rowIndex = 0; rowIndex < toggleRows.length; ++rowIndex) {
            var toggleRow = toggleRows[rowIndex];
            var detailRow = document.getElementById(toggleRow.getAttribute('data-cpu-target'));

            if (expanded) {
                toggleRow.classList.add('expanded');
                if (detailRow) {
                    detailRow.classList.add('expanded');
                }
            } else {
                toggleRow.classList.remove('expanded');
                if (detailRow) {
                    detailRow.classList.remove('expanded');
                }
            }
        }
    }

    // Lazily builds (via buildInlineCpuMethodCallerTree, on first expansion
    // only - see detailRow's own data-cpu-method-lazy attribute) and marks
    // expanded a top-level method row + its paired .callPathsDetail row.
    // Shared by the single-row click handler (wireProfileInnerTabs, which
    // follows this with followCpuDrillDownLinearRun for a partial "expand
    // to the first branch" descent) and expandAllCpuMethodRows below (which
    // follows this with a full setAllCpuDrillDownRowsExpanded descent
    // instead) - both need this same "make sure it's built and open" step
    // first, just with a different depth of auto-expansion afterward.
    function buildAndExpandCpuMethodRow(methodRow, detailRow) {
        var lazyIndex = detailRow.getAttribute('data-cpu-method-lazy');
        if (lazyIndex !== null) {
            var methodIndex = parseInt(lazyIndex, 10);
            var entry = cpuProfileJson["hotMethodDrillDown"] ? cpuProfileJson["hotMethodDrillDown"][methodIndex] : null;
            var callerHtml = buildInlineCpuMethodCallerTree(
                entry,
                cpuProfileJson["methodNames"],
                cpuProfileJson["totalSampleCount"]);
            detailRow.querySelector('.callerTreeCell').innerHTML = callerHtml;
            detailRow.removeAttribute('data-cpu-method-lazy');
        }

        methodRow.classList.add('expanded');
        detailRow.classList.add('expanded');
    }

    // Master Expand All/Collapse All for the WHOLE ranked Methods table (see
    // CpuProfileRenderer.ts's methodsExpandControlsHtml, between the
    // timeline chart and the table) - distinct from the per-method Expand
    // All/Collapse All buttons inside one already-open method's own caller
    // tree (setAllCpuDrillDownRowsExpanded), which this reuses per-row for
    // a full-depth expand rather than followCpuDrillDownLinearRun's partial
    // "stop at the first branch" descent - Expand All means everything,
    // unlike a single row's own click.
    function expandAllCpuMethodRows(expand) {
        var hotMethodsTable = document.querySelector('.cpuHotMethodsTable table');
        if (!hotMethodsTable) {
            return;
        }

        var methodRows = hotMethodsTable.querySelectorAll('[data-cpu-method-expandable="true"]');
        for (var rowIndex = 0; rowIndex < methodRows.length; ++rowIndex) {
            var methodRow = methodRows[rowIndex];
            var detailRow = document.getElementById(methodRow.getAttribute('data-cpu-method-target'));
            if (!detailRow) {
                continue;
            }

            if (expand) {
                buildAndExpandCpuMethodRow(methodRow, detailRow);
                setAllCpuDrillDownRowsExpanded(detailRow, true);
            } else {
                methodRow.classList.remove('expanded');
                detailRow.classList.remove('expanded');
                // Also resets every interior caller row within it back to
                // collapsed, so re-expanding this method later (either via
                // its own row or a future Expand All) starts from a clean
                // state instead of remembering whatever was open before -
                // the only reason to call this on an ALREADY-built detail
                // row too (setAllCpuDrillDownRowsExpanded's own lazy-build
                // pass is a no-op here since nothing is left marked lazy).
                setAllCpuDrillDownRowsExpanded(detailRow, false);
            }
        }
    }

    // Follows a non-branching (single-child) chain of caller rows starting
    // from an ALREADY-EXPANDED detailRow, auto-expanding (and lazily
    // building) each one in turn until it hits a real branch (2+ children)
    // or runs out of children - shared by expandCpuDrillDownRowFollowingLinearRun
    // below (interior caller-row clicks) AND wireProfileInnerTabs' top-level
    // method-row click handler (whose own first level is built via a
    // different function, buildInlineCpuMethodCallerTree - see that
    // function's own comment - so it can't reuse
    // expandCpuDrillDownRowFollowingLinearRun's initial
    // buildCpuDrillDownRowIfLazy call, only this shared descent).
    function followCpuDrillDownLinearRun(detailRow) {
        var currentDetailRow = detailRow;
        for (;;) {
            var innerTable = currentDetailRow.querySelector('table.callerTreeInner');
            if (!innerTable) {
                return;
            }

            var childRows = [];
            for (var rowIndex = 0; rowIndex < innerTable.rows.length; ++rowIndex) {
                if (innerTable.rows[rowIndex].classList.contains('callerRow')) {
                    childRows.push(innerTable.rows[rowIndex]);
                }
            }

            if (childRows.length !== 1) {
                return;
            }

            var onlyChildRow = childRows[0];
            if (onlyChildRow.getAttribute('data-cpu-expandable') !== 'true') {
                return;
            }

            var onlyChildDetailRow = document.getElementById(onlyChildRow.getAttribute('data-cpu-target'));
            if (!onlyChildDetailRow) {
                return;
            }

            buildCpuDrillDownRowIfLazy(onlyChildDetailRow);
            onlyChildRow.classList.add('expanded');
            onlyChildDetailRow.classList.add('expanded');
            currentDetailRow = onlyChildDetailRow;
        }
    }

    // Mirrors expandExceptionDrillDownRowFollowingLinearRun - see that
    // function's own comment for the full rationale (follow a
    // non-branching chain down to the first real fork or the end, in one
    // click). Used for interior caller-row clicks - see
    // followCpuDrillDownLinearRun's own comment for why the top-level
    // method row's first expansion calls that shared descent directly
    // instead of this wrapper.
    function expandCpuDrillDownRowFollowingLinearRun(toggleRow, detailRow) {
        buildCpuDrillDownRowIfLazy(detailRow);
        toggleRow.classList.add('expanded');
        detailRow.classList.add('expanded');
        followCpuDrillDownLinearRun(detailRow);
    }

    // Mirrors buildCpuDrillDownRowIfLazy, against exceptionDrillDownStats.js's
    // buildLazyExceptionDrillDownSubtree and the data-exception-caller-lazy
    // attribute (see that file's header comment on why this is a parallel
    // implementation rather than a shared one). Covers both throw-site rows
    // and caller rows uniformly - both use the same data-exception-caller-*
    // attributes (see renderExceptionTreeRow's own comment), distinct from
    // the OUTER ranked table's own data-exception-expandable (type-level).
    function buildExceptionDrillDownRowIfLazy(detailRow) {
        if (!detailRow || detailRow.getAttribute('data-exception-caller-lazy') !== 'true') {
            return;
        }

        var builtHtml = buildLazyExceptionDrillDownSubtree(detailRow.id);
        if (builtHtml !== null) {
            detailRow.querySelector('.callerTreeCell').innerHTML = builtHtml;
        }
        detailRow.removeAttribute('data-exception-caller-lazy');
    }

    // Follows a non-branching (single-child) chain of rows starting from an
    // ALREADY-EXPANDED detailRow, auto-expanding (and lazily building) each
    // one in turn until it hits a real branch (2+ children) or runs out of
    // children - mirrors followCpuDrillDownLinearRun, but counts BOTH
    // .leafMethodRow and .callerRow children (not just .callerRow) since
    // Exceptions' tree has a role CPU's doesn't: a type's own direct
    // children are throw sites (leafMethodRow), and only a throw site's OWN
    // children are callers (callerRow) - counting only callerRow here would
    // make a type with exactly one throw site look like 0 children (an
    // immediate, incorrect stop) instead of the single non-branching child
    // it actually has. Shared by both a type row's own first expansion
    // (buildAndExpandExceptionTypeRow) and any interior throw-site/caller
    // click (expandExceptionDrillDownRowFollowingLinearRun below) - a real
    // branch at ANY level (multiple throw sites, or multiple callers)
    // naturally stops the descent via the childRows.length !== 1 check,
    // with no need to special-case which level that branch occurred at.
    function followExceptionDrillDownLinearRun(detailRow) {
        var currentDetailRow = detailRow;
        for (;;) {
            var innerTable = currentDetailRow.querySelector('table.callerTreeInner');
            if (!innerTable) {
                return;
            }

            var childRows = [];
            for (var rowIndex = 0; rowIndex < innerTable.rows.length; ++rowIndex) {
                var candidateRow = innerTable.rows[rowIndex];
                if (candidateRow.classList.contains('leafMethodRow') || candidateRow.classList.contains('callerRow')) {
                    childRows.push(candidateRow);
                }
            }

            if (childRows.length !== 1) {
                return;
            }

            var onlyChildRow = childRows[0];
            if (onlyChildRow.getAttribute('data-exception-caller-expandable') !== 'true') {
                return;
            }

            var onlyChildDetailRow = document.getElementById(onlyChildRow.getAttribute('data-exception-caller-target'));
            if (!onlyChildDetailRow) {
                return;
            }

            buildExceptionDrillDownRowIfLazy(onlyChildDetailRow);
            onlyChildRow.classList.add('expanded');
            onlyChildDetailRow.classList.add('expanded');
            currentDetailRow = onlyChildDetailRow;
        }
    }

    // Mirrors expandCpuDrillDownRowFollowingLinearRun - see
    // followExceptionDrillDownLinearRun's own comment for the full
    // rationale (follow a non-branching chain down to the first real fork
    // or the end, in one click). Used for interior throw-site/caller-row
    // clicks - see buildAndExpandExceptionTypeRow's own comment for why the
    // top-level type row's first expansion calls that shared descent
    // directly instead of this wrapper.
    function expandExceptionDrillDownRowFollowingLinearRun(toggleRow, detailRow) {
        buildExceptionDrillDownRowIfLazy(detailRow);
        toggleRow.classList.add('expanded');
        detailRow.classList.add('expanded');
        followExceptionDrillDownLinearRun(detailRow);
    }

    // Lazily builds (via buildInlineExceptionTypeCallerTree, on first
    // expansion only - see detailRow's own data-exception-type-lazy
    // attribute) and marks expanded a top-level exception type row + its
    // paired .callPathsDetail row - mirrors buildAndExpandCpuMethodRow,
    // including the auto-descend afterward (followExceptionDrillDownLinearRun,
    // CPU's own equivalent being followCpuDrillDownLinearRun) - a type with
    // exactly one throw site, which itself has exactly one caller, and so
    // on, now descends all the way to the first real branch or the end in
    // one click, the same as every other level already did. A type with
    // multiple throw sites stops right there (a real branch), same as
    // multiple callers already did one level down - no special-casing
    // needed for "how many throw sites" vs "how many callers", since
    // followExceptionDrillDownLinearRun's own single childRows.length !== 1
    // check already covers both uniformly.
    function buildAndExpandExceptionTypeRow(typeRow, detailRow) {
        var lazyIndex = detailRow.getAttribute('data-exception-type-lazy');
        if (lazyIndex !== null) {
            var typeIndex = parseInt(lazyIndex, 10);
            var typeDrillDown = exceptionSummaryJson["typeDrillDown"];
            var entry = typeDrillDown ? typeDrillDown[typeIndex] : null;
            var callerHtml = buildInlineExceptionTypeCallerTree(
                entry,
                exceptionSummaryJson["methodNames"],
                exceptionSummaryJson["totalExceptionCount"]);
            detailRow.querySelector('.callerTreeCell').innerHTML = callerHtml;
            detailRow.removeAttribute('data-exception-type-lazy');
        }

        typeRow.classList.add('expanded');
        detailRow.classList.add('expanded');
        followExceptionDrillDownLinearRun(detailRow);
    }

    // Manually-hidden Exceptions ranked-type rows (data-exception-type-index).
    // onChange rebuilds the table's own %/tiles AND re-renders the timeline
    // chart (if present), so a hide/show-all cascades into the chart the
    // same way a hidden CPU method already does.
    var exceptionTypeHider = createRowHideController('exceptionTypesHideStatus', 'exceptionTypesHideStatusLabel', function () {
        rebuildExceptionTypesTable();
        renderExceptionTimeline(exceptionTimelineZoomRange);
    });

    // Rewrites the Exceptions table's % of Total cells (and, unlike the
    // hidden row itself, the row stays in the DOM display:none'd - simplest
    // to reuse the same "hide via display:none, never remove" discipline
    // the CPU/Contention tables use since this table has no existing
    // rebuild-from-JSON path to extend the way Allocation's does) plus the
    // Total/Distinct Types tiles, against a denominator that excludes every
    // hidden row's own Count - Count itself (a per-type, disjoint partition
    // of every real exception throw) is exactly the kind of additive field
    // this feature's design calls for, same reasoning as CPU's selfSamples
    // and Contention's TotalWaitMSec.
    function rebuildExceptionTypesTable() {
        var topTypes = exceptionSummaryJson ? exceptionSummaryJson["topTypes"] : null;
        var table = document.getElementById('exceptionTypeTable');
        if (!topTypes || !table) {
            return;
        }

        var hiddenCount = 0;
        var visibleTypeCount = 0;
        for (var sumIndex = 0; sumIndex < topTypes.length; ++sumIndex) {
            if (exceptionTypeHider.isHidden(sumIndex)) {
                hiddenCount += topTypes[sumIndex]["Count"];
            } else {
                ++visibleTypeCount;
            }
        }

        var adjustedTotalCount = exceptionSummaryJson["totalExceptionCount"] - hiddenCount;

        var rows = table.rows;
        for (var rowIndex = 1; rowIndex < rows.length; ++rowIndex) {
            var row = rows[rowIndex];
            var typeIndex = parseInt(row.getAttribute('data-exception-type-index'), 10);
            if (isNaN(typeIndex) || !topTypes[typeIndex]) {
                continue;
            }

            var isHidden = exceptionTypeHider.isHidden(typeIndex);
            row.style.display = isHidden ? 'none' : '';

            // Hide the paired callPathsDetail row too, same as
            // filterCpuMethodsTableToZoomRange does for CPU - otherwise an
            // already-expanded type's own caller tree would keep showing
            // with no visible row above it. Clearing the inline style
            // (empty string, not left unset) when visible hands control
            // back to the .expanded CSS rule instead of permanently
            // overriding it - see that function's own comment for why this
            // matters for a later re-expand click to still work.
            var pairedDetailId = row.getAttribute('data-exception-target');
            if (pairedDetailId) {
                var pairedDetailRow = document.getElementById(pairedDetailId);
                if (pairedDetailRow) {
                    pairedDetailRow.style.display = isHidden ? 'none' : '';
                }
            }

            var percentOfTotal = adjustedTotalCount > 0 ? (topTypes[typeIndex]["Count"] * 100.0) / adjustedTotalCount : 0;

            // cells[0] is the rowHideBtn column, cells[1] is Exception Type -
            // % of Total (the only recomputed column) is cells[3].
            row.cells[3].textContent = percentOfTotal.toFixed(2);
        }

        // Plain numbers, no thousands separators - matches
        // ExceptionSummaryRenderer.ts's own initial render of these two
        // tiles exactly (unlike CPU/Allocation's tiles, which do use
        // toLocaleString server-side).
        var totalTile = document.getElementById('exceptionsTotalTile');
        if (totalTile) {
            totalTile.textContent = String(adjustedTotalCount);
        }

        var distinctTypesTile = document.getElementById('exceptionsDistinctTypesTile');
        if (distinctTypesTile) {
            distinctTypesTile.textContent = String(visibleTypeCount);
        }
    }

    // Exceptions timeline chart state - scoped here so renderExceptionTimeline/
    // performGoBackAction can share it. No table-visibility filter analog to
    // filterCpuMethodsTableToZoomRange exists for Exceptions - dragging a
    // zoom here only affects the chart's own x-axis range and the hide-aware
    // exclusion math below, not which ranked rows are shown.
    var exceptionTimelineZoomRange = null;
    var exceptionTimelineChartHandle = null;

    function updateExceptionTimelineZoomStatusUi(zoomRange) {
        var statusEl = document.getElementById('exceptionTimelineZoomStatus');

        var hintEl = document.getElementById('exceptionTimelineZoomHint');
        if (hintEl) {
            hintEl.style.display = zoomRange ? 'none' : '';
        }

        if (!statusEl) {
            return;
        }

        if (!zoomRange) {
            statusEl.style.display = 'none';
            return;
        }

        statusEl.style.display = 'block';
        var labelEl = document.getElementById('exceptionTimelineZoomLabel');
        if (labelEl) {
            labelEl.textContent = `Zoom: ${formatElapsedMs(zoomRange.startMSec)} – ${formatElapsedMs(zoomRange.endMSec)}`;
        }
    }

    // Rebuilds the exception-throw-density chart for the current zoom range -
    // mirrors renderCpuTimeline exactly (linear x-axis on real RelativeMSec
    // values, non-zero-based y-axis so real throw bursts read as visible
    // peaks rather than being flattened against a 0-based scale, crosshair +
    // drag-to-zoom via the same chartZoomHelper.js infrastructure). The
    // exclusion loop only checks exceptionTypeHider (no automatic "known
    // noise" heuristic exists for exceptions the way isKnownCpuIdleWaitLeafMethodName
    // does for CPU wait methods - every exclusion here is user-driven).
    function renderExceptionTimeline(zoomRange) {
        var timeline = exceptionSummaryJson ? exceptionSummaryJson["timeline"] : null;
        var canvasElement = document.getElementById("exceptionTimeline");
        if (!timeline || !canvasElement) {
            return;
        }

        if (exceptionTimelineChartHandle) {
            exceptionTimelineChartHandle.zoomHandle.detach();
            exceptionTimelineChartHandle.crosshairHandle.detach();
            exceptionTimelineChartHandle.chart.destroy();
            exceptionTimelineChartHandle = null;
        }

        var countByBucket = timeline["countByBucket"];
        var bucketDurationMSec = timeline["bucketDurationMSec"];
        var minRelativeMSec = timeline["minRelativeMSec"];
        var bucketCount = timeline["bucketCount"];

        // typeSelfByBucket carries a per-bucket throw-count breakdown for
        // every RANKED exception type (see ExceptionJsonExporter.cs's own
        // timeline-writing code) - every ranked type hidden via
        // exceptionTypeHider has its own per-bucket contribution summed into
        // excludedBucketTotals, then subtracted from the chart's own total
        // per bucket below, purely client-side - same arithmetic as
        // renderCpuTimeline's own excludedBucketTotals.
        var excludedBucketTotals = null;
        var excludedTypeCount = 0;
        var typeSelfByBucket = timeline["typeSelfByBucket"];
        if (typeSelfByBucket) {
            for (var excludeSearchIndex = 0; excludeSearchIndex < typeSelfByBucket.length; ++excludeSearchIndex) {
                if (!exceptionTypeHider.isHidden(excludeSearchIndex)) {
                    continue;
                }

                ++excludedTypeCount;
                var candidateBuckets = typeSelfByBucket[excludeSearchIndex];
                if (!excludedBucketTotals) {
                    excludedBucketTotals = candidateBuckets.slice();
                    continue;
                }

                for (var accumulateBucketIndex = 0; accumulateBucketIndex < candidateBuckets.length; ++accumulateBucketIndex) {
                    excludedBucketTotals[accumulateBucketIndex] += candidateBuckets[accumulateBucketIndex];
                }
            }
        }

        var points = [];
        for (var bucketIndex = 0; bucketIndex < bucketCount; ++bucketIndex) {
            var bucketTotal = countByBucket[bucketIndex];
            if (excludedBucketTotals) {
                bucketTotal -= excludedBucketTotals[bucketIndex];
            }

            points.push({
                x: minRelativeMSec + bucketIndex * bucketDurationMSec,
                y: bucketTotal
            });
        }

        var xAxisTicks = { callback: formatElapsedMs };
        if (zoomRange) {
            xAxisTicks.min = zoomRange.startMSec;
            xAxisTicks.max = zoomRange.endMSec;
        }

        var dragStateHolder = { current: null };
        var crosshairStateHolder = { current: null };

        var chart = new Chart(canvasElement.getContext('2d'), {
            type: 'line',
            data: {
                datasets: [{
                    label: excludedBucketTotals ? ('Exceptions (excl. ' + excludedTypeCount + ' hidden type' + (excludedTypeCount === 1 ? '' : 's') + ')') : 'Exceptions',
                    data: points,
                    backgroundColor: 'rgba(180, 80, 80, 0.2)',
                    borderColor: 'rgba(180, 80, 80, 1)',
                    borderWidth: 1,
                    lineTension: 0,
                    pointRadius: 2,
                    pointHoverRadius: 4
                }]
            },
            // Top-level plugins array, not options.plugins - see
            // renderCpuTimeline's own comment on why (a real, previously-hit
            // bug for this exact mistake).
            plugins: [createCrosshairPlugin(crosshairStateHolder), createZoomSelectionPlugin(dragStateHolder)],
            options: {
                animation: { duration: 0 },
                maintainAspectRatio: false,
                scales: {
                    xAxes: [{ type: 'linear', ticks: xAxisTicks }],
                    // NOT beginAtZero - same "show real spikes, not a
                    // flat-looking line squashed against a 0-based axis"
                    // reasoning as renderCpuTimeline's own y-axis.
                    yAxes: [{
                        scaleLabel: { display: true, labelString: 'Exceptions (not zero-based - see chart)' }
                    }]
                }
            }
        });

        var zoomHandle = attachDragToZoom(chart, canvasElement, dragStateHolder, pixelToMSecLinear, function (startMSec, endMSec) {
            exceptionTimelineZoomRange = { startMSec: startMSec, endMSec: endMSec };
            renderExceptionTimeline(exceptionTimelineZoomRange);
            updateExceptionTimelineZoomStatusUi(exceptionTimelineZoomRange);
        });

        var crosshairHandle = attachCrosshair(chart, canvasElement, crosshairStateHolder, pixelToMSecLinear, formatElapsedMs);

        exceptionTimelineChartHandle = { chart: chart, zoomHandle: zoomHandle, crosshairHandle: crosshairHandle };
        updateExceptionTimelineZoomStatusUi(zoomRange);
    }

    // CPU timeline chart state - scoped here so renderCpuTimeline /
    // filterCpuMethodsTableToZoomRange / performGoBackAction can share it.
    var cpuTimelineZoomRange = null;
    var cpuTimelineChartHandle = null;

    // Manually-hidden CPU Methods rows (data-cpu-hotmethod-index, the same
    // index used by hotMethods/methodSelfByBucket) - toggled via the
    // rowHideBtn in each row (see wireProfileInnerTabs below). onChange
    // rebuilds the table's own Self%/Total% columns and tiles AND re-renders
    // the timeline chart, so a hide affects both places at once, the same
    // way the built-in wait-method heuristic already does.
    var cpuMethodHider = createRowHideController('cpuMethodsHideStatus', 'cpuMethodsHideStatusLabel', function () {
        rebuildHotMethodsTable();
        renderCpuTimeline(cpuTimelineZoomRange);
    });

    function updateCpuTimelineZoomStatusUi(zoomRange) {
        var statusEl = document.getElementById('cpuTimelineZoomStatus');

        // The "Drag to zoom" hint (CpuProfileRenderer.ts) and the zoom
        // status bar occupy the same corner of attention - once there's an
        // actual zoom to report, the hint (which was only ever explaining
        // how to get here) is redundant and hidden in favor of it.
        var hintEl = document.getElementById('cpuTimelineZoomHint');
        if (hintEl) {
            hintEl.style.display = zoomRange ? 'none' : '';
        }

        if (!statusEl) {
            return;
        }

        if (!zoomRange) {
            statusEl.style.display = 'none';
            return;
        }

        statusEl.style.display = 'block';
        var labelEl = document.getElementById('cpuTimelineZoomLabel');
        if (labelEl) {
            labelEl.textContent = `Zoom: ${formatElapsedMs(zoomRange.startMSec)} – ${formatElapsedMs(zoomRange.endMSec)}`;
        }
    }

    // Rebuilds the CPU sample-density chart for the current zoom range.
    // Called once when the Methods tab is first shown (zoomRange=null) and
    // on every zoom change. Uses the same attachDragToZoom infrastructure
    // the GC charts already use (chartZoomHelper.js).
    // Microsoft-DotNETCore-SampleProfiler samples EVERY managed thread's
    // stack at each tick regardless of whether that thread is actually
    // running on a core - there is no "was this thread really on-CPU" flag
    // in the event data itself, so "real CPU-bound work" has to be
    // INFERRED by recognizing known parked/blocking leaf frames and
    // excluding them, not read directly off a sample. A thread pool with
    // more idle workers than active ones (the normal, healthy state for a
    // lightly-loaded service) can otherwise make these leaves dominate the
    // timeline's own sample count without reflecting any real work at all.
    //
    // Two complementary rules, both checked against the LEAF (self) frame
    // only:
    //   - Type-prefix: these types exist purely to block/synchronize - a
    //     LowLevelLifoSemaphore or WaitHandle has no "hot compute" method,
    //     so matching every method on the type is safe.
    //   - Exact-name/contains: Thread/Task/raw-syscall types have other,
    //     genuinely CPU-bound methods too, so only specific known blocking
    //     ones are matched. PollGCWorker is a `contains` check (not exact)
    //     because .NET compiles it as a local function with a
    //     compiler-generated numeric suffix that isn't stable across
    //     builds/runtime versions (e.g. `<PollGC>g__PollGCWorker|67_0`).
    //
    // A heuristic, not a precise signal - see this feature's own design
    // discussion: an unfamiliar synchronization primitive won't be
    // recognized (undercounts the exclusion, not overcounts), and nothing
    // here can misclassify genuine user code as a wait (every entry is a
    // real BCL/CLR/interop blocking primitive, not a name pattern like
    // "contains Wait" that could false-positive on an unrelated method).
    var CPU_TIMELINE_WAIT_TYPE_PREFIXES = [
        "System.Threading.WaitHandle.",
        "System.Threading.Monitor.",
        "System.Threading.SemaphoreSlim.",
        "System.Threading.Semaphore.",
        "System.Threading.ManualResetEventSlim.",
        "System.Threading.ManualResetEvent.",
        "System.Threading.AutoResetEvent.",
        "System.Threading.LowLevelLifoSemaphore.",
        "System.Threading.LowLevelMonitor.",
        "System.Threading.SpinWait.",
        "System.Threading.SpinLock."
    ];

    var CPU_TIMELINE_WAIT_EXACT_NAMES = [
        "System.Threading.Thread.Sleep",
        "System.Threading.Thread.Join",
        "System.Threading.Tasks.Task.Wait",
        "System.Threading.Tasks.Task.InternalWait",
        "Interop+Sys.Read",
        "Interop+Sys.Write",
        "Interop+Sys.Poll"
    ];

    function isKnownCpuIdleWaitLeafMethodName(rawName) {
        for (var prefixIndex = 0; prefixIndex < CPU_TIMELINE_WAIT_TYPE_PREFIXES.length; ++prefixIndex) {
            if (rawName.indexOf(CPU_TIMELINE_WAIT_TYPE_PREFIXES[prefixIndex]) === 0) {
                return true;
            }
        }

        for (var exactIndex = 0; exactIndex < CPU_TIMELINE_WAIT_EXACT_NAMES.length; ++exactIndex) {
            if (rawName === CPU_TIMELINE_WAIT_EXACT_NAMES[exactIndex]) {
                return true;
            }
        }

        return rawName.indexOf("PollGCWorker") !== -1;
    }

    // Narrower than isKnownCpuIdleWaitLeafMethodName above - that list is
    // mostly general thread synchronization (Monitor/Sleep/Join/generic
    // semaphores), which blocks a thread but isn't itself "I/O". This one is
    // scoped to blocking I/O and its thread-pool corollary: raw syscalls,
    // sockets, files, pipes - used by the CPU Methods table's own "Hide
    // IO-Bound Methods" button (hideAllIoBoundCpuMethods below), not the
    // automatic timeline exclusion (which stays as-is; this is a separate,
    // user-triggered bulk-hide of TABLE rows, not a change to what the chart
    // auto-excludes). Interop+Sys. is a type-prefix here (not limited to the
    // three exact names - Read/Write/Poll - the wait list above hardcodes)
    // since every method on that type is a raw POSIX syscall wrapper in
    // this runtime, and syscalls this parser sees leaf-sampled are
    // overwhelmingly I/O ones (read/write/send/recv/poll/accept/connect),
    // not compute.
    //
    // System.Threading.LowLevelLifoSemaphore. is included even though it's
    // a generic semaphore wait, not a syscall - on a real capture it's the
    // ThreadPool's own worker-thread idle wait (WaitForSignal: "no work
    // queued yet"), and in a typical async server workload that idle time
    // is overwhelmingly time spent waiting for I/O completions (socket
    // reads, DB calls, etc.) to be dispatched back to a pool thread, not
    // waiting on another thread the way Monitor/lock contention is. It's
    // also, by a wide margin, the single largest leaf method on most real
    // captures (confirmed: ~74% of all samples on one production capture) -
    // a button people reach for specifically to see past thread-pool idle
    // time down to real I/O and compute work isn't doing its job if it
    // leaves the biggest contributor to that idle time out.
    var CPU_IO_BOUND_TYPE_PREFIXES = [
        "Interop+Sys.",
        "System.Net.Sockets.Socket.",
        "System.Net.Sockets.NetworkStream.",
        "System.IO.FileStream.",
        "System.IO.Pipes.",
        "System.Threading.LowLevelLifoSemaphore."
    ];

    var CPU_IO_BOUND_EXACT_NAMES = [
        "System.IO.Stream.Read",
        "System.IO.Stream.Write",
        "System.IO.Stream.ReadAsync",
        "System.IO.Stream.WriteAsync",
        "System.IO.FileSystem.ReadFile",
        "System.IO.FileSystem.WriteFile"
    ];

    function isKnownIoBoundLeafMethodName(rawName) {
        for (var prefixIndex = 0; prefixIndex < CPU_IO_BOUND_TYPE_PREFIXES.length; ++prefixIndex) {
            if (rawName.indexOf(CPU_IO_BOUND_TYPE_PREFIXES[prefixIndex]) === 0) {
                return true;
            }
        }

        for (var exactIndex = 0; exactIndex < CPU_IO_BOUND_EXACT_NAMES.length; ++exactIndex) {
            if (rawName === CPU_IO_BOUND_EXACT_NAMES[exactIndex]) {
                return true;
            }
        }

        return false;
    }

    // Bulk-hides every ranked CPU Methods row isKnownIoBoundLeafMethodName
    // recognizes - reuses the exact same per-row hide state
    // (cpuMethodHider) a manual click on one row's own rowHideBtn would set,
    // so the result composes identically with everything that already reads
    // that state (rebuildHotMethodsTable's Self%/Total%/tiles,
    // renderCpuTimeline's chart exclusion, filterCpuMethodsTableToZoomRange's
    // visibility) - this button is just a faster way to reach a state the
    // user could otherwise build one row at a time.
    function hideAllIoBoundCpuMethods() {
        var hotMethods = cpuProfileJson ? cpuProfileJson["hotMethods"] : null;
        var methodNamesForIoBound = cpuProfileJson ? cpuProfileJson["methodNames"] : null;
        if (!hotMethods || !methodNamesForIoBound) {
            return;
        }

        var matchingIndices = [];
        for (var index = 0; index < hotMethods.length; ++index) {
            var name = methodNamesForIoBound[hotMethods[index]["frame"]];
            if (isKnownIoBoundLeafMethodName(name)) {
                matchingIndices.push(index);
            }
        }

        cpuMethodHider.hideMany(matchingIndices);
    }

    function renderCpuTimeline(zoomRange) {
        var sampleTimeline = cpuProfileJson ? cpuProfileJson["sampleTimeline"] : null;
        var canvasElement = document.getElementById("cpuProfileTimeline");
        if (!sampleTimeline || !canvasElement) {
            return;
        }

        if (cpuTimelineChartHandle) {
            cpuTimelineChartHandle.zoomHandle.detach();
            cpuTimelineChartHandle.crosshairHandle.detach();
            cpuTimelineChartHandle.chart.destroy();
            cpuTimelineChartHandle = null;
        }

        var samplesByBucket = sampleTimeline["samplesByBucket"];
        var bucketDurationMSec = sampleTimeline["bucketDurationMSec"];
        var minRelativeMSec = sampleTimeline["minRelativeMSec"];
        var bucketCount = sampleTimeline["bucketCount"];

        // methodSelfByBucket already carries a per-bucket self-sample
        // breakdown for every RANKED hot method (see
        // Cpu/CpuProfileJsonExporter.cs's own WriteTimeline) - every ranked
        // method matching isKnownCpuIdleWaitLeafMethodName, OR manually
        // hidden via cpuMethodHider (see the Methods table's own rowHideBtn),
        // has its own per-bucket contribution summed into
        // excludedBucketTotals, then subtracted from the chart's own total
        // per bucket below, purely client-side. Methods that don't match
        // either condition, or that aren't ranked in the top-200 hot methods
        // at all (so have no per-bucket breakdown to draw from), simply
        // don't contribute - the chart falls back to the fully unmodified
        // total if nothing in this capture matches.
        var excludedBucketTotals = null;
        var excludedMethodCount = 0;
        var hotMethods = cpuProfileJson ? cpuProfileJson["hotMethods"] : null;
        var methodNamesForExclusion = cpuProfileJson ? cpuProfileJson["methodNames"] : null;
        var methodSelfByBucket = sampleTimeline["methodSelfByBucket"];
        if (hotMethods && methodNamesForExclusion && methodSelfByBucket) {
            for (var excludeSearchIndex = 0; excludeSearchIndex < hotMethods.length; ++excludeSearchIndex) {
                var candidateName = methodNamesForExclusion[hotMethods[excludeSearchIndex]["frame"]];
                if (!isKnownCpuIdleWaitLeafMethodName(candidateName) && !cpuMethodHider.isHidden(excludeSearchIndex)) {
                    continue;
                }

                ++excludedMethodCount;
                var candidateBuckets = methodSelfByBucket[excludeSearchIndex];
                if (!excludedBucketTotals) {
                    excludedBucketTotals = candidateBuckets.slice();
                    continue;
                }

                for (var accumulateBucketIndex = 0; accumulateBucketIndex < candidateBuckets.length; ++accumulateBucketIndex) {
                    excludedBucketTotals[accumulateBucketIndex] += candidateBuckets[accumulateBucketIndex];
                }
            }
        }

        var points = [];
        for (var bucketIndex = 0; bucketIndex < bucketCount; ++bucketIndex) {
            var bucketTotal = samplesByBucket[bucketIndex];
            if (excludedBucketTotals) {
                bucketTotal -= excludedBucketTotals[bucketIndex];
            }

            points.push({
                x: minRelativeMSec + bucketIndex * bucketDurationMSec,
                y: bucketTotal
            });
        }

        var xAxisTicks = { callback: formatElapsedMs };
        if (zoomRange) {
            xAxisTicks.min = zoomRange.startMSec;
            xAxisTicks.max = zoomRange.endMSec;
        }

        var dragStateHolder = { current: null };
        var crosshairStateHolder = { current: null };

        var chart = new Chart(canvasElement.getContext('2d'), {
            type: 'line',
            data: {
                datasets: [{
                    // Labeled to flag the exclusion explicitly - otherwise
                    // this dataset's own max looking smaller than the
                    // summary tiles' own unfiltered "Total" (which is NOT
                    // adjusted - see isKnownCpuIdleWaitLeafMethodName's own
                    // comment) would read as a bug, not a deliberate choice.
                    // "excluded" here covers both the automatic wait-method
                    // heuristic and any rows the user hid manually - the
                    // count doesn't distinguish which, since both mean the
                    // same thing to someone reading the chart (this method
                    // is no longer counted).
                    label: excludedBucketTotals ? ('CPU Samples (excl. ' + excludedMethodCount + ' method' + (excludedMethodCount === 1 ? '' : 's') + ')') : 'CPU Samples',
                    data: points,
                    backgroundColor: 'rgba(72, 83, 136, 0.2)',
                    borderColor: 'rgba(72, 83, 136, 1)',
                    borderWidth: 1,
                    lineTension: 0,
                    pointRadius: 2,
                    pointHoverRadius: 4
                }]
            },
            // Chart.js 2.9.4's per-instance plugin array is a TOP-LEVEL config
            // key (config.plugins), a SIBLING of options/data - NOT nested
            // inside options (verified directly against
            // node_modules/chart.js/dist/Chart.js's own core_plugins.descriptors:
            // it reads config.plugins directly, while config.options.plugins is
            // a completely different thing, an { [pluginId]: perPluginOptions }
            // lookup table for configuring ALREADY-REGISTERED plugins, not a
            // place to hand it new plugin objects). Placing this inside
            // options - as this chart's own config previously did - meant
            // BOTH the crosshair AND the drag-selection rectangle plugins
            // were silently never invoked at all: registration failed with
            // no error, so the underlying drag-to-zoom mouse handling
            // (attachDragToZoom, which is independent of Chart.js's plugin
            // system) still worked, but its own visual feedback never
            // rendered - confirmed by instrumenting a real headless-browser
            // hover and finding the plugin's own afterDraw hook was never
            // called, even on the chart's very first render. Matches the
            // OTHER four charts' own top-level placement (see e.g. the
            // "GC Pause Time by Generation" chart above) - this one had
            // drifted from that pattern, not the other four.
            //
            // Crosshair listed first so the drag-selection rectangle (on
            // top of it) stays fully opaque/legible during an active drag
            // rather than a dashed line showing through it.
            plugins: [createCrosshairPlugin(crosshairStateHolder), createZoomSelectionPlugin(dragStateHolder)],
            options: {
                animation: { duration: 0 },
                maintainAspectRatio: false,
                scales: {
                    xAxes: [{ type: 'linear', ticks: xAxisTicks }],
                    // NOT beginAtZero - the whole point of this chart, after
                    // excluding known/hidden wait methods, is to show real
                    // CPU-bound work as visible peaks. A steady-traffic
                    // service's real CPU-bound signal sits on a high,
                    // fairly narrow baseline (measured on a real capture:
                    // adjusted per-bucket samples ranged ~5,600-6,600, a
                    // real ~15-18% swing) - forcing the axis down to 0 wastes
                    // the bottom ~85% of the chart's own height on empty
                    // space no bucket ever reaches, visually flattening that
                    // swing into what reads as a dead-flat line. Auto-scaling
                    // to the data's own range (Chart.js 2.x default when
                    // beginAtZero is omitted) turns the same real variation
                    // into clearly visible peaks - this is a relative, not
                    // absolute, view of CPU-bound activity, which is exactly
                    // what "show spikes of CPU-bound work" calls for.
                    yAxes: [{
                        scaleLabel: { display: true, labelString: 'CPU Samples (not zero-based - see chart)' }
                    }]
                }
            }
        });

        var zoomHandle = attachDragToZoom(chart, canvasElement, dragStateHolder, pixelToMSecLinear, function (startMSec, endMSec) {
            cpuTimelineZoomRange = { startMSec: startMSec, endMSec: endMSec };
            renderCpuTimeline(cpuTimelineZoomRange);
            filterCpuMethodsTableToZoomRange(cpuTimelineZoomRange);
            updateCpuTimelineZoomStatusUi(cpuTimelineZoomRange);
        });

        var crosshairHandle = attachCrosshair(chart, canvasElement, crosshairStateHolder, pixelToMSecLinear, formatElapsedMs);

        cpuTimelineChartHandle = { chart: chart, zoomHandle: zoomHandle, crosshairHandle: crosshairHandle };
        updateCpuTimelineZoomStatusUi(zoomRange);
    }

    // Rewrites the CPU Methods table's Self%/Total% cells and the
    // Total/Ranked Methods summary tiles against a denominator that
    // excludes every manually-hidden row's own selfSamples - the same
    // "total sample count minus hidden self samples" denominator the
    // timeline chart's own excludedBucketTotals already uses (see
    // renderCpuTimeline above), so a hidden row means the same thing
    // everywhere on this page. Total % is recomputed against this SAME new
    // denominator too, not by summing every row's own totalSamples (which
    // are inclusive-of-callees and don't partition the capture the way
    // selfSamples does).
    // Rows are never removed from the DOM (indices must stay stable for the
    // sort/expand/timeline code that keys off data-cpu-hotmethod-index) -
    // only their text and, via filterCpuMethodsTableToZoomRange at the end,
    // their visibility change.
    function rebuildHotMethodsTable() {
        var hotMethods = cpuProfileJson ? cpuProfileJson["hotMethods"] : null;
        var totalSampleCount = cpuProfileJson ? cpuProfileJson["totalSampleCount"] : 0;
        var table = document.getElementById('cpuMethodsTable');
        if (!hotMethods || !totalSampleCount || !table) {
            return;
        }

        var hiddenSelfSamples = 0;
        var visibleMethodCount = 0;
        for (var sumIndex = 0; sumIndex < hotMethods.length; ++sumIndex) {
            if (cpuMethodHider.isHidden(sumIndex)) {
                hiddenSelfSamples += hotMethods[sumIndex]["selfSamples"];
            } else {
                ++visibleMethodCount;
            }
        }

        var adjustedTotal = totalSampleCount - hiddenSelfSamples;

        var rows = table.rows;
        for (var rowIndex = 1; rowIndex < rows.length; ++rowIndex) {
            var row = rows[rowIndex];
            if (row.classList.contains('callPathsDetail')) {
                continue;
            }

            var methodIndex = parseInt(row.getAttribute('data-cpu-hotmethod-index'), 10);
            if (isNaN(methodIndex) || !hotMethods[methodIndex]) {
                continue;
            }

            var method = hotMethods[methodIndex];
            var selfPercent = adjustedTotal > 0 ? (method["selfSamples"] * 100.0) / adjustedTotal : 0;
            var totalPercent = adjustedTotal > 0 ? (method["totalSamples"] * 100.0) / adjustedTotal : 0;

            // cells[0] is the rowHideBtn column, cells[1] is Method - Self %
            // and Total % (the two columns this function rewrites) are
            // cells[2]/cells[4]; Self Samples/Total Samples (cells[3]/[5])
            // are raw counts, unaffected by hiding.
            row.cells[2].textContent = selfPercent.toFixed(2);
            row.cells[4].textContent = totalPercent.toFixed(2);
        }

        var totalTile = document.getElementById('cpuMethodsTotalTile');
        if (totalTile) {
            totalTile.textContent = adjustedTotal.toLocaleString();
        }

        var rankedTile = document.getElementById('cpuMethodsRankedTile');
        if (rankedTile) {
            rankedTile.textContent = visibleMethodCount.toLocaleString();
        }

        filterCpuMethodsTableToZoomRange(cpuTimelineZoomRange);
    }

    // Shows/hides rows in the CPU hot-methods table based on whether the
    // method had any self-time samples within the selected time range.
    // Methods with zero self-samples in the range are hidden; their paired
    // callPathsDetail rows follow suit. zoomRange=null restores all rows.
    function filterCpuMethodsTableToZoomRange(zoomRange) {
        var sampleTimeline = cpuProfileJson ? cpuProfileJson["sampleTimeline"] : null;
        var hotMethodsTable = document.querySelector('.cpuHotMethodsTable table');
        if (!hotMethodsTable) {
            return;
        }

        var methodSelfByBucket = sampleTimeline ? sampleTimeline["methodSelfByBucket"] : null;
        var bucketDurationMSec = sampleTimeline ? sampleTimeline["bucketDurationMSec"] : 1;
        var minRelativeMSec = sampleTimeline ? sampleTimeline["minRelativeMSec"] : 0;
        var bucketCount = sampleTimeline ? sampleTimeline["bucketCount"] : 0;

        var startBucket = 0;
        var endBucket = bucketCount - 1;
        if (zoomRange && bucketCount > 0) {
            startBucket = Math.max(0, Math.floor((zoomRange.startMSec - minRelativeMSec) / bucketDurationMSec));
            endBucket = Math.min(bucketCount - 1, Math.ceil((zoomRange.endMSec - minRelativeMSec) / bucketDurationMSec));
        }

        var rows = hotMethodsTable.rows;
        for (var rowIndex = 1; rowIndex < rows.length; ++rowIndex) {
            var row = rows[rowIndex];
            if (row.classList.contains('callPathsDetail')) {
                continue;
            }

            var isVisible = !zoomRange;
            if (!isVisible && methodSelfByBucket) {
                var methodIndex = parseInt(row.getAttribute('data-cpu-hotmethod-index'), 10);
                if (!isNaN(methodIndex) && methodSelfByBucket[methodIndex]) {
                    var methodBuckets = methodSelfByBucket[methodIndex];
                    for (var bucketIndex = startBucket; bucketIndex <= endBucket; ++bucketIndex) {
                        if (methodBuckets[bucketIndex] > 0) {
                            isVisible = true;
                            break;
                        }
                    }
                }
            }

            // A manually-hidden row (cpuMethodHider) stays hidden regardless
            // of zoom - the two visibility conditions compose (both must
            // agree the row should show), so un-zooming never un-hides a row
            // the user hid on purpose, and hiding a row while zoomed in
            // doesn't get silently undone by the next zoom change.
            var rowIndexForHide = parseInt(row.getAttribute('data-cpu-hotmethod-index'), 10);
            if (isVisible && !isNaN(rowIndexForHide) && cpuMethodHider.isHidden(rowIndexForHide)) {
                isVisible = false;
            }

            row.style.display = isVisible ? '' : 'none';
            var pairedDetailId = row.getAttribute('data-cpu-method-target');
            if (pairedDetailId) {
                var pairedDetailRow = document.getElementById(pairedDetailId);
                if (pairedDetailRow) {
                    // An inline style is only actually NEEDED to force-hide
                    // an already-open detail row whose own top-level row
                    // just got filtered out by the zoom (an expanded row
                    // with no visible parent above it would look broken).
                    // Setting one unconditionally - as this used to, via
                    // `(isVisible && expanded) ? '' : 'none'` - PERMANENTLY
                    // pinned an inline "display:none" onto every row that
                    // happened to be visible-but-not-yet-expanded at the
                    // moment of a zoom, since inline style always wins over
                    // a stylesheet rule regardless of specificity: the very
                    // next click to expand it (which only ever toggles the
                    // .expanded CLASS - see wireProfileInnerTabs/
                    // buildAndExpandCpuMethodRow) had that inline override
                    // sitting on top of it with no code path that ever
                    // cleared it, so .callPathsDetail.expanded's own
                    // display:table-row rule could never take effect again -
                    // a row would silently stop expanding the first time
                    // ANY zoom action touched the chart. Clearing the
                    // inline style (empty string removes the property
                    // entirely, not "set to an empty value") whenever the
                    // row is visible hands control back to those CSS class
                    // rules, so a later expand/collapse click behaves
                    // exactly as it does before any zoom ever happened.
                    pairedDetailRow.style.display = isVisible ? '' : 'none';
                }
            }
        }
    }

    function wireProfileInnerTabs() {
        if (cpuProfileJson) {
            initCpuDrillDownMethodNames(cpuProfileJson["methodNames"]);
        }

        var profileTabButtons = document.querySelectorAll('#view-profile .heapContentsTabButton');
        for (var tabButtonIndex = 0; tabButtonIndex < profileTabButtons.length; ++tabButtonIndex) {
            profileTabButtons[tabButtonIndex].addEventListener('click', function (event) {
                switchProfileTab(event.currentTarget.getAttribute('data-profiletab'));
            });
        }

        var cpuTimelineResetBtn = document.getElementById('cpuTimelineResetZoomBtn');
        if (cpuTimelineResetBtn) {
            cpuTimelineResetBtn.addEventListener('click', function () {
                cpuTimelineZoomRange = null;
                renderCpuTimeline(null);
                filterCpuMethodsTableToZoomRange(null);
                updateCpuTimelineZoomStatusUi(null);
            });
        }

        var cpuMethodsShowAllBtn = document.getElementById('cpuMethodsShowAllBtn');
        if (cpuMethodsShowAllBtn) {
            cpuMethodsShowAllBtn.addEventListener('click', function () {
                cpuMethodHider.reset();
            });
        }

        // The Methods panel holds both the top-level expandable method rows
        // (data-cpu-method-expandable) and the nested caller-tree rows
        // (data-cpu-expandable) within each expanded method. One delegated
        // listener handles both: the method-expandable check runs first, and
        // only falls through to the cpu-expandable check if no method row was
        // clicked - their attribute names are distinct so there's no collision.
        var methodsPanel = document.getElementById('profile-tab-hotmethods');
        if (methodsPanel) {
            methodsPanel.addEventListener('click', function (event) {
                // Row-hide cell - checked first, ahead of every other check
                // below, so a click anywhere in it never also triggers the
                // row's own expand toggle (data-cpu-method-expandable sits
                // on the same <tr>). Whole cell is the click target, not
                // just the ✕ glyph itself. stopPropagation isn't needed here
                // (this listener IS the delegation target), but the early
                // return is - every other branch below would otherwise
                // still run.
                var hideCell = event.target.closest('.rowHideColumn');
                if (hideCell) {
                    var hideRow = hideCell.closest('[data-cpu-hotmethod-index]');
                    if (hideRow) {
                        cpuMethodHider.toggle(parseInt(hideRow.getAttribute('data-cpu-hotmethod-index'), 10));
                    }
                    return;
                }

                // Master Expand All/Collapse All - every method row at once
                // (see CpuProfileRenderer.ts's methodsExpandControlsHtml,
                // between the timeline chart and the table). No per-method
                // pair anymore (buildInlineCpuMethodCallerTree used to emit
                // one inside each opened row's own tree, redundant with
                // this master pair - see that function's own comment on
                // why it was removed), so this is the only Expand
                // All/Collapse All entry point left, checked before the
                // row-toggle checks below.
                if (event.target.closest('.cpuMethodsExpandAllBtn')) {
                    expandAllCpuMethodRows(true);
                    return;
                }

                if (event.target.closest('.cpuMethodsCollapseAllBtn')) {
                    expandAllCpuMethodRows(false);
                    return;
                }

                if (event.target.closest('.cpuMethodsHideIoBoundBtn')) {
                    hideAllIoBoundCpuMethods();
                    return;
                }

                // Top-level method row expand/collapse
                var methodRow = event.target.closest('[data-cpu-method-expandable="true"]');
                if (methodRow) {
                    var isExpanded = methodRow.classList.contains('expanded');
                    var targetId = methodRow.getAttribute('data-cpu-method-target');
                    var detailRow = document.getElementById(targetId);
                    if (!detailRow) {
                        return;
                    }

                    if (isExpanded) {
                        methodRow.classList.remove('expanded');
                        detailRow.classList.remove('expanded');
                    } else {
                        buildAndExpandCpuMethodRow(methodRow, detailRow);

                        // Auto-descend through any non-branching chain of
                        // callers (a long, straight call stack is the common
                        // case - see followCpuDrillDownLinearRun's own
                        // comment) so a click reveals the first real
                        // decision point immediately, instead of requiring
                        // one click per frame down a stack that never
                        // branches.
                        followCpuDrillDownLinearRun(detailRow);
                    }

                    return;
                }

                // Interior caller-tree node expand/collapse (within an already-
                // expanded method's inline caller tree)
                var leafRow = event.target.closest('[data-cpu-expandable="true"]');
                if (!leafRow) {
                    return;
                }

                var callerDetailRow = document.getElementById(leafRow.getAttribute('data-cpu-target'));
                if (!callerDetailRow) {
                    return;
                }

                if (leafRow.classList.contains('expanded')) {
                    leafRow.classList.remove('expanded');
                    callerDetailRow.classList.remove('expanded');
                    return;
                }

                expandCpuDrillDownRowFollowingLinearRun(leafRow, callerDetailRow);
            });
        }
    }

    // Wires the unified Exceptions panel - no tabs/back button anymore (see
    // ExceptionSummaryRenderer.ts's own header comment), so this now mirrors
    // wireProfileInnerTabs'/wireContentionTab's click-delegation shape
    // directly: row-hide cell first, then top-level type-row expand, then
    // interior caller-node expand. One delegated listener on the whole
    // #view-exceptions container handles all three, since (like the CPU
    // Methods table) rows are injected once and never wholesale replaced -
    // only individual .callerTreeCell contents change on lazy-build.
    function wireExceptionsPanel() {
        if (exceptionSummaryJson) {
            initExceptionDrillDownMethodNames(exceptionSummaryJson["methodNames"]);
        }

        var resetZoomBtn = document.getElementById('exceptionTimelineResetZoomBtn');
        if (resetZoomBtn) {
            resetZoomBtn.addEventListener('click', function () {
                exceptionTimelineZoomRange = null;
                renderExceptionTimeline(null);
                updateExceptionTimelineZoomStatusUi(null);
            });
        }

        var exceptionsPanel = document.getElementById('view-exceptions');
        if (!exceptionsPanel) {
            return;
        }

        exceptionsPanel.addEventListener('click', function (event) {
            if (event.target.closest('#exceptionTypesShowAllBtn')) {
                exceptionTypeHider.reset();
                return;
            }

            // Row-hide cell - checked first, ahead of every other check
            // below, so a click anywhere in it never also toggles that
            // row's own expand state. Whole cell is the click target, not
            // just the ✕ glyph itself.
            var hideCell = event.target.closest('.rowHideColumn');
            if (hideCell) {
                var hideRow = hideCell.closest('[data-exception-type-index]');
                if (hideRow) {
                    exceptionTypeHider.toggle(parseInt(hideRow.getAttribute('data-exception-type-index'), 10));
                }
                return;
            }

            // Top-level exception type row expand/collapse.
            var typeRow = event.target.closest('[data-exception-expandable="true"]');
            if (typeRow) {
                var typeDetailRow = document.getElementById(typeRow.getAttribute('data-exception-target'));
                if (!typeDetailRow) {
                    return;
                }

                if (typeRow.classList.contains('expanded')) {
                    typeRow.classList.remove('expanded');
                    typeDetailRow.classList.remove('expanded');
                    return;
                }

                buildAndExpandExceptionTypeRow(typeRow, typeDetailRow);
                return;
            }

            // Interior throw-site/caller node expand/collapse (within an
            // already-expanded type's inline tree) - see
            // renderExceptionTreeRow's own comment on why both roles share
            // this one attribute name.
            var callerRow = event.target.closest('[data-exception-caller-expandable="true"]');
            if (!callerRow) {
                return;
            }

            var callerDetailRow = document.getElementById(callerRow.getAttribute('data-exception-caller-target'));
            if (!callerDetailRow) {
                return;
            }

            if (callerRow.classList.contains('expanded')) {
                callerRow.classList.remove('expanded');
                callerDetailRow.classList.remove('expanded');
                return;
            }

            expandExceptionDrillDownRowFollowingLinearRun(callerRow, callerDetailRow);
        });

        if (exceptionSummaryJson && exceptionSummaryJson["timeline"]) {
            renderExceptionTimeline(null);
        }
    }

    // The contention timeline's current zoom range - null means unzoomed
    // (all contentions visible), a {startMSec, endMSec} range means only
    // contentions in that window affect the table display.
    var contentionTimelineZoomRange = null;
    var contentionTimelineChartHandle = null;

    // Manually-hidden Contention Top Sites rows (data-contention-site-index).
    // No per-site timeline breakdown exists (see filterContentionSitesToZoomRange's
    // own comment), so unlike cpuMethodHider this only affects the table's
    // own % of Wait column and tiles - the contention timeline chart is
    // unaffected by a hide.
    var contentionSiteHider = createRowHideController('contentionSitesHideStatus', 'contentionSitesHideStatusLabel', function () {
        rebuildContentionSitesTable();
    });

    function updateContentionTimelineZoomStatusUi(zoomRange) {
        var statusEl = document.getElementById('contentionTimelineZoomStatus');
        var labelEl = document.getElementById('contentionTimelineZoomLabel');
        if (!statusEl) {
            return;
        }

        if (!zoomRange) {
            statusEl.style.display = 'none';
            return;
        }

        statusEl.style.display = '';
        if (labelEl) {
            labelEl.textContent = formatElapsedMs(zoomRange.startMSec) + " – " + formatElapsedMs(zoomRange.endMSec) + " (Backspace to reset)";
        }
    }

    // Was calling attachDragToZoom/pixelToMSecLinear/createZoomSelectionPlugin
    // against a signature none of them actually have (attachDragToZoom's
    // real signature - chartZoomHelper.js - is (chart, canvasElement,
    // dragStateHolder, pixelToMSecFn, onRangeSelected); this call passed
    // (canvas, callback, plugin) instead) - so canvasElement inside
    // attachDragToZoom was actually the onRangeSelected callback function,
    // and `canvasElement.addEventListener(...)` (its very first statement)
    // threw a TypeError on every single call, confirmed against a real
    // production capture's own DevTools console. Since this function never
    // caught that exception, it also aborted wireContentionTab's own
    // caller (the view-nav click handler) partway through - skipping
    // `contentionInjected = true` on every visit - real user-visible
    // fallout, not just a broken chart: the Contention view's very first
    // click handler attachment could still succeed before the throw (so a
    // direct-to-Contention visit's rows worked), but any FOLLOW-UP visit to
    // Contention re-ran the entire injection block from scratch (innerHTML
    // included) since contentionInjected was still false - a second,
    // fully-replaced click listener queued on top of DOM elements from a
    // prior injection pass in a way that, combined with whatever else
    // happened to be attached first (e.g. an Exceptions Drill Down visit's
    // own timing), left the freshly-injected rows' own listener never
    // actually reached by the browser's dispatch order.
    //
    // Fixed to match the SAME pattern renderCpuTimeline (a linear-scale
    // chart) and allocationStats.js's renderAllocationTypeTimelineChart (a
    // category-scale chart, like this one - labels here are pre-formatted
    // strings via formatElapsedMs, not raw values) already use correctly:
    // build the dragStateHolder and the zoom-selection plugin BEFORE
    // constructing the Chart (the plugin has to exist before the chart
    // does), pass the plugin via the chart's own `plugins:` array at
    // construction time (not stuffed into `options.plugins`, and not the
    // zoomHandle - a { detach } handle, not a Chart.js plugin, which is
    // what this used to hand it), then call attachDragToZoom with the real
    // chart instance AFTER construction. pixelToMSecCategory (not
    // pixelToMSecLinear) since this chart's x-axis has no `type: 'linear'`
    // set, so Chart.js defaults it to 'category' - see chartZoomHelper.js's
    // own header comment on why the two need different pixel->value math.
    function renderContentionTimeline(zoomRange) {
        var canvas = document.getElementById('contentionTimeline');
        if (!canvas || !contentionSummaryJson) {
            return;
        }

        if (contentionTimelineChartHandle) {
            contentionTimelineChartHandle.zoomHandle.detach();
            contentionTimelineChartHandle.chart.destroy();
            contentionTimelineChartHandle = null;
        }

        var timeline = contentionSummaryJson["timeline"];
        if (!timeline) {
            return;
        }

        var bucketCount = timeline["bucketCount"];
        var bucketDurationMSec = timeline["bucketDurationMSec"];
        var minRelativeMSec = timeline["minRelativeMSec"];
        var waitMSecByBucket = timeline["waitMSecByBucket"];

        var labels = [];
        var bucketStartMSecs = [];
        var data = [];
        for (var bucketIndex = 0; bucketIndex < bucketCount; ++bucketIndex) {
            var bucketStartMSec = minRelativeMSec + bucketIndex * bucketDurationMSec;
            labels.push(formatElapsedMs(bucketStartMSec));
            bucketStartMSecs.push(bucketStartMSec);
            data.push(waitMSecByBucket[bucketIndex]);
        }

        var isZoomed = zoomRange !== null;
        var zoomStartBucket = 0;
        var zoomEndBucket = bucketCount - 1;
        if (isZoomed) {
            zoomStartBucket = Math.max(0, Math.floor((zoomRange.startMSec - minRelativeMSec) / bucketDurationMSec));
            zoomEndBucket = Math.min(bucketCount - 1, Math.ceil((zoomRange.endMSec - minRelativeMSec) / bucketDurationMSec));
        }

        var visibleLabels = labels.slice(zoomStartBucket, zoomEndBucket + 1);
        var visibleBucketStartMSecs = bucketStartMSecs.slice(zoomStartBucket, zoomEndBucket + 1);
        var visibleData = data.slice(zoomStartBucket, zoomEndBucket + 1);

        var dragStateHolder = { current: null };

        var chart = new Chart(canvas, {
            type: 'line',
            data: {
                labels: visibleLabels,
                datasets: [{
                    label: 'Lock Wait (ms)',
                    data: visibleData,
                    borderColor: 'rgba(180, 80, 80, 0.8)',
                    backgroundColor: 'rgba(180, 80, 80, 0.2)',
                    borderWidth: 1,
                    pointRadius: 2,
                    fill: true
                }]
            },
            plugins: [createZoomSelectionPlugin(dragStateHolder)],
            options: {
                animation: { duration: 0 },
                responsive: true,
                maintainAspectRatio: false,
                legend: { display: false },
                scales: {
                    xAxes: [{ display: false }],
                    yAxes: [{ display: true, ticks: { beginAtZero: true } }]
                },
                tooltips: {
                    callbacks: {
                        title: function (tooltipItems) {
                            return tooltipItems.length > 0 ? tooltipItems[0].xLabel : '';
                        }
                    }
                }
            }
        });

        var zoomHandle = attachDragToZoom(chart, canvas, dragStateHolder, function (chartArg, pixelX) {
            return pixelToMSecCategory(chartArg, pixelX, visibleBucketStartMSecs);
        }, function (startMSec, endMSec) {
            contentionTimelineZoomRange = { startMSec: startMSec, endMSec: endMSec };
            filterContentionSitesToZoomRange(contentionTimelineZoomRange);
            updateContentionTimelineZoomStatusUi(contentionTimelineZoomRange);
            renderContentionTimeline(contentionTimelineZoomRange);
        });

        contentionTimelineChartHandle = { chart: chart, zoomHandle: zoomHandle };
    }

    function filterContentionSitesToZoomRange(zoomRange) {
        var sitesTable = document.querySelector('#view-contention .cpuHotMethodsTable table');
        if (!sitesTable || !contentionSummaryJson) {
            return;
        }

        var timeline = contentionSummaryJson["timeline"];
        if (!timeline) {
            return;
        }

        // No per-site timeline in siteDrillDown - use topSites data
        // to determine which rows to show: show all rows when unzoomed,
        // hide rows when their zoom-range wait share is zero. For now
        // simply show all rows when zoomed (row-level timeline data not
        // available at the site granularity in the current JSON shape).
        // Individual contention site timeline is a future enhancement.
        var rows = sitesTable.querySelectorAll('tr.contentionSiteRow');
        for (var rowIndex = 0; rowIndex < rows.length; ++rowIndex) {
            var siteIndex = parseInt(rows[rowIndex].getAttribute('data-contention-site-index'), 10);
            var isHiddenByUser = !isNaN(siteIndex) && contentionSiteHider.isHidden(siteIndex);
            rows[rowIndex].style.display = isHiddenByUser ? 'none' : '';
            var detailRow = document.getElementById('contentionSiteDetail' + siteIndex);
            if (detailRow) {
                detailRow.style.display = isHiddenByUser ? 'none' : '';
            }
        }
    }

    // Rewrites the Contention Top Sites table's % of Wait cells and the
    // Total/Avg Wait tiles against a denominator that excludes every
    // manually-hidden row's own TotalWaitMSec - mirrors
    // rebuildHotMethodsTable's "adjusted total" approach for the CPU
    // Methods table. Total Events is left unchanged (hiding a row doesn't
    // remove events from the capture, just from this recomputed view).
    function rebuildContentionSitesTable() {
        var topSites = contentionSummaryJson ? contentionSummaryJson["topSites"] : null;
        var table = document.getElementById('contentionSitesTable');
        if (!topSites || !table) {
            return;
        }

        // Both tiles use "full capture total minus the hidden RANKED sites'
        // own share" (not "sum of visible topSites' own values") as their
        // denominator - topSites is a bounded top-N ranking, same as CPU's
        // hotMethods, so summing only the visible ranked entries would
        // silently drop whatever long tail exists beyond topSites. Matches
        // rebuildHotMethodsTable's identical reasoning for totalSampleCount.
        var hiddenWaitMSec = 0;
        var hiddenContentionCount = 0;
        for (var sumIndex = 0; sumIndex < topSites.length; ++sumIndex) {
            if (contentionSiteHider.isHidden(sumIndex)) {
                hiddenWaitMSec += topSites[sumIndex]["TotalWaitMSec"];
                hiddenContentionCount += topSites[sumIndex]["ContentionCount"];
            }
        }

        var adjustedTotalWaitMSec = contentionSummaryJson["totalContentionWaitMSec"] - hiddenWaitMSec;
        var adjustedContentionCount = contentionSummaryJson["totalContentionCount"] - hiddenContentionCount;

        var rows = table.rows;
        for (var rowIndex = 1; rowIndex < rows.length; ++rowIndex) {
            var row = rows[rowIndex];
            if (row.classList.contains('callPathsDetail')) {
                continue;
            }

            var siteIndex = parseInt(row.getAttribute('data-contention-site-index'), 10);
            if (isNaN(siteIndex) || !topSites[siteIndex]) {
                continue;
            }

            var site = topSites[siteIndex];
            var percentOfWait = adjustedTotalWaitMSec > 0 ? (site["TotalWaitMSec"] * 100.0) / adjustedTotalWaitMSec : 0;

            // cells[0] is the rowHideBtn column, cells[1] is the site name -
            // % of Wait (the only recomputed column) is cells[5].
            row.cells[5].textContent = percentOfWait.toFixed(2);
        }

        var totalWaitTile = document.getElementById('contentionTotalWaitTile');
        if (totalWaitTile) {
            totalWaitTile.textContent = adjustedTotalWaitMSec.toFixed(1);
        }

        var avgWaitTile = document.getElementById('contentionAvgWaitTile');
        if (avgWaitTile) {
            avgWaitTile.textContent = (adjustedContentionCount > 0 ? (adjustedTotalWaitMSec / adjustedContentionCount) : 0).toFixed(3);
        }

        filterContentionSitesToZoomRange(contentionTimelineZoomRange);
    }

    // Mirrors buildCpuDrillDownRowIfLazy against contentionDrillDownStats.js's
    // buildLazyContentionDrillDownSubtree and the data-contention-lazy-inner
    // attribute.
    function buildContentionDrillDownRowIfLazy(detailRow) {
        if (detailRow.getAttribute('data-contention-lazy-inner') !== 'true') {
            return;
        }

        var subtreeHtml = buildLazyContentionDrillDownSubtree(detailRow.id);
        if (subtreeHtml) {
            detailRow.querySelector('.callerTreeCell').innerHTML = subtreeHtml;
            detailRow.removeAttribute('data-contention-lazy-inner');
        }
    }

    // Mirrors followCpuDrillDownLinearRun/followExceptionDrillDownLinearRun -
    // see those for the full rationale (follow a chain of single-child rows
    // down to the first real branch or the end, in one click, so a long
    // non-branching call chain doesn't need one click per frame). Contention
    // was the only drill-down tab that never had this, so expanding a site
    // stopped at its single immediate caller.
    function followContentionDrillDownLinearRun(detailRow) {
        var currentDetailRow = detailRow;
        for (;;) {
            var innerTable = currentDetailRow.querySelector('table.callerTreeInner');
            if (!innerTable) {
                return;
            }

            var childRows = [];
            for (var rowIndex = 0; rowIndex < innerTable.rows.length; ++rowIndex) {
                if (innerTable.rows[rowIndex].classList.contains('callerRow')) {
                    childRows.push(innerTable.rows[rowIndex]);
                }
            }

            if (childRows.length !== 1) {
                return;
            }

            var onlyChildRow = childRows[0];
            if (onlyChildRow.getAttribute('data-contention-expandable') !== 'true') {
                return;
            }

            var onlyChildDetailRow = document.getElementById(onlyChildRow.getAttribute('data-contention-target'));
            if (!onlyChildDetailRow) {
                return;
            }

            buildContentionDrillDownRowIfLazy(onlyChildDetailRow);
            onlyChildRow.classList.add('expanded');
            onlyChildDetailRow.classList.add('expanded');
            currentDetailRow = onlyChildDetailRow;
        }
    }

    // Mirrors switchProfileTab. The Lock Timeline canvas is drawn on first
    // reveal, never at injection time: a canvas inside a display:none panel
    // has zero layout width, so sizing it there produces a 0-wide backing
    // store and an invisible chart.
    function switchContentionTab(targetTab) {
        var buttons = document.querySelectorAll('#view-contention .heapContentsTabButton');
        for (var buttonIndex = 0; buttonIndex < buttons.length; ++buttonIndex) {
            buttons[buttonIndex].classList.remove('active');
            if (buttons[buttonIndex].getAttribute('data-contentiontab') === targetTab) {
                buttons[buttonIndex].classList.add('active');
            }
        }

        var panels = document.querySelectorAll('#view-contention .heapContentsTabPanel');
        for (var panelIndex = 0; panelIndex < panels.length; ++panelIndex) {
            panels[panelIndex].classList.remove('active');
        }

        var targetPanel = document.getElementById('contention-tab-' + targetTab);
        if (targetPanel) {
            targetPanel.classList.add('active');
        }

        if (targetTab === 'locktimeline' && contentionSummaryJson && contentionSummaryJson["lockTimeline"]) {
            // renderLockTimeline is idempotent (it re-draws against its own
            // retained state on every call), so this also covers a redraw
            // after the panel was hidden and re-shown at a different size.
            renderLockTimeline(contentionSummaryJson["lockTimeline"], contentionSummaryJson["methodNames"]);
        }
    }

    function wireLockTimelinePanel() {
        var lockTimeline = contentionSummaryJson ? contentionSummaryJson["lockTimeline"] : null;
        if (!lockTimeline) {
            return;
        }

        var filterList = document.getElementById('lockFilterList');
        if (filterList) {
            // Delegated, not one listener per row - the list is rebuilt
            // whenever the Top-N slice changes (and can hold every lock in
            // the capture), so per-row listeners would have to be re-bound
            // each time and would leak on every rebuild.
            filterList.addEventListener('change', function (event) {
                var checkbox = event.target.closest('.lockFilterCheckbox');
                if (!checkbox) {
                    return;
                }

                setLockTimelineLockVisible(parseInt(checkbox.getAttribute('data-lock-index'), 10), checkbox.checked);
            });

            // Clicking the lock id itself (not the checkbox) opens that
            // lock's contended stacks.
            filterList.addEventListener('click', function (event) {
                var idSpan = event.target.closest('[data-lock-select]');
                if (!idSpan) {
                    return;
                }

                // The span lives inside a <label>, whose default behavior is
                // to forward the click to its checkbox - which would toggle
                // the lock's visibility as a side effect of asking to see
                // its stacks.
                event.preventDefault();
                selectLockTimelineLock(parseInt(idSpan.getAttribute('data-lock-select'), 10));
            });
        }

        var rankMetricSelect = document.getElementById('lockRankMetricSelect');
        if (rankMetricSelect) {
            rankMetricSelect.addEventListener('change', function (event) {
                setLockTimelineRankMetric(event.currentTarget.value);
            });
        }

        var topNSelect = document.getElementById('lockTopNSelect');
        if (topNSelect) {
            topNSelect.addEventListener('change', function (event) {
                var rawValue = event.currentTarget.value;
                setLockTimelineTopCount(rawValue === 'all' ? null : parseInt(rawValue, 10));
            });
        }

        // Right-click to filter, from any of the three surfaces that
        // represent a lock: its track on the canvas, its sidebar row, and
        // its table row. One delegated handler per surface, all funnelling
        // into the same menu.
        var lockTimelineCanvas = document.getElementById('lockTimelineCanvas');
        if (lockTimelineCanvas) {
            lockTimelineCanvas.addEventListener('contextmenu', function (event) {
                var lockIndex = lockTimelineLockIndexAtY(event.offsetY);
                if (lockIndex < 0) {
                    return;
                }

                event.preventDefault();
                showLockTimelineContextMenu(lockIndex, event.clientX, event.clientY);
            });
        }

        var lockFilterListForMenu = document.getElementById('lockFilterList');
        if (lockFilterListForMenu) {
            lockFilterListForMenu.addEventListener('contextmenu', function (event) {
                var row = event.target.closest('[data-lock-row]');
                if (!row) {
                    return;
                }

                event.preventDefault();
                showLockTimelineContextMenu(parseInt(row.getAttribute('data-lock-row'), 10), event.clientX, event.clientY);
            });
        }

        var lockContextMenu = document.getElementById('lockContextMenu');
        if (lockContextMenu) {
            lockContextMenu.addEventListener('click', function (event) {
                var item = event.target.closest('[data-lock-menu]');
                if (!item) {
                    return;
                }

                runLockTimelineContextAction(item.getAttribute('data-lock-menu'));
            });
        }

        // Any click outside the menu, Escape, or scrolling the tracks
        // dismisses it - a menu left floating over a chart that has since
        // scrolled points at the wrong row.
        document.addEventListener('click', function (event) {
            if (!event.target.closest('#lockContextMenu')) {
                hideLockTimelineContextMenu();
            }
        });

        document.addEventListener('keydown', function (event) {
            if (event.key === 'Escape') {
                hideLockTimelineContextMenu();
            }
        });

        var lockTimelineContainerForMenu = document.getElementById('lockTimelineContainer');
        if (lockTimelineContainerForMenu) {
            lockTimelineContainerForMenu.addEventListener('scroll', function () {
                hideLockTimelineContextMenu();
            });
        }

        var lockTableContainer = document.getElementById('lockTableContainer');
        if (lockTableContainer) {
            // Delegated on the container, which is stable - the table inside
            // it is re-rendered on every sort, filter and selection change.
            lockTableContainer.addEventListener('click', function (event) {
                var header = event.target.closest('[data-lock-sort]');
                if (header) {
                    setLockTimelineSortColumn(header.getAttribute('data-lock-sort'));
                    return;
                }

                var row = event.target.closest('[data-lock-row-index]');
                if (row) {
                    selectLockTimelineLock(parseInt(row.getAttribute('data-lock-row-index'), 10));
                }
            });

            lockTableContainer.addEventListener('contextmenu', function (event) {
                var row = event.target.closest('[data-lock-row-index]');
                if (!row) {
                    return;
                }

                event.preventDefault();
                showLockTimelineContextMenu(parseInt(row.getAttribute('data-lock-row-index'), 10), event.clientX, event.clientY);
            });
        }

        var longestWaitSelect = document.getElementById('lockLongestWaitSelect');
        if (longestWaitSelect) {
            longestWaitSelect.addEventListener('change', function (event) {
                var rawValue = event.currentTarget.value;
                if (rawValue === '') {
                    return;
                }

                jumpToLockTimelineLongestWait(parseInt(rawValue, 10));
            });
        }

        var threadFilterList = document.getElementById('threadFilterList');
        if (threadFilterList) {
            // Delegated - the list is rebuilt on every selection change and
            // on every search keystroke, so per-checkbox listeners would be
            // re-bound (and leaked) each time.
            threadFilterList.addEventListener('change', function (event) {
                var checkbox = event.target.closest('.threadFilterCheckbox');
                if (!checkbox) {
                    return;
                }

                setLockTimelineThreadSelected(parseInt(checkbox.getAttribute('data-thread-id'), 10), checkbox.checked);
            });
        }

        var threadSearch = document.getElementById('threadFilterSearch');
        if (threadSearch) {
            threadSearch.addEventListener('input', function () {
                refreshLockTimelineThreadList();
            });
        }

        var threadAllBtn = document.getElementById('threadFilterAllBtn');
        if (threadAllBtn) {
            threadAllBtn.addEventListener('click', function () {
                setLockTimelineThreadSelectionMode('all');
            });
        }

        var threadNoneBtn = document.getElementById('threadFilterNoneBtn');
        if (threadNoneBtn) {
            threadNoneBtn.addEventListener('click', function () {
                setLockTimelineThreadSelectionMode('none');
            });
        }

        var threadWorkerBtn = document.getElementById('threadFilterWorkerBtn');
        if (threadWorkerBtn) {
            threadWorkerBtn.addEventListener('click', function () {
                setLockTimelineThreadSelectionMode('worker');
            });
        }

        var stackCloseBtn = document.getElementById('lockStackCloseBtn');
        if (stackCloseBtn) {
            stackCloseBtn.addEventListener('click', function () {
                closeLockTimelineStackPanel();
            });
        }

        var allBtn = document.getElementById('lockFilterAllBtn');
        if (allBtn) {
            allBtn.addEventListener('click', function () {
                setAllLockFilterCheckboxes(true);
            });
        }

        var noneBtn = document.getElementById('lockFilterNoneBtn');
        if (noneBtn) {
            noneBtn.addEventListener('click', function () {
                setAllLockFilterCheckboxes(false);
            });
        }

        var resetZoomBtn = document.getElementById('lockTimelineResetZoomBtn');
        if (resetZoomBtn) {
            resetZoomBtn.addEventListener('click', function () {
                resetLockTimelineZoom();
            });
        }
    }

    function setAllLockFilterCheckboxes(isChecked) {
        var checkboxes = document.getElementsByClassName('lockFilterCheckbox');
        for (var checkboxIndex = 0; checkboxIndex < checkboxes.length; ++checkboxIndex) {
            checkboxes[checkboxIndex].checked = isChecked;
            setLockTimelineLockVisible(parseInt(checkboxes[checkboxIndex].getAttribute('data-lock-index'), 10), isChecked);
        }
    }

    // Expand All / Collapse All for one threading table. The .expanded class
    // lives on BOTH halves of each pair (the summary row and its detail row),
    // so both are set - matching the delegated per-row toggle in
    // wireThreadingTab, which does the same.
    function setAllThreadingStacksExpanded(tableId, isExpanded) {
        var table = document.getElementById(tableId);
        if (!table) {
            return;
        }

        var rows = table.querySelectorAll('[data-threading-expandable="true"]');
        for (var rowIndex = 0; rowIndex < rows.length; ++rowIndex) {
            var row = rows[rowIndex];
            var detailRow = document.getElementById(row.getAttribute('data-threading-target'));

            if (isExpanded) {
                row.classList.add('expanded');
                if (detailRow) {
                    detailRow.classList.add('expanded');
                }
            } else {
                row.classList.remove('expanded');
                if (detailRow) {
                    detailRow.classList.remove('expanded');
                }
            }
        }
    }

    // Worker-thread count over time. Deliberately a line chart with three
    // series (min/avg/max per bucket) rather than one: the pool's average
    // hides exactly the excursions worth seeing - a bucket whose max spikes
    // while its average barely moves is a brief injection burst, which is
    // what a stall looks like from the outside.
    //
    // Drag-to-zoom follows the same contract as the CPU/contention/exception
    // timelines: the chart is destroyed and rebuilt against a bucket slice
    // rather than mutating axis bounds, and the previous zoom handle is
    // detached first (a leaked handle keeps listening on a dead canvas).
    var threadingTimelineZoomRange = null;
    var threadingTimelineChartHandle = null;

    function renderThreadingTimeline(zoomRange) {
        var canvas = document.getElementById('threadingTimeline');
        if (!canvas || !threadingSummaryJson) {
            return;
        }

        if (threadingTimelineChartHandle) {
            threadingTimelineChartHandle.zoomHandle.detach();
            threadingTimelineChartHandle.crosshairHandle.detach();
            threadingTimelineChartHandle.chart.destroy();
            threadingTimelineChartHandle = null;
        }

        var timeline = threadingSummaryJson["timeline"];
        if (!timeline) {
            return;
        }

        var bucketCount = timeline["bucketCount"];
        var bucketDurationMSec = timeline["bucketDurationMSec"];
        var minRelativeMSec = timeline["minRelativeMSec"];

        var labels = [];
        var bucketStartMSecs = [];
        for (var bucketIndex = 0; bucketIndex < bucketCount; ++bucketIndex) {
            var bucketStartMSec = minRelativeMSec + bucketIndex * bucketDurationMSec;
            labels.push(formatElapsedMs(bucketStartMSec));
            bucketStartMSecs.push(bucketStartMSec);
        }

        var zoomStartBucket = 0;
        var zoomEndBucket = bucketCount - 1;
        if (zoomRange !== null) {
            zoomStartBucket = Math.max(0, Math.floor((zoomRange.startMSec - minRelativeMSec) / bucketDurationMSec));
            zoomEndBucket = Math.min(bucketCount - 1, Math.ceil((zoomRange.endMSec - minRelativeMSec) / bucketDurationMSec));
        }

        var visibleLabels = labels.slice(zoomStartBucket, zoomEndBucket + 1);
        var visibleBucketStartMSecs = bucketStartMSecs.slice(zoomStartBucket, zoomEndBucket + 1);
        var visibleMax = timeline["maxActiveByBucket"].slice(zoomStartBucket, zoomEndBucket + 1);
        var visibleAverage = timeline["averageActiveByBucket"].slice(zoomStartBucket, zoomEndBucket + 1);
        var visibleMin = timeline["minActiveByBucket"].slice(zoomStartBucket, zoomEndBucket + 1);

        var dragStateHolder = { current: null };
        var crosshairStateHolder = { current: null };

        // Category scale (labels are pre-formatted strings), so the category
        // variant - the linear one would misread the axis. Shared by the drag
        // and the crosshair so both read the hovered time the same way.
        function pixelToMSecOnThreadingTimeline(chartArg, pixelX) {
            return pixelToMSecCategory(chartArg, pixelX, visibleBucketStartMSecs);
        }

        var chart = new Chart(canvas, {
            type: 'line',
            data: {
                labels: visibleLabels,
                datasets: [
                    {
                        label: 'Max workers',
                        data: visibleMax,
                        borderColor: 'rgba(224, 82, 82, 0.9)',
                        backgroundColor: 'rgba(224, 82, 82, 0.10)',
                        borderWidth: 1,
                        pointRadius: 1,
                        fill: false
                    },
                    {
                        label: 'Average workers',
                        data: visibleAverage,
                        borderColor: 'rgba(78, 121, 167, 0.95)',
                        backgroundColor: 'rgba(78, 121, 167, 0.15)',
                        borderWidth: 2,
                        pointRadius: 1,
                        fill: false
                    },
                    {
                        label: 'Min workers',
                        data: visibleMin,
                        borderColor: 'rgba(53, 163, 83, 0.9)',
                        backgroundColor: 'rgba(53, 163, 83, 0.10)',
                        borderWidth: 1,
                        pointRadius: 1,
                        fill: false
                    }
                ]
            },
            // Chart.js 2.x wants the plugins array as a TOP-LEVEL config key,
            // not under options - see CLAUDE.md; this has been a real bug here
            // before.
            plugins: [createCrosshairPlugin(crosshairStateHolder), createZoomSelectionPlugin(dragStateHolder)],
            options: {
                responsive: true,
                maintainAspectRatio: false,
                animation: { duration: 0 },
                scales: {
                    // Chart.js 2.x shape (yAxes array, not a `y` object).
                    yAxes: [{
                        ticks: { beginAtZero: false },
                        scaleLabel: { display: true, labelString: 'Worker threads' }
                    }],
                    xAxes: [{
                        ticks: { maxTicksLimit: 12, autoSkip: true }
                    }]
                },
                tooltips: {
                    mode: 'index',
                    intersect: false
                }
            }
        });

        var zoomHandle = attachDragToZoom(chart, canvas, dragStateHolder, pixelToMSecOnThreadingTimeline, function (startMSec, endMSec) {
            setThreadingTimelineZoom({ startMSec: startMSec, endMSec: endMSec });
        });

        var crosshairHandle = attachCrosshair(chart, canvas, crosshairStateHolder, pixelToMSecOnThreadingTimeline, formatElapsedMs);

        threadingTimelineChartHandle = { chart: chart, zoomHandle: zoomHandle, crosshairHandle: crosshairHandle };
    }

    // The single place the threading zoom changes - drag, Reset button and the
    // Backspace/swipe back gesture all route through here so the chart, the
    // status strip and the tables below can never disagree about what window
    // is being shown. Pass null to clear the zoom.
    function setThreadingTimelineZoom(zoomRange) {
        threadingTimelineZoomRange = zoomRange;
        updateThreadingTimelineZoomStatusUi(zoomRange);
        renderThreadingTimeline(zoomRange);
        applyThreadingZoomFilter(zoomRange);
    }

    // Zooming the timeline narrows the tables below it to the same window.
    // Only the thread/lock creation tables can follow: their rows carry a
    // data-threading-msec stamp. The stall-correlation and adjustment-reason
    // tables are aggregated whole-capture in the parser, so they get a visible
    // "does not follow the zoom" note instead of being silently left stale.
    function applyThreadingZoomFilter(zoomRange) {
        var rows = document.querySelectorAll('#view-threading [data-threading-msec]');
        for (var rowIndex = 0; rowIndex < rows.length; ++rowIndex) {
            var row = rows[rowIndex];
            var relativeMSec = parseFloat(row.getAttribute('data-threading-msec'));
            var isVisible = zoomRange === null ||
                (relativeMSec >= zoomRange.startMSec && relativeMSec <= zoomRange.endMSec);

            // The detail row keeps its own .expanded state so collapsing is not
            // a side effect of filtering - it just stays hidden while filtered
            // out, and comes back expanded if it was expanded before.
            if (row.classList.contains('callPathsDetail')) {
                row.classList.toggle('threadingFilteredOut', !isVisible);
                continue;
            }

            row.style.display = isVisible ? '' : 'none';
        }

        updateThreadingSectionCounts();

        var aggregateNotes = document.getElementsByClassName('threadingZoomAggregateNote');
        for (var noteIndex = 0; noteIndex < aggregateNotes.length; ++noteIndex) {
            aggregateNotes[noteIndex].style.display = zoomRange === null ? 'none' : '';
        }
    }

    function updateThreadingSectionCounts() {
        var counts = document.getElementsByClassName('threadingSectionCount');
        for (var countIndex = 0; countIndex < counts.length; ++countIndex) {
            var countElement = counts[countIndex];
            var total = parseInt(countElement.getAttribute('data-threading-total'), 10);

            // The count element is named "<idPrefix>Count" for the table
            // "<idPrefix>Table" - same prefix the expand-all buttons key off.
            var tableId = countElement.id.replace(/Count$/, 'Table');
            var table = document.getElementById(tableId);
            if (!table) {
                continue;
            }

            var visibleCount = 0;
            var rows = table.querySelectorAll('[data-threading-expandable="true"]');
            for (var rowIndex = 0; rowIndex < rows.length; ++rowIndex) {
                if (rows[rowIndex].style.display !== 'none') {
                    ++visibleCount;
                }
            }

            countElement.textContent = visibleCount === total
                ? total.toLocaleString()
                : visibleCount.toLocaleString() + ' of ' + total.toLocaleString();
        }
    }

    function updateThreadingTimelineZoomStatusUi(zoomRange) {
        var statusEl = document.getElementById('threadingTimelineZoomStatus');
        var labelEl = document.getElementById('threadingTimelineZoomLabel');
        if (!statusEl) {
            return;
        }

        if (!zoomRange) {
            statusEl.style.display = 'none';
            return;
        }

        statusEl.style.display = '';
        if (labelEl) {
            labelEl.textContent = formatElapsedMs(zoomRange.startMSec) + " – " + formatElapsedMs(zoomRange.endMSec) + " (Backspace to reset)";
        }
    }

    function wireThreadingTab() {
        renderThreadingTimeline(null);

        var threadingResetZoomBtn = document.getElementById('threadingTimelineResetZoomBtn');
        if (threadingResetZoomBtn) {
            threadingResetZoomBtn.addEventListener('click', function () {
                setThreadingTimelineZoom(null);
            });
        }

        var threadingPanel = document.getElementById('view-threading');
        if (!threadingPanel) {
            return;
        }

        // Thread/lock creation rows expand to their captured stack. Delegated
        // so the two tables share one handler.
        threadingPanel.addEventListener('click', function (event) {
            // Expand All/Collapse All is checked first, before the row
            // toggle below - the buttons sit outside the table, but a
            // delegated listener on the panel sees both.
            var expandButton = event.target.closest('[data-threading-expand-target]');
            if (expandButton) {
                setAllThreadingStacksExpanded(
                    expandButton.getAttribute('data-threading-expand-target'),
                    expandButton.getAttribute('data-threading-expand') === 'true');
                return;
            }

            var row = event.target.closest('[data-threading-expandable="true"]');
            if (!row) {
                return;
            }

            var detailRow = document.getElementById(row.getAttribute('data-threading-target'));
            if (!detailRow) {
                return;
            }

            if (row.classList.contains('expanded')) {
                row.classList.remove('expanded');
                detailRow.classList.remove('expanded');
                return;
            }

            row.classList.add('expanded');
            detailRow.classList.add('expanded');
        });
    }

    function wireContentionTab() {
        if (!contentionSummaryJson) {
            return;
        }

        initContentionDrillDownMethodNames(contentionSummaryJson["methodNames"]);

        var contentionTabButtons = document.querySelectorAll('#view-contention .heapContentsTabButton');
        for (var tabButtonIndex = 0; tabButtonIndex < contentionTabButtons.length; ++tabButtonIndex) {
            contentionTabButtons[tabButtonIndex].addEventListener('click', function (event) {
                switchContentionTab(event.currentTarget.getAttribute('data-contentiontab'));
            });
        }

        wireLockTimelinePanel();

        var resetZoomBtn = document.getElementById('contentionTimelineResetZoomBtn');
        if (resetZoomBtn) {
            resetZoomBtn.addEventListener('click', function () {
                contentionTimelineZoomRange = null;
                filterContentionSitesToZoomRange(null);
                updateContentionTimelineZoomStatusUi(null);
                renderContentionTimeline(null);
            });
        }

        var contentionSitesShowAllBtn = document.getElementById('contentionSitesShowAllBtn');
        if (contentionSitesShowAllBtn) {
            contentionSitesShowAllBtn.addEventListener('click', function () {
                contentionSiteHider.reset();
            });
        }

        var contentionPanel = document.getElementById('view-contention');
        if (!contentionPanel) {
            return;
        }

        // Delegated click handler for both top-level site rows
        // (data-contention-expandable on .contentionSiteRow) and interior
        // caller nodes (data-contention-expandable on interior .callerRow
        // within the already-expanded callPathsDetail). The two are
        // distinguished by whether the row also has data-contention-lazy
        // (top-level site, populated on first expand) or not (interior
        // caller node, already in pendingContentionLazySubtrees).
        contentionPanel.addEventListener('click', function (event) {
            // Row-hide cell - checked first, before either expand path
            // below, so a click anywhere in it never also toggles the site
            // row's own caller-tree expansion. Whole cell is the click
            // target, not just the ✕ glyph itself.
            var hideCell = event.target.closest('.rowHideColumn');
            if (hideCell) {
                var hideRow = hideCell.closest('[data-contention-site-index]');
                if (hideRow) {
                    contentionSiteHider.toggle(parseInt(hideRow.getAttribute('data-contention-site-index'), 10));
                }
                return;
            }

            // Interior caller node expansion (lazy subtree).
            var callerRow = event.target.closest('[data-contention-expandable="true"]:not([data-contention-site-index])');
            if (callerRow && !callerRow.hasAttribute('data-contention-site-index')) {
                var innerDetailRow = document.getElementById(callerRow.getAttribute('data-contention-target'));
                if (!innerDetailRow) {
                    return;
                }

                if (callerRow.classList.contains('expanded')) {
                    callerRow.classList.remove('expanded');
                    innerDetailRow.classList.remove('expanded');
                    return;
                }

                buildContentionDrillDownRowIfLazy(innerDetailRow);
                callerRow.classList.add('expanded');
                innerDetailRow.classList.add('expanded');
                followContentionDrillDownLinearRun(innerDetailRow);
                return;
            }

            // Top-level site row expansion.
            var siteRow = event.target.closest('[data-contention-site-index]');
            if (!siteRow || !siteRow.hasAttribute('data-contention-expandable')) {
                return;
            }

            var siteIndex = parseInt(siteRow.getAttribute('data-contention-site-index'), 10);
            var detailRow = document.getElementById('contentionSiteDetail' + siteIndex);
            if (!detailRow) {
                return;
            }

            if (siteRow.classList.contains('expanded')) {
                siteRow.classList.remove('expanded');
                detailRow.classList.remove('expanded');
                return;
            }

            // Build the inline caller tree on first expand.
            if (detailRow.hasAttribute('data-contention-lazy')) {
                var drillDown = contentionSummaryJson["siteDrillDown"];
                var entry = drillDown ? drillDown[siteIndex] : null;
                var totalWaitMSec = contentionSummaryJson["totalContentionWaitMSec"];
                var treeHtml = buildInlineContentionSiteCallerTree(entry, contentionSummaryJson["methodNames"], totalWaitMSec);
                detailRow.querySelector('.callerTreeCell').innerHTML = treeHtml;
                detailRow.removeAttribute('data-contention-lazy');
            }

            siteRow.classList.add('expanded');
            detailRow.classList.add('expanded');
            followContentionDrillDownLinearRun(detailRow);

            // Rotate toggle arrow.
            var toggle = siteRow.querySelector('.leafMethodToggle');
            if (toggle) {
                toggle.style.display = 'inline-block';
            }
        });

        // Build the timeline chart if data is available.
        if (contentionSummaryJson["timeline"]) {
            renderContentionTimeline(null);
        }
    }

    // "Go back" has two mutually-exclusive meanings on this page, checked in
    // one function so their precedence is explicit rather than relying on
    // two independent call sites never happening to both fire (which they
    // can't anyway, since a tab being active is exclusive - but stating
    // that as one function's own logic is clearer than trusting an
    // invariant across separate ones):
    //   - Drill Down tab active: return to Charts (existing behavior).
    //   - Charts tab active (either view) and a zoom is applied: reset
    //     sharedZoomRange back to the full capture via applySharedZoom -
    //     only when zoomed, so going back does nothing surprising otherwise.
    //     The GC and Heap Contents views are mutually exclusive (only one
    //     .viewPanel is ever active), so this can't conflict with the
    //     Drill Down check above it.
    // Shared by both the Backspace key and the macOS two-finger swipe-back
    // trackpad gesture below - returns true if it actually did something,
    // so each caller only preventDefault()s a real navigation/zoom-reset
    // rather than swallowing every keystroke or wheel tick unconditionally.
    var performGoBackAction = function () {
        var gcViewPanel = document.getElementById('view-gc');
        if (gcViewPanel && gcViewPanel.classList.contains('active') && sharedZoomRange) {
            applySharedZoom(null);
            return true;
        }

        var drillDownPanel = document.getElementById('heapContents-tab-drilldown');
        if (drillDownPanel && drillDownPanel.classList.contains('active')) {
            goBackToChartsView();
            return true;
        }

        var cpuMethodsPanel = document.getElementById('profile-tab-hotmethods');
        if (cpuMethodsPanel && cpuMethodsPanel.classList.contains('active') && cpuTimelineZoomRange) {
            cpuTimelineZoomRange = null;
            renderCpuTimeline(null);
            filterCpuMethodsTableToZoomRange(null);
            updateCpuTimelineZoomStatusUi(null);
            return true;
        }

        // Threading view's worker-thread timeline - same single-level zoom
        // as the CPU/contention/exception timelines, so "back" clears it.
        var threadingPanelForZoom = document.getElementById('view-threading');
        if (threadingPanelForZoom && threadingPanelForZoom.classList.contains('active') && threadingTimelineZoomRange) {
            setThreadingTimelineZoom(null);
            return true;
        }

        // Contention view's Lock Timeline tab - checked BEFORE the Sites
        // branch below, and gated on its own panel being the active tab, so
        // a zoom left behind on the Sites timeline can't swallow a swipe
        // aimed at the lock chart. lockTimelineSwipeZoomOut steps back one
        // level (see media/lockTimeline.js) and reports whether it did
        // anything, same contract as flameGraphSwipeZoomOut below.
        var lockTimelinePanel = document.getElementById('contention-tab-locktimeline');
        if (lockTimelinePanel && lockTimelinePanel.classList.contains('active')) {
            return lockTimelineSwipeZoomOut();
        }

        var contentionPanel = document.getElementById('view-contention');
        if (contentionPanel && contentionPanel.classList.contains('active') && contentionTimelineZoomRange) {
            contentionTimelineZoomRange = null;
            filterContentionSitesToZoomRange(null);
            updateContentionTimelineZoomStatusUi(null);
            renderContentionTimeline(null);
            return true;
        }

        var exceptionsPanel = document.getElementById('view-exceptions');
        if (exceptionsPanel && exceptionsPanel.classList.contains('active') && exceptionTimelineZoomRange) {
            exceptionTimelineZoomRange = null;
            renderExceptionTimeline(null);
            updateExceptionTimelineZoomStatusUi(null);
            return true;
        }

        var chartsPanelForZoom = document.getElementById('heapContents-tab-charts');
        if (chartsPanelForZoom && chartsPanelForZoom.classList.contains('active') && sharedZoomRange) {
            applySharedZoom(null);
            return true;
        }

        // Profile view's Flame Graph tab - flameGraphSwipeZoomOut (see
        // media/flameGraph.js) already checks its own "actually zoomed"
        // condition and returns false otherwise, same contract every other
        // branch above follows.
        var flameGraphPanel = document.getElementById('profile-tab-flame');
        if (flameGraphPanel && flameGraphPanel.classList.contains('active') && flameGraphSwipeZoomOut()) {
            return true;
        }

        return false;
    };

    // The forward counterpart to performGoBackAction above - restores
    // zoomRangeForForward (the range a prior reset just cleared) via
    // applySharedZoom's isForwardRestore path. Only meaningful for the
    // zoom-reset half of "back", not the Drill Down "return to Charts"
    // half - there's no equivalent "forward into Drill Down" concept, and
    // sharedZoomRange being null is what actually gates whether there's
    // anything to redo, same as a browser disabling its forward button
    // once you're not "back" in history.
    var performGoForwardAction = function () {
        // Profile view's Flame Graph tab - checked independently of the
        // sharedZoomRange/zoomRangeForForward pair below, which is specific
        // to the GC/Heap Contents charts and unrelated to flameGraph.js's
        // own zoomChain state. flameGraphSwipeZoomForward already checks
        // its own "currently unzoomed AND has something to restore"
        // condition.
        var flameGraphPanel = document.getElementById('profile-tab-flame');
        if (flameGraphPanel && flameGraphPanel.classList.contains('active') && flameGraphSwipeZoomForward()) {
            return true;
        }

        // Lock Timeline's redo counterpart - same panel-active gate as its
        // own branch in performGoBackAction.
        var lockTimelinePanel = document.getElementById('contention-tab-locktimeline');
        if (lockTimelinePanel && lockTimelinePanel.classList.contains('active')) {
            return lockTimelineSwipeZoomForward();
        }

        if (sharedZoomRange || !zoomRangeForForward) {
            return false;
        }

        var gcViewPanel = document.getElementById('view-gc');
        var chartsPanelForZoom = document.getElementById('heapContents-tab-charts');
        var zoomableViewActive = (gcViewPanel && gcViewPanel.classList.contains('active')) ||
            (chartsPanelForZoom && chartsPanelForZoom.classList.contains('active'));

        if (!zoomableViewActive) {
            return false;
        }

        applySharedZoom(zoomRangeForForward, true);
        return true;
    };

    document.addEventListener('keydown', function (event) {
        if (event.key !== 'Backspace') {
            return;
        }

        if (performGoBackAction()) {
            event.preventDefault();
        }
    });

    // macOS two-finger swipe-back/forward (trackpad) mirror Backspace/redo
    // above. A physical swipe doesn't arrive as one discrete event - it's a
    // burst of many 'wheel' events, so a horizontal-dominant burst is
    // accumulated until it crosses the threshold in either direction, fires
    // the matching action once, then ignores the rest of that burst (a
    // short quiet gap resets the accumulator for the next gesture).
    // Pinch-to-zoom also arrives as 'wheel' with ctrlKey set on Chromium/
    // macOS, and plain vertical scrolling has deltaY dominant - both are
    // excluded so this only fires on an actual horizontal swipe.
    //
    // Direction: with macOS's default "natural" scrolling, the back gesture
    // (fingers swipe left-to-right, as if dragging the previous page in
    // from the left) reports negative deltaX - the same sign Chrome itself
    // keys its built-in swipe-to-go-back navigation off of; forward
    // (fingers swipe right-to-left) is the positive-deltaX mirror image. If
    // a user has natural scrolling disabled these fire on the opposite
    // swipe directions instead; swap the two branches below if that's ever
    // reported.
    var SWIPE_THRESHOLD_PX = 60;
    var SWIPE_GESTURE_IDLE_RESET_MS = 400;

    // One physical swipe must fire exactly ONE action, and two swipes in a
    // row must fire two - which is harder than it sounds on macOS, because a
    // trackpad swipe doesn't end when the fingers lift. It keeps emitting
    // wheel events as decaying momentum for the better part of a second.
    //
    // Two failure modes had to be closed, in opposite directions:
    //
    //  - Firing repeatedly within one swipe. Merely zeroing the accumulator
    //    after firing isn't enough: the same burst keeps arriving and
    //    re-crosses SWIPE_THRESHOLD_PX several more times. Against a
    //    single-level toggle that's invisible, but against the flame graph's
    //    multi-level undo stack (flameGraph.js's flameGraphBackStack) it
    //    drained the whole stack per swipe, so "step back one zoom" behaved
    //    like "reset entirely". Hence latching after a fire.
    //
    //  - Never re-arming, so only the FIRST swipe ever works. Clearing the
    //    latch purely on an idle gap fails for exactly the case that matters
    //    (going back several levels): the momentum tail keeps resetting the
    //    idle timer, so a second deliberate swipe ~200ms later lands while
    //    still latched and is swallowed. Reproduced directly against a
    //    simulated ramp-then-decay burst.
    //
    // So re-arming is driven by momentum DECAY, not elapsed time: once the
    // per-event magnitude falls to REARM_QUIET_DELTA the tail is spent and
    // the next real swipe can fire. The idle timer stays only as a backstop.
    // GESTURE_START_DELTA then stops that spent tail from itself
    // accumulating into a fresh gesture - a deliberate swipe ramps well past
    // it within a couple of events, while 1-3px momentum dust never does.
    // The gap between the two constants is what keeps those two rules from
    // fighting each other.
    var SWIPE_REARM_QUIET_DELTA = 3;
    var SWIPE_GESTURE_START_DELTA = 8;

    var swipeAccumulatedDeltaX = 0;
    var swipeGestureResetTimer = null;
    var swipeGestureLatched = false;

    document.addEventListener('wheel', function (event) {
        if (event.ctrlKey || Math.abs(event.deltaX) <= Math.abs(event.deltaY)) {
            swipeAccumulatedDeltaX = 0;
            return;
        }

        clearTimeout(swipeGestureResetTimer);
        swipeGestureResetTimer = setTimeout(function () {
            swipeAccumulatedDeltaX = 0;
            swipeGestureLatched = false;
        }, SWIPE_GESTURE_IDLE_RESET_MS);

        var magnitude = Math.abs(event.deltaX);

        if (swipeGestureLatched) {
            // Momentum has decayed to nothing - this gesture is over, so
            // re-arm for the next one without waiting out the idle timer.
            if (magnitude <= SWIPE_REARM_QUIET_DELTA) {
                swipeGestureLatched = false;
                swipeAccumulatedDeltaX = 0;
            }

            return;
        }

        // Don't let a spent momentum tail accumulate into a gesture of its
        // own - a real swipe opens well above this.
        if (swipeAccumulatedDeltaX === 0 && magnitude < SWIPE_GESTURE_START_DELTA) {
            return;
        }

        swipeAccumulatedDeltaX += event.deltaX;

        if (swipeAccumulatedDeltaX <= -SWIPE_THRESHOLD_PX) {
            swipeAccumulatedDeltaX = 0;
            swipeGestureLatched = true;

            if (performGoBackAction()) {
                event.preventDefault();
            }
        } else if (swipeAccumulatedDeltaX >= SWIPE_THRESHOLD_PX) {
            swipeAccumulatedDeltaX = 0;
            swipeGestureLatched = true;

            if (performGoForwardAction()) {
                event.preventDefault();
            }
        }
    }, { passive: false });

    // ── Heap Snapshot (gcHeapAnalyzer output) ────────────────────────────────
    // File is read entirely in the webview via FileReader — no extension-host
    // round-trip needed. The tab button is hidden until a snapshot is loaded.

    var HEAP_GEN_LABELS = ['Gen0', 'Gen1', 'Gen2', 'LOH', 'POH'];

    var genLabelFor = function (generation) {
        return (generation >= 0 && generation < HEAP_GEN_LABELS.length)
            ? HEAP_GEN_LABELS[generation] : 'Gen' + generation;
    };

    // Loaded snapshot + the currently-selected generation filter (-1 = "All
    // Generations") for the Free Chunk Distribution section - module-level
    // so the filter buttons can re-render just that section without
    // re-parsing or re-walking the whole snapshot.
    var heapSnapshotData = null;
    var heapSnapshotGenFilter = -1;
    var freeChunkCountChartInstance = null;
    var freeChunkBytesChartInstance = null;

    var formatBytes = function (bytes) {
        if (bytes >= 1073741824) { return (bytes / 1073741824).toFixed(1) + ' GB'; }
        if (bytes >= 1048576)    { return (bytes / 1048576).toFixed(1) + ' MB'; }
        if (bytes >= 1024)       { return (bytes / 1024).toFixed(1) + ' KB'; }
        return bytes + ' B';
    };

    var fragPctClass = function (pct) {
        if (pct > 40) { return 'expensiveGc'; }
        if (pct > 20) { return 'warnGc'; }
        if (pct > 10) { return 'interstingGc'; }
        return '';
    };

    var buildSnapshotSummaryHtml = function (snapshot) {
        var summary = snapshot.summary;
        return '<div class="snapshotCaptureInfo">' +
            '<span class="snapshotProcessName">' + snapshot.processName + '</span>' +
            ' &mdash; captured ' + formatHumanDateTime(snapshot.captureTimeUtc) +
            '</div>' +
            '<div class="summaryGcDiv">' +
                '<div class="total">' +
                    '<div>Heap</div>' +
                    '<div>Committed<span>' + formatBytes(summary.totalCommittedBytes) + '</span></div>' +
                    '<div>Live<span>' + formatBytes(summary.totalObjectBytes) + '</span></div>' +
                    '<div>Free<span>' + formatBytes(summary.totalFreeBytes) + '</span></div>' +
                    '<div>Frag %<span class="' + fragPctClass(summary.fragmentationPct) + '">' + summary.fragmentationPct.toFixed(1) + '%</span></div>' +
                '</div>' +
                '<div class="gen0">' +
                    '<div>Holes</div>' +
                    '<div>Total Chunks<span>' + snapshot.freeChunks.totalCount + '</span></div>' +
                    '<div>Large (&ge;85 KB)<span>' + snapshot.freeChunks.largeChunks.length + '</span></div>' +
                    '<div>Pinned Objects<span>' + summary.pinnedObjectCount + '</span></div>' +
                    '<div>Segments<span>' + summary.segmentCount + '</span></div>' +
                '</div>' +
            '</div>';
    };

    var buildGenerationTableHtml = function (generations) {
        var header = '<tr class="tableHeader">' +
            '<th>Generation</th><th>Committed</th><th>Live</th><th>Free</th>' +
            '<th>Frag %</th><th>Segments</th><th>Free Chunks</th></tr>';
        var rows = '';
        for (var genIdx = 0; genIdx < generations.length; ++genIdx) {
            var gen = generations[genIdx];
            if (gen.committedBytes === 0 && gen.generation === 4) { continue; }
            rows += '<tr>' +
                '<td>' + gen.label + '</td>' +
                '<td>' + formatBytes(gen.committedBytes) + '</td>' +
                '<td>' + formatBytes(gen.objectBytes) + '</td>' +
                '<td>' + formatBytes(gen.freeBytes) + '</td>' +
                '<td class="' + fragPctClass(gen.fragmentationPct) + '">' + gen.fragmentationPct.toFixed(1) + '%</td>' +
                '<td>' + gen.segmentCount + '</td>' +
                '<td>' + gen.freeChunkCount + '</td>' +
                '</tr>';
        }
        return '<div class="detailTable"><table>' + header + rows + '</table></div>';
    };

    var buildFreeChunkTableHtml = function (freeChunks) {
        var header = '<tr class="tableHeader">' +
            '<th>Size Range</th><th>Count</th><th>Total Free</th><th>% of Free</th></tr>';
        var rows = '';
        for (var bucketIdx = 0; bucketIdx < freeChunks.histogram.length; ++bucketIdx) {
            var bucket = freeChunks.histogram[bucketIdx];
            var pct = freeChunks.totalFreeBytes > 0
                ? ((bucket.totalBytes / freeChunks.totalFreeBytes) * 100).toFixed(1)
                : '0.0';
            rows += '<tr>' +
                '<td>' + bucket.label + '</td>' +
                '<td>' + bucket.count + '</td>' +
                '<td>' + formatBytes(bucket.totalBytes) + '</td>' +
                '<td>' + pct + '%</td>' +
                '</tr>';
        }
        return '<div class="detailTable"><table>' + header + rows + '</table></div>';
    };

    // Preceding/Following describe the nearest live object on either side of
    // the hole (see AddToHistogram/LargeFreeChunk in HeapAnalyzer.cs) - a
    // pin badge on either side is the direct root-cause signal for Gen2:
    // the GC can't compact past a pinned object, so a hole bounded by one is
    // permanent regardless of how many more collections run, whereas a hole
    // bounded by two ordinary objects points at a GC that simply hasn't
    // compacted yet, or a free-list size mismatch.
    var buildAdjacencyCellHtml = function (typeName, isPinned) {
        var pinBadge = isPinned ? ' <span class="pinnedBadge" title="Pinned">&#128204;</span>' : '';
        return '<code>' + typeName + '</code>' + pinBadge;
    };

    var buildLargeChunksTableHtml = function (largeChunks) {
        var displayChunks = largeChunks.length > 50 ? largeChunks.slice(0, 50) : largeChunks;
        var header = '<tr class="tableHeader"><th>Address</th><th>Size</th><th>Generation</th>' +
            '<th>Preceding</th><th>Following</th></tr>';
        var rows = '';
        for (var chunkIdx = 0; chunkIdx < displayChunks.length; ++chunkIdx) {
            var chunk = displayChunks[chunkIdx];
            rows += '<tr>' +
                '<td><code>' + chunk.address + '</code></td>' +
                '<td>' + formatBytes(chunk.sizeBytes) + '</td>' +
                '<td>' + genLabelFor(chunk.generation) + '</td>' +
                '<td>' + buildAdjacencyCellHtml(chunk.precedingType, chunk.precedingIsPinned) + '</td>' +
                '<td>' + buildAdjacencyCellHtml(chunk.followingType, chunk.followingIsPinned) + '</td>' +
                '</tr>';
        }
        var note = largeChunks.length > 50
            ? '<p style="margin-top:4px;font-style:italic">Showing first 50 of ' + largeChunks.length + ' large chunks.</p>'
            : '';
        return '<div class="detailTable"><table>' + header + rows + '</table></div>' + note;
    };

    var buildPinnedTableHtml = function (pinnedObjects) {
        var header = '<tr class="tableHeader"><th>Type</th><th>Generation</th><th>Count</th><th>Total Size</th></tr>';
        var rows = '';
        for (var pinnedIdx = 0; pinnedIdx < pinnedObjects.length; ++pinnedIdx) {
            var pinned = pinnedObjects[pinnedIdx];
            rows += '<tr>' +
                '<td>' + pinned.typeName + '</td>' +
                '<td>' + genLabelFor(pinned.generation) + '</td>' +
                '<td>' + pinned.count + '</td>' +
                '<td>' + formatBytes(pinned.totalBytes) + '</td>' +
                '</tr>';
        }
        return '<div class="detailTable"><table>' + header + rows + '</table></div>';
    };

    var occupancyPctClass = function (pct) {
        if (pct < 10) { return 'expensiveGc'; }
        if (pct < 30) { return 'warnGc'; }
        if (pct < 60) { return 'interstingGc'; }
        return '';
    };

    var buildSegmentTableHtml = function (segments) {
        var header = '<tr class="tableHeader"><th>Address</th><th>Generation</th>' +
            '<th>Committed</th><th>Live</th><th>Occupancy %</th></tr>';
        var rows = '';
        for (var segmentIdx = 0; segmentIdx < segments.length; ++segmentIdx) {
            var segment = segments[segmentIdx];
            rows += '<tr>' +
                '<td><code>' + segment.address + '</code></td>' +
                '<td>' + genLabelFor(segment.generation) + '</td>' +
                '<td>' + formatBytes(segment.committedBytes) + '</td>' +
                '<td>' + formatBytes(segment.liveBytes) + '</td>' +
                '<td class="' + occupancyPctClass(segment.occupancyPct) + '">' + segment.occupancyPct.toFixed(1) + '%</td>' +
                '</tr>';
        }
        return '<div class="detailTable"><table>' + header + rows + '</table></div>';
    };

    // Shared by the Top LOH Types and Top POH Types tables - both are plain
    // type-ranked lists with the same {typeName, count, totalBytes} shape
    // (see ReportJsonExporter.cs's SerializeTypeStats, which writes both).
    var buildTypeStatTableHtml = function (typeStats) {
        var header = '<tr class="tableHeader"><th>Type</th><th>Count</th><th>Total Size</th></tr>';
        var rows = '';
        for (var typeIdx = 0; typeIdx < typeStats.length; ++typeIdx) {
            var typeStat = typeStats[typeIdx];
            rows += '<tr>' +
                '<td>' + typeStat.typeName + '</td>' +
                '<td>' + typeStat.count + '</td>' +
                '<td>' + formatBytes(typeStat.totalBytes) + '</td>' +
                '</tr>';
        }
        return '<div class="detailTable"><table>' + header + rows + '</table></div>';
    };

    // Re-rendered on every generation-filter change (see wireGenFilterButtons
    // below), so any previous chart instances must be destroyed first - the
    // canvases persist across re-renders (only innerHTML around them
    // changes), and Chart.js throws if a second chart is attached to a
    // canvas that already has one.
    var buildFreeChunkCharts = function (histogram, titleSuffix) {
        if (freeChunkCountChartInstance) { freeChunkCountChartInstance.destroy(); freeChunkCountChartInstance = null; }
        if (freeChunkBytesChartInstance) { freeChunkBytesChartInstance.destroy(); freeChunkBytesChartInstance = null; }

        var bucketLabels = [];
        var countData = [];
        var bytesData = [];

        for (var bucketIdx = 0; bucketIdx < histogram.length; ++bucketIdx) {
            bucketLabels.push(histogram[bucketIdx].label);
            countData.push(histogram[bucketIdx].count);
            bytesData.push(histogram[bucketIdx].totalBytes);
        }

        var chartColors = [
            'rgba(72, 83, 136, 0.6)',
            'rgba(96, 165, 69, 0.6)',
            'rgba(141, 31, 95, 0.6)',
            'rgba(220, 53, 69, 0.6)',
            'rgba(201, 221, 84, 0.6)'
        ];

        var countCanvas = document.getElementById('freeChunkCountChart');
        if (countCanvas) {
            freeChunkCountChartInstance = new Chart(countCanvas.getContext('2d'), {
                type: 'horizontalBar',
                data: {
                    labels: bucketLabels,
                    datasets: [{
                        label: 'Count',
                        data: countData,
                        backgroundColor: chartColors,
                        borderWidth: 1
                    }]
                },
                options: {
                    title: { display: true, text: 'Free Chunks by Count' + titleSuffix },
                    scales: { xAxes: [{ ticks: { beginAtZero: true } }] },
                    legend: { display: false },
                    animation: { duration: 0 },
                    maintainAspectRatio: false
                }
            });
        }

        var bytesCanvas = document.getElementById('freeChunkBytesChart');
        if (bytesCanvas) {
            freeChunkBytesChartInstance = new Chart(bytesCanvas.getContext('2d'), {
                type: 'horizontalBar',
                data: {
                    labels: bucketLabels,
                    datasets: [{
                        label: 'Bytes',
                        data: bytesData,
                        backgroundColor: chartColors,
                        borderWidth: 1
                    }]
                },
                options: {
                    title: { display: true, text: 'Free Space by Size Bucket' + titleSuffix },
                    scales: {
                        xAxes: [{
                            ticks: {
                                beginAtZero: true,
                                callback: function (value) { return formatBytes(value); }
                            }
                        }]
                    },
                    legend: { display: false },
                    animation: { duration: 0 },
                    maintainAspectRatio: false
                }
            });
        }
    };

    // Free Chunk Distribution + Large Free Holes, scoped to whichever
    // generation is currently selected (heapSnapshotGenFilter, -1 = All).
    // Rebuilds just this section's DOM + charts rather than the whole
    // snapshot panel, since it's the only part a filter change affects.
    var renderFreeChunkSection = function () {
        var snapshot = heapSnapshotData;
        if (!snapshot) { return; }

        var container = document.getElementById('freeChunkSection');
        if (!container) { return; }

        var isAll = heapSnapshotGenFilter < 0;
        var histogram = isAll ? snapshot.freeChunks.histogram : snapshot.generations[heapSnapshotGenFilter].histogram;
        var titleSuffix = isAll ? '' : ' (' + genLabelFor(heapSnapshotGenFilter) + ')';

        var largeChunks = snapshot.freeChunks.largeChunks;
        if (!isAll) {
            largeChunks = [];
            for (var chunkIdx = 0; chunkIdx < snapshot.freeChunks.largeChunks.length; ++chunkIdx) {
                if (snapshot.freeChunks.largeChunks[chunkIdx].generation === heapSnapshotGenFilter) {
                    largeChunks.push(snapshot.freeChunks.largeChunks[chunkIdx]);
                }
            }
        }

        var html = '<div class="freeChunkHistogramRow">' +
                        '<div class="freeChunkHistogramChart"><canvas id="freeChunkCountChart"></canvas></div>' +
                        '<div class="freeChunkHistogramChart"><canvas id="freeChunkBytesChart"></canvas></div>' +
                    '</div>';
        html += buildFreeChunkTableHtml({ histogram: histogram, totalFreeBytes: histogram.reduce(function (sum, bucket) { return sum + bucket.totalBytes; }, 0) });

        if (largeChunks.length > 0) {
            html += '<h4 class="detailTableHeading">Large Free Holes (&ge; 85 KB)' + titleSuffix + ' &mdash; ' +
                    largeChunks.length + ' total</h4>';
            html += buildLargeChunksTableHtml(largeChunks);
        } else {
            html += '<p>No large free holes' + titleSuffix + '.</p>';
        }

        container.innerHTML = html;
        buildFreeChunkCharts(histogram, titleSuffix);
    };

    var wireGenFilterButtons = function () {
        var filterButtons = document.getElementsByClassName('genFilterButton');
        for (var buttonIdx = 0; buttonIdx < filterButtons.length; ++buttonIdx) {
            filterButtons[buttonIdx].addEventListener('click', function (event) {
                var buttons = document.getElementsByClassName('genFilterButton');
                for (var idx = 0; idx < buttons.length; ++idx) {
                    buttons[idx].classList.remove('active');
                }
                event.currentTarget.classList.add('active');

                heapSnapshotGenFilter = parseInt(event.currentTarget.getAttribute('data-gen-filter'), 10);
                renderFreeChunkSection();
            });
        }
    };

    // Real bytes span orders of magnitude (a 20 MB gap next to a 5 KB live
    // run) - a linear width would make every small block invisible, so
    // widths are sqrt-scaled instead: compresses the dynamic range while
    // still keeping larger blocks visibly larger, and a CSS min-width (see
    // .segmentBlock) keeps labels legible regardless.
    var segmentBlockFlexGrow = function (bytes) {
        return Math.max(1, Math.round(Math.sqrt(Math.max(bytes, 1))));
    };

    var buildSegmentBlockHtml = function (block) {
        var flexGrow = segmentBlockFlexGrow(block.bytes);

        if (block.isGap) {
            return '<div class="segmentBlock segmentBlockGap" style="flex-grow:' + flexGrow + '" ' +
                'title="Free / fragmented gap - ' + formatBytes(block.bytes) + '">' +
                '<div class="segmentBlockLabel">Free</div>' +
                '<div class="segmentBlockSize">' + formatBytes(block.bytes) + '</div>' +
                '</div>';
        }

        var moreLabel = block.otherTypeCount > 0 ? ' <span class="segmentBlockMore">+' + block.otherTypeCount + ' more</span>' : '';
        var pinBadge = block.hasPinnedObject ? ' <span class="pinnedBadge" title="Contains a pinned object">&#128204;</span>' : '';
        var classes = 'segmentBlock segmentBlockLive' + (block.hasPinnedObject ? ' segmentBlockPinned' : '');
        var titleText = block.typeName + (block.otherTypeCount > 0 ? ' (+' + block.otherTypeCount + ' more types)' : '') +
            ' - ' + block.objectCount + ' object(s), ' + formatBytes(block.bytes);

        return '<div class="' + classes + '" style="flex-grow:' + flexGrow + '" title="' + titleText + '">' +
            '<div class="segmentBlockLabel">' + block.typeName + moreLabel + pinBadge + '</div>' +
            '<div class="segmentBlockSize">' + formatBytes(block.bytes) + '</div>' +
            '</div>';
    };

    var buildSegmentStripHtml = function (segmentMap, occupancy) {
        var occLabel = occupancy ? (' &mdash; ' + occupancy.occupancyPct.toFixed(1) + '% occupied') : '';
        var heading = '<h4 class="segmentStripHeading">' + genLabelFor(segmentMap.generation) + ' segment ' +
            '<code>' + segmentMap.address + '</code>' + occLabel + ', address-ordered</h4>';

        var blocksHtml = '<div class="segmentStripRow">';
        for (var blockIdx = 0; blockIdx < segmentMap.blocks.length; ++blockIdx) {
            blocksHtml += buildSegmentBlockHtml(segmentMap.blocks[blockIdx]);
        }
        blocksHtml += '</div>';

        return heading + blocksHtml;
    };

    // One address-ordered strip per Gen2/LOH/POH segment (see SegmentMap in
    // HeapAnalyzer.cs) - occupancyByAddress correlates each strip back to
    // its Segment Occupancy table row so the strip's heading can show the
    // same % the Overview tab already reports for that segment.
    var renderSegmentMapSection = function (snapshot) {
        var container = document.getElementById('heapSnapshot-tab-segmentmap');
        if (!container) { return; }

        var occupancyByAddress = {};
        if (snapshot.segments) {
            for (var segIdx = 0; segIdx < snapshot.segments.length; ++segIdx) {
                occupancyByAddress[snapshot.segments[segIdx].address] = snapshot.segments[segIdx];
            }
        }

        var html = '<div class="segmentStripLegend">' +
                '<span class="segmentStripLegendItem"><span class="segmentBlock segmentBlockLive segmentBlockPinned segmentStripLegendSwatch"></span>Pinned live object(s)</span>' +
                '<span class="segmentStripLegendItem"><span class="segmentBlock segmentBlockLive segmentStripLegendSwatch"></span>Live object(s)</span>' +
                '<span class="segmentStripLegendItem"><span class="segmentBlock segmentBlockGap segmentStripLegendSwatch"></span>Free / fragmented gap</span>' +
            '</div>';

        for (var mapIdx = 0; mapIdx < snapshot.segmentMaps.length; ++mapIdx) {
            var segmentMap = snapshot.segmentMaps[mapIdx];
            html += buildSegmentStripHtml(segmentMap, occupancyByAddress[segmentMap.address]);
        }

        container.innerHTML = html;
    };

    var wireHeapSnapshotInnerTabs = function () {
        var tabButtons = document.getElementsByClassName('heapSnapshotInnerTabButton');
        for (var buttonIdx = 0; buttonIdx < tabButtons.length; ++buttonIdx) {
            tabButtons[buttonIdx].addEventListener('click', function (event) {
                var targetTab = event.currentTarget.getAttribute('data-heapsnaptab');

                var buttons = document.getElementsByClassName('heapSnapshotInnerTabButton');
                for (var idx = 0; idx < buttons.length; ++idx) {
                    buttons[idx].classList.remove('active');
                }
                event.currentTarget.classList.add('active');

                var panels = document.getElementsByClassName('heapSnapshotInnerTabPanel');
                for (var panelIdx = 0; panelIdx < panels.length; ++panelIdx) {
                    panels[panelIdx].classList.remove('active');
                }
                document.getElementById('heapSnapshot-tab-' + targetTab).classList.add('active');
            });
        }
    };

    var renderHeapSnapshot = function (snapshot) {
        var panel = document.getElementById('tab-heapSnapshot');
        if (!panel) { return; }

        heapSnapshotData = snapshot;
        heapSnapshotGenFilter = -1;

        var overviewHtml = buildSnapshotSummaryHtml(snapshot);

        overviewHtml += '<h3 class="detailTableHeading">Generation Breakdown</h3>';
        overviewHtml += buildGenerationTableHtml(snapshot.generations);

        if (snapshot.segments && snapshot.segments.length > 0) {
            overviewHtml += '<h3 class="detailTableHeading">Segment Occupancy</h3>' +
                '<p class="segmentOccupancyHint">Sorted least-occupied first - a low-occupancy segment is a fragmentation ' +
                'signal on its own, whatever the cause (a pinned object, an ordinary long-lived anchor, or a GC that ' +
                'simply hasn\'t compacted recently).</p>';
            overviewHtml += buildSegmentTableHtml(snapshot.segments);
        }

        overviewHtml += '<h3 class="detailTableHeading">Free Chunk Distribution</h3>';
        overviewHtml += '<div class="genFilterRow">' +
                    '<button class="genFilterButton active" data-gen-filter="-1">All</button>' +
                    '<button class="genFilterButton" data-gen-filter="0">Gen0</button>' +
                    '<button class="genFilterButton" data-gen-filter="1">Gen1</button>' +
                    '<button class="genFilterButton" data-gen-filter="2">Gen2</button>' +
                    '<button class="genFilterButton" data-gen-filter="3">LOH</button>' +
                    '<button class="genFilterButton" data-gen-filter="4">POH</button>' +
                '</div>';
        overviewHtml += '<div id="freeChunkSection"></div>';

        if (snapshot.pinnedObjects && snapshot.pinnedObjects.length > 0) {
            overviewHtml += '<h3 class="detailTableHeading">Pinned Object Types</h3>';
            overviewHtml += buildPinnedTableHtml(snapshot.pinnedObjects);
        } else {
            overviewHtml += '<h3 class="detailTableHeading">Pinned Objects</h3><p>No pinned objects detected.</p>';
        }

        if (snapshot.topLohTypes && snapshot.topLohTypes.length > 0) {
            overviewHtml += '<h3 class="detailTableHeading">Top LOH Types</h3>';
            overviewHtml += buildTypeStatTableHtml(snapshot.topLohTypes);
        }

        if (snapshot.topPohTypes && snapshot.topPohTypes.length > 0) {
            overviewHtml += '<h3 class="detailTableHeading">Top POH Types</h3>';
            overviewHtml += buildTypeStatTableHtml(snapshot.topPohTypes);
        }

        var hasSegmentMaps = snapshot.segmentMaps && snapshot.segmentMaps.length > 0;

        var html = '<div class="heapSnapshotInnerTabBar">' +
                '<button class="heapSnapshotInnerTabButton active" data-heapsnaptab="overview">Overview</button>' +
                (hasSegmentMaps ? '<button class="heapSnapshotInnerTabButton" data-heapsnaptab="segmentmap">Segment Map</button>' : '') +
            '</div>';
        html += '<div id="heapSnapshot-tab-overview" class="heapSnapshotInnerTabPanel active">' + overviewHtml + '</div>';
        if (hasSegmentMaps) {
            html += '<div id="heapSnapshot-tab-segmentmap" class="heapSnapshotInnerTabPanel"></div>';
        }

        panel.innerHTML = html;
        renderFreeChunkSection();
        wireGenFilterButtons();
        wireHeapSnapshotInnerTabs();

        if (hasSegmentMaps) {
            renderSegmentMapSection(snapshot);
        }

        var tabBtn = document.getElementById('heapSnapshotTabBtn');
        if (tabBtn) {
            tabBtn.style.display = 'inline-block';
            tabBtn.click();
        }
    };

    var loadHeapSnapshotBtn = document.getElementById('loadHeapSnapshotBtn');
    var heapSnapshotInput = document.getElementById('heapSnapshotInput');

    if (loadHeapSnapshotBtn && heapSnapshotInput) {
        loadHeapSnapshotBtn.addEventListener('click', function () {
            heapSnapshotInput.click();
        });

        heapSnapshotInput.addEventListener('change', function (event) {
            var file = event.target.files[0];
            if (!file) { return; }

            var reader = new FileReader();
            reader.onload = function (loadEvent) {
                var snapshot;
                var showSnapshotLoadError = function (msg) {
                    var snapshotPanel = document.getElementById('tab-heapSnapshot');
                    if (snapshotPanel) {
                        snapshotPanel.innerHTML = '<p class="snapshotLoadError">' + msg + '</p>';
                    }
                    var snapshotTabBtn = document.getElementById('heapSnapshotTabBtn');
                    if (snapshotTabBtn) {
                        snapshotTabBtn.style.display = 'inline-block';
                        snapshotTabBtn.click();
                    }
                };

                try {
                    snapshot = JSON.parse(loadEvent.target.result);
                } catch (parseErr) {
                    showSnapshotLoadError('Could not parse heap snapshot: ' + parseErr.message);
                    return;
                }

                if (!snapshot.summary || !snapshot.generations || !snapshot.freeChunks) {
                    showSnapshotLoadError('File does not appear to be a gcHeapAnalyzer output (missing summary / generations / freeChunks).');
                    return;
                }

                renderHeapSnapshot(snapshot);

                // Allow reloading a different snapshot
                heapSnapshotInput.value = '';
            };

            reader.readAsText(file);
        });
    }

    // Handle messages sent from the extension to the webview
    window.addEventListener('message', event => {
        const message = event.data; // The json data that the extension sent
        switch (message.type) {
            case 'update':
                const text = message.text;

                // Then persist state information.
                // This state is returned in the call to `vscode.getState` below when a webview is reloaded.
                vscode.setState({ text });

                return;
        }
    });

    // Webviews are normally torn down when not visible and re-created when they become visible again.
    // State lets us save information across these re-loads
    const state = vscode.getState();

}());