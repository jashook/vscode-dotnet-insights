// Behaviour for the Insights view (see src/InsightsRenderer.ts).
//
// Deliberately tiny, and deliberately not a fourth copy of the drill-down
// machinery: the insight cards are fully server-rendered, so the only things
// that need wiring are the audit list's disclosure toggle and the "go look at
// this yourself" links.
//
// NAVIGATION IS A CLICK ON AN EXISTING NAV BUTTON, not a reimplementation of
// view switching. Each link carries the `view` name the C# rule chose, and
// those names are exactly the `data-view` attributes GcSnapshotRenderer.ts
// already puts on its nav buttons - so this finds that button and clicks it,
// inheriting whatever the real switcher does (lazy panel injection, chart
// resizing, zoom state) for free. Duplicating that switch here is how the two
// would drift.
//
// Loaded as a plain global script alongside the other media/ files, before
// snapshotGcStats.js, matching how rankedTable.js is shared - see CLAUDE.md's
// note on why the shared behaviours live outside snapshotGcStats.js's IIFE.

(function () {
    function wireInsightsAuditToggle() {
        var toggle = document.getElementById("insightAuditToggle");
        var body = document.getElementById("insightAuditBody");

        if (toggle === null || body === null) {
            return;
        }

        var ruleCount = body.getElementsByClassName("insightAuditRow").length;

        toggle.addEventListener("click", function () {
            var isHidden = body.style.display === "none";
            body.style.display = isHidden ? "block" : "none";
            toggle.textContent = isHidden
                ? "Hide the " + ruleCount + " rules that were evaluated"
                : "Show all " + ruleCount + " rules that were evaluated";
        });
    }

    function wireInsightLinks() {
        var links = document.getElementsByClassName("insightLink");

        for (var linkIndex = 0; linkIndex < links.length; ++linkIndex) {
            links[linkIndex].addEventListener("click", function (event) {
                var targetView = event.currentTarget.getAttribute("data-insight-view");

                if (targetView === null || targetView.length === 0) {
                    return;
                }

                var navButtons = document.getElementsByClassName("viewNavButton");

                for (var buttonIndex = 0; buttonIndex < navButtons.length; ++buttonIndex) {
                    var navButton = navButtons[buttonIndex];

                    if (navButton.getAttribute("data-view") !== targetView) {
                        continue;
                    }

                    // A disabled nav button means the capture has no data for
                    // that view. That should not be reachable - a rule only
                    // links to a view it read data from - but clicking one
                    // would switch to an empty panel and look like a bug, so
                    // it is left alone.
                    if (navButton.disabled) {
                        return;
                    }

                    navButton.click();
                    return;
                }
            });
        }
    }

    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", function () {
            wireInsightsAuditToggle();
            wireInsightLinks();
        });
    }
    else {
        wireInsightsAuditToggle();
        wireInsightLinks();
    }
})();
