// /admin/tickets: search, filter, sort and page through every ticket; create one for an employee.
// Served by Admin/AdminEndpoints.cs after script.js (api, setMessage, statusBadge, priorityLabel) and
// admin-common.js (badges, formatWhen). The state lives in the address bar, so reload, back and "copy link" keep the view.
// Everything is drawn with textContent / createElement, never innerHTML: titles and usernames are other people's text.

const PAGE_SIZE = 25;
const DEFAULT_SORT = "id:desc";
const STATUSES = ["Open", "In Progress", "Resolved"];
const PRIORITIES = ["High", "Medium", "Low"];
const NAME_SHAPE = /^[A-Za-z0-9_]{1,14}$/;

const view = { q: "", status: "", priority: "", creator: "", assignee: "", deleted: "exclude", sort: DEFAULT_SORT, offset: 0 };
let latestRequest = 0;       // only the newest answer may draw (quick typing can answer out of order)
let searchTimer = 0;

// ---- state <-> address bar

function readView() {
    const p = new URLSearchParams(window.location.search);
    view.q = (p.get("q") ?? "").slice(0, 50);
    view.status = STATUSES.includes(p.get("status")) ? p.get("status") : "";
    view.priority = PRIORITIES.includes(p.get("priority")) ? p.get("priority") : "";
    view.creator = NAME_SHAPE.test(p.get("creator") ?? "") ? p.get("creator") : "";
    view.assignee = NAME_SHAPE.test(p.get("assignee") ?? "") ? p.get("assignee") : "";
    view.deleted = ["exclude", "include", "only"].includes(p.get("deleted")) ? p.get("deleted") : "exclude";
    const sort = p.get("sort") ?? "";
    view.sort = Array.from(document.getElementById("filter-sort").options).some(o => o.value === sort) ? sort : DEFAULT_SORT;
    const offset = Number.parseInt(p.get("offset") ?? "0", 10);
    view.offset = Number.isFinite(offset) && offset > 0 ? offset : 0;

    document.getElementById("filter-q").value = view.q;
    document.getElementById("filter-status").value = view.status;
    document.getElementById("filter-priority").value = view.priority;
    document.getElementById("filter-creator").value = view.creator;
    document.getElementById("filter-assignee").value = view.assignee;
    document.getElementById("filter-deleted").value = view.deleted;
    document.getElementById("filter-sort").value = view.sort;
}

function writeView() {
    const p = new URLSearchParams();
    if (view.q) p.set("q", view.q);
    if (view.status) p.set("status", view.status);
    if (view.priority) p.set("priority", view.priority);
    if (view.creator) p.set("creator", view.creator);
    if (view.assignee) p.set("assignee", view.assignee);
    if (view.deleted !== "exclude") p.set("deleted", view.deleted);
    if (view.sort !== DEFAULT_SORT) p.set("sort", view.sort);
    if (view.offset > 0) p.set("offset", String(view.offset));
    const qs = p.toString();
    window.history.replaceState(null, "", window.location.pathname + (qs ? "?" + qs : ""));
}

function apiQuery() {
    const [sort, dir] = view.sort.split(":");
    const p = new URLSearchParams({ sort, dir, deleted: view.deleted, limit: String(PAGE_SIZE), offset: String(view.offset) });
    if (view.q) p.set("q", view.q);
    if (view.status) p.set("status", view.status);
    if (view.priority) p.set("priority", view.priority);
    if (view.creator) p.set("creator", view.creator);
    if (view.assignee) p.set("assignee", view.assignee);
    return p.toString();
}

// ---- list

// One <td> with the label the phone layout shows above the value
function cell(label, content) {
    const td = document.createElement("td");
    td.dataset.label = label;
    if (content instanceof Node) td.append(content);
    else td.textContent = content;
    return td;
}

// A username that links to the user's page; "Nobody" when there is none
function userLink(id, name, none) {
    if (id === null || id === undefined) return none;
    const a = document.createElement("a");
    a.href = "/admin/users/" + encodeURIComponent(id);
    a.textContent = name;
    return a;
}

function renderTickets(items) {
    const body = document.getElementById("tickets-body");
    body.replaceChildren();

    for (const t of items) {
        const link = document.createElement("a");
        link.href = "/admin/tickets/" + encodeURIComponent(t.id);
        link.textContent = `#${t.id} ${t.title}`;

        const name = document.createElement("th");
        name.scope = "row";
        name.append(link);
        if (t.deleted) {
            const gone = document.createElement("span");
            gone.className = "badge badge-off";
            gone.textContent = "Deleted";
            name.append(" ", gone);
        }

        const row = document.createElement("tr");
        if (t.deleted) row.className = "row-deleted";
        row.append(
            name,
            cell("Status", statusBadge(t.status)),
            cell("Priority", priorityLabel(t.priority)),
            cell("Reported by", userLink(t.creatorId, t.creator, "")),
            cell("Assigned to", userLink(t.assigneeId, t.assignee, "Nobody")),
            cell("Changed", formatWhen(t.updatedAt)));
        body.append(row);
    }
}

async function loadTickets() {
    const mine = ++latestRequest;
    writeView();

    const res = await api("/api/admin/tickets?" + apiQuery());
    if (mine !== latestRequest) return;

    const summary = document.getElementById("list-summary");
    const empty = document.getElementById("list-empty");
    const wrap = document.getElementById("table-wrap");
    const pager = document.getElementById("pager");

    if (!res.ok) {
        renderTickets([]);
        wrap.hidden = true;
        pager.hidden = true;
        summary.textContent = "";
        empty.textContent = "";
        setMessage("message", res.message, "error");
        return;
    }
    setMessage("message", "");

    const { total, limit, offset, items } = res.data;

    // The last page can disappear (a ticket was deleted elsewhere): step back to the one that exists
    if (items.length === 0 && total > 0 && offset > 0) {
        view.offset = Math.max(0, Math.floor((total - 1) / limit) * limit);
        return loadTickets();
    }

    renderTickets(items);
    wrap.hidden = items.length === 0;

    const filtered = Boolean(view.q || view.status || view.priority || view.creator || view.assignee || view.deleted !== "exclude");
    empty.textContent = items.length > 0 ? ""
        : filtered ? "No tickets match these filters." : "No tickets yet.";

    summary.textContent = total === 0 ? ""
        : `Showing ${offset + 1}-${offset + items.length} of ${total} ${total === 1 ? "ticket" : "tickets"}`;

    const pages = Math.max(1, Math.ceil(total / limit));
    pager.hidden = pages <= 1;
    document.getElementById("page-info").textContent = `Page ${Math.floor(offset / limit) + 1} of ${pages}`;
    document.getElementById("page-prev").disabled = offset === 0;
    document.getElementById("page-next").disabled = offset + limit >= total;
}

function changeView(patch) {
    Object.assign(view, patch, { offset: patch.offset ?? 0 });   // any filter change goes back to the first page
    return loadTickets();
}

// ---- new ticket

function openCreate(open) {
    const panel = document.getElementById("tk-new-panel");
    panel.hidden = !open;
    document.getElementById("tk-new-button").setAttribute("aria-expanded", String(open));
    setMessage("tk-new-message", "");
    if (open) document.getElementById("tk-new-for").focus();
    else {
        document.getElementById("tk-new-form").reset();
        document.getElementById("tk-new-button").focus();
    }
}

async function createTicket() {
    const button = document.getElementById("tk-new-submit");
    if (button.disabled) return;

    const createdFor = trimLikeServer(document.getElementById("tk-new-for").value);
    const title = trimLikeServer(document.getElementById("tk-new-title-input").value);
    const description = trimLikeServer(document.getElementById("tk-new-description").value);
    const priority = document.getElementById("tk-new-priority").value;

    setMessage("tk-new-message", "");
    // The server's own texts; it stays the authority
    const problem = !NAME_SHAPE.test(createdFor) ? ["tk-new-for", "Enter the username of an active employee."]
        : title.length === 0 ? ["tk-new-title-input", "Title is required."]
        : title.length > 100 ? ["tk-new-title-input", "Title must be at most 100 characters."]
        : description.length === 0 ? ["tk-new-description", "Description is required."]
        : description.length > 2000 ? ["tk-new-description", "Description must be at most 2000 characters."]
        : null;
    if (problem) {
        setMessage("tk-new-message", problem[1], "error");
        document.getElementById(problem[0]).focus();
        return;
    }

    button.disabled = true;
    try {
        const res = await api("/api/admin/tickets", { method: "POST", body: { createdFor, title, description, priority } });
        if (!res.ok) {
            setMessage("tk-new-message", res.message, "error");
            return;
        }
        openCreate(false);
        setMessage("message", `Ticket #${res.data.id} was created for ${createdFor}.`, "success");
        await loadTickets();
    } catch {
        setMessage("tk-new-message", "Network error. Please try again.", "error");
    } finally {
        button.disabled = false;
    }
}

// ---- wiring (no inline handlers: the Content-Security-Policy forbids them)

readView();

// The three text boxes share one timer, and when it fires it reads all of them: a quick change in two boxes is never lost.
// A half-typed username is not a valid filter yet: the name boxes only count once they have the shape of a username
// (or are empty).
function readTextFilters() {
    const q = document.getElementById("filter-q").value.trim();
    const creator = document.getElementById("filter-creator").value.trim();
    const assignee = document.getElementById("filter-assignee").value.trim();
    return {
        q,
        creator: creator === "" || NAME_SHAPE.test(creator) ? creator : view.creator,
        assignee: assignee === "" || NAME_SHAPE.test(assignee) ? assignee : view.assignee,
    };
}
function picked(id, key) {
    document.getElementById(id).addEventListener("change", event => changeView({ [key]: event.target.value }));
}

document.getElementById("filters").addEventListener("submit", event => event.preventDefault());
for (const id of ["filter-q", "filter-creator", "filter-assignee"]) {
    document.getElementById(id).addEventListener("input", () => {
        window.clearTimeout(searchTimer);
        searchTimer = window.setTimeout(() => changeView(readTextFilters()), 250);
    });
}
picked("filter-status", "status");
picked("filter-priority", "priority");
picked("filter-deleted", "deleted");
picked("filter-sort", "sort");
document.getElementById("page-prev").addEventListener("click", () => changeView({ offset: Math.max(0, view.offset - PAGE_SIZE) }));
document.getElementById("page-next").addEventListener("click", () => changeView({ offset: view.offset + PAGE_SIZE }));

document.getElementById("tk-new-button").addEventListener("click", () =>
    openCreate(document.getElementById("tk-new-panel").hidden));
document.getElementById("tk-new-cancel").addEventListener("click", () => openCreate(false));
document.getElementById("tk-new-form").addEventListener("submit", event => { event.preventDefault(); createTicket(); });

loadTickets();
