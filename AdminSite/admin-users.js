// /admin/users: search, filter, sort and page through every account; create a new one.
// Served by Admin/AdminEndpoints.cs after script.js (api, setMessage) and admin-common.js (badges, formatWhen).
// The state lives in the address bar (?q=&role=&status=&sort=&offset=), so reload, back and "copy link" keep the view.
// Everything is drawn with textContent / createElement, never innerHTML.

const PAGE_SIZE = 25;

const view = { q: "", role: "", status: "", sort: "name:asc", offset: 0 };
let latestRequest = 0;       // only the newest answer may draw (quick typing can answer out of order)
let searchTimer = 0;

// ---- state <-> address bar

function readView() {
    const p = new URLSearchParams(window.location.search);
    view.q = (p.get("q") ?? "").slice(0, 50);
    view.role = ["Employee", "Technician", "Admin"].includes(p.get("role")) ? p.get("role") : "";
    view.status = ["active", "disabled"].includes(p.get("status")) ? p.get("status") : "";
    const sort = p.get("sort") ?? "";
    view.sort = Array.from(document.getElementById("filter-sort").options).some(o => o.value === sort) ? sort : "name:asc";
    const offset = Number.parseInt(p.get("offset") ?? "0", 10);
    view.offset = Number.isFinite(offset) && offset > 0 ? offset : 0;

    document.getElementById("filter-q").value = view.q;
    document.getElementById("filter-role").value = view.role;
    document.getElementById("filter-status").value = view.status;
    document.getElementById("filter-sort").value = view.sort;
}

function writeView() {
    const p = new URLSearchParams();
    if (view.q) p.set("q", view.q);
    if (view.role) p.set("role", view.role);
    if (view.status) p.set("status", view.status);
    if (view.sort !== "name:asc") p.set("sort", view.sort);
    if (view.offset > 0) p.set("offset", String(view.offset));
    const qs = p.toString();
    window.history.replaceState(null, "", window.location.pathname + (qs ? "?" + qs : ""));
}

function apiQuery() {
    const [sort, dir] = view.sort.split(":");
    const p = new URLSearchParams({ sort, dir, limit: String(PAGE_SIZE), offset: String(view.offset) });
    if (view.q) p.set("q", view.q);
    if (view.role) p.set("role", view.role);
    if (view.status) p.set("status", view.status);
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

function renderUsers(items) {
    const body = document.getElementById("users-body");
    body.replaceChildren();

    for (const u of items) {
        const link = document.createElement("a");
        link.href = "/admin/users/" + encodeURIComponent(u.id);
        link.textContent = u.username;

        const name = document.createElement("th");
        name.scope = "row";
        name.append(link);

        const row = document.createElement("tr");
        row.append(
            name,
            cell("Role", roleBadge(u.role)),
            cell("Status", activeBadge(u.isActive)),
            cell("Created", formatWhen(u.createdAt, "Before tracking")),
            cell("Last login", formatWhen(u.lastLogin, "Not recorded")));
        body.append(row);
    }
}

async function loadUsers() {
    const mine = ++latestRequest;
    writeView();

    const res = await api("/api/admin/users?" + apiQuery());
    if (mine !== latestRequest) return;

    const summary = document.getElementById("list-summary");
    const empty = document.getElementById("list-empty");
    const wrap = document.getElementById("table-wrap");
    const pager = document.getElementById("pager");

    if (!res.ok) {
        renderUsers([]);
        wrap.hidden = true;
        pager.hidden = true;
        summary.textContent = "";
        empty.textContent = "";
        setMessage("message", res.message, "error");
        return;
    }
    setMessage("message", "");

    const { total, limit, offset, items } = res.data;

    // The last page can disappear (someone was deactivated elsewhere): step back to the one that exists
    if (items.length === 0 && total > 0 && offset > 0) {
        view.offset = Math.max(0, Math.floor((total - 1) / limit) * limit);
        return loadUsers();
    }

    renderUsers(items);
    wrap.hidden = items.length === 0;

    const filtered = Boolean(view.q || view.role || view.status);
    empty.textContent = items.length > 0 ? ""
        : filtered ? "No users match these filters." : "No users yet.";

    summary.textContent = total === 0 ? ""
        : `Showing ${offset + 1}-${offset + items.length} of ${total} ${total === 1 ? "user" : "users"}`;

    const pages = Math.max(1, Math.ceil(total / limit));
    pager.hidden = pages <= 1;
    document.getElementById("page-info").textContent = `Page ${Math.floor(offset / limit) + 1} of ${pages}`;
    document.getElementById("page-prev").disabled = offset === 0;
    document.getElementById("page-next").disabled = offset + limit >= total;
}

function changeView(patch) {
    Object.assign(view, patch, { offset: patch.offset ?? 0 });   // any filter change goes back to the first page
    return loadUsers();
}

// ---- new user

function openCreate(open) {
    const panel = document.getElementById("new-user-panel");
    panel.hidden = !open;
    document.getElementById("new-user-button").setAttribute("aria-expanded", String(open));
    setMessage("new-user-message", "");
    if (open) {
        document.getElementById("new-user-result").hidden = true;
        document.getElementById("new-username").focus();
    } else {
        document.getElementById("new-user-form").reset();
        document.getElementById("new-user-button").focus();
    }
}

async function createUser() {
    const button = document.getElementById("new-user-submit");
    if (button.disabled) return;

    const username = trimLikeServer(document.getElementById("new-username").value);
    const role = document.getElementById("new-role").value;

    setMessage("new-user-message", "");
    if (!/^[A-Za-z0-9_]{4,14}$/.test(username)) {          // the server's own text; it stays the authority
        setMessage("new-user-message", "Username must be 4-14 characters: letters, numbers or underscore.", "error");
        document.getElementById("new-username").focus();
        return;
    }

    button.disabled = true;
    try {
        const res = await api("/api/admin/users", { method: "POST", body: { username, role } });
        if (!res.ok) {
            setMessage("new-user-message", res.message, "error");
            return;
        }

        openCreate(false);
        showTemporaryPassword(document.getElementById("new-user-result"), {
            title: `User ${res.data.username} created`,
            intro: `${res.data.username} is now ${/^[AEIOU]/i.test(res.data.role) ? "an" : "a"} ${res.data.role}. Give them this temporary password and the address of the site:`,
            password: res.data.temporaryPassword,
            onDone: () => document.getElementById("new-user-button").focus()
        });
        await loadUsers();
    } catch {
        setMessage("new-user-message", "Network error. Please try again.", "error");
    } finally {
        button.disabled = false;
    }
}

// ---- wiring (no inline handlers: the Content-Security-Policy forbids them)

readView();

document.getElementById("filters").addEventListener("submit", event => event.preventDefault());
document.getElementById("filter-q").addEventListener("input", event => {
    window.clearTimeout(searchTimer);
    searchTimer = window.setTimeout(() => changeView({ q: event.target.value.trim() }), 250);
});
document.getElementById("filter-role").addEventListener("change", event => changeView({ role: event.target.value }));
document.getElementById("filter-status").addEventListener("change", event => changeView({ status: event.target.value }));
document.getElementById("filter-sort").addEventListener("change", event => changeView({ sort: event.target.value }));
document.getElementById("page-prev").addEventListener("click", () => changeView({ offset: Math.max(0, view.offset - PAGE_SIZE) }));
document.getElementById("page-next").addEventListener("click", () => changeView({ offset: view.offset + PAGE_SIZE }));

document.getElementById("new-user-button").addEventListener("click", () =>
    openCreate(document.getElementById("new-user-panel").hidden));
document.getElementById("new-user-cancel").addEventListener("click", () => openCreate(false));
document.getElementById("new-user-form").addEventListener("submit", event => { event.preventDefault(); createUser(); });

loadUsers();
