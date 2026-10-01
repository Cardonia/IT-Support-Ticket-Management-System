async function register() {

    let username = document.getElementById("username").value;
    let password = document.getElementById("password").value;
    let repassword = document.getElementById("repassword").value;

    let message = document.getElementById("message");

    // Check username
    if (username.length <= 3 || username.length >= 15) {
        message.textContent = "Username must be 4-14 characters.";
        return;
    }

    // Check password
    if (password.length === 0) {
        message.textContent = "Please write a password.";
        return;
    }

    if (password.length <= 7 || password.length >= 16) {
        message.textContent = "Password must be 8-15 characters.";
        return;
    }

    // Check passwords
    if (password !== repassword) {
        message.textContent = "Passwords do not match.";
        return;
    }

    // Send data to ASP.NET
    const response = await fetch("/api/register", {
        method: "POST",

        headers: {
            "Content-Type": "application/json"
        },

        body: JSON.stringify({
            username: username,
            password: password
        })
    });

    // Read server response
    const result = await response.text();

    // Show server message
    message.textContent = result;

    if (response.ok) {
    window.location.href = "/home.html";
    } else {
        message.textContent = result;
    }
}


// Reads a server message whether it is a JSON string, {error: ...}, or empty (429).
async function readMessage(res) {
    const text = await res.text();
    if (!text) return res.status === 429 ? "Too many attempts. Try again in a minute." : "Request failed.";
    try {
        const v = JSON.parse(text);
        return typeof v === "string" ? v : (v.error ?? text);
    } catch {
        return text;
    }
}

async function login() {
    const message = document.getElementById("message");
    const username = document.getElementById("username").value;
    const password = document.getElementById("password").value;

    try {
        const res = await fetch("/api/login", {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ username, password })
        });

        if (res.ok) {
            window.location.href = "/home.html";
            return;
        }

        message.textContent = await readMessage(res);
    } catch {
        message.textContent = "Network error. Please try again.";
    }
}


async function logout() {
    const message = document.getElementById("message");

    try {
        const res = await fetch("/api/logout", { method: "POST" });

        if (res.ok) {
            window.location.href = "/";
            return;
        }

        message.textContent = await readMessage(res);
    } catch {
        message.textContent = "Network error. Please try again.";
    }
}


// Shared fetch helper: JSON in, JSON out, clean error messages.
// Returns { ok, status, data } on success or { ok: false, status, message } on failure.
// A 401 sends the user to "/" (session expired or logged out elsewhere).
// Pass redirectOn401: false for calls where 401 is an expected answer (e.g. wrong password).
// Network failures throw, so callers wrap it in try/catch.
async function api(url, { method = "GET", body, redirectOn401 = true } = {}) {
    const init = { method, headers: {} };
    if (body !== undefined) {
        init.headers["Content-Type"] = "application/json";
        init.body = JSON.stringify(body);
    }

    const res = await fetch(url, init);

    if (res.status === 401 && redirectOn401) {
        window.location.href = "/";
        return { ok: false, status: 401, message: "Not authenticated." };
    }

    if (!res.ok) {
        return { ok: false, status: res.status, message: await readMessage(res) };
    }

    const text = await res.text();
    let data = null;
    try { data = text ? JSON.parse(text) : null; } catch { data = text; }
    return { ok: true, status: res.status, data };
}

// Heading that home.html shows for each role
const HOME_TITLES = { Employee: "My Tickets", Technician: "All Tickets" };

// home.html: show who is logged in and what their role sees (textContent only, never innerHTML)
async function loadMe() {
    const greeting = document.getElementById("greeting");
    const title = document.getElementById("section-title");
    const empty = document.getElementById("section-empty");
    const createLink = document.getElementById("create-ticket-link");
    const filterBox = document.getElementById("filter-box");
    const message = document.getElementById("message");

    try {
        const res = await api("/api/me");
        if (!res.ok) {
            if (res.status !== 401) message.textContent = res.message;   // 401 already redirected
            return;
        }
        const { username, role } = res.data;
        greeting.textContent = "Hello, " + username + " (" + role + ")";
        title.textContent = HOME_TITLES[role] ?? "Tickets";
        createLink.hidden = role !== "Employee";      // only Employees can create tickets
        filterBox.hidden = role !== "Technician";     // only Technicians can filter by status

        if (role === "Employee") {
            await loadTicketList("/api/tickets/mine");
        } else if (role === "Technician") {
            await loadTicketList("/api/tickets");
        } else {
            empty.textContent = "No tickets yet.";
        }
    } catch {
        message.textContent = "Network error. Please try again.";
    }
}

// Draws ticket rows into a <ul>; each row links to ticket.html?id=. Only textContent is used,
// never innerHTML, so a ticket title can never inject markup.
function renderTickets(list, tickets) {
    list.replaceChildren();
    for (const t of tickets) {
        const title = document.createElement("div");
        title.className = "ticket-title";
        title.textContent = t.title;

        const meta = document.createElement("div");
        meta.className = "ticket-meta";
        const parts = [t.priority, t.status];
        if (t.createdBy) parts.push("by " + t.createdBy);        // only the technician list has it
        parts.push(new Date(t.createdAt).toLocaleDateString());
        meta.textContent = parts.join(" \u00b7 ");

        const link = document.createElement("a");
        link.className = "ticket-link";
        link.href = "/ticket.html?id=" + encodeURIComponent(t.id);
        link.append(title, meta);

        const li = document.createElement("li");
        li.append(link);
        list.append(li);
    }
}

// home.html: fetch a ticket list (own tickets for Employees, all tickets for Technicians) and draw it.
// Errors bubble up to loadMe()'s try/catch.
let latestListRequest = 0;    // only the newest request may draw (quick filter changes can answer out of order)

async function loadTicketList(url, emptyText = "No tickets yet.") {
    const list = document.getElementById("ticket-list");
    const empty = document.getElementById("section-empty");
    const message = document.getElementById("message");

    const mine = ++latestListRequest;
    const res = await api(url);
    if (mine !== latestListRequest) return;            // a newer request replaced this one

    if (!res.ok) {
        list.replaceChildren();                        // never leave an old list under an error message
        empty.textContent = "";
        if (res.status !== 401) message.textContent = res.message;   // 401 already redirected
        return;
    }

    renderTickets(list, res.data);
    empty.textContent = res.data.length === 0 ? emptyText : "";
}

// home.html (Technician): reload the list for the status chosen in the dropdown ("" = all)
async function filterTickets() {
    const status = document.getElementById("status-filter").value;
    const message = document.getElementById("message");
    message.textContent = "";

    try {
        if (status === "") {
            await loadTicketList("/api/tickets");
        } else {
            await loadTicketList("/api/tickets?status=" + encodeURIComponent(status),
                "No tickets with this status.");
        }
    } catch {
        message.textContent = "Network error. Please try again.";
    }
}

// One "Label: value" row in the details list (textContent only)
function addDetail(list, label, value) {
    const name = document.createElement("strong");
    name.textContent = label + ": ";

    const li = document.createElement("li");
    li.append(name, document.createTextNode(value));
    list.append(li);
}

// ticket.html: the ticket id from the address (?id=12), or null when it is not a plain number
function getTicketId() {
    const id = new URLSearchParams(window.location.search).get("id") ?? "";
    return /^[0-9]{1,18}$/.test(id) ? id : null;
}

// ticket.html: view of the ticket whose id is in the address (?id=12).
// Safe to call again to refresh (it clears what it drew before).
async function loadTicket() {
    const title = document.getElementById("ticket-title");
    const description = document.getElementById("ticket-description");
    const details = document.getElementById("ticket-details");
    const takeButton = document.getElementById("take-button");
    const resolveButton = document.getElementById("resolve-button");
    const message = document.getElementById("message");

    message.textContent = "";
    const id = getTicketId();
    if (id === null) {
        message.textContent = "Ticket not found.";
        return;
    }

    try {
        const res = await api("/api/tickets/" + id);
        if (!res.ok) {
            if (res.status !== 401) {                          // 401 already redirected
                message.textContent = res.status === 404 ? "Ticket not found." : res.message;
            }
            return;
        }

        const t = res.data;
        title.textContent = t.title;
        description.textContent = t.description;
        details.replaceChildren();
        addDetail(details, "Status", t.status);
        addDetail(details, "Priority", t.priority);
        addDetail(details, "Created by", t.createdBy);
        addDetail(details, "Created", new Date(t.createdAt).toLocaleString());
        addDetail(details, "Assigned to", t.assignedTo ?? "Unassigned");
        addDetail(details, "Updated", new Date(t.updatedAt).toLocaleString());
        if (t.resolvedAt) addDetail(details, "Resolved", new Date(t.resolvedAt).toLocaleString());
        takeButton.hidden = !t.canTake;                    // the server says whether this caller can take it now
        resolveButton.hidden = !t.canResolve;              // ... and whether this caller can resolve it now
    } catch {
        message.textContent = "Network error. Please try again.";
    }
}

// ticket.html: shared by the Take and Resolve buttons. Sends one request, then redraws the ticket
// with its real new state and shows a message. `texts` maps a status code (or "ok") to the message;
// a status without an entry shows the server's own message.
async function ticketAction(button, path, options, texts) {
    if (button.disabled) return;                       // one request at a time

    const id = getTicketId();
    if (id === null) return;

    const message = document.getElementById("message");
    button.disabled = true;
    message.textContent = "";

    let text = "";
    try {
        const res = await api("/api/tickets/" + id + path, options);

        if (res.ok) {
            text = texts.ok;
        } else if (res.status === 401) {               // already redirected to "/"
            return;
        } else {
            text = texts[res.status] ?? res.message;
        }

        await loadTicket();                            // show the real state (this also hides the button when needed)
        message.textContent = text;                    // after loadTicket(), because it clears #message
    } catch {
        message.textContent = "Network error. Please try again.";
    } finally {
        button.disabled = false;
    }
}

// ticket.html (Technician, Open ticket): take the ticket
function takeTicket() {
    return ticketAction(document.getElementById("take-button"), "/take", { method: "POST" }, {
        ok: "You took this ticket.",
        409: "This ticket was already taken.",
        404: "Ticket not found.",
        403: "Only technicians can take tickets.",
    });
}

// ticket.html (the technician who took it, In Progress): mark the ticket resolved
function resolveTicket() {
    return ticketAction(document.getElementById("resolve-button"), "/status",
        { method: "PATCH", body: { status: "Resolved" } }, {
        ok: "Ticket marked as resolved.",
        409: "Only an In Progress ticket can be resolved.",
        404: "Ticket not found.",
    });                                                // 403 shows the server's message ("Only the technician who took this ticket ...")
}

// create-ticket.html: send the form to POST /api/tickets. The server validates and is the authority;
// the maxlength attributes in the HTML only stop typing past the same limits.
async function createTicket() {
    const button = document.getElementById("submit-ticket");
    const message = document.getElementById("message");
    if (button.disabled) return;                      // one request at a time (no duplicate tickets)

    const title = document.getElementById("title").value;
    const description = document.getElementById("description").value;
    const priority = document.getElementById("priority").value;

    button.disabled = true;
    message.textContent = "";

    try {
        const res = await api("/api/tickets", { method: "POST", body: { title, description, priority } });

        if (res.ok) {
            window.location.href = "/home.html";      // button stays disabled while the page changes
            return;
        }

        if (res.status !== 401) {                      // 401 already redirected to "/"
            message.textContent = res.status === 403
                ? "Only employees can create tickets."
                : res.message;
        }
    } catch {
        message.textContent = "Network error. Please try again.";
    }

    button.disabled = false;
}

// Run automatically on pages that have the greeting element (no inline script needed)
if (document.getElementById("greeting")) loadMe();
if (document.getElementById("ticket-title")) loadTicket();
