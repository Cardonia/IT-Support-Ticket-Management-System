// /admin/tickets/<id>: one ticket with its notes and history, and what an Admin may do to it right now.
// Served by Admin/AdminEndpoints.cs after script.js (api, setMessage, statusBadge, priorityLabel, trimLikeServer)
// and admin-common.js (badges, formatWhen).
// Everything is drawn with textContent / createElement, never innerHTML: titles, descriptions and notes are
// other people's text and may contain markup.
// The page shows only the actions the server says are possible, and sends back the state it SAW
// (expectedStatus / expectedAssigneeId): if the ticket changed meanwhile, the server answers 409 and the page reloads.

const ticketId = (/^\/admin\/tickets\/(\d+)\/?$/.exec(window.location.pathname) ?? [])[1];
let current = null;          // the last loaded view (the page's picture of the ticket; the server re-checks everything)
let pendingState = null;     // "unassign" | "resolve" | "reopen" while the confirmation panel is open

// ---- small DOM helpers

function addPair(list, label, value) {
    const box = document.createElement("div");
    const dt = document.createElement("dt");
    dt.textContent = label;
    const dd = document.createElement("dd");
    if (value instanceof Node) dd.append(value);
    else dd.textContent = value;
    box.append(dt, dd);
    list.append(box);
}

function userLink(user, none) {
    if (!user) return none;
    const a = document.createElement("a");
    a.href = "/admin/users/" + encodeURIComponent(user.id);
    a.textContent = user.username;
    return a;
}

function emptyItem(list, text) {
    const li = document.createElement("li");
    li.className = "mini-empty";
    li.textContent = text;
    list.append(li);
}

// ---- drawing

function renderDetails(d) {
    const t = d.ticket;

    document.title = `#${t.id} ${t.title} - IT Help Desk Admin`;
    document.getElementById("tk-title").textContent = t.title;

    const badges = [statusBadge(t.status), priorityLabel(t.priority)];
    if (t.deleted) {
        const gone = document.createElement("span");
        gone.className = "badge badge-off";
        gone.textContent = "Deleted";
        badges.push(gone);
    }
    document.getElementById("tk-badges").replaceChildren(...badges);

    const note = document.getElementById("tk-deleted-note");
    note.hidden = !t.deleted;
    note.textContent = t.deleted
        ? `This ticket was deleted by ${t.deletedBy ?? "an admin"} on ${formatWhen(t.deletedAt)}. `
          + "It is hidden from the reporter and the technicians and can not be changed here. "
          + "Only the database terminal can restore it."
        : "";

    const list = document.getElementById("tk-details");
    list.replaceChildren();
    addPair(list, "Number", `#${t.id}`);
    addPair(list, "Reported by", userLink(t.creator, ""));
    addPair(list, "Assigned to", userLink(t.assignee, "Nobody"));
    addPair(list, "Created", formatWhen(t.createdAt));
    addPair(list, "Changed", formatWhen(t.updatedAt));
    if (t.resolvedAt) addPair(list, "Resolved", formatWhen(t.resolvedAt));

    document.getElementById("tk-description").textContent = t.description;
}

function renderNotes(d) {
    const list = document.getElementById("tk-notes");
    const empty = document.getElementById("tk-notes-empty");
    const summary = document.getElementById("tk-notes-summary");
    list.replaceChildren();

    for (const n of d.notes) {
        const author = document.createElement("strong");
        author.textContent = n.author;
        const when = document.createElement("span");
        when.textContent = formatWhen(n.createdAt);
        const meta = document.createElement("div");
        meta.className = "note-meta";
        meta.append(author, when);

        const body = document.createElement("p");
        body.className = "note-body";
        body.textContent = n.body;

        const li = document.createElement("li");
        li.append(meta, body);
        list.append(li);
    }

    empty.textContent = d.noteCount === 0 ? "No notes on this ticket." : "";
    summary.textContent = d.noteCount > d.notes.length
        ? `Showing the first ${d.notes.length} of ${d.noteCount} notes.` : "";
}

function renderHistory(d) {
    const list = document.getElementById("tk-history");
    list.replaceChildren();
    if (d.events.length === 0) {
        emptyItem(list, "Nothing recorded for this ticket.");
        return;
    }
    for (const e of d.events) {
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

function actionButton(label, danger, onClick) {
    const b = document.createElement("button");
    b.type = "button";
    b.className = danger ? "btn btn-danger-outline" : "btn btn-secondary";
    b.textContent = label;
    b.addEventListener("click", onClick);
    return b;
}

function renderActions(d) {
    const a = d.actions;
    const t = d.ticket;
    const panel = document.getElementById("tk-actions-panel");
    const any = Object.values(a).some(Boolean);
    panel.hidden = !any;

    closeConfirm();
    closeDelete();
    setMessage("edit-message", "");
    setMessage("assign-message", "");
    if (!any) return;

    // edit
    const edit = document.getElementById("edit-form");
    edit.hidden = !a.edit;
    document.getElementById("edit-title").value = t.title;
    document.getElementById("edit-description").value = t.description;
    document.getElementById("edit-priority").value = t.priority;

    // assign / reassign: the technicians the server offered (a reassign never offers the current one)
    const assign = document.getElementById("assign-form");
    const wantsTech = a.assign || a.reassign;
    assign.hidden = !wantsTech;
    if (wantsTech) {
        const select = document.getElementById("assign-select");
        select.replaceChildren();
        const options = d.technicians.filter(x => !t.assignee || x.id !== t.assignee.id);
        for (const tech of options) {
            const option = document.createElement("option");
            option.value = String(tech.id);
            option.textContent = tech.username;
            select.append(option);
        }
        document.getElementById("assign-label").textContent = a.assign ? "Assign to technician" : "Move to another technician";
        document.getElementById("assign-button").textContent = a.assign ? "Assign" : "Reassign";
        document.getElementById("assign-button").disabled = options.length === 0;
        if (options.length === 0) setMessage("assign-message", "There is no other active technician.", "error");
    }

    // buttons
    const buttons = document.getElementById("tk-action-buttons");
    buttons.replaceChildren();
    if (a.resolve) buttons.append(actionButton("Mark resolved...", false, () => openConfirm("resolve")));
    if (a.unassign) buttons.append(actionButton("Return to Open...", false, () => openConfirm("unassign")));
    if (a.reopen) buttons.append(actionButton("Reopen...", false, () => openConfirm("reopen")));
    if (a.delete) buttons.append(actionButton("Delete ticket...", true, openDelete));
}

function render(d) {
    current = d;
    renderDetails(d);
    renderActions(d);
    renderNotes(d);
    renderHistory(d);
}

async function load() {
    const res = await api("/api/admin/tickets/" + encodeURIComponent(ticketId));
    if (!res.ok) {
        document.getElementById("tk-title").textContent = "Ticket not found";
        setMessage("message", res.message, "error");
        return false;
    }
    render(res.data);
    return true;
}

// The state the page SAW, sent with every lifecycle change
function seen() {
    return { expectedStatus: current.ticket.status, expectedAssigneeId: current.ticket.assignee?.id ?? null };
}

// A 404 / 409 means the page was out of date: show the true state first, then say why
async function outOfDate(res) {
    await load();
    setMessage("message", res.message, "error");
}

// ---- edit

async function saveEdit() {
    const button = document.getElementById("edit-button");
    if (button.disabled) return;

    const t = current.ticket;
    const title = trimLikeServer(document.getElementById("edit-title").value);
    const description = trimLikeServer(document.getElementById("edit-description").value);
    const priority = document.getElementById("edit-priority").value;

    setMessage("edit-message", "");
    const problem = title.length === 0 ? ["edit-title", "Title is required."]
        : title.length > 100 ? ["edit-title", "Title must be at most 100 characters."]
        : description.length === 0 ? ["edit-description", "Description is required."]
        : description.length > 2000 ? ["edit-description", "Description must be at most 2000 characters."]
        : null;
    if (problem) {
        setMessage("edit-message", problem[1], "error");
        document.getElementById(problem[0]).focus();
        return;
    }

    const body = {};
    if (title !== t.title) body.title = title;
    if (description !== t.description) body.description = description;
    if (priority !== t.priority) body.priority = priority;
    if (Object.keys(body).length === 0) {
        setMessage("edit-message", "Nothing to change.", "error");
        return;
    }

    button.disabled = true;
    try {
        const res = await api("/api/admin/tickets/" + encodeURIComponent(ticketId), { method: "PATCH", body });
        if (!res.ok) {
            if (res.status === 404 || res.status === 409) await outOfDate(res);
            else setMessage("edit-message", res.message, "error");
            return;
        }
        await load();
        setMessage("message", "Changes saved.", "success");
    } catch {
        setMessage("edit-message", "Network error. Please try again.", "error");
    } finally {
        button.disabled = false;
    }
}

// ---- assign / reassign

async function assign() {
    const button = document.getElementById("assign-button");
    if (button.disabled) return;

    const technicianId = Number.parseInt(document.getElementById("assign-select").value, 10);
    if (!Number.isFinite(technicianId)) {
        setMessage("assign-message", "Choose a technician.", "error");
        return;
    }
    const name = document.getElementById("assign-select").selectedOptions[0].textContent;
    const action = current.actions.assign ? "assign" : "reassign";

    button.disabled = true;
    setMessage("assign-message", "");
    try {
        const res = await api(`/api/admin/tickets/${encodeURIComponent(ticketId)}/state`, {
            method: "POST", body: { action, technicianId, ...seen() }
        });
        if (!res.ok) {
            if (res.status === 404 || res.status === 409) await outOfDate(res);
            else setMessage("assign-message", res.message, "error");
            return;
        }
        await load();
        setMessage("message", action === "assign" ? `Assigned to ${name}.` : `Moved to ${name}.`, "success");
    } catch {
        setMessage("assign-message", "Network error. Please try again.", "error");
    } finally {
        button.disabled = false;
    }
}

// ---- resolve / unassign / reopen (confirmation panel without a password)

function stateText(kind) {
    const t = current.ticket;
    const who = t.assignee ? t.assignee.username : "the technician";
    if (kind === "resolve")
        return `Ticket #${t.id} becomes Resolved. ${who} stays credited with it, and the reporter sees it as resolved.`;
    if (kind === "unassign")
        return `Ticket #${t.id} returns to Open and nobody is assigned. ${who} no longer has it, and any technician can take it.`;
    return `Ticket #${t.id} goes back to In Progress with ${who} if they are still an active technician. Otherwise it returns to Open and unassigned.`;
}

const STATE_LABELS = {
    resolve: { title: "Mark this ticket as resolved?", button: "Mark resolved", done: "Ticket marked as resolved." },
    unassign: { title: "Return this ticket to Open?", button: "Return to Open", done: "Ticket returned to Open." },
    reopen: { title: "Reopen this ticket?", button: "Reopen", done: "Ticket reopened." },
};

function openConfirm(kind) {
    closeDelete();
    pendingState = kind;
    document.getElementById("confirm-title").textContent = STATE_LABELS[kind].title;
    document.getElementById("confirm-text").textContent = stateText(kind);
    document.getElementById("confirm-button").textContent = STATE_LABELS[kind].button;
    setMessage("confirm-message", "");
    document.getElementById("confirm-panel").hidden = false;
    document.getElementById("confirm-button").focus();
}

function closeConfirm() {
    pendingState = null;
    document.getElementById("confirm-panel").hidden = true;
    setMessage("confirm-message", "");
}

async function confirmState() {
    const button = document.getElementById("confirm-button");
    if (button.disabled || !pendingState) return;

    const kind = pendingState;
    button.disabled = true;
    setMessage("confirm-message", "");
    try {
        const res = await api(`/api/admin/tickets/${encodeURIComponent(ticketId)}/state`, {
            method: "POST", body: { action: kind, ...seen() }
        });
        if (!res.ok) {
            if (res.status === 404 || res.status === 409) await outOfDate(res);
            else setMessage("confirm-message", res.message, "error");
            return;
        }
        await load();
        const text = kind === "reopen" && res.data.status === "Open"
            ? "Ticket reopened. The technician is no longer active, so it is Open and unassigned."
            : STATE_LABELS[kind].done;
        setMessage("message", text, "success");
    } catch {
        setMessage("confirm-message", "Network error. Please try again.", "error");
    } finally {
        button.disabled = false;
    }
}

// ---- delete

function openDelete() {
    closeConfirm();
    const t = current.ticket;
    document.getElementById("delete-title").textContent = `Delete ticket #${t.id}?`;
    document.getElementById("delete-text").textContent =
        "The ticket and its notes disappear from every page, for the reporter and for the technicians. "
        + "They stay in the database and in the history. Only the database terminal can bring them back.";
    document.getElementById("delete-number-hint").textContent = `Type ${t.id} to confirm.`;
    document.getElementById("delete-number").value = "";
    document.getElementById("delete-password").value = "";
    setMessage("delete-message", "");
    document.getElementById("delete-panel").hidden = false;
    document.getElementById("delete-number").focus();
}

function closeDelete() {
    document.getElementById("delete-panel").hidden = true;
    document.getElementById("delete-number").value = "";
    document.getElementById("delete-password").value = "";      // the password never stays in the page
    setMessage("delete-message", "");
}

async function deleteTicket() {
    const button = document.getElementById("delete-button");
    if (button.disabled) return;

    const confirmId = trimLikeServer(document.getElementById("delete-number").value);
    const password = document.getElementById("delete-password").value;

    setMessage("delete-message", "");
    if (confirmId.replace(/^#/, "") !== String(current.ticket.id)) {
        setMessage("delete-message", "Type the ticket number to confirm.", "error");
        document.getElementById("delete-number").focus();
        return;
    }
    if (password.length === 0) {
        setMessage("delete-message", "Enter your password to confirm.", "error");
        document.getElementById("delete-password").focus();
        return;
    }

    button.disabled = true;
    try {
        const res = await api("/api/admin/tickets/" + encodeURIComponent(ticketId), {
            method: "DELETE", body: { password, confirmId }
        });
        document.getElementById("delete-password").value = "";
        if (!res.ok) {
            if (res.status === 404 || res.status === 409) await outOfDate(res);
            else {
                setMessage("delete-message", res.message, "error");   // wrong password (403), locked (429), missing (400) ...
                document.getElementById("delete-password").focus();   // disabling the button dropped the focus
            }
            return;
        }
        await load();
        setMessage("message", "Ticket deleted.", "success");
    } catch {
        setMessage("delete-message", "Network error. Please try again.", "error");
    } finally {
        button.disabled = false;
    }
}

// ---- wiring (no inline handlers: the Content-Security-Policy forbids them)

document.getElementById("edit-form").addEventListener("submit", event => { event.preventDefault(); saveEdit(); });
document.getElementById("assign-form").addEventListener("submit", event => { event.preventDefault(); assign(); });
document.getElementById("confirm-panel").addEventListener("submit", event => { event.preventDefault(); confirmState(); });
document.getElementById("confirm-cancel").addEventListener("click", closeConfirm);
document.getElementById("confirm-panel").addEventListener("keydown", event => { if (event.key === "Escape") closeConfirm(); });
document.getElementById("delete-panel").addEventListener("submit", event => { event.preventDefault(); deleteTicket(); });
document.getElementById("delete-cancel").addEventListener("click", closeDelete);
document.getElementById("delete-panel").addEventListener("keydown", event => { if (event.key === "Escape") closeDelete(); });

if (ticketId) load();
else setMessage("message", "Ticket not found.", "error");
