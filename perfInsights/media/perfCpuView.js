////////////////////////////////////////////////////////////////////////////////
// Module: perfCpuView.js
//
// Notes:
// Wires the shared CPU view's client behaviour for this extension. The tables,
// the caller trees and their markup are all produced by dotnetInsights'
// CpuProfileRenderer and rendered by its cpuDrillDownStats.js / rankedTable.js
// - copied in verbatim by scripts/sync-shared.js. This file is only the glue
// that the .NET extension keeps inside its own much larger view switcher
// (snapshotGcStats.js), which is bound to the GC snapshot document and is not
// reusable here.
//
// The two click paths are deliberately distinct and must stay that way:
// `data-cpu-method-expandable` is a top-level ranked row expanding into its
// caller tree, and `data-cpu-expandable` is an interior node of an already
// built tree expanding one level further. They use different attribute names
// precisely so one delegation cannot swallow the other.
//
// .rowHideBtn is checked FIRST. It lives in its own cell inside the row, so a
// click on it is also a click on the row - without this check, hiding a row
// would also toggle its caller tree open underneath it.
////////////////////////////////////////////////////////////////////////////////

(function () {
    var profileData = window.perfProfileData || {};
    var methodNames = profileData.methodNames || [];
    var drillDown = profileData.hotMethodDrillDown || [];
    var totalSampleCount = profileData.totalSampleCount || 0;

    if (typeof initCpuDrillDownMethodNames === "function") {
        initCpuDrillDownMethodNames(methodNames);
    }

    var methodsTable = document.getElementById("cpuMethodsTable");

    function toggleMethodRow(row) {
        var targetId = row.getAttribute("data-cpu-method-target");
        var detailRow = document.getElementById(targetId);
        if (!detailRow) {
            return;
        }

        var isExpanded = detailRow.classList.contains("expanded");

        if (!isExpanded) {
            // Built on FIRST expand only. A real capture ranks hundreds of
            // methods and each carries a full caller tree; building them all
            // up front is work nobody asked for on a view most people scroll
            // rather than expand.
            var lazyIndex = detailRow.getAttribute("data-cpu-method-lazy");
            if (lazyIndex !== null) {
                var cell = detailRow.querySelector(".callerTreeCell");
                if (cell) {
                    cell.innerHTML = buildInlineCpuMethodCallerTree(
                        drillDown[Number(lazyIndex)],
                        methodNames,
                        totalSampleCount,
                        "cpuCallerForestRoot");
                }

                detailRow.removeAttribute("data-cpu-method-lazy");
            }
        }

        detailRow.classList.toggle("expanded", !isExpanded);
        row.classList.toggle("expanded", !isExpanded);

        var toggle = row.querySelector(".leafMethodToggle");
        if (toggle) {
            toggle.innerHTML = isExpanded ? "&#9656;" : "&#9662;";
        }
    }

    function toggleInteriorNode(row) {
        var targetId = row.getAttribute("data-cpu-target");
        var detailRow = document.getElementById(targetId);
        if (!detailRow) {
            return;
        }

        if (typeof buildLazyCpuDrillDownSubtree === "function") {
            var built = buildLazyCpuDrillDownSubtree(targetId);
            if (built !== null && built !== undefined) {
                var cell = detailRow.querySelector(".callerTreeCell") || detailRow.querySelector("td");
                if (cell) {
                    cell.innerHTML = built;
                }
            }
        }

        detailRow.classList.toggle("expanded");
        row.classList.toggle("expanded");
    }

    document.addEventListener("click", function (event) {
        var target = event.target;
        if (!target || !target.closest) {
            return;
        }

        // Checked before any expand handling - see this file's header.
        if (target.closest(".rowHideBtn")) {
            var hiddenRow = target.closest("tr");
            if (hiddenRow) {
                hiddenRow.style.display = "none";

                var pairedId = hiddenRow.getAttribute("data-cpu-method-target");
                if (pairedId) {
                    var paired = document.getElementById(pairedId);
                    if (paired) {
                        paired.classList.remove("expanded");
                    }
                }
            }

            return;
        }

        var methodRow = target.closest("[data-cpu-method-expandable]");
        if (methodRow) {
            toggleMethodRow(methodRow);
            return;
        }

        var interiorRow = target.closest("[data-cpu-expandable]");
        if (interiorRow) {
            toggleInteriorNode(interiorRow);
            return;
        }

        // Off-CPU stack rows expand into their frame list, which is already in
        // the DOM - there is no tree to build, so no lazy path.
        var offCpuRow = target.closest("[data-offcpu-target]");
        if (offCpuRow) {
            var detail = document.getElementById(offCpuRow.getAttribute("data-offcpu-target"));
            if (detail) {
                var wasExpanded = detail.classList.contains("expanded");
                detail.classList.toggle("expanded", !wasExpanded);
                offCpuRow.classList.toggle("expanded", !wasExpanded);

                var offCpuToggle = offCpuRow.querySelector(".leafMethodToggle");
                if (offCpuToggle) {
                    offCpuToggle.innerHTML = wasExpanded ? "&#9656;" : "&#9662;";
                }
            }
        }
    });

    // View switching. The panels are all rendered up front and hidden, rather
    // than built on demand: a capture large enough for that to matter is
    // dominated by the parse, and building every panel once keeps the three
    // views' scroll positions independent.
    var navButtons = document.querySelectorAll(".perfNavButton");
    for (var buttonIndex = 0; buttonIndex < navButtons.length; ++buttonIndex) {
        navButtons[buttonIndex].addEventListener("click", function (event) {
            var target = event.currentTarget.getAttribute("data-perf-view");

            var panels = document.querySelectorAll(".perfViewPanel");
            for (var panelIndex = 0; panelIndex < panels.length; ++panelIndex) {
                panels[panelIndex].style.display = panels[panelIndex].id === target ? "" : "none";
            }

            var buttons = document.querySelectorAll(".perfNavButton");
            for (var index = 0; index < buttons.length; ++index) {
                buttons[index].classList.toggle("active", buttons[index] === event.currentTarget);
            }
        });
    }

    // The contention view's own drill-down, wired the same way the CPU one is.
    var contentionData = window.perfContentionData || {};
    if (typeof initContentionDrillDownMethodNames === "function") {
        initContentionDrillDownMethodNames(contentionData.methodNames || []);
    }

    // Click-to-sort on the ranked table's headers. The rows are already sorted
    // by self samples server-side, so this only matters when the reader wants a
    // different order - but every other ranked table in these webviews sorts,
    // and one that silently does not is read as broken.
    if (methodsTable && typeof wireSortableTableHeaders === "function" && typeof sortDetailTableByColumn === "function") {
        wireSortableTableHeaders(methodsTable, function (columnIndex, ascending) {
            sortDetailTableByColumn(methodsTable, columnIndex, ascending);
        });
    }
})();
