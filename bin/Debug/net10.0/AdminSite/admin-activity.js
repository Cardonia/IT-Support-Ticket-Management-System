// /admin/activity: who did what and when, newest first, read only. One sentence per event, grouped by day.
// Served by Admin/AdminEndpoints.cs after script.js (api, setMessage) and admin-common.js (adminLink, formatWhen,
// localDayToIso). The filters live in the address bar. "Show older activity" asks for the page after the last id we
// hold (keyset), so events added meanwhile never shift the page. Days are the viewer's own days.
// Everything is drawn with textContent / createElement, never innerHTML: names in the log are other people's text.

const PAGE_SIZE = 50;
const NAME_SHAPE = /^[A-Za-z0-9_]{1,14}$/;
const NUMBER_SHAPE = /^[0-9]{1,18}$/;
const DATE_SHAPE = /^\d{4}-\d{2}-\d{2}$/;
const GROUPS = ["auth", "user", "ticket", "note", "admin", "security"];
const TARGET_TYPES = ["user", "ticket", "note"];

// every action the log can hold (the same list as the database check), with the words shown in the filter
const ACTIONS = [
    ["auth.login", "Logged in"], ["auth.logout", "Logged out"], ["auth.login_failed", "Login failed"],
    ["user.registered", "Registered"], ["user.password_changed", "Password changed"],
    ["ticket.created", "Ticket created"], ["ticket.taken", "Ticket taken"], ["ticket.resolved", "Ticket resolved"],
    ["ticket.released", "Ticket released"], ["note.added", "Note added"],
    ["admin.user_created", "Admin created an account"], ["admin.user_updated", "Admin changed an account"],
    ["admin.role_changed", "Admin changed a role"], ["admin.user_deactivated", "Admin deactivated an account"],
    ["admin.user_reactivated", "Admin reactivated an account"], ["admin.password_reset", "Admin reset a password"],
    ["admin.ticket_created", "Admin created a ticket"], ["admin.ticket_updated", "Admin edited a ticket"],
    ["admin.ticket_assigned", "Admin assigned a ticket"], ["admin.ticket_reassigned", "Admin reassigned a ticket"],
    ["admin.ticket_unassigned", "Admin unassigned a ticket"], ["admin.ticket_resolved", "Admin resolved a ticket"],
    ["admin.ticket_reopened", "Admin reopened a ticket"], ["admin.ticket_deleted", "Admin deleted a ticket"],
    ["security.admin_denied", "Admin area refused"], ["security.reauth_failed", "Wrong password at confirmation"],
];

const view = { group: "", action: "", user: "", targetType: "", targetId: "", from: "", to: "" };
let latestRequest = 0;       // only the newest answer may draw (quick typing can answer out of order)
let searchTimer = 0;
let shown = 0;
let nextBefore = null;       // cursor for "Show older activity"; null when there is nothing older
let loading = false;
let lastDay = "";            // the day heading the last list belongs to, so a later page continues it
let lastList = null;

// ---- filters

// The "exact action" box only offers actions of the chosen kind
function fillActions() {
    const select = document.getElementById("ac-action");
    select.replaceChildren(new Option("Any action", ""));
    for (const [value, label] of ACTIONS) {
        if (view.group === "" || value.startsWith(view.group + ".")) select.append(new Option(label, value));
    }
    select.value = ACTIONS.some(([v]) => v === view.action) && (view.group === "" || view.action.startsWith(view.group + ".")) ? view.action : "";
    view.action = select.value;
}

function readView() {
    const p = new URLSearchParams(window.location.search);
    view.group = GROUPS.includes(p.get("group")) ? p.get("group") : "";
    view.action = ACTIONS.some(([v]) => v === p.get("action")) ? p.get("action") : "";
    view.user = NAME_SHAPE.test(p.get("user") ?? "") ? p.get("user") : "";
    view.targetType = TARGET_TYPES.includes(p.get("targetType")) ? p.get("targetType") : "";
    view.targetId = view.targetType && NUMBER_SHAPE.test(p.get("targetId") ?? "") ? p.get("targetId") : "";
    view.from = DATE_SHAPE.test(p.get("from") ?? "") ? p.get("from") : "";
    view.to = DATE_SHAPE.test(p.get("to") ?? "") ? p.get("to") : "";

    document.getElementById("ac-group").value = view.group;
    fillActions();
    document.getElementById("ac-user").value = view.user;
    document.getElementById("ac-target-type").value = view.targetType;
    document.getElementById("ac-target-id").value = view.targetId;
    document.getElementById("ac-target-id").disabled = view.targetType === "";
    document.getElementById("ac-from").value = view.from;
    document.getElementById("ac-to").value = view.to;
}

function writeView() {
    const p = new URLSearchParams();
    for (const key of ["group", "action", "user", "targetType", "targetId", "from", "to"]) if (view[key]) p.set(key, view[key]);
    const qs = p.toString();
    window.history.replaceState(null, "", window.location.pathname + (qs ? "?" + qs : ""));
}

function isFiltered() {
    return Object.values(view).some(Boolean);
}

function dateProblem() {
    if (view.from && !localDayToIso(view.from)) return "From date must be a date between the years 2000 and 2199.";
    if (view.to && !localDayToIso(view.to)) return "To date must be a date between the years 2000 and 2199.";
    if (view.from && view.to && view.from > view.to) return "The From date is after the To date.";
    return null;
}

function apiQuery(before) {
    const p = new URLSearchParams({ limit: String(PAGE_SIZE) });
    if (view.group) p.set("group", view.group);
    if (view.action) p.set("action", view.action);
    if (view.user) p.set("user", view.user);
    if (view.targetType) p.set("targetType", view.targetType);
    if (view.targetType && view.targetId) p.set("targetId", view.targetId);
    if (view.from) p.set("from", localDayToIso(view.from));
    if (view.to) p.set("to", localDayToIso(view.to, 1));      // "to" is exclusive on the server: the day after
    if (before !== null) p.set("before", String(before));
    return p.toString();
}

// ---- feed

// The viewer's calendar day of a moment, as a sortable key and a heading
function dayOf(value) {
    const d = new Date(value);
    if (Number.isNaN(d.getTime())) return { key: "unknown", label: "Unknown date" };
    const key = `${d.getFullYear()}-${d.getMonth() + 1}-${d.getDate()}`;
    return { key, label: d.toLocaleDateString(undefined, { weekday: "long", year: "numeric", month: "long", day: "numeric" }) };
}

function timeOf(value) {
    const d = new Date(value);
    return Number.isNaN(d.getTime()) ? "" : d.toLocaleTimeString(undefined, { hour: "2-digit", minute: "2-digit" });
}

function renderEvent(e) {
    return activityLine(e, timeOf(e.at));
}

function appendEvents(items) {
    const feed = document.getElementById("ac-feed");
    for (const e of items) {
        const day = dayOf(e.at);
        if (day.key !== lastDay || lastList === null) {
            const heading = document.createElement("h2");
            heading.className = "feed-day";
            heading.textContent = day.label;
            lastList = document.createElement("ul");
            lastList.className = "mini-list feed-events";
            feed.append(heading, lastList);
            lastDay = day.key;
        }
        lastList.append(renderEvent(e));
    }
}

function updateChrome(problem) {
    document.getElementById("ac-empty").textContent = shown > 0 || problem ? ""
        : isFiltered() ? "No activity matches these filters." : "Nothing has been recorded yet.";
    document.getElementById("ac-summary").textContent = shown === 0 ? ""
        : `${shown} ${shown === 1 ? "event" : "events"} shown${nextBefore !== null ? ", older ones are available" : ""}.`;
    document.getElementById("ac-more-wrap").hidden = nextBefore === null;
}

async function loadActivity(append) {
    const mine = ++latestRequest;
    writeView();

    if (!append) {
        document.getElementById("ac-feed").replaceChildren();
        shown = 0;
        nextBefore = null;
        lastDay = "";
        lastList = null;
    }

    const problem = dateProblem();
    if (problem) {
        loading = false;
        setMessage("message", problem, "error");
        updateChrome(problem);
        return;
    }

    loading = true;
    document.getElementById("ac-more").disabled = true;
    try {
        const res = await api("/api/admin/activity?" + apiQuery(append ? nextBefore : null));
        if (mine !== latestRequest) return;
        if (!res.ok) {
            setMessage("message", res.message, "error");
            nextBefore = null;
            updateChrome(res.message);
            return;
        }
        setMessage("message", "");

        appendEvents(res.data.items);
        shown += res.data.items.length;
        nextBefore = res.data.hasMore ? res.data.nextBefore : null;
        updateChrome(null);
    } catch {
        if (mine !== latestRequest) return;
        setMessage("message", "Network error. Please try again.", "error");
        nextBefore = null;
        updateChrome("error");
    } finally {
        if (mine === latestRequest) {
            loading = false;
            document.getElementById("ac-more").disabled = false;
        }
    }
}

function changeView(patch) {
    Object.assign(view, patch);
    if ("group" in patch) fillActions();            // the action list follows the kind
    if (view.targetType === "") view.targetId = "";
    document.getElementById("ac-target-id").disabled = view.targetType === "";
    if (view.targetType === "") document.getElementById("ac-target-id").value = "";
    return loadActivity(false);
}

// ---- wiring (no inline handlers: the Content-Security-Policy forbids them)

readView();
for (const id of ["ac-from", "ac-to"]) {
    document.getElementById(id).min = "2000-01-01";
    document.getElementById(id).max = "2199-12-31";
}

// The two text boxes share one timer. A half-typed username or number is not a valid filter yet: they only count
// once they have the right shape (or are empty).
function readTextFilters() {
    const user = document.getElementById("ac-user").value.trim();
    const targetId = document.getElementById("ac-target-id").value.trim();
    return {
        user: user === "" || NAME_SHAPE.test(user) ? user : view.user,
        targetId: targetId === "" || NUMBER_SHAPE.test(targetId) ? targetId : view.targetId,
    };
}

document.getElementById("ac-filters").addEventListener("submit", event => event.preventDefault());
for (const id of ["ac-user", "ac-target-id"]) {
    document.getElementById(id).addEventListener("input", () => {
        window.clearTimeout(searchTimer);
        searchTimer = window.setTimeout(() => changeView(readTextFilters()), 250);
    });
}
document.getElementById("ac-group").addEventListener("change", event => changeView({ group: event.target.value }));
document.getElementById("ac-action").addEventListener("change", event => changeView({ action: event.target.value }));
document.getElementById("ac-target-type").addEventListener("change", event => {
    const type = event.target.value;
    changeView({ targetType: type, targetId: type === "" ? "" : view.targetId });
});
document.getElementById("ac-from").addEventListener("change", event => changeView({ from: event.target.value }));
document.getElementById("ac-to").addEventListener("change", event => changeView({ to: event.target.value }));
document.getElementById("ac-reset").addEventListener("click", () => {
    window.clearTimeout(searchTimer);
    changeView({ group: "", action: "", user: "", targetType: "", targetId: "", from: "", to: "" });
    readView();
    document.getElementById("ac-group").focus();
});
document.getElementById("ac-more").addEventListener("click", () => {
    if (!loading && nextBefore !== null) loadActivity(true);
});

loadActivity(false);
