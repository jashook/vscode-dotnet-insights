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
//
// EVERY VIEW HERE IS ONE COMPONENT. All three tabs are the same ranked table
// with an inline, lazily-built tree inside each expanded row - the same
// component the Profile (CPU methods -> callers), Exceptions (type -> throw
// sites) and Heap Contents (type -> allocating stacks) views already use, with
// the same markup, the same classes and the same shared helpers from
// rankedTable.js. Retention paths used to be a fourth tab driven by a <select>;
// they are now what a type row expands INTO, which is exactly the move the
// Profile view already made when its separate "Drill Down" tab became inline
// caller trees in the Methods table.
//
// The structural rules that markup has to obey (all of them learned the hard
// way, all of them enforced by snapshot.css selectors rather than by anything
// that would fail loudly):
//
//   - The ranked table lives in a `.detailTable.cpuHotMethodsTable` DIV
//     wrapping a bare <table>. The class on the <table> itself matches nothing.
//   - Column 1 is the row-hide column, column 2 is the name column, 3+ are
//     numeric. Those positions are what the shared CSS keys off.
//   - A nested tree level is its own `<table class="callerTreeInner">` with a
//     <colgroup> whose first <col> is a 1.6em spacer matching the hide column,
//     and every row starts with a matching empty <td>. Without that spacer the
//     tree's numeric columns sit one column to the left of the ranked table's,
//     which reads as "expanding a row broke the alignment".

(function () {
    var payload = JSON.parse(document.getElementById('gcDumpJson').textContent);

    var typeNames = payload.typeNames || [];
    var typeModules = payload.typeModules || [];
    var census = payload.types || [];
    var summary = payload.summary || {};
    var totalBytes = Number(summary.totalBytes || 0);

    // Rows are capped for RENDERING only. A heap can carry tens of thousands of
    // distinct types and nobody scrolls past the first few hundred - but the
    // filter and the sort both run over every row, so a type outside the cap is
    // still reachable by typing its name or by sorting on another column,
    // rather than silently absent.
    var MaxRenderedRows = 500;

    // A reference graph collapsed by type is densely cyclic (String -> Object[]
    // -> String is entirely normal), so an expandable subtree is infinite by
    // construction. Expansion is one click per level, so this cap is a
    // backstop, not the primary guard - that is the cycle check in
    // renderReferenceRow, which stops a branch the moment it revisits a type
    // already on its own path.
    var MaxReferenceDepth = 24;

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

    function formatPercentOfHeap(bytes) {
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

    // Same presentation as every method name in the Profile/Exceptions tables:
    // dimmed namespace prefix, emphasized final segment (splitQualifiedName in
    // rankedTable.js, which knows not to split inside a generic argument), plus
    // the defining module. The full name goes on `title` because the column
    // wraps rather than scrolls - a 567-character generic is real, ordinary
    // input here.
    function typeNameHtml(poolIndex) {
        var rawName = typeLabel(poolIndex);
        var split = splitQualifiedName(rawName);
        var moduleName = moduleFileName(typeModules[poolIndex]);

        var html = '<span class="gcDumpTypeName" title="' + escapeHtml(rawName) + '">';

        if (split.prefix) {
            html += '<span class="methodTypePrefix">' + escapeHtml(split.prefix) + '</span>';
        }

        html += '<span class="methodName">' + escapeHtml(split.name) + '</span></span>';

        if (moduleName) {
            html += ' <span class="gcDumpModule">[' + escapeHtml(moduleName) + ']</span>';
        }

        return html;
    }

    function expandToggleHtml(isExpandable) {
        return isExpandable
            ? '<span class="leafMethodToggle">&#9656;</span>'
            : '<span class="leafMethodToggle leafMethodToggleEmpty"></span>';
    }

    // ---------------------------------------------------------------------
    // Retention paths (what a census/retained row expands into)
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

    for (var childListKey in childrenByPathIndex) {
        if (Object.prototype.hasOwnProperty.call(childrenByPathIndex, childListKey)) {
            childrenByPathIndex[childListKey].sort(function (left, right) {
                return rootPaths[right].c - rootPaths[left].c;
            });
        }
    }

    // Only the heaviest types by retained size get a trie at all (see
    // GcDumpAnalysisLimits.InterestingTypeCount) - this is what decides whether
    // a ranked row gets a real chevron or an invisible placeholder.
    var rootPathIndexByPoolIndex = {};

    for (var rootTypeIndex = 0; rootTypeIndex < rootPathIndexByType.length; ++rootTypeIndex) {
        rootPathIndexByPoolIndex[rootPathIndexByType[rootTypeIndex].t] = rootPathIndexByType[rootTypeIndex].i;
    }

    // ---------------------------------------------------------------------
    // Reference graph (what a references row expands into)
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

        for (var indexKey in index) {
            if (Object.prototype.hasOwnProperty.call(index, indexKey)) {
                index[indexKey].sort(function (left, right) {
                    return right.b - left.b;
                });
            }
        }

        return index;
    }

    var outgoingByFrom = indexEdgesBy(outgoingEdges, 'f');
    var incomingByTo = indexEdgesBy(incomingEdges, 't');

    var referenceDirection = document.getElementById('referenceDirection');

    function edgesFor(poolIndex) {
        if (referenceDirection.value === 'outgoing') {
            return { edges: outgoingByFrom[poolIndex] || [], otherField: 't' };
        }

        return { edges: incomingByTo[poolIndex] || [], otherField: 'f' };
    }

    // ---------------------------------------------------------------------
    // Lazily-built tree levels
    //
    // One level of one tree is built when its row is first expanded and cached
    // in the DOM from then on - the same lazy-expand discipline
    // cpuDrillDownStats.js / exceptionDrillDownStats.js use, and for the same
    // reason: a retention trie has thousands of nodes and a reference graph is
    // unbounded, so building either eagerly would render a tree nobody asked
    // to see.
    // ---------------------------------------------------------------------

    var treeRowIdCounter = 0;

    // rowId -> the description of the level to build when that row is first
    // expanded. Entries are added as rows are rendered and removed as they are
    // consumed, so this holds only the frontier of what has been drawn.
    var pendingSubtrees = new Map();

    // Leading 1.6em spacer <col> matching the ranked table's own hide column,
    // then the name column (left unset so it absorbs the width), then the
    // numeric columns - which pick up the ranked table's shared
    // --rankedNumericColumnWidth through .cpuHotMethodsTable .callerTreeInner
    // .bytesColumn/.percentColumn, so a tree's numbers land on the same pixels
    // as the header above it.
    var RETENTION_COLGROUP = '<colgroup><col style="width: 1.6em"><col><col class="bytesColumn"><col class="bytesColumn"><col class="percentColumn"></colgroup>';
    var REFERENCE_COLGROUP = '<colgroup><col style="width: 1.6em"><col><col class="bytesColumn"><col class="bytesColumn"></colgroup>';

    // Same values as drillDownStats.js's CALLER_INDENT_EM_PER_LEVEL/
    // CALLER_INDENT_MAX_EM - a cap, because a deep chain would otherwise indent
    // its way off the right-hand edge of the column.
    var TreeIndentEmPerLevel = 0.85;
    var TreeIndentMaxEm = 17;

    function indentStyleAttribute(depth) {
        var indentEm = depth * TreeIndentEmPerLevel;
        return ' style="padding-left: ' + (indentEm < TreeIndentMaxEm ? indentEm : TreeIndentMaxEm) + 'em"';
    }

    // The hidden row that holds one expanded row's next level. colspan has to
    // cover the whole tree table or the nested table is confined to one column.
    // Column labels for a tree's own metrics, emitted once at the top of an
    // expansion. The nested levels below repeat this table's shape but not this
    // row - a label per level would be noise, and the alignment carries it.
    function treeColumnLabelRowHtml(labels) {
        var html = '<tr class="treeColumnLabelRow"><td></td>';

        for (var labelIndex = 0; labelIndex < labels.length; ++labelIndex) {
            html += '<td>' + labels[labelIndex] + '</td>';
        }

        return html + '</tr>';
    }

    function detailRowHtml(rowId, columnCount) {
        return '<tr id="' + rowId + '" class="callPathsDetail"><td colspan="' + columnCount + '" class="callerTreeCell"></td></tr>';
    }

    // Alternates a tint across sibling branches so a long chain reads as
    // distinct branches rather than one undifferentiated block - assigned once
    // per branch and inherited down every non-branching continuation, exactly
    // as drillDownStats.js does it.
    function branchClassFor(childIndex, isBranch, inheritedBranchClass) {
        var toggledClass = inheritedBranchClass === 'drillDownAltBranch' ? '' : 'drillDownAltBranch';

        if (!isBranch) {
            return toggledClass;
        }

        return childIndex % 2 === 1 ? toggledClass : inheritedBranchClass;
    }

    // One retention-trie node: "held by <type>", with the sampled instance
    // count that reached it and that count's share of the type's own sample.
    function renderRetentionRow(pathIndex, depth, sampledInstances, branchClass) {
        var node = rootPaths[pathIndex];
        var children = childrenByPathIndex[pathIndex];
        var hasChildren = !!(children && children.length > 0);
        var rowId = hasChildren ? 'gcDumpTreeRow' + (++treeRowIdCounter) : null;

        var share = sampledInstances > 0 ? ((node.c / sampledInstances) * 100).toFixed(1) + '%' : '';

        var rowHtml = '<tr class="callerRow ' + branchClass + '"' +
            (hasChildren ? ' data-expandable="true" data-gcdump-target="' + rowId + '"' : '') + '>' +
            '<td></td>' +
            '<td' + indentStyleAttribute(depth) + '>' + expandToggleHtml(hasChildren) +
            '<span class="calledByLabel">held by </span>' + typeNameHtml(node.t) + '</td>' +
            '<td>' + formatNumber(node.c) + '</td>' +
            '<td>' + formatBytes(node.b) + '</td>' +
            '<td>' + share + '</td>' +
            '</tr>';

        if (!hasChildren) {
            return rowHtml;
        }

        pendingSubtrees.set(rowId, {
            kind: 'retention',
            pathIndex: pathIndex,
            depth: depth + 1,
            sampledInstances: sampledInstances,
            branchClass: branchClass
        });

        return rowHtml + detailRowHtml(rowId, 5);
    }

    function buildRetentionLevelHtml(pending) {
        var children = childrenByPathIndex[pending.pathIndex] || [];
        var isBranch = children.length > 1;

        var rowsHtml = '';
        for (var childIndex = 0; childIndex < children.length; ++childIndex) {
            rowsHtml += renderRetentionRow(
                children[childIndex],
                pending.depth,
                pending.sampledInstances,
                branchClassFor(childIndex, isBranch, pending.branchClass));
        }

        return '<table class="callerTreeInner">' + RETENTION_COLGROUP + rowsHtml + '</table>';
    }

    // What a Type Census / Retained Size row expands into: the retention trie
    // for that type, read downward one reference at a time toward a GC root.
    function buildRetentionTreeHtml(poolIndex) {
        var rootIndex = rootPathIndexByPoolIndex[poolIndex];

        if (rootIndex === undefined || !rootPaths[rootIndex]) {
            return '<p class="gcDumpEmpty">No retention paths were traced for this type.</p>';
        }

        var rootNode = rootPaths[rootIndex];
        var children = childrenByPathIndex[rootIndex] || [];

        if (children.length === 0) {
            return '<p class="gcDumpEmpty">Instances of this type are reachable directly from a GC root.</p>';
        }

        var isBranch = children.length > 1;
        var rowsHtml = '';

        for (var childIndex = 0; childIndex < children.length; ++childIndex) {
            rowsHtml += renderRetentionRow(
                children[childIndex],
                0,
                rootNode.c,
                branchClassFor(childIndex, isBranch, ''));
        }

        // Instance counts on the trie are counts of SAMPLED instances (see
        // RootPathBuilder.cs's own note on why it samples) - presenting them as
        // exact instance counts would be a lie the data cannot support, so the
        // sample size is stated rather than implied.
        var note = '<p class="gcDumpEmpty">Branches merged across ' + formatNumber(rootNode.c) +
            ' sampled instances. Each level is one reference closer to a GC root.</p>';

        var labelRow = treeColumnLabelRowHtml(['Holder', 'Instances', 'Bytes', 'Share']);

        return note + '<table class="callerTreeInner">' + RETENTION_COLGROUP + labelRow + rowsHtml + '</table>';
    }

    // One reference-graph edge, rendered as the type on the far end of it.
    // `ancestors` is the chain of types already on this branch: revisiting one
    // is a cycle, and this graph is full of them, so such a row is drawn as a
    // leaf with a marker rather than as another expandable level that would
    // walk the same loop forever.
    function renderReferenceRow(edge, otherPoolIndex, depth, ancestors, branchClass) {
        var isCycle = ancestors.indexOf(otherPoolIndex) >= 0;
        var hasEdges = edgesFor(otherPoolIndex).edges.length > 0;
        var hasChildren = hasEdges && !isCycle && depth < MaxReferenceDepth;
        var rowId = hasChildren ? 'gcDumpTreeRow' + (++treeRowIdCounter) : null;

        var cycleSuffix = isCycle ? ' <span class="pathCount">(already on this path)</span>' : '';

        var rowHtml = '<tr class="callerRow ' + branchClass + '"' +
            (hasChildren ? ' data-expandable="true" data-gcdump-target="' + rowId + '"' : '') + '>' +
            '<td></td>' +
            '<td' + indentStyleAttribute(depth) + '>' + expandToggleHtml(hasChildren) +
            typeNameHtml(otherPoolIndex) + cycleSuffix + '</td>' +
            '<td>' + formatNumber(edge.n) + '</td>' +
            '<td>' + formatBytes(edge.b) + '</td>' +
            '</tr>';

        if (!hasChildren) {
            return rowHtml;
        }

        pendingSubtrees.set(rowId, {
            kind: 'reference',
            poolIndex: otherPoolIndex,
            depth: depth + 1,
            ancestors: ancestors.concat([otherPoolIndex]),
            branchClass: branchClass
        });

        return rowHtml + detailRowHtml(rowId, 4);
    }

    function buildReferenceLevelHtml(poolIndex, depth, ancestors, inheritedBranchClass) {
        var group = edgesFor(poolIndex);
        var isBranch = group.edges.length > 1;

        var rowsHtml = '';
        for (var edgeIndex = 0; edgeIndex < group.edges.length; ++edgeIndex) {
            var edge = group.edges[edgeIndex];
            rowsHtml += renderReferenceRow(
                edge,
                edge[group.otherField],
                depth,
                ancestors,
                branchClassFor(edgeIndex, isBranch, inheritedBranchClass));
        }

        if (rowsHtml.length === 0) {
            return '<p class="gcDumpEmpty">No references in this direction.</p>';
        }

        var labelRow = depth === 0
            ? treeColumnLabelRowHtml(['Type', 'References', 'Bytes'])
            : '';

        return '<table class="callerTreeInner">' + REFERENCE_COLGROUP + labelRow + rowsHtml + '</table>';
    }

    function buildPendingSubtreeHtml(rowId) {
        var pending = pendingSubtrees.get(rowId);

        if (!pending) {
            return null;
        }

        pendingSubtrees.delete(rowId);

        if (pending.kind === 'retention') {
            return buildRetentionLevelHtml(pending);
        }

        return buildReferenceLevelHtml(pending.poolIndex, pending.depth, pending.ancestors, pending.branchClass);
    }

    // Expands or collapses one row of a nested tree. Both the row and its
    // paired detail row carry the state: `expanded` on the row rotates its
    // chevron, `expanded` on the detail row is what actually shows it (a
    // .callPathsDetail row is display:none otherwise).
    function toggleTreeRow(row) {
        var detailRow = document.getElementById(row.getAttribute('data-gcdump-target'));

        if (!detailRow) {
            return;
        }

        if (detailRow.classList.contains('expanded')) {
            detailRow.classList.remove('expanded');
            row.classList.remove('expanded');
            return;
        }

        var subtreeHtml = buildPendingSubtreeHtml(detailRow.id);

        if (subtreeHtml !== null) {
            detailRow.getElementsByClassName('callerTreeCell')[0].innerHTML = subtreeHtml;
        }

        detailRow.classList.add('expanded');
        row.classList.add('expanded');
    }

    // One delegated listener per view container rather than one per row: rows
    // are created and destroyed on every filter, sort, hide and expand, so
    // per-row handlers would have to be rebound each time.
    function wireTreeExpansion(container) {
        container.addEventListener('click', function (event) {
            var row = event.target.closest('tr.callerRow[data-expandable="true"]');

            if (!row || !container.contains(row)) {
                return;
            }

            toggleTreeRow(row);
        });
    }

    // ---------------------------------------------------------------------
    // The ranked table itself - one implementation, three instances
    // ---------------------------------------------------------------------

    // `columns` describes the columns AFTER the hide column, starting with the
    // type name column, and must match the header GcDumpRenderer.ts rendered
    // for this table one-for-one. Each entry carries the sort key and the cell
    // renderer for that column; the type column's own `key` is what a "text"
    // sort compares.
    function createRankedTable(config) {
        // Every id in one of these views is derived from the view's own name,
        // exactly as GcDumpRenderer.ts's renderRankedTable emits them - one
        // name in, a whole table's worth of elements found.
        var viewId = config.tableId.replace(/Table$/, '');
        var table = document.getElementById(config.tableId);
        var tbody = document.getElementById(viewId + 'TableBody');
        var filterInput = document.getElementById(viewId + 'Filter');
        var countLabel = document.getElementById(viewId + 'FilterCount');

        assertColumnContract(table, config.columns);

        var rows = config.rows;
        var sortColumnIndex = config.defaultSortColumnIndex;
        var sortAscending = false;

        var hider = createRowHideController(viewId + 'HideStatus', viewId + 'HideStatusLabel', function () {
            render();
        });

        function compareRows(left, right) {
            var column = config.columns[sortColumnIndex];
            var leftValue = column.key(left);
            var rightValue = column.key(right);

            var comparison = 0;
            if (leftValue < rightValue) {
                comparison = -1;
            } else if (leftValue > rightValue) {
                comparison = 1;
            }

            return sortAscending ? comparison : -comparison;
        }

        function renderRow(row) {
            var hasDetail = config.hasDetail(row);
            var detailId = viewId + 'Detail' + row.index;

            var html = '<tr class="typeRow gcDumpTypeRow" data-row-index="' + row.index + '"' +
                (hasDetail ? ' data-gcdump-expandable="true" data-detail-target="' + detailId + '"' : '') + '>' +
                '<td class="rowHideColumn"><button class="rowHideBtn" type="button" title="Hide this row">&#10005;</button></td>' +
                '<td>' + expandToggleHtml(hasDetail) + typeNameHtml(row.t) + '</td>';

            for (var columnIndex = 1; columnIndex < config.columns.length; ++columnIndex) {
                var column = config.columns[columnIndex];
                // data-sort-value carries the raw number behind a formatted
                // cell ("553 MB"), which is what any DOM-level sort of this
                // table would otherwise have to parse out of the text.
                html += '<td data-sort-value="' + column.key(row) + '">' + column.render(row) + '</td>';
            }

            html += '</tr>';

            if (hasDetail) {
                html += '<tr id="' + detailId + '" class="callPathsDetail" data-gcdump-type="' + row.t + '">' +
                    '<td colspan="' + (config.columns.length + 1) + '" class="callerTreeCell"></td></tr>';
            }

            return html;
        }

        function render() {
            var filterText = filterInput.value ? filterInput.value.toLowerCase() : '';
            var matched = [];

            for (var rowIndex = 0; rowIndex < rows.length; ++rowIndex) {
                var row = rows[rowIndex];

                if (hider.isHidden(row.index)) {
                    continue;
                }

                if (filterText && typeLabel(row.t).toLowerCase().indexOf(filterText) < 0) {
                    continue;
                }

                matched.push(row);
            }

            matched.sort(compareRows);

            var shown = matched.length > MaxRenderedRows ? matched.slice(0, MaxRenderedRows) : matched;

            var html = '';
            for (var shownIndex = 0; shownIndex < shown.length; ++shownIndex) {
                html += renderRow(shown[shownIndex]);
            }

            tbody.innerHTML = html;

            if (matched.length > shown.length) {
                countLabel.textContent = 'showing ' + formatNumber(shown.length) + ' of ' + formatNumber(matched.length) + ' types';
            } else {
                countLabel.textContent = formatNumber(matched.length) + ' types';
            }
        }

        // Sorting re-sorts the DATA and re-renders rather than reordering the
        // rows in the DOM (sortDetailTableByColumn's job for a fully-rendered
        // table): this table shows only the first MaxRenderedRows of thousands,
        // so a DOM-level sort would reorder that one capped page and present it
        // as the top N by the newly clicked column, which it is not.
        wireSortableTableHeaders(table, function (columnIndex, sortType, ascending) {
            // columnIndex is a DOM column index, so it counts the hide column
            // at 0; config.columns starts at the type column.
            sortColumnIndex = columnIndex - 1;
            sortAscending = ascending;
            render();
        });

        showInitialSortIndicator(table, config.defaultSortColumnIndex + 1);

        filterInput.addEventListener('input', render);

        var panel = table.closest('.viewPanel');

        panel.addEventListener('click', function (event) {
            if (event.target.closest('#' + viewId + 'ShowAllBtn')) {
                hider.reset();
                return;
            }

            // Whole cell is the click target, not just the ✕ glyph itself - a
            // small icon-only hit target is easy to miss. Checked BEFORE the
            // expand toggle below, since the hide cell sits inside a row that
            // is itself expandable.
            var hideCell = event.target.closest('td.rowHideColumn');

            if (hideCell) {
                var hideRow = hideCell.closest('[data-row-index]');

                if (hideRow) {
                    hider.toggle(parseInt(hideRow.getAttribute('data-row-index'), 10));
                }

                return;
            }

            var typeRow = event.target.closest('tr[data-gcdump-expandable="true"]');

            if (!typeRow) {
                return;
            }

            var detailRow = document.getElementById(typeRow.getAttribute('data-detail-target'));

            if (!detailRow) {
                return;
            }

            if (detailRow.classList.contains('expanded')) {
                detailRow.classList.remove('expanded');
                typeRow.classList.remove('expanded');
                return;
            }

            var detailCell = detailRow.getElementsByClassName('callerTreeCell')[0];

            if (detailCell.innerHTML.length === 0) {
                detailCell.innerHTML = config.buildDetail(parseInt(detailRow.getAttribute('data-gcdump-type'), 10));
            }

            detailRow.classList.add('expanded');
            typeRow.classList.add('expanded');
        });

        wireTreeExpansion(panel);

        render();

        return { render: render, setRows: function (newRows) { rows = newRows; render(); } };
    }

    // The header is rendered server-side (GcDumpRenderer.ts) and the sort keys
    // and cell formatters live here, so the two halves of one table are written
    // in two files and could drift. They cannot drift silently: a mismatch
    // means clicking a header sorts by the wrong column, which looks like data
    // corruption rather than a wiring bug.
    function assertColumnContract(table, columns) {
        var headerCellCount = table.rows[0].cells.length;

        if (headerCellCount !== columns.length + 1) {
            console.error('gcDumpView: ' + table.id + ' has ' + headerCellCount +
                ' header cells but ' + columns.length + ' column definitions (+1 for the hide column).');
        }
    }

    // Every table opens sorted by its own headline column, descending - the
    // indicator has to say so, or the first click on that same column appears
    // to do nothing (it sorts ascending, which is a real change, but from a
    // state the header never claimed).
    function showInitialSortIndicator(table, domColumnIndex) {
        var indicator = table.rows[0].cells[domColumnIndex].getElementsByClassName('sortIndicator')[0];

        if (indicator) {
            indicator.textContent = ' ▼';
        }
    }

    // ---------------------------------------------------------------------
    // The three views
    // ---------------------------------------------------------------------

    // Census rows are the payload's own type entries, given a stable index so
    // the hide controller can name one independently of the current sort.
    var censusRows = [];

    for (var censusIndex = 0; censusIndex < census.length; ++censusIndex) {
        var censusEntry = census[censusIndex];
        censusRows.push({
            index: censusIndex,
            t: censusEntry.t,
            c: censusEntry.c,
            b: censusEntry.b,
            r: censusEntry.r,
            m: censusEntry.m
        });
    }

    function typeSortKey(row) {
        return typeLabel(row.t).toLowerCase();
    }

    function hasRetentionPath(row) {
        return rootPathIndexByPoolIndex[row.t] !== undefined;
    }

    createRankedTable({
        tableId: 'censusTable',
        rows: censusRows,
        defaultSortColumnIndex: 2,
        columns: [
            { key: typeSortKey, render: function (row) { return typeNameHtml(row.t); } },
            { key: function (row) { return row.c; }, render: function (row) { return formatNumber(row.c); } },
            { key: function (row) { return row.b; }, render: function (row) { return formatBytes(row.b); } },
            { key: function (row) { return row.b; }, render: function (row) { return formatPercentOfHeap(row.b); } },
            { key: function (row) { return row.r; }, render: function (row) { return formatBytes(row.r); } },
            { key: function (row) { return row.m; }, render: function (row) { return formatBytes(row.m); } }
        ],
        hasDetail: hasRetentionPath,
        buildDetail: buildRetentionTreeHtml
    });

    createRankedTable({
        tableId: 'retainedTable',
        rows: censusRows,
        defaultSortColumnIndex: 1,
        columns: [
            { key: typeSortKey, render: function (row) { return typeNameHtml(row.t); } },
            { key: function (row) { return row.r; }, render: function (row) { return formatBytes(row.r); } },
            { key: function (row) { return row.r; }, render: function (row) { return formatPercentOfHeap(row.r); } },
            { key: function (row) { return row.m; }, render: function (row) { return formatBytes(row.m); } },
            { key: function (row) { return row.c; }, render: function (row) { return formatNumber(row.c); } },
            { key: function (row) { return row.b; }, render: function (row) { return formatBytes(row.b); } }
        ],
        hasDetail: hasRetentionPath,
        buildDetail: buildRetentionTreeHtml
    });

    // The references table's rows are DERIVED, not a slice of the payload: one
    // row per type that has any edge in the current direction, carrying that
    // type's totals across those edges. Switching direction rebuilds them.
    function buildReferenceRows() {
        var index = referenceDirection.value === 'outgoing' ? outgoingByFrom : incomingByTo;
        var rows = [];

        for (var censusRowIndex = 0; censusRowIndex < censusRows.length; ++censusRowIndex) {
            var poolIndex = censusRows[censusRowIndex].t;
            var edges = index[poolIndex];

            if (!edges || edges.length === 0) {
                continue;
            }

            var totalReferences = 0;
            var totalReferencedBytes = 0;

            for (var edgeIndex = 0; edgeIndex < edges.length; ++edgeIndex) {
                totalReferences += edges[edgeIndex].n;
                totalReferencedBytes += edges[edgeIndex].b;
            }

            rows.push({
                index: censusRows[censusRowIndex].index,
                t: poolIndex,
                n: totalReferences,
                b: totalReferencedBytes
            });
        }

        return rows;
    }

    var referenceTable = createRankedTable({
        tableId: 'referenceTable',
        rows: buildReferenceRows(),
        defaultSortColumnIndex: 2,
        columns: [
            { key: typeSortKey, render: function (row) { return typeNameHtml(row.t); } },
            { key: function (row) { return row.n; }, render: function (row) { return formatNumber(row.n); } },
            { key: function (row) { return row.b; }, render: function (row) { return formatBytes(row.b); } }
        ],
        hasDetail: function () { return true; },
        buildDetail: function (poolIndex) { return buildReferenceLevelHtml(poolIndex, 0, [poolIndex], ''); }
    });

    referenceDirection.addEventListener('change', function () {
        // Every already-built expansion describes the OLD direction, and the
        // rows themselves are per-direction totals - so this rebuilds rather
        // than re-renders, and drops the pending frontier with them.
        pendingSubtrees.clear();
        referenceTable.setRows(buildReferenceRows());
    });

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
