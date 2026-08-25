// Renders the "Insights" view from the report nettraceParser's rule engine
// produced (nettraceParser/Insights/, serialized by InsightReportJson.cs and
// read back from the sidecar - see NettraceJsonStreamReader.readInsightsJson).
//
// THERE IS NO RULE LOGIC IN THIS FILE, and there must never be. Every
// threshold, every severity and every headline is decided in C# and rendered
// here verbatim. The reason is not tidiness: the same report is printed by
// `nettraceParser --insights` on the command line and by this view, and if
// the two could disagree about a capture there would be no way to tell which
// one was right. Anything this file computes is layout.
//
// WHY THE NOT-FIRED LIST IS RENDERED AT ALL. A reader who only ever sees what
// fired has no way to distinguish "the rule ran and this capture is clean"
// from "the rule never ran" - and the second of those is how a broken rule
// hides for months. It is the same principle the Threading view's excluded-
// samples table already follows: a filter that cannot be audited is one
// nobody believes the first time it hides something they expected. It is
// collapsed by default because it is reference material, not the answer.
//
// Severity colours are deliberately NOT hard-coded rgba: these webviews
// follow the VS Code theme, and a near-black glyph on a dark theme is
// invisible. See snapshot.css's own note on .methodTypePrefix and friends.

function escapeHtml(value: string): string {
    return String(value)
        .replace(/&/g, "&amp;")
        .replace(/</g, "&lt;")
        .replace(/>/g, "&gt;")
        .replace(/"/g, "&quot;");
}

// The three outcomes InsightRuleResult carries. Kept in sync with
// InsightReportJson.OutcomeName - a value this doesn't recognize renders as
// itself rather than being dropped, so a new outcome added on the C# side
// shows up as unstyled text instead of silently vanishing from the audit
// list.
function outcomeLabel(outcome: string): string {
    switch (outcome) {
        case "fired": return "fired";
        case "belowThreshold": return "measured, under threshold";
        case "notApplicable": return "no data";
        default: return outcome;
    }
}

function renderEvidence(evidence: any[]): string {
    if (!evidence || evidence.length === 0) {
        return "";
    }

    const rows = evidence.map((entry: any) => {
        // A rule indents a label to nest it under the row above (see
        // CpuCategoryOutlierRule's "  Top method"). Leading spaces do nothing
        // in HTML, so that intent is carried by a class instead.
        const rawLabel = String(entry["label"] ?? "");
        const isNested = rawLabel.startsWith("  ");
        const label = escapeHtml(rawLabel.trim());
        const value = escapeHtml(String(entry["value"] ?? ""));

        return `<tr class="${isNested ? "insightEvidenceNested" : ""}">
            <td class="insightEvidenceLabel">${label}</td>
            <td class="insightEvidenceValue">${value}</td>
        </tr>`;
    }).join("");

    return `<table class="insightEvidenceTable">${rows}</table>`;
}

function renderActions(actions: string[]): string {
    if (!actions || actions.length === 0) {
        return "";
    }

    const items = actions.map((action: string) => `<li>${escapeHtml(action)}</li>`).join("");
    return `<ul class="insightActions">${items}</ul>`;
}

// Each link carries the `view` name matching a nav button's own data-view
// attribute (GcSnapshotRenderer.ts), so navigation needs no mapping table on
// this side - media/insightsView.js clicks the button the insight names. The
// targetKind/targetValue pair is passed through for the views that can act on
// it; a view that can't simply opens.
function renderLinks(links: any[]): string {
    if (!links || links.length === 0) {
        return "";
    }

    const buttons = links.map((link: any) => {
        const view = escapeHtml(String(link["view"] ?? ""));
        const label = escapeHtml(String(link["label"] ?? view));
        const targetKind = escapeHtml(String(link["targetKind"] ?? ""));
        const targetValue = escapeHtml(String(link["targetValue"] ?? ""));

        return `<button class="insightLink" data-insight-view="${view}" data-insight-target-kind="${targetKind}" data-insight-target-value="${targetValue}">${label}</button>`;
    }).join("");

    return `<div class="insightLinks">${buttons}</div>`;
}

function renderInsight(insight: any): string {
    const severity = String(insight["severity"] ?? "info");
    const confidence = String(insight["confidence"] ?? "");
    const id = String(insight["id"] ?? "");

    return `
    <div class="insightCard insightSeverity-${escapeHtml(severity)}">
        <div class="insightHeader">
            <span class="insightSeverityBadge">${escapeHtml(severity)}</span>
            <span class="insightHeadline">${escapeHtml(String(insight["headline"] ?? ""))}</span>
        </div>
        <div class="insightMeta">
            <span class="insightId">${escapeHtml(id)}</span>
            <span class="insightConfidence">confidence: ${escapeHtml(confidence)}</span>
        </div>
        ${renderEvidence(insight["evidence"])}
        <div class="insightDetail">${escapeHtml(String(insight["detail"] ?? ""))}</div>
        <div class="insightThreshold">Fires when: ${escapeHtml(String(insight["threshold"] ?? ""))}</div>
        ${renderActions(insight["actions"])}
        ${renderLinks(insight["links"])}
    </div>`;
}

function renderAuditList(rules: any[]): string {
    if (!rules || rules.length === 0) {
        return "";
    }

    const rows = rules.map((rule: any) => {
        const outcome = String(rule["outcome"] ?? "");
        const measured = String(rule["measured"] ?? "");
        const reason = String(rule["notApplicableReason"] ?? "");
        const detail = outcome === "notApplicable" ? reason : measured;

        return `<tr class="insightAuditRow insightAuditOutcome-${escapeHtml(outcome)}">
            <td class="insightAuditOutcome">${escapeHtml(outcomeLabel(outcome))}</td>
            <td class="insightAuditId">${escapeHtml(String(rule["id"] ?? ""))}</td>
            <td class="insightAuditDetail">${escapeHtml(detail)}</td>
        </tr>`;
    }).join("");

    return `
    <div class="insightAuditSection">
        <button class="insightAuditToggle" id="insightAuditToggle">Show all ${rules.length} rules that were evaluated</button>
        <div class="insightAuditBody" id="insightAuditBody" style="display:none">
            <p class="insightAuditNote">
                Every rule that ran, whether or not it fired, with what it measured or why it had no data.
                A rule reporting &quot;no data&quot; is not a clean result &mdash; it means this capture does not
                contain what that rule needs.
            </p>
            <table class="insightAuditTable">${rows}</table>
        </div>
    </div>`;
}

export function renderInsightsView(insightsReport: any): string {
    if (insightsReport === null || insightsReport === undefined) {
        return `<div class="insightsView"><p>No insights are available for this capture.</p></div>`;
    }

    const insights = insightsReport["insights"] || [];
    const rules = insightsReport["rules"] || [];

    const criticalCount = insightsReport["criticalCount"] ?? 0;
    const warningCount = insightsReport["warningCount"] ?? 0;
    const infoCount = insightsReport["infoCount"] ?? 0;
    const belowThresholdCount = insightsReport["belowThresholdCount"] ?? 0;
    const notApplicableCount = insightsReport["notApplicableCount"] ?? 0;

    const summaryTilesHtml = `
        <div class="summaryGcDiv">
            <div class="total">
                <div>Insights</div>
                <div>Critical<span>${criticalCount}</span></div>
                <div>Warning<span>${warningCount}</span></div>
                <div>Info<span>${infoCount}</span></div>
            </div>
            <div class="total">
                <div>Rules</div>
                <div>Evaluated<span>${rules.length}</span></div>
                <div>Under threshold<span>${belowThresholdCount}</span></div>
                <div>No data<span>${notApplicableCount}</span></div>
            </div>
        </div>`;

    // "Nothing fired" is stated as a positive result rather than left blank,
    // and it points at the audit list - because on a capture that recorded
    // little, "nothing fired" and "nothing could be checked" look identical
    // until you read which rules had data.
    const bodyHtml = insights.length > 0
        ? insights.map(renderInsight).join("")
        : `<div class="insightCard insightSeverity-none">
               <div class="insightHeadline">No rule found anything worth reporting in this capture.</div>
               <div class="insightDetail">
                   ${belowThresholdCount} rules measured this capture and came in under their thresholds;
                   ${notApplicableCount} had no data to work with. Open the list below to see which was which.
               </div>
           </div>`;

    return `
    <div class="insightsView">
        ${summaryTilesHtml}
        <div class="insightsList">${bodyHtml}</div>
        ${renderAuditList(rules)}
    </div>`;
}
