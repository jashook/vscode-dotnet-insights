////////////////////////////////////////////////////////////////////////////////
// Module: nettraceDiff.js
//
// Notes:
// Drives the two-capture comparison webview (see src/NettraceDiffRenderer.ts).
// Deliberately a separate, small IIFE rather than an addition to
// snapshotGcStats.js: that file's state is module-level singletons for ONE
// capture, and a second dataset would collide with all of it.
//
// Rows are rendered here rather than server-side because the same row has two
// presentations - absolute and per-second - and toggling between them must not
// require a round trip to the extension host. The payload carries both numbers
// for every row precisely so this can be a pure client-side switch.
////////////////////////////////////////////////////////////////////////////////

(function () {
    var diffPayload = JSON.parse(document.getElementById('diffPayload').textContent);

    var ROWS_KEY_BY_TABLE = {
        allocations: 'allocationTypes',
        exceptions: 'exceptionTypes',
        cpu: 'cpuMethods',
        contention: 'contentionSites',
        locks: 'locks',
        events: 'eventTypes'
    };

    // Per-table sort state, keyed by table id. Defaults to the delta column
    // descending, which is the question the view exists to answer.
    var sortStateByTable = {};

    var normalized = document.getElementById('diffNormalizeToggle').checked;

    function amountFieldNames() {
        // The payload carries both forms for every row; which pair is read is
        // the only thing the toggle changes.
        if (normalized) {
            return { baseline: 'baselineAmountPerSecond', comparison: 'comparisonAmountPerSecond', delta: 'deltaAmountPerSecond' };
        }

        return { baseline: 'baselineAmount', comparison: 'comparisonAmount', delta: 'deltaAmount' };
    }

    function formatBytes(bytes) {
        var absolute = Math.abs(bytes);

        if (absolute >= 1024 * 1024 * 1024) {
            return (bytes / (1024 * 1024 * 1024)).toFixed(2) + ' GB';
        }

        if (absolute >= 1024 * 1024) {
            return (bytes / (1024 * 1024)).toFixed(2) + ' MB';
        }

        if (absolute >= 1024) {
            return (bytes / 1024).toFixed(2) + ' KB';
        }

        return Math.round(bytes) + ' B';
    }

    function formatAmount(value, unit) {
        if (unit === 'bytes') {
            return formatBytes(value);
        }

        if (unit === 'msec') {
            // Sub-millisecond rates are common once normalized, and a plain
            // toFixed(1) would render most of them as a flat "0.0".
            return Math.abs(value) < 0.01 && value !== 0 ? value.toExponential(1) : value.toFixed(2);
        }

        return Math.abs(value) < 1 && value !== 0 ? value.toFixed(2) : Math.round(value).toLocaleString();
    }

    function formatSigned(value, unit) {
        var sign = value > 0 ? '+' : (value < 0 ? '−' : '');
        return sign + formatAmount(Math.abs(value), unit);
    }

    function deltaClass(delta, direction) {
        if (delta === 0) {
            return '';
        }

        if (direction === 'neutral') {
            return 'deltaNeutral';
        }

        return delta > 0 ? 'deltaWorse' : 'deltaBetter';
    }

    function escapeHtml(value) {
        return String(value)
            .replace(/&/g, '&amp;')
            .replace(/</g, '&lt;')
            .replace(/>/g, '&gt;');
    }

    function renderTable(tableId) {
        var table = document.querySelector('[data-diff-table="' + tableId + '"]');
        if (!table) {
            return;
        }

        var rows = diffPayload[ROWS_KEY_BY_TABLE[tableId]] || [];
        var unit = table.getAttribute('data-diff-unit');
        var direction = table.getAttribute('data-diff-direction');
        var fields = amountFieldNames();

        var sortState = sortStateByTable[tableId] || { column: 'deltaAmount', ascending: false };
        sortStateByTable[tableId] = sortState;

        var sorted = rows.slice();
        sorted.sort(function (left, right) {
            var comparison;

            if (sortState.column === 'name') {
                comparison = String(left.name).localeCompare(String(right.name));
            } else if (sortState.column === 'deltaAmount') {
                // Magnitude, not signed value - a big improvement is as
                // interesting as a big regression, and a signed sort buries
                // every improvement at the far end of the list.
                comparison = Math.abs(right[fields.delta]) - Math.abs(left[fields.delta]);
                return sortState.ascending ? -comparison : comparison;
            } else if (sortState.column === 'percentChange') {
                // Rows with no baseline have no percentage; they sort last
                // rather than being treated as zero change.
                var leftPercent = left.percentChange === null ? -Infinity : Math.abs(left.percentChange);
                var rightPercent = right.percentChange === null ? -Infinity : Math.abs(right.percentChange);
                comparison = rightPercent - leftPercent;
                return sortState.ascending ? -comparison : comparison;
            } else if (sortState.column === 'baselineAmount') {
                comparison = right[fields.baseline] - left[fields.baseline];
                return sortState.ascending ? -comparison : comparison;
            } else if (sortState.column === 'comparisonAmount') {
                comparison = right[fields.comparison] - left[fields.comparison];
                return sortState.ascending ? -comparison : comparison;
            } else {
                comparison = right.deltaCount - left.deltaCount;
                return sortState.ascending ? -comparison : comparison;
            }

            return sortState.ascending ? comparison : -comparison;
        });

        var html = '';
        for (var rowIndex = 0; rowIndex < sorted.length; ++rowIndex) {
            var row = sorted[rowIndex];
            var kindBadge = '';

            if (row.kind === 'added') {
                kindBadge = '<span class="deltaNew">new</span> ';
            } else if (row.kind === 'removed') {
                kindBadge = '<span class="deltaGone">gone</span> ';
            }

            var percentCell = row.percentChange === null
                ? '<span class="deltaNew">new</span>'
                : (row.percentChange > 0 ? '+' : (row.percentChange < 0 ? '−' : '')) + Math.abs(row.percentChange).toFixed(1) + '%';

            html += '<tr>' +
                '<td style="text-align:left" title="' + escapeHtml(row.name) + '">' + kindBadge + escapeHtml(row.name) + '</td>' +
                '<td style="text-align:right">' + formatAmount(row[fields.baseline], unit) + '</td>' +
                '<td style="text-align:right">' + formatAmount(row[fields.comparison], unit) + '</td>' +
                '<td style="text-align:right" class="' + deltaClass(row[fields.delta], direction) + '">' + formatSigned(row[fields.delta], unit) + '</td>' +
                '<td style="text-align:right" class="' + deltaClass(row[fields.delta], direction) + '">' + percentCell + '</td>' +
                '<td style="text-align:right">' + formatSigned(row.deltaCount, 'count') + '</td>' +
                '</tr>';
        }

        // tBodies[0] is the explicit <tbody> the renderer emits after
        // <thead>; the header is safely outside it.
        table.tBodies[0].innerHTML = html;

        var headerCells = table.getElementsByTagName('th');
        for (var headerIndex = 0; headerIndex < headerCells.length; ++headerIndex) {
            var indicator = headerCells[headerIndex].getElementsByClassName('sortIndicator')[0];
            if (!indicator) {
                continue;
            }

            var column = headerCells[headerIndex].getAttribute('data-diff-sort');
            indicator.textContent = (column === sortState.column) ? (sortState.ascending ? ' ▲' : ' ▼') : '';
        }
    }

    function renderAllTables() {
        for (var tableId in ROWS_KEY_BY_TABLE) {
            if (Object.prototype.hasOwnProperty.call(ROWS_KEY_BY_TABLE, tableId)) {
                renderTable(tableId);
            }
        }
    }

    function updateNormalizeHint() {
        var hint = document.getElementById('diffNormalizeHint');
        if (!hint) {
            return;
        }

        var baselineSeconds = diffPayload.baseline.captureDurationMSec / 1000;
        var comparisonSeconds = diffPayload.comparison.captureDurationMSec / 1000;

        hint.textContent = normalized
            ? 'Showing per-second rates (baseline ' + baselineSeconds.toFixed(0) + 's, comparison ' + comparisonSeconds.toFixed(0) + 's).'
            : 'Showing absolute totals — captures span ' + baselineSeconds.toFixed(0) + 's and ' + comparisonSeconds.toFixed(0) + 's.';
    }

    // ---- wiring ----

    var navButtons = document.getElementsByClassName('viewNavButton');
    for (var buttonIndex = 0; buttonIndex < navButtons.length; ++buttonIndex) {
        navButtons[buttonIndex].addEventListener('click', function (event) {
            var target = event.currentTarget.getAttribute('data-diffview');

            var buttons = document.getElementsByClassName('viewNavButton');
            for (var index = 0; index < buttons.length; ++index) {
                buttons[index].classList.remove('active');
            }
            event.currentTarget.classList.add('active');

            var panels = document.getElementsByClassName('viewPanel');
            for (var panelIndex = 0; panelIndex < panels.length; ++panelIndex) {
                panels[panelIndex].classList.remove('active');
            }

            var panel = document.getElementById('diffview-' + target);
            if (panel) {
                panel.classList.add('active');
            }
        });
    }

    document.getElementById('diffNormalizeToggle').addEventListener('change', function (event) {
        normalized = event.currentTarget.checked;
        updateNormalizeHint();
        renderAllTables();
    });

    // Delegated: every table body is re-rendered on each sort and toggle, so
    // per-header listeners would need re-binding each time.
    document.addEventListener('click', function (event) {
        var header = event.target.closest('[data-diff-sort]');
        if (!header) {
            return;
        }

        var table = header.closest('[data-diff-table]');
        if (!table) {
            return;
        }

        var tableId = table.getAttribute('data-diff-table');
        var column = header.getAttribute('data-diff-sort');
        var sortState = sortStateByTable[tableId] || { column: 'deltaAmount', ascending: false };

        if (sortState.column === column) {
            sortState.ascending = !sortState.ascending;
        } else {
            sortState.column = column;
            sortState.ascending = (column === 'name');
        }

        sortStateByTable[tableId] = sortState;
        renderTable(tableId);
    });

    updateNormalizeHint();
    renderAllTables();
})();
