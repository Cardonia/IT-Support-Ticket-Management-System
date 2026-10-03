// /admin/notes: every technician note, newest first, read only.
// Served by Admin/AdminEndpoints.cs after script.js (api, setMessage) and admin-common.js (adminLink, formatWhen,
// localDayToIso). The filters live in the address bar, so reload, back and "copy link" keep the view.
// "Show older notes" asks for the page after the last id we hold (keyset), so notes added meanwhile never shift it.
// Everything is drawn with textContent / createElement, never innerHTML: a note is another person's text.

const PAGE_SIZE = 25;
const NAME_SHAPE = /^[A-Za-z0-9_]{1,14}$/;
const NUMBER_SHAPE = /^[0-9]{1,18}$/;
const DATE_SHAPE = /^\d{4}-\d{2}-\d{2}$/;

const view = { q: "", author: "", ticket: "", from: "", to: "" };
let latestRequest = 0;       // only the newest answer may draw (quick typing can answer out of order)
let searchTimer = 0;
let shown = 0;               // notes on the page now
let nextBefore = null;       // cursor for "Show older notes"; null when there is nothing older
let loading = false;

// ---- state <-> address bar

function readView() {
    const p = new URLSearchParams(window.location.search);
    view.q = (p.get("q") ?? "").slice(0, 50);
    view.author = NAME_SHAPE.test(p.get("author") ?? "") ? p.get("author") : "";
    view.ticket = NUMBER_SHAPE.test(p.get("ticket") ?? "") ? p.get("ticket") : "";
    view.from = DATE_SHAPE.test(p.get("from") ?? "") ? p.get("from") : "";
    view.to = DATE_SHAPE.test(p.get("to") ?? "") ? p.get("to") : "";

    document.getElementById("nt-q").value = view.q;
    document.getElementById("nt-author").value = view.author;
    document.getElementById("nt-ticket").value = view.ticket;
    document.getElementById("nt-from").value = view.from;
    document.getElementById("nt-to").value = view.to;
}

function writeView() {
    const p = new URLSearchParams();
    for (const key of ["q", "author", "ticket", "from", "to"]) if (view[key]) p.set(key, view[key]);
    const qs = p.toString();
    window.history.replaceState(null, "", window.location.pathname + (qs ? "?" + qs : ""));
}

function isFiltered() {
    return Boolean(view.q || view.author || view.ticket || view.from || view.to);
}

// null when the dates are fine, else the sentence to show
function dateProblem() {
    if (view.from && !localDayToIso(view.from)) return "From date must be a date between the years 2000 and 2199.";
    if (view.to && !localDayToIso(view.to)) return "To date must be a date between the years 2000 and 2199.";
    if (view.from && view.to && view.from > view.to) return "The From date is after the To date.";
    return null;
}

function apiQuery(before) {
    const p = new URLSearchParams({ limit: String(PAGE_SIZE) });
    if (view.q) p.set("q", view.q);
    if (view.author) p.set("author", view.author);
    if (view.ticket) p.set("ticket", view.ticket);
    if (view.from) p.set("from", localDayToIso(view.from));
    if (view.to) p.set("to", localDayToIso(view.to, 1));      // "to" is exclusive on the server: the day after
    if (before !== null) p.set("before", String(before));
    return p.toString();
}

// ---- list

function renderNote(n) {
    const meta = document.createElement("div");
    meta.className = "note-meta";

    const who = document.createElement("strong");
    if (n.authorId === null || n.authorId === undefined) who.textContent = n.author ?? "Unknown";
    else who.append(adminLink("users", n.authorId, n.author));

    const when = document.createElement("span");
    when.textContent = formatWhen(n.createdAt, "Before tracking");

    const ticket = document.createElement("span");
    ticket.append("on ", adminLink("tickets", n.ticketId, `#${n.ticketId} ${n.ticketTitle}`));
    if (n.ticketDeleted) {
        const gone = document.createElement("span");
        gone.className = "badge badge-off";
        gone.textContent = "Deleted ticket";
        ticket.append(" ", gone);
    }

    meta.append(who, when, ticket);

    const body = document.createElement("p");
    body.className = "note-body";
    body.textContent = n.body;

    const li = document.createElement("li");
    li.append(meta, body);
    return li;
}

function updateChrome(problem) {
    const empty = document.getElementById("nt-empty");
    const summary = document.getElementById("nt-summary");
    const more = document.getElementById("nt-more-wrap");

    empty.textContent = shown > 0 || problem ? ""
        : isFiltered() ? "No notes match these filters." : "No notes yet.";
    summary.textContent = shown === 0 ? ""
        : `${shown} ${shown === 1 ? "note" : "notes"} shown${nextBefore !== null ? ", older ones are available" : ""}.`;
    more.hidden = nextBefore === null;
}

async function loadNotes(append) {
    const list = document.getElementById("nt-list");
    const mine = ++latestRequest;
    writeView();

    if (!append) {
        list.replaceChildren();
        shown = 0;
        nextBefore = null;
    }

    const problem = dateProblem();
    if (problem) {
        loading = false;
        setMessage("message", problem, "error");
        updateChrome(problem);
        return;
    }

    loading = true;
    document.getElementById("nt-more").disabled = true;
    try {
        const res = await api("/api/admin/notes?" + apiQuery(append ? nextBefore : null));
        if (mine !== latestRequest) return;
        if (!res.ok) {
            setMessage("message", res.message, "error");
            nextBefore = null;
            updateChrome(res.message);
            return;
        }
        setMessage("message", "");

        for (const n of res.data.items) list.append(renderNote(n));
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
            document.getElementById("nt-more").disabled = false;
        }
    }
}

function changeView(patch) {
    Object.assign(view, patch);
    return loadNotes(false);
}

// ---- wiring (no inline handlers: the Content-Security-Policy forbids them)

readView();
for (const id of ["nt-from", "nt-to"]) {
    document.getElementById(id).min = "2000-01-01";
    document.getElementById(id).max = "2199-12-31";
}

// The text boxes share one timer, and when it fires it reads all of them. A half-typed username or number is not a
// valid filter yet: those boxes only count once they have the right shape (or are empty).
function readTextFilters() {
    const q = document.getElementById("nt-q").value.trim();
    const author = document.getElementById("nt-author").value.trim();
    const ticket = document.getElementById("nt-ticket").value.trim();
    return {
        q,
        author: author === "" || NAME_SHAPE.test(author) ? author : view.author,
        ticket: ticket === "" || NUMBER_SHAPE.test(ticket) ? ticket : view.ticket,
    };
}

document.getElementById("nt-filters").addEventListener("submit", event => event.preventDefault());
for (const id of ["nt-q", "nt-author", "nt-ticket"]) {
    document.getElementById(id).addEventListener("input", () => {
        window.clearTimeout(searchTimer);
        searchTimer = window.setTimeout(() => changeView(readTextFilters()), 250);
    });
}
document.getElementById("nt-from").addEventListener("change", event => changeView({ from: event.target.value }));
document.getElementById("nt-to").addEventListener("change", event => changeView({ to: event.target.value }));
document.getElementById("nt-reset").addEventListener("click", () => {
    window.clearTimeout(searchTimer);
    changeView({ q: "", author: "", ticket: "", from: "", to: "" });
    readView();
    document.getElementById("nt-q").focus();
});
document.getElementById("nt-more").addEventListener("click", () => {
    if (!loading && nextBefore !== null) loadNotes(true);
});

loadNotes(false);
