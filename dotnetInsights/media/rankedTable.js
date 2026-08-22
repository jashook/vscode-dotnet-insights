// Shared behaviour for every ranked table in this extension's webviews - the
// CPU Methods / Contention Sites / Exception Types / Allocation types / GC
// Detailed tables rendered by snapshotGcStats.js, and the .gcdump heap views
// rendered by gcDumpView.js.
//
// WHY THIS FILE EXISTS. These two webviews are separate documents that load
// separate scripts, but they render the SAME table: a .detailTable wrapper, a
// tr.tableHeader of sortable <th data-sort> cells (see
// GcDetailTableRenderer.ts's renderSortableTableHeader/renderRankedTableHeader),
// a leading narrow .rowHideColumn, a left-aligned name column carrying a
// .leafMethodToggle chevron, and numeric columns after it. Sorting and
// row-hiding were implemented once inside snapshotGcStats.js's own IIFE, which
// made them unreachable from any other document - so the .gcdump view shipped
// with sortable-LOOKING headers that did nothing when clicked. Both behaviours
// live here now; snapshotGcStats.js calls these as globals (this file is loaded
// before it) rather than keeping its own copies.
//
// Plain global functions, no module wrapper - matching drillDownStats.js /
// cpuDrillDownStats.js / exceptionDrillDownStats.js, which are loaded the same
// way and already establish that convention in this codebase.

// A cell's sort key. 'date' reads the cell's own data-raw attribute (an ISO-8601
// timestamp or a zero-padded "+elapsed" string - see GcDetailTableRenderer.ts),
// because the human-formatted display text ("21-Jul-2026 03:42:13 PM PDT") does
// not sort correctly as either text or a date.
function detailTableSortValue(cell, sortType) {
    if (sortType === 'date') {
        return cell.getAttribute('data-raw') || '';
    }

    if (sortType === 'number') {
        // data-sort-value lets a cell that DISPLAYS a formatted value ("553 MB",
        // "1,204.7") declare the raw number to sort by. Without it, parseFloat
        // stops at the first non-numeric character, so "553 MB" and "553 KB"
        // compare equal - which is exactly what a byte column formatted for
        // humans produces. Cells that display a plain number don't need it.
        var declared = cell.getAttribute('data-sort-value');
        if (declared !== null) {
            var declaredValue = parseFloat(declared);
            return isNaN(declaredValue) ? -Infinity : declaredValue;
        }

        // Strips grouping separators before parsing: "1,204,733" would
        // otherwise parse as 1.
        var parsed = parseFloat(String(cell.textContent).replace(/,/g, ''));
        return isNaN(parsed) ? -Infinity : parsed;
    }

    return cell.textContent.toLowerCase();
}

// Attribute names a data row uses to name its own paired, hidden detail row
// (the .callPathsDetail row holding that row's expanded stack tree). Sorting
// moves a data row and its detail row together; without this the two would
// separate and an expanded tree would end up under an unrelated row.
// data-detail-target is the generic form; the other two predate it and are
// still emitted by CpuProfileRenderer.ts / ContentionRenderer.ts.
const DETAIL_ROW_TARGET_ATTRIBUTES = ['data-detail-target', 'data-cpu-method-target', 'data-contention-target'];

function pairedDetailRowIdFor(row) {
    for (var attributeIndex = 0; attributeIndex < DETAIL_ROW_TARGET_ATTRIBUTES.length; ++attributeIndex) {
        var targetId = row.getAttribute(DETAIL_ROW_TARGET_ATTRIBUTES[attributeIndex]);
        if (targetId) {
            return targetId;
        }
    }

    return null;
}

// Sorts the rows already IN the DOM. Correct for a table whose every row is
// present (the server-rendered ranked tables) - see wireSortableTableHeaders'
// own comment for the case where it is not.
function sortDetailTableByColumn(table, columnIndex, sortType, ascending) {
    var tbody = table.tBodies[0] || table;
    // Snapshots the live HTMLCollection before any row gets moved -
    // table.rows[0] is the header row, left untouched. Skip .callPathsDetail
    // rows: they're paired with their own data row and moved along with it
    // below, not sorted independently (they'd sort to a random position
    // relative to their data row otherwise).
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

    // appendChild on a node already in the tree moves it - iterating in the
    // desired final order and re-appending each row leaves the header (never
    // touched) first and every data row following in sorted order. Each
    // expandable row's paired callPathsDetail row is moved immediately after
    // it so it stays correctly associated after the sort.
    for (var rowIndex = 0; rowIndex < dataRows.length; ++rowIndex) {
        tbody.appendChild(dataRows[rowIndex]);
        var pairedDetailId = pairedDetailRowIdFor(dataRows[rowIndex]);
        if (pairedDetailId) {
            var pairedDetailRow = document.getElementById(pairedDetailId);
            if (pairedDetailRow) {
                tbody.appendChild(pairedDetailRow);
            }
        }
    }
}

// Wires click-to-sort onto one table's header cells and hands the actual
// sorting to onSortColumn(columnIndex, sortType, ascending).
//
// The indirection is the point: a table whose rows are all in the DOM sorts by
// reordering them (sortDetailTableByColumn, which is what
// setupDetailTableSortHandlers passes here), but a table rendered from data in
// JS and CAPPED at the first N rows - the .gcdump census, 5,375 types capped at
// 500 - must sort its underlying array and re-render, or clicking a column
// would only ever reorder the 500 rows that happened to survive the previous
// sort's cap and silently present them as the top 500 by the new column.
function wireSortableTableHeaders(table, onSortColumn) {
    if (!table || !table.rows.length) {
        return;
    }

    // Scoped to this one table (not module-level) - two distinct tables each
    // get their own independent "which column, which direction" state, so
    // sorting one table's column 2 doesn't leave a stale ascending/descending
    // toggle for an unrelated table's own column 2.
    var currentSortColumnIndex = -1;
    var currentSortAscending = true;

    var headerCells = table.rows[0].cells;
    for (var headerIndex = 0; headerIndex < headerCells.length; ++headerIndex) {
        var headerCell = headerCells[headerIndex];

        // The row-hide button column's own <th> is a bare, unlabeled cell with
        // no data-sort attribute (see GcDetailTableRenderer.ts's
        // renderRankedTableHeader) - skip it rather than wiring a handler that
        // would sort by a null sortType and then throw reaching for a
        // .sortIndicator span this cell doesn't have.
        if (!headerCell.hasAttribute('data-sort')) {
            continue;
        }

        (function (columnIndex, boundHeaderCell) {
            boundHeaderCell.addEventListener('click', function () {
                var ascending = (currentSortColumnIndex === columnIndex) ? !currentSortAscending : true;
                onSortColumn(columnIndex, boundHeaderCell.getAttribute('data-sort'), ascending);

                // The row-hide column's own blank <th> (skipped above) has no
                // .sortIndicator span at all - guard against it here too, since
                // this loop walks every header cell unconditionally regardless
                // of which one was actually clicked.
                for (var clearIndex = 0; clearIndex < headerCells.length; ++clearIndex) {
                    var indicatorToClear = headerCells[clearIndex].getElementsByClassName('sortIndicator')[0];
                    if (indicatorToClear) {
                        indicatorToClear.textContent = '';
                    }
                }

                var indicator = boundHeaderCell.getElementsByClassName('sortIndicator')[0];
                if (indicator) {
                    indicator.textContent = ascending ? ' ▲' : ' ▼';
                }

                currentSortColumnIndex = columnIndex;
                currentSortAscending = ascending;
            });
        })(headerIndex, headerCell);
    }
}

// The common case: the first .detailTable table inside `container`, sorted in
// place by reordering its own rows.
function setupDetailTableSortHandlers(container) {
    var table = container.querySelector(".detailTable table");
    if (!table) {
        return;
    }

    wireSortableTableHeaders(table, function (columnIndex, sortType, ascending) {
        sortDetailTableByColumn(table, columnIndex, sortType, ascending);
    });
}

// Generic "hide this row and recompute everything else" controller, one
// instance per table (not a shared registry) since each table's own onChange
// callback does entirely different recompute work - rebuild rows, or rebuild
// rows plus summary tiles plus a timeline chart.
//
// Hidden state is in-memory only, same as every other piece of interactive UI
// state in these webviews (zoom range, expand/collapse, sort column) - none of
// which persist via vscode.getState()/setState() today, so a webview reload
// resets it like everything else.
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
        // Bulk-hide, used by "Hide IO-Bound Methods" - adds every index in one
        // pass and fires onChange (a full table/tile/timeline rebuild) once at
        // the end, rather than once per row the way a loop of individual
        // toggle() calls would. Idempotent per index (already-hidden ones are
        // skipped) and a no-op (never calls onChange at all) if nothing in the
        // given set was newly hidden - clicking the bulk button twice in a row
        // shouldn't force a pointless rebuild the second time.
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

// Splits a fully-qualified .NET name into a dimmed namespace/type prefix and
// the emphasized final segment, the presentation every ranked table in this
// extension already uses for method names (see CpuProfileRenderer.ts's
// formatMethodNameHtml and drillDownStats.js's formatFrameHtml).
//
// Splitting on the LAST '.' is wrong for the names a heap dump carries:
// "System.Collections.Generic.List<System.Net.Security.SslApplicationProtocol>"
// would split inside the generic argument and emphasize "SslApplicationProtocol>",
// which is not this type's name at all. Only a '.' at bracket depth zero is a
// real namespace separator, so the scan tracks '<', '[' and '(' depth and
// ignores everything nested inside them.
function splitQualifiedName(rawName) {
    var depth = 0;
    var separatorIndex = -1;

    for (var charIndex = 0; charIndex < rawName.length; ++charIndex) {
        var character = rawName.charAt(charIndex);

        if (character === '<' || character === '[' || character === '(') {
            ++depth;
        } else if (character === '>' || character === ']' || character === ')') {
            if (depth > 0) {
                --depth;
            }
        } else if (character === '.' && depth === 0) {
            separatorIndex = charIndex;
        }
    }

    if (separatorIndex < 0) {
        return { prefix: '', name: rawName };
    }

    return { prefix: rawName.slice(0, separatorIndex + 1), name: rawName.slice(separatorIndex + 1) };
}
