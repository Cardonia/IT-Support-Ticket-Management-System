// Removes the same characters as C#'s string.Trim(), which the server uses on the username.
// (JavaScript's own trim() differs in two characters: it also strips U+FEFF and keeps U+0085.)
function trimLikeServer(text) {
    return text.replace(/^[\t-\r \u0085\u00a0\u1680\u2000-\u200a\u2028\u2029\u202f\u205f\u3000]+|[\t-\r \u0085\u00a0\u1680\u2000-\u200a\u2028\u2029\u202f\u205f\u3000]+$/g, "");
}

// The register rules, word for word the same as the server's (AuthEndpoints.Validate), in the same order.
// Returns null when everything is fine, otherwise { field, text } (the field to focus and the message).
// The server stays the authority: this only saves a round trip and gives the same message sooner.
function checkRegisterForm(username, password, repassword) {
    username = trimLikeServer(username);               // the server trims the username too

    if (!/^[A-Za-z0-9_]{4,14}$/.test(username))
        return { field: "username", text: "Username must be 4-14 characters: letters, numbers or underscore." };

    if (password.length < 8)                           // .length counts UTF-16 units, like C#'s string.Length
        return { field: "password", text: "Password must be at least 8 characters." };

    if (new TextEncoder().encode(password).length > 72)   // bcrypt only reads the first 72 bytes
        return { field: "password", text: "Password is too long (max 72 bytes)." };

    if (password !== repassword)                       // the only rule the server cannot check (it never sees the 2nd box)
        return { field: "repassword", text: "Passwords do not match." };

    return null;
}

// register.html: runs when the form is submitted (button click or Enter key)
async function register() {
    const button = document.getElementById("register-button");
    const message = document.getElementById("message");
    if (button.disabled) return;                       // one request at a time

    const username = trimLikeServer(document.getElementById("username").value);
    const password = document.getElementById("password").value;
    const repassword = document.getElementById("repassword").value;

    message.className = "";
    message.textContent = "";

    const problem = checkRegisterForm(username, password, repassword);
    if (problem) {
        message.className = "error";
        message.textContent = problem.text;
        document.getElementById(problem.field).focus();
        return;
    }

    button.disabled = true;
    let leaving = false;
    try {
        const res = await api("/api/register", {
            method: "POST",
            body: { username, password },
            redirectOn401: false                       // register never answers 401; never bounce this page
        });

        if (res.ok) {
            leaving = true;                            // the cookie is already set by the response
            window.location.href = "/home.html";
            return;
        }

        message.className = "error";
        message.textContent = res.message;             // plain text from the server (no quotes), e.g. 409 / 429 / 500
    } catch {
        message.className = "error";
        message.textContent = "Network error. Please try again.";
    } finally {
        if (!leaving) button.disabled = false;         // stays disabled while the page changes
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

// Shared fetch helper: JSON in, JSON out, clean error messages. Every request in this app goes through it.
// Returns { ok, status, data } on success or { ok: false, status, message } on failure.
// A 401 sends the user to "/" (session expired or logged out elsewhere).
// Pass redirectOn401: false for calls where 401 is an expected answer (e.g. wrong password).
// Network failures throw, so callers wrap it in try/catch.
// It adds "X-Requested-With: fetch" to every request. The server (CsrfMiddleware.cs) refuses every
// POST / PATCH / DELETE without it: a page on another site cannot add this header, so it cannot
// make the browser send a request the server accepts, even though the cookie would travel with it.
async function api(url, { method = "GET", body, redirectOn401 = true } = {}) {
    const init = { method, headers: { "X-Requested-With": "fetch" } };
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

// login.html: runs when the form is submitted (button click or Enter key)
async function login() {
    const button = document.getElementById("login-button");
    const message = document.getElementById("message");
    if (button.disabled) return;                       // one request at a time (also saves the 10-per-minute login limit)

    const usernameBox = document.getElementById("username");
    const passwordBox = document.getElementById("password");
    const username = trimLikeServer(usernameBox.value);   // the server trims it too
    const password = passwordBox.value;                // never trimmed

    message.className = "";
    message.textContent = "";

    if (username.length === 0 || password.length === 0) {   // the server's own text, without spending a request
        message.className = "error";
        message.textContent = "Username and password are required.";
        (username.length === 0 ? usernameBox : passwordBox).focus();
        return;
    }

    button.disabled = true;
    let leaving = false;
    try {
        const res = await api("/api/login", {
            method: "POST",
            body: { username, password },
            redirectOn401: false                       // a wrong password is a 401 and must show its message here
        });

        if (res.ok) {
            leaving = true;                            // the cookie is already set by the response
            window.location.href = "/home.html";
            return;
        }

        message.className = "error";
        message.textContent = res.message;             // 400 / 401 / 429 / 500 text from the server
    } catch {
        message.className = "error";
        message.textContent = "Network error. Please try again.";
    } finally {
        if (!leaving) button.disabled = false;         // stays disabled while the page changes
    }
}

// Writes text into one of the message boxes (#message, #action-message, #note-message) and styles it:
// kind is "error" (red) or "success" (green); an empty text hides the box. textContent only, never innerHTML.
function setMessage(id, text, kind = "") {
    const box = document.getElementById(id);
    if (!box) return;
    box.className = text ? kind : "";
    box.textContent = text;
}

// Class names for the status badge and the priority marker. A Map, so an unexpected value from the
// server can never pick up a property of Object (it just gets no colour).
const STATUS_CLASSES = new Map([["Open", "badge-open"], ["In Progress", "badge-progress"], ["Resolved", "badge-resolved"]]);
const PRIORITY_CLASSES = new Map([["High", "priority-high"], ["Medium", "priority-medium"], ["Low", "priority-low"]]);

// <span class="badge badge-...">Status</span>: the word is always shown, the colour only helps
function statusBadge(status) {
    const badge = document.createElement("span");
    badge.className = "badge " + (STATUS_CLASSES.get(status) ?? "");
    badge.textContent = status;
    return badge;
}

// <span class="priority priority-...">High</span>: a dot (drawn by CSS) in the priority's colour plus the word
function priorityLabel(priority) {
    const label = document.createElement("span");
    label.className = "priority " + (PRIORITY_CLASSES.get(priority) ?? "");
    label.textContent = priority;
    return label;
}

// The top bar of the signed-in pages: who is logged in (textContent only)
function fillNav(username, role) {
    const user = document.getElementById("nav-user");
    const roleBox = document.getElementById("nav-role");
    if (user) user.textContent = username;
    if (roleBox) roleBox.textContent = role;
}

// create-ticket.html and ticket.html: fill the top bar. A failure is silent (the page's own calls
// show their errors, and a 401 already sent the user to "/").
async function loadNav() {
    try {
        const res = await api("/api/me");
        if (res.ok) fillNav(res.data.username, res.data.role);
    } catch {
        // the top bar just stays without a name
    }
}

// home.html: end the session, then go to the start page
async function logout() {
    const button = document.getElementById("logout-button");
    if (button.disabled) return;

    button.disabled = true;
    setMessage("message", "");

    let leaving = false;
    try {
        const res = await api("/api/logout", { method: "POST", redirectOn401: false });

        if (res.ok) {
            leaving = true;
            window.location.href = "/";
            return;
        }

        setMessage("message", res.message, "error");
    } catch {
        setMessage("message", "Network error. Please try again.", "error");
    } finally {
        if (!leaving) button.disabled = false;
    }
}

// Heading that home.html shows for each role
const HOME_TITLES = { Employee: "My Tickets", Technician: "All Tickets" };

// home.html: show who is logged in (top bar) and what their role sees (textContent only, never innerHTML)
async function loadMe() {
    const title = document.getElementById("section-title");
    const empty = document.getElementById("section-empty");
    const createLink = document.getElementById("create-ticket-link");
    const filterBox = document.getElementById("filter-box");

    try {
        const res = await api("/api/me");
        if (!res.ok) {
            if (res.status !== 401) setMessage("message", res.message, "error");   // 401 already redirected
            return;
        }
        const { username, role } = res.data;
        if (role === "Admin") {                       // an Admin has no ticket list of their own
            window.location.replace("/admin");
            return;
        }
        fillNav(username, role);
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
        setMessage("message", "Network error. Please try again.", "error");
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

        const main = document.createElement("div");
        main.className = "ticket-main";
        main.append(title);
        if (t.createdBy) {                                       // only the technician list has it
            const by = document.createElement("div");
            by.className = "ticket-by";
            by.textContent = "by " + t.createdBy;
            main.append(by);
        }

        const date = document.createElement("span");
        date.className = "ticket-date";
        date.textContent = new Date(t.createdAt).toLocaleDateString();

        const meta = document.createElement("div");
        meta.className = "ticket-meta";
        meta.append(statusBadge(t.status), priorityLabel(t.priority), date);

        const link = document.createElement("a");
        link.className = "ticket-link";
        link.href = "/ticket.html?id=" + encodeURIComponent(t.id);
        link.append(main, meta);

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

    const mine = ++latestListRequest;
    const res = await api(url);
    if (mine !== latestListRequest) return;            // a newer request replaced this one

    if (!res.ok) {
        list.replaceChildren();                        // never leave an old list under an error message
        empty.textContent = "";
        if (res.status !== 401) setMessage("message", res.message, "error");   // 401 already redirected
        return;
    }

    renderTickets(list, res.data);
    empty.textContent = res.data.length === 0 ? emptyText : "";
}

// home.html (Technician): reload the list for the status chosen in the dropdown ("" = all)
async function filterTickets() {
    const status = document.getElementById("status-filter").value;
    setMessage("message", "");

    try {
        if (status === "") {
            await loadTicketList("/api/tickets");
        } else {
            await loadTicketList("/api/tickets?status=" + encodeURIComponent(status),
                "No tickets with this status.");
        }
    } catch {
        setMessage("message", "Network error. Please try again.", "error");
    }
}

// One label / value pair in the details list <dl> (textContent only)
function addDetail(list, label, value) {
    const term = document.createElement("dt");
    term.textContent = label;

    const definition = document.createElement("dd");
    definition.textContent = value;

    const row = document.createElement("div");
    row.append(term, definition);
    list.append(row);
}

// ticket.html: the ticket id from the address (?id=12), or null when it is not a plain number
function getTicketId() {
    const id = new URLSearchParams(window.location.search).get("id") ?? "";
    return /^[0-9]{1,18}$/.test(id) ? id : null;
}

// ticket.html: the notes under the details (oldest first). Everyone who can see the ticket sees its notes.
// Safe to call again to refresh. Everything is written with textContent, so HTML in a note is shown as text.
async function loadNotes(id) {
    const box = document.getElementById("notes-box");
    const list = document.getElementById("note-list");
    const empty = document.getElementById("notes-empty");

    try {
        const res = await api("/api/tickets/" + id + "/notes");
        if (res.status === 401) return;                    // already redirected to "/"

        list.replaceChildren();
        if (!res.ok) {
            empty.textContent = res.status === 404 ? "Ticket not found." : res.message;
        } else {
            for (const n of res.data) {
                const li = document.createElement("li");

                const meta = document.createElement("div");
                meta.className = "note-meta";
                const who = document.createElement("strong");
                who.textContent = n.author;
                const when = document.createElement("span");
                when.textContent = new Date(n.createdAt).toLocaleString();
                meta.append(who, when);

                const body = document.createElement("p");
                body.className = "note-body";
                body.textContent = n.body;

                li.append(meta, body);
                list.append(li);
            }
            empty.textContent = res.data.length === 0 ? "No notes yet." : "";
        }
        box.hidden = false;
    } catch {
        list.replaceChildren();
        empty.textContent = "Network error. Please try again.";
        box.hidden = false;
    }
}

// ticket.html: view of the ticket whose id is in the address (?id=12).
// Safe to call again to refresh (it clears what it drew before).
async function loadTicket() {
    const title = document.getElementById("ticket-title");
    const badges = document.getElementById("ticket-badges");
    const description = document.getElementById("ticket-description");
    const details = document.getElementById("ticket-details");
    const takeButton = document.getElementById("take-button");
    const resolveButton = document.getElementById("resolve-button");
    const noteForm = document.getElementById("note-form");

    setMessage("message", "");
    setMessage("action-message", "");
    setMessage("note-message", "");
    const id = getTicketId();
    if (id === null) {
        setMessage("message", "Ticket not found.", "error");
        return;
    }

    try {
        const res = await api("/api/tickets/" + id);
        if (!res.ok) {
            if (res.status !== 401) {                          // 401 already redirected
                setMessage("message", res.status === 404 ? "Ticket not found." : res.message, "error");
            }
            return;
        }

        const t = res.data;
        title.textContent = t.title;
        badges.replaceChildren(statusBadge(t.status), priorityLabel(t.priority));
        description.textContent = t.description;
        details.replaceChildren();
        addDetail(details, "Created by", t.createdBy);
        addDetail(details, "Assigned to", t.assignedTo ?? "Unassigned");
        addDetail(details, "Created", new Date(t.createdAt).toLocaleString());
        addDetail(details, "Updated", new Date(t.updatedAt).toLocaleString());
        if (t.resolvedAt) addDetail(details, "Resolved", new Date(t.resolvedAt).toLocaleString());
        takeButton.hidden = !t.canTake;                    // the server says whether this caller can take it now
        resolveButton.hidden = !t.canResolve;              // ... and whether this caller can resolve it now
        noteForm.hidden = !t.canAddNote;                   // ... and whether this caller can write notes on it
        await loadNotes(id);                               // the notes list (it handles its own errors)
    } catch {
        setMessage("message", "Network error. Please try again.", "error");
    }
}

// ticket.html: shared by the Take, Resolve and Add Note buttons. Sends one request, then redraws the
// ticket with its real new state and shows a message next to the button (#action-message for Take and
// Resolve, #note-message for the note form: so the message is on screen where the user just tapped).
// `texts` maps a status code (or "ok") to the message; a status without an entry shows the server's own
// message. `onOk` (optional) runs right after a successful answer.
async function ticketAction(button, path, options, texts, onOk, messageId = "action-message") {
    if (button.disabled) return;                       // one request at a time

    const id = getTicketId();
    if (id === null) return;

    button.disabled = true;
    setMessage(messageId, "");

    let text = "";
    let kind = "error";
    try {
        const res = await api("/api/tickets/" + id + path, options);

        if (res.ok) {
            text = texts.ok;
            kind = "success";
            if (onOk) onOk();
        } else if (res.status === 401) {               // already redirected to "/"
            return;
        } else {
            text = texts[res.status] ?? res.message;
        }

        await loadTicket();                            // show the real state (this also hides the button when needed)
        setMessage(messageId, text, kind);             // after loadTicket(), because it clears the message boxes
    } catch {
        setMessage(messageId, "Network error. Please try again.", "error");
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

// ticket.html (the technician who took the ticket): save the text of #note-body as a note.
// The server trims and validates it; the box is emptied only when the note was saved.
function addNote() {
    const box = document.getElementById("note-body");
    return ticketAction(document.getElementById("note-button"), "/notes",
        { method: "POST", body: { body: box.value } }, {
        ok: "Note added.",
        403: "Only the technician who took this ticket can add notes.",
        404: "Ticket not found.",
    }, () => { box.value = ""; }, "note-message");      // 400 shows the server's message and keeps what was typed
}

// create-ticket.html: send the form to POST /api/tickets. The server validates and is the authority;
// the maxlength attributes in the HTML only stop typing past the same limits.
async function createTicket() {
    const button = document.getElementById("submit-ticket");
    if (button.disabled) return;                      // one request at a time (no duplicate tickets)

    const title = document.getElementById("title").value;
    const description = document.getElementById("description").value;
    const priority = document.getElementById("priority").value;

    button.disabled = true;
    setMessage("message", "");

    try {
        const res = await api("/api/tickets", { method: "POST", body: { title, description, priority } });

        if (res.ok) {
            window.location.href = "/home.html";      // button stays disabled while the page changes
            return;
        }

        if (res.status !== 401) {                      // 401 already redirected to "/"
            setMessage("message", res.status === 403
                ? "Only employees can create tickets."
                : res.message, "error");
        }
    } catch {
        setMessage("message", "Network error. Please try again.", "error");
    }

    button.disabled = false;
}

// The change-password rules, in the same order and words as the server (POST /api/me/password). Returns null when
// everything is fine, otherwise { field, text } (the box to focus and the message). The server stays the authority.
function checkPasswordChange(current, next, repeat) {
    if (current.length === 0)
        return { field: "current-password", text: "Enter your current password." };

    if (next.length < 8)
        return { field: "new-password", text: "Password must be at least 8 characters." };

    if (new TextEncoder().encode(next).length > 72)
        return { field: "new-password", text: "Password is too long (max 72 bytes)." };

    if (next === current)
        return { field: "new-password", text: "The new password must be different from the current one." };

    if (next !== repeat)                               // the only rule the server cannot check (it never sees the 2nd box)
        return { field: "repeat-password", text: "Passwords do not match." };

    return null;
}

// change-password.html: runs when the form is submitted (button click or Enter key)
async function changePassword() {
    const button = document.getElementById("change-button");
    if (button.disabled) return;                       // one request at a time

    const current = document.getElementById("current-password").value;   // passwords are never trimmed
    const next = document.getElementById("new-password").value;
    const repeat = document.getElementById("repeat-password").value;

    setMessage("message", "");

    const problem = checkPasswordChange(current, next, repeat);
    if (problem) {
        setMessage("message", problem.text, "error");
        document.getElementById(problem.field).focus();
        return;
    }

    button.disabled = true;
    let leaving = false;
    try {
        const res = await api("/api/me/password", {
            method: "POST",
            body: { currentPassword: current, newPassword: next }
        });

        if (res.ok) {
            leaving = true;                            // other sessions of this account have been ended by the server
            setMessage("message", "Password changed.", "success");
            window.setTimeout(() => { window.location.href = "/home.html"; }, 1200);
            return;
        }

        if (res.status !== 401) {                      // 401 already redirected to "/"
            setMessage("message", res.message, "error");
            if (res.status === 403) document.getElementById("current-password").focus();
        }
    } catch {
        setMessage("message", "Network error. Please try again.", "error");
    } finally {
        if (!leaving) button.disabled = false;         // stays disabled while the page changes
    }
}

// change-password.html: top bar, and the "temporary password" note (with no way back) when the server says the
// password must be changed first. Every other page is unreachable in that state anyway (AuthenticationMiddleware).
async function loadChangePasswordPage() {
    try {
        const res = await api("/api/me");
        if (!res.ok) return;
        fillNav(res.data.username, res.data.role);
        const forced = res.data.mustChangePassword === true;
        document.getElementById("forced-note").hidden = !forced;
        document.getElementById("back-box").hidden = forced;
    } catch {
        // the page still works; the server decides everything
    }
}

// Connects the page's controls to the functions above. There is no inline onclick / onsubmit / onchange
// in any page, because the Content-Security-Policy (SecurityHeaders.cs) does not allow inline script.
// Every page loads script.js at the end of <body>, so the elements already exist; a page simply skips
// the ids it does not have.
function listen(id, type, handler) {
    const element = document.getElementById(id);
    if (element) element.addEventListener(type, handler);
}

listen("register-form", "submit", event => { event.preventDefault(); register(); });
listen("login-form", "submit", event => { event.preventDefault(); login(); });
listen("logout-button", "click", () => logout());
listen("status-filter", "change", () => filterTickets());
listen("create-form", "submit", event => { event.preventDefault(); createTicket(); });
listen("take-button", "click", () => takeTicket());
listen("resolve-button", "click", () => resolveTicket());
listen("note-button", "click", () => addNote());
listen("change-form", "submit", event => { event.preventDefault(); changePassword(); });

// Run automatically (no inline script needed): the home page loads the user and the list, the other
// signed-in pages fill the top bar, and the ticket page also loads its ticket.
if (document.getElementById("section-title")) loadMe();
else if (document.getElementById("change-form")) loadChangePasswordPage();
else if (document.getElementById("nav-user")) loadNav();
if (document.getElementById("ticket-title")) loadTicket();
