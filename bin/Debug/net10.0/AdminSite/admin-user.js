// /admin/users/<id>: one account with its numbers, latest tickets / notes / activity, and the actions an Admin may take.
// Served by Admin/AdminEndpoints.cs after script.js (api, setMessage, statusBadge, priorityLabel, trimLikeServer)
// and admin-common.js (badges, formatWhen, showTemporaryPassword).
// Everything is drawn with textContent / createElement, never innerHTML: usernames, ticket titles and notes are
// other people's text and may contain markup.

const userId = (/^\/admin\/users\/(\d+)\/?$/.exec(window.location.pathname) ?? [])[1];
let current = null;          // the last loaded detail (the page's picture of the user; the server re-checks everything)
let pendingAction = null;    // "deactivate" | "reactivate" | "reset" | "role" while the confirmation panel is open

const ACTIONS = {
    deactivate: { path: "deactivate", button: "Deactivate user", danger: true },
    reactivate: { path: "reactivate", button: "Reactivate user", danger: false },
    reset: { path: "reset-password", button: "Reset password", danger: true },
    role: { path: "role", method: "PATCH", button: "Change role", danger: false },
};

// The only role change offered is to the OTHER one of Employee / Technician. "Admin" is not an option anywhere.
function otherRole(role) {
    return role === "Technician" ? "Employee" : "Technician";
}

// ---- small DOM helpers

function addRow(list, label, value) {
    const dt = document.createElement("dt");
    dt.textContent = label;
    const dd = document.createElement("dd");
    if (value instanceof Node) dd.append(value);
    else dd.textContent = value;
    list.append(dt, dd);
}

// <dt>/<dd> pairs must be wrapped so the grid keeps each pair together
function addPair(list, label, value) {
    const box = document.createElement("div");
    addRow(box, label, value);
    list.append(box);
}

function plural(n, one, many) {
    return `${n} ${n === 1 ? one : many}`;
}

// ---- drawing

function renderProfile(d) {
    const u = d.user;

    document.title = `${u.username} - IT Help Desk Admin`;
    document.getElementById("user-title").textContent = u.username;
    document.getElementById("user-badges").replaceChildren(roleBadge(u.role), activeBadge(u.isActive));

    const list = document.getElementById("profile");
    list.replaceChildren();
    addPair(list, "Created", formatWhen(u.createdAt, "Before tracking"));
    addPair(list, "Last login", formatWhen(u.lastLogin, "Not recorded"));
    addPair(list, "Logged in now", plural(d.activeSessions, "session", "sessions"));
    addPair(list, "Password", u.mustChangePassword ? "Temporary: must be changed at next login" : "Chosen by the user");
    addPair(list, "Tickets reported", String(d.counts.ticketsCreated));
    addPair(list, "Tickets worked on", String(d.counts.ticketsAssigned));
    addPair(list, "In progress now", String(d.counts.inProgress));
    addPair(list, "Notes written", String(d.counts.notes));
}

function ticketLinkOrText(ticket) {
    const label = `#${ticket.id} ${ticket.title}`;
    const tickets = ADMIN_SECTIONS.find(s => s.key === "tickets");
    if (!tickets || !tickets.ready) return document.createTextNode(label);   // the ticket pages arrive in a later level
    const a = document.createElement("a");
    a.href = "/admin/tickets/" + encodeURIComponent(ticket.id);
    a.textContent = label;
    return a;
}

function renderTickets(listId, tickets, emptyText) {
    const list = document.getElementById(listId);
    list.replaceChildren();
    if (tickets.length === 0) {
        const li = document.createElement("li");
        li.className = "mini-empty";
        li.textContent = emptyText;
        list.append(li);
        return;
    }
    for (const t of tickets) {
        const title = document.createElement("span");
        title.className = "mini-main";
        title.append(ticketLinkOrText(t));

        const meta = document.createElement("span");
        meta.className = "mini-meta";
        meta.append(statusBadge(t.status), priorityLabel(t.priority), document.createTextNode(formatWhen(t.createdAt)));

        const li = document.createElement("li");
        li.append(title, meta);
        list.append(li);
    }
}

function renderNotes(notes) {
    const list = document.getElementById("list-notes");
    list.replaceChildren();
    if (notes.length === 0) {
        const li = document.createElement("li");
        li.className = "mini-empty";
        li.textContent = "No notes written.";
        list.append(li);
        return;
    }
    for (const n of notes) {
        const text = document.createElement("span");
        text.className = "mini-main note-excerpt";
        text.textContent = n.excerpt;

        const meta = document.createElement("span");
        meta.className = "mini-meta";
        meta.textContent = `Ticket #${n.ticketId}, ${formatWhen(n.createdAt)}`;

        const li = document.createElement("li");
        li.append(text, meta);
        list.append(li);
    }
}

function renderEvents(events) {
    const list = document.getElementById("list-events");
    list.replaceChildren();
    if (events.length === 0) {
        const li = document.createElement("li");
        li.className = "mini-empty";
        li.textContent = "Nothing recorded yet.";
        list.append(li);
        return;
    }
    for (const e of events) {
        const text = document.createElement("span");
        text.className = "mini-main";
        text.textContent = e.summary;

        const meta = document.createElement("span");
        meta.className = "mini-meta";
        meta.textContent = `${e.actorName ?? "Not signed in"}${e.actorRole === "Admin" ? " (admin)" : ""}, ${formatWhen(e.at)}`;

        const li = document.createElement("li");
        li.append(text, meta);
        list.append(li);
    }
}

function actionButton(kind, label, danger) {
    const b = document.createElement("button");
    b.type = "button";
    b.className = danger ? "btn btn-danger-outline" : "btn btn-secondary";
    b.textContent = label;
    b.addEventListener("click", () => openConfirm(kind));
    return b;
}

function renderActions(d) {
    const u = d.user;
    const panel = document.getElementById("actions-panel");
    const note = document.getElementById("actions-note");
    const rename = document.getElementById("rename-form");
    const buttons = document.getElementById("action-buttons");

    panel.hidden = false;
    closeConfirm();
    buttons.replaceChildren();
    setMessage("rename-message", "");

    // An Admin is never changed through the app (and the signed-in admin is one): no buttons, only the reason.
    if (u.role === "Admin") {
        note.hidden = false;
        note.textContent = "Admin accounts can only be created, changed or removed from the database terminal. "
            + "Nothing on this page can change this account.";
        rename.hidden = true;
        return;
    }

    note.hidden = true;
    rename.hidden = false;
    document.getElementById("rename-input").value = u.username;

    buttons.append(actionButton("role", `Make ${otherRole(u.role)}...`, false));
    if (u.isActive) {
        buttons.append(actionButton("reset", "Reset password...", true), actionButton("deactivate", "Deactivate...", true));
    } else {
        buttons.append(actionButton("reactivate", "Reactivate...", false));
    }
}

function render(d) {
    current = d;
    renderProfile(d);
    renderActions(d);
    renderTickets("list-created", d.latestCreated, "No tickets reported.");
    renderTickets("list-assigned", d.latestAssigned, "No tickets worked on.");
    renderNotes(d.latestNotes);
    renderEvents(d.events);
}

async function load() {
    const res = await api("/api/admin/users/" + encodeURIComponent(userId));
    if (!res.ok) {
        document.getElementById("user-title").textContent = "User not found";
        setMessage("message", res.message, "error");
        return false;
    }
    render(res.data);
    return true;
}

// ---- confirmation panel

function consequence(kind, u) {
    const inProgress = current.counts.inProgress;
    if (kind === "deactivate") {
        const tickets = inProgress > 0
            ? `${plural(inProgress, "in-progress ticket", "in-progress tickets")} will return to Open and unassigned. `
            : "";
        return `${tickets}${u.username} is signed out at once and can no longer log in. Their tickets, notes and history stay.`;
    }
    if (kind === "reactivate")
        return `${u.username} can log in again with the password they had.`;
    if (kind === "role") {
        if (u.role === "Technician") {
            const tickets = inProgress > 0
                ? `${plural(inProgress, "in-progress ticket", "in-progress tickets")} will return to Open and unassigned. `
                : "";
            return `${tickets}${u.username} will no longer see or work on other people's tickets, only their own. `
                + "The change applies at their next request; they stay signed in. Their notes and history stay.";
        }
        return `${u.username} will be able to see all tickets, take them, add notes and resolve them. `
            + "The change applies at their next request; they stay signed in.";
    }
    return `A new temporary password is created and shown to you once. ${u.username} is signed out everywhere and must choose their own password at the next login.`;
}

function openConfirm(kind) {
    pendingAction = kind;
    const u = current.user;
    document.getElementById("confirm-title").textContent =
        kind === "deactivate" ? `Deactivate ${u.username}?`
        : kind === "reactivate" ? `Reactivate ${u.username}?`
        : kind === "role" ? `Make ${u.username} ${otherRole(u.role) === "Employee" ? "an" : "a"} ${otherRole(u.role)}?`
        : `Reset the password of ${u.username}?`;
    document.getElementById("confirm-text").textContent = consequence(kind, u);

    const button = document.getElementById("confirm-button");
    button.textContent = kind === "role" ? `Make ${otherRole(u.role)}` : ACTIONS[kind].button;
    button.className = "btn " + (ACTIONS[kind].danger ? "btn-danger" : "");
    setMessage("confirm-message", "");
    document.getElementById("confirm-password").value = "";
    document.getElementById("confirm-panel").hidden = false;
    document.getElementById("confirm-password").focus();
}

function closeConfirm() {
    pendingAction = null;
    document.getElementById("confirm-panel").hidden = true;
    document.getElementById("confirm-password").value = "";     // the password never stays in the page
    setMessage("confirm-message", "");
}

async function confirmAction() {
    const button = document.getElementById("confirm-button");
    if (button.disabled || !pendingAction) return;

    const kind = pendingAction;
    const password = document.getElementById("confirm-password").value;
    if (password.length === 0) {
        setMessage("confirm-message", "Enter your password to confirm.", "error");
        document.getElementById("confirm-password").focus();
        return;
    }

    button.disabled = true;
    setMessage("confirm-message", "");
    try {
        // For a role change the page sends the role it SAW; the server refuses (409) if it has changed since.
        const body = kind === "role"
            ? { role: otherRole(current.user.role), expectedRole: current.user.role, password }
            : { password };
        const res = await api(`/api/admin/users/${encodeURIComponent(userId)}/${ACTIONS[kind].path}`, {
            method: ACTIONS[kind].method ?? "POST",
            body
        });

        document.getElementById("confirm-password").value = "";
        if (!res.ok) {
            if (res.status === 404 || res.status === 409) {     // the page was out of date: show the true state, then say why
                await load();
                setMessage("message", res.message, "error");
            } else {
                setMessage("confirm-message", res.message, "error");   // wrong password (403), locked (429), missing (400) ...
                document.getElementById("confirm-password").focus();   // disabling the button dropped the focus; keyboard users stay in the panel
            }
            return;
        }

        const name = current.user.username;
        closeConfirm();
        if (kind === "reset") {
            showTemporaryPassword(document.getElementById("result-panel"), {
                title: `New temporary password for ${name}`,
                intro: `${name} was signed out everywhere. Give them this password:`,
                password: res.data.temporaryPassword
            });
            setMessage("message", "");
        } else if (kind === "role") {
            const released = res.data.releasedTickets;
            setMessage("message", `${name} is now ${res.data.role === "Employee" ? "an" : "a"} ${res.data.role}.`
                + (released > 0 ? ` ${plural(released, "ticket", "tickets")} returned to Open.` : ""), "success");
        } else if (kind === "deactivate") {
            const released = res.data.releasedTickets;
            setMessage("message", `${name} was deactivated.` + (released > 0 ? ` ${plural(released, "ticket", "tickets")} returned to Open.` : ""), "success");
        } else {
            setMessage("message", `${name} was reactivated.`, "success");
        }
        await load();
    } catch {
        setMessage("confirm-message", "Network error. Please try again.", "error");
    } finally {
        button.disabled = false;
    }
}

// ---- rename

async function rename() {
    const button = document.getElementById("rename-button");
    if (button.disabled) return;

    const username = trimLikeServer(document.getElementById("rename-input").value);
    setMessage("rename-message", "");
    if (!/^[A-Za-z0-9_]{4,14}$/.test(username)) {
        setMessage("rename-message", "Username must be 4-14 characters: letters, numbers or underscore.", "error");
        document.getElementById("rename-input").focus();
        return;
    }
    if (username === current.user.username) {
        setMessage("rename-message", "That is already this user's username.", "error");
        return;
    }

    button.disabled = true;
    try {
        const res = await api("/api/admin/users/" + encodeURIComponent(userId), { method: "PATCH", body: { username } });
        if (!res.ok) {
            setMessage("rename-message", res.message, "error");
            return;
        }
        setMessage("message", `Username changed to ${res.data.username}.`, "success");
        await load();
    } catch {
        setMessage("rename-message", "Network error. Please try again.", "error");
    } finally {
        button.disabled = false;
    }
}

// ---- wiring (no inline handlers: the Content-Security-Policy forbids them)

document.getElementById("rename-form").addEventListener("submit", event => { event.preventDefault(); rename(); });
document.getElementById("confirm-panel").addEventListener("submit", event => { event.preventDefault(); confirmAction(); });
document.getElementById("confirm-cancel").addEventListener("click", closeConfirm);
document.getElementById("confirm-panel").addEventListener("keydown", event => { if (event.key === "Escape") closeConfirm(); });

if (userId) load();
else setMessage("message", "User not found.", "error");
