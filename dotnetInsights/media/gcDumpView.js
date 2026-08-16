// Webview logic for the .gcdump heap snapshot view (see
// dotnetInsights/src/GcDumpRenderer.ts for the document this drives, and
// nettraceParser/GcDump/GcDumpJsonExporter.cs for the payload's contract).
//
// The payload is aggregated to the TYPE level before it ever gets here, so
// everything below is O(types) - a few thousand rows - regardless of whether
// the heap held eight thousand objects or ten million. That is the whole
// reason this file can render tables and trees directly with no windowing,
// no virtual scrolling and no incremental fetch.
//
// Type names are interned: the payload carries a `typeNames`/`typeModules`
// pool and everything else refers to types by index into it. Names on a real
// heap are long and repeat across the census, both edge lists and every node
// of the root-path trie, so inlining them would dominate the payload - the
// same interning this repo already had to add to the .nettrace path after a
// real capture blew past Node's maximum string length.

(function () {
    var payload = JSON.parse(document.getElementById('gcDumpJson').textContent);

    var typeNames = payload.typeNames || [];
    var typeModules = payload.typeModules || [];
    var census = payload.types || [];
    var summary = payload.summary || {};
    var totalBytes = Number(summary.totalBytes || 0);

    // ---------------------------------------------------------------------
    // Formatting
    // ---------------------------------------------------------------------

    function formatNumber(value) {
        return Number(value).toLocaleString('en-US');
    }

    function formatBytes(bytes) {
        bytes = Number(bytes);

        if (bytes < 1024) {
            return bytes + ' B';
        }

        var units = ['KB', 'MB', 'GB', 'TB'];
        var value = bytes / 1024;
        var unitIndex = 0;

        while (value >= 1024 && unitIndex < units.length - 1) {
            value /= 1024;
            ++unitIndex;
        }

        return value.toFixed(value >= 100 ? 0 : 1) + ' ' + units[unitIndex];
    }

    function percentOfHeap(bytes) {
        if (totalBytes <= 0) {
            return '0.0%';
        }

        return ((Number(bytes) / totalBytes) * 100).toFixed(1) + '%';
    }

    function escapeHtml(value) {
        return String(value)
            .replace(/&/g, '&amp;')
            .replace(/</g, '&lt;')
            .replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;')
            .replace(/'/g, '&#39;');
    }

    // dotnet-gcdump stores a full module path from the machine that captured
    // the dump, which may not be this machine's OS - so '\' has to be treated
    // as a separator too, not just '/'.
    function moduleFileName(modulePath) {
        if (!modulePath) {
            return '';
        }

        var normalized = String(modulePath).replace(/\\/g, '/');
        var lastSlash = normalized.lastIndexOf('/');
        return lastSlash < 0 ? normalized : normalized.substring(lastSlash + 1);
    }

    function typeLabel(poolIndex) {
        return typeNames[poolIndex] || '?';
    }

    function typeCellHtml(poolIndex) {
        var name = typeLabel(poolIndex);
        var module = moduleFileName(typeModules[poolIndex]);
        var moduleHtml = module ? ' <span class="gcDumpModule">[' + escapeHtml(module) + ']</span>' : '';
        return '<span class="gcDumpTypeName">' + escapeHtml(name) + '</span>' + moduleHtml;
    }

    // ---------------------------------------------------------------------
    // Ranked tables (Type Census, Retained Size)
    // ---------------------------------------------------------------------

    // Both tables render the same rows with different column orders and sort
    // keys, so they share one renderer rather than two near-identical ones.
    function renderCensusRows(tbody, rows, columns) {
        var html = '';

        for (var rowIndex = 0; rowIndex < rows.length; ++rowIndex) {
            var row = rows[rowIndex];
            html += '<tr>';
            html += '<td>' + typeCellHtml(row.t) + '</td>';

            for (var columnIndex = 0; columnIndex < columns.length; ++columnIndex) {
                html += '<td class="numericCell">' + columns[columnIndex](row) + '</td>';
            }

            html += '</tr>';
        }

        tbody.innerHTML = html;
    }

    var censusColumns = [
        function (row) { return formatNumber(row.c); },
        function (row) { return formatBytes(row.b); },
        function (row) { return percentOfHeap(row.b); },
        function (row) { return formatBytes(row.r); },
        function (row) { return formatBytes(row.m); }
    ];

    var retainedColumns = [
        function (row) { return formatBytes(row.r); },
        function (row) { return percentOfHeap(row.r); },
        function (row) { return formatBytes(row.m); },
        function (row) { return formatNumber(row.c); },
        function (row) { return formatBytes(row.b); }
    ];

    // Rows are capped for rendering only. A heap can carry tens of thousands
    // of distinct types and nobody scrolls past the first few hundred - but
    // the FILTER runs over every row, so a type outside the cap is still
    // reachable by typing its name rather than silently absent.
    var MaxRenderedRows = 500;

    function applyFilter(rows, filterText) {
        if (!filterText) {
            return rows;
        }

        var needle = filterText.toLowerCase();
        var filtered = [];

        for (var rowIndex = 0; rowIndex < rows.length; ++rowIndex) {
            if (typeLabel(rows[rowIndex].t).toLowerCase().indexOf(needle) >= 0) {
                filtered.push(rows[rowIndex]);
            }
        }

        return filtered;
    }

    function setUpRankedTable(tableId, tbodyId, filterId, countId, columns, sortKey) {
        var tbody = document.getElementById(tbodyId);
        var filterInput = document.getElementById(filterId);
        var countLabel = document.getElementById(countId);

        var sorted = census.slice();
        sorted.sort(function (left, right) {
            return sortKey(right) - sortKey(left);
        });

        function refresh() {
            var filtered = applyFilter(sorted, filterInput.value);
            var shown = filtered.slice(0, MaxRenderedRows);

            renderCensusRows(tbody, shown, columns);

            if (filtered.length > shown.length) {
                countLabel.textContent = 'showing ' + formatNumber(shown.length) + ' of ' + formatNumber(filtered.length) + ' types';
            } else {
                countLabel.textContent = formatNumber(filtered.length) + ' types';
            }
        }

        filterInput.addEventListener('input', refresh);
        refresh();
    }

    setUpRankedTable('censusTable', 'censusTableBody', 'censusFilter', 'censusFilterCount', censusColumns, function (row) { return row.b; });
    setUpRankedTable('retainedTable', 'retainedTableBody', 'retainedFilter', 'retainedFilterCount', retainedColumns, function (row) { return row.r; });

    // ---------------------------------------------------------------------
    // Paths to Root
    // ---------------------------------------------------------------------

    var rootPaths = payload.rootPaths || [];
    var rootPathIndexByType = payload.rootPathIndexByType || [];

    // The trie arrives as a flat parent-pointer array (each node names its
    // parent), which is compact on the wire but useless for rendering, since
    // drawing a tree needs to walk DOWNWARD. Inverting it once here is one
    // pass; doing it per expansion would be one pass per click.
    var childrenByPathIndex = {};

    for (var pathIndex = 0; pathIndex < rootPaths.length; ++pathIndex) {
        var parentIndex = rootPaths[pathIndex].p;

        if (parentIndex < 0) {
            continue;
        }

        if (!childrenByPathIndex[parentIndex]) {
            childrenByPathIndex[parentIndex] = [];
        }

        childrenByPathIndex[parentIndex].push(pathIndex);
    }

    for (var key in childrenByPathIndex) {
        if (Object.prototype.hasOwnProperty.call(childrenByPathIndex, key)) {
            childrenByPathIndex[key].sort(function (left, right) {
                return rootPaths[right].c - rootPaths[left].c;
            });
        }
    }

    var rootTypeSelect = document.getElementById('rootTypeSelect');
    var rootPathTree = document.getElementById('rootPathTree');

    function populateRootTypeSelect() {
        // Ordered by what each type retains, matching the ranking that made
        // it "interesting" enough to have a tree computed at all (see
        // GcDumpAnalysisLimits.InterestingTypeCount).
        var entries = rootPathIndexByType.slice();
        var retainedByPoolIndex = {};

        for (var censusIndex = 0; censusIndex < census.length; ++censusIndex) {
            retainedByPoolIndex[census[censusIndex].t] = census[censusIndex].r;
        }

        entries.sort(function (left, right) {
            return (retainedByPoolIndex[right.t] || 0) - (retainedByPoolIndex[left.t] || 0);
        });

        var html = '';

        for (var entryIndex = 0; entryIndex < entries.length; ++entryIndex) {
            html += '<option value="' + entries[entryIndex].i + '">' + escapeHtml(typeLabel(entries[entryIndex].t)) + '</option>';
        }

        rootTypeSelect.innerHTML = html;
    }

    function renderRootPathTree(rootIndexOfTree) {
        if (rootIndexOfTree === undefined || rootIndexOfTree === null || !rootPaths[rootIndexOfTree]) {
            rootPathTree.innerHTML = '<p class="gcDumpEmpty">No retention paths were recorded for this type.</p>';
            return;
        }

        var sampledInstances = rootPaths[rootIndexOfTree].c;

        var html = '<table class="cpuHotMethodsTable gcDumpTreeTable">';
        html += '<thead><tr class="tableHeader">';
        html += '<th><span class="thLabel">Holder</span></th>';
        html += '<th data-sort="number"><span class="thLabel">Instances</span></th>';
        html += '<th data-sort="number"><span class="thLabel">Share</span></th>';
        html += '</tr></thead><tbody>';

        // Iterative, not recursive: the trie is depth-capped in C# but this
        // keeps the renderer independent of that cap.
        var stack = [{ index: rootIndexOfTree, depth: 0 }];

        while (stack.length > 0) {
            var current = stack.pop();
            var node = rootPaths[current.index];

            var share = sampledInstances > 0 ? ((node.c / sampledInstances) * 100).toFixed(1) + '%' : '';
            var indentPx = current.depth * 18;

            html += '<tr>';
            html += '<td><span class="gcDumpTreeIndent" style="padding-left:' + indentPx + 'px"></span>';

            if (current.depth > 0) {
                html += '<span class="gcDumpTreeArrow">&#8592;</span> ';
            }

            html += typeCellHtml(node.t) + '</td>';
            html += '<td class="numericCell">' + formatNumber(node.c) + '</td>';
            html += '<td class="numericCell">' + share + '</td>';
            html += '</tr>';

            var children = childrenByPathIndex[current.index];

            if (children) {
                // Pushed in reverse so the highest-count branch is popped
                // (and therefore drawn) first.
                for (var childIndex = children.length - 1; childIndex >= 0; --childIndex) {
                    stack.push({ index: children[childIndex], depth: current.depth + 1 });
                }
            }
        }

        html += '</tbody></table>';

        var note = '<p class="gcDumpEmpty">Branches merged across ' + formatNumber(sampledInstances) +
            ' sampled instances. Each level is one reference closer to a GC root.</p>';

        rootPathTree.innerHTML = note + html;
    }

    populateRootTypeSelect();
    rootTypeSelect.addEventListener('change', function () {
        renderRootPathTree(parseInt(rootTypeSelect.value, 10));
    });

    if (rootTypeSelect.options.length > 0) {
        renderRootPathTree(parseInt(rootTypeSelect.value, 10));
    } else {
        renderRootPathTree(null);
    }

    // ---------------------------------------------------------------------
    // References
    // ---------------------------------------------------------------------

    var outgoingEdges = payload.outgoingReferences || [];
    var incomingEdges = payload.incomingReferences || [];

    // Both directions get an index keyed by the type the user is expanding
    // FROM, so an expansion is a lookup rather than a scan of the whole edge
    // list. Built once; a real heap's edge list runs to hundreds of thousands
    // of rows and this view expands nodes interactively.
    function indexEdgesBy(edges, keyField) {
        var index = {};

        for (var edgeIndex = 0; edgeIndex < edges.length; ++edgeIndex) {
            var key = edges[edgeIndex][keyField];

            if (!index[key]) {
                index[key] = [];
            }

            index[key].push(edges[edgeIndex]);
        }

        return index;
    }

    var outgoingByFrom = indexEdgesBy(outgoingEdges, 'f');
    var incomingByTo = indexEdgesBy(incomingEdges, 't');

    var referenceDirection = document.getElementById('referenceDirection');
    var referenceFilter = document.getElementById('referenceFilter');
    var referenceTree = document.getElementById('referenceTree');

    function edgesFor(poolIndex) {
        if (referenceDirection.value === 'outgoing') {
            return { edges: outgoingByFrom[poolIndex] || [], otherField: 't' };
        }

        return { edges: incomingByTo[poolIndex] || [], otherField: 'f' };
    }

    function renderReferenceRoots() {
        var filterText = referenceFilter.value ? referenceFilter.value.toLowerCase() : '';

        var sorted = census.slice();
        sorted.sort(function (left, right) {
            return right.b - left.b;
        });

        var html = '<table class="cpuHotMethodsTable gcDumpTreeTable" id="referenceRootTable">';
        html += '<thead><tr class="tableHeader">';
        html += '<th><span class="thLabel">Type</span></th>';
        html += '<th data-sort="number"><span class="thLabel">References</span></th>';
        html += '<th data-sort="number"><span class="thLabel">Bytes Pointed At</span></th>';
        html += '</tr></thead><tbody>';

        var rendered = 0;

        for (var rowIndex = 0; rowIndex < sorted.length && rendered < MaxRenderedRows; ++rowIndex) {
            var poolIndex = sorted[rowIndex].t;

            if (filterText && typeLabel(poolIndex).toLowerCase().indexOf(filterText) < 0) {
                continue;
            }

            var group = edgesFor(poolIndex);

            if (group.edges.length === 0) {
                continue;
            }

            var totalReferences = 0;
            var totalReferencedBytes = 0;

            for (var edgeIndex = 0; edgeIndex < group.edges.length; ++edgeIndex) {
                totalReferences += group.edges[edgeIndex].n;
                totalReferencedBytes += group.edges[edgeIndex].b;
            }

            html += '<tr class="gcDumpExpandable" data-pool-index="' + poolIndex + '" data-depth="0">';
            html += '<td><span class="gcDumpTreeToggle">&#9654;</span> ' + typeCellHtml(poolIndex) + '</td>';
            html += '<td class="numericCell">' + formatNumber(totalReferences) + '</td>';
            html += '<td class="numericCell">' + formatBytes(totalReferencedBytes) + '</td>';
            html += '</tr>';

            ++rendered;
        }

        html += '</tbody></table>';

        if (rendered === 0) {
            referenceTree.innerHTML = '<p class="gcDumpEmpty">No references match this filter.</p>';
            return;
        }

        referenceTree.innerHTML = html;
    }

    // Expansion inserts child rows directly after the clicked row and marks
    // them with the parent's depth, so collapsing can remove exactly the rows
    // that belong to it without tracking a separate tree structure.
    function toggleExpansion(row) {
        var depth = parseInt(row.getAttribute('data-depth'), 10);
        var poolIndex = parseInt(row.getAttribute('data-pool-index'), 10);
        var toggle = row.querySelector('.gcDumpTreeToggle');

        if (row.getAttribute('data-expanded') === 'true') {
            var next = row.nextElementSibling;

            while (next && parseInt(next.getAttribute('data-depth'), 10) > depth) {
                var toRemove = next;
                next = next.nextElementSibling;
                toRemove.parentNode.removeChild(toRemove);
            }

            row.setAttribute('data-expanded', 'false');
            toggle.innerHTML = '&#9654;';
            return;
        }

        var group = edgesFor(poolIndex);
        var insertAfter = row;

        for (var edgeIndex = 0; edgeIndex < group.edges.length; ++edgeIndex) {
            var edge = group.edges[edgeIndex];
            var otherPoolIndex = edge[group.otherField];

            var childRow = document.createElement('tr');
            childRow.className = 'gcDumpExpandable';
            childRow.setAttribute('data-pool-index', String(otherPoolIndex));
            childRow.setAttribute('data-depth', String(depth + 1));

            var hasChildren = edgesFor(otherPoolIndex).edges.length > 0;
            var toggleHtml = hasChildren ? '<span class="gcDumpTreeToggle">&#9654;</span> ' : '<span class="gcDumpTreeToggle gcDumpTreeToggleLeaf"></span> ';

            childRow.innerHTML =
                '<td><span class="gcDumpTreeIndent" style="padding-left:' + ((depth + 1) * 18) + 'px"></span>' +
                toggleHtml + typeCellHtml(otherPoolIndex) + '</td>' +
                '<td class="numericCell">' + formatNumber(edge.n) + '</td>' +
                '<td class="numericCell">' + formatBytes(edge.b) + '</td>';

            insertAfter.parentNode.insertBefore(childRow, insertAfter.nextSibling);
            insertAfter = childRow;
        }

        row.setAttribute('data-expanded', 'true');
        toggle.innerHTML = '&#9660;';
    }

    // One delegated listener on the container rather than one per row: rows
    // are created and destroyed on every expand/collapse, so per-row handlers
    // would have to be rebound each time.
    referenceTree.addEventListener('click', function (event) {
        var row = event.target.closest('tr.gcDumpExpandable');

        if (!row) {
            return;
        }

        // A leaf has no outgoing edges in this direction; clicking it should
        // do nothing rather than toggling an empty expansion.
        var poolIndex = parseInt(row.getAttribute('data-pool-index'), 10);

        if (edgesFor(poolIndex).edges.length === 0) {
            return;
        }

        toggleExpansion(row);
    });

    referenceDirection.addEventListener('change', renderReferenceRoots);
    referenceFilter.addEventListener('input', renderReferenceRoots);
    renderReferenceRoots();

    // ---------------------------------------------------------------------
    // View switching
    // ---------------------------------------------------------------------

    var viewButtons = document.getElementsByClassName('viewNavButton');

    for (var buttonIndex = 0; buttonIndex < viewButtons.length; ++buttonIndex) {
        viewButtons[buttonIndex].addEventListener('click', function (event) {
            var targetView = event.currentTarget.getAttribute('data-view');

            var buttons = document.getElementsByClassName('viewNavButton');
            for (var index = 0; index < buttons.length; ++index) {
                buttons[index].classList.remove('active');
            }
            event.currentTarget.classList.add('active');

            var panels = document.getElementsByClassName('viewPanel');
            for (var panelIndex = 0; panelIndex < panels.length; ++panelIndex) {
                panels[panelIndex].classList.remove('active');
            }

            document.getElementById('view-' + targetView).classList.add('active');
        });
    }
})();
