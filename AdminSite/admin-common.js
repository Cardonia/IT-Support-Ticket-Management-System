// Shared by every Admin page (served by Admin/AdminEndpoints.cs, loaded after /script.js).
// script.js already wires #logout-button, fills #nav-user / #nav-role (loadNav) and provides api(),
// setMessage(), statusBadge() and priorityLabel(); this file adds only what is specific to the Admin area.
// Everything is drawn with textContent / createElement, never innerHTML.

// The sections of the Admin area. `ready: false` shows the section as "not available yet" (no link, so
// nobody lands on a 404); the level that builds a page flips its flag to true.
const ADMIN_SECTIONS = [
    { key: "dashboard", label: "Dashboard", href: "/admin",          ready: true,  text: "Counts and the latest activity at a glance." },
    { key: "users",     label: "Users",     href: "/admin/users",    ready: true,  text: "Find people, see their data, create accounts, reset passwords, deactivate." },
    { key: "tickets",   label: "Tickets",   href: "/admin/tickets",  ready: true,  text: "Every ticket, with edit, assign, resolve and reopen." },
    { key: "notes",     label: "Notes",     href: "/admin/notes",    ready: true , text: "Every technician note, searchable." },
    { key: "activity",  label: "Activity",  href: "/admin/activity", ready: true , text: "Who did what and when, newest first." },
];

// The navigation under the top bar. The current page is marked with aria-current.
function renderAdminNav(currentKey) {
    const list = document.getElementById("admin-nav");
    if (!list) return;
    list.replaceChildren();

    for (const section of ADMIN_SECTIONS) {
        const item = document.createElement("li");
        let control;

        if (section.ready) {
            control = document.createElement("a");
            control.href = section.href;
            if (section.key === currentKey) control.setAttribute("aria-current", "page");
        } else {
            control = document.createElement("span");
            control.className = "is-soon";
            control.setAttribute("aria-disabled", "true");
        }
        control.classList.add("admin-nav-link");
        control.textContent = section.label;
        item.append(control);
        list.append(item);
    }
}

// The dashboard's list of sections (the real counters arrive with the dashboard level).
function renderAdminSections() {
    const list = document.getElementById("admin-sections");
    if (!list) return;
    list.replaceChildren();

    for (const section of ADMIN_SECTIONS) {
        if (section.key === "dashboard") continue;

        const item = document.createElement("li");
        item.className = "admin-card";

        const title = document.createElement("h2");
        const text = document.createElement("p");
        text.textContent = section.text;

        if (section.ready) {
            const link = document.createElement("a");
            link.href = section.href;
            link.textContent = section.label;
            title.append(link);
        } else {
            title.textContent = section.label;
        }
        item.append(title, text);

        if (!section.ready) {
            const soon = document.createElement("p");
            soon.className = "admin-card-soon";
            soon.textContent = "Not available yet";
            item.append(soon);
        }
        list.append(item);
    }
}

// ---------------------------------------------------------------------------------------------
// Helpers for the user pages (Level 30). Text goes in with textContent only, never innerHTML.
// ---------------------------------------------------------------------------------------------

// A Map, so an unexpected role from the server can never pick up a property of Object
const ROLE_CLASSES = new Map([["Admin", "badge-admin"], ["Technician", "badge-tech"], ["Employee", "badge-employee"]]);

// <span class="badge badge-...">Role</span>: the word is always shown, the colour only helps
function roleBadge(role) {
    const badge = document.createElement("span");
    badge.className = "badge " + (ROLE_CLASSES.get(role) ?? "");
    badge.textContent = role;
    return badge;
}

function activeBadge(isActive) {
    const badge = document.createElement("span");
    badge.className = "badge " + (isActive ? "badge-on" : "badge-off");
    badge.textContent = isActive ? "Active" : "Deactivated";
    return badge;
}

// A date and time in the viewer's own format; `fallback` when the server has no value
function formatWhen(value, fallback = "") {
    if (!value) return fallback;
    const date = new Date(value);
    return Number.isNaN(date.getTime()) ? fallback : date.toLocaleString();
}

// ---------------------------------------------------------------------------------------------
// Helpers for the notes and activity pages (Level 33).
// ---------------------------------------------------------------------------------------------

// A link to /admin/<area>/<id> with plain text; the id is encoded, the text is never parsed as HTML
function adminLink(area, id, text) {
    const a = document.createElement("a");
    a.href = "/admin/" + area + "/" + encodeURIComponent(id);
    a.textContent = text;
    return a;
}

// "2026-10-03" (a date box) -> the instant that day starts in the viewer's own time zone, as ISO text for
// the server (UTC). `plusDays` = 1 gives the start of the NEXT day, which is how an included "to" date is sent.
// Returns "" for an empty or impossible value; the server accepts the years 2000-2200 only.
function localDayToIso(value, plusDays = 0) {
    const m = /^(\d{4})-(\d{2})-(\d{2})$/.exec(value ?? "");
    if (!m) return "";
    const year = Number(m[1]);
    if (year < 2000 || year > 2199) return "";
    const date = new Date(year, Number(m[2]) - 1, Number(m[3]) + plusDays);
    return Number.isNaN(date.getTime()) ? "" : date.toISOString();
}

// One line of the activity feed (used by the dashboard and the activity page): the sentence, then who / when / what it
// was about. `whenText` is the time (or date and time) the caller wants shown. `e` is an item of /api/admin/activity.
function activityLine(e, whenText) {
    const text = document.createElement("span");
    text.className = "mini-main";
    text.textContent = e.summary;

    const meta = document.createElement("span");
    meta.className = "mini-meta";

    const when = document.createElement("span");
    when.textContent = whenText;
    meta.append(when);

    const badge = (label, cls) => {
        const b = document.createElement("span");
        b.className = "badge " + cls;
        b.textContent = label;
        return b;
    };

    // who did it
    if (e.terminal) meta.append(badge("Database terminal", "badge-off"));
    else if (e.actorId !== null && e.actorId !== undefined) meta.append(adminLink("users", e.actorId, e.actorName ?? "User #" + e.actorId));
    else meta.append(e.actorName ?? "Not signed in");
    if (e.actorRole === "Admin") meta.append(badge("Admin action", "badge-admin"));

    // what it was about: the user or the ticket (a note belongs to its ticket)
    if (e.targetType === "user" && e.targetId !== e.actorId) meta.append(adminLink("users", e.targetId, "User #" + e.targetId));
    const ticketId = e.targetType === "ticket" ? e.targetId : e.ticketId;
    if (ticketId !== null && ticketId !== undefined) meta.append(adminLink("tickets", ticketId, "Ticket #" + ticketId));

    const li = document.createElement("li");
    li.append(text, meta);
    return li;
}

// Draws the one-time temporary password into `panel` (an element that is hidden until now) and shows it.
// The password only ever lives in this element; nothing stores it, and "Done" removes it from the page.
function showTemporaryPassword(panel, { title, intro, password, onDone }) {
    panel.replaceChildren();

    const heading = document.createElement("h2");
    heading.textContent = title;

    const text = document.createElement("p");
    text.textContent = intro;

    const code = document.createElement("code");
    code.className = "temp-password";
    code.textContent = password;

    const copy = document.createElement("button");
    copy.type = "button";
    copy.className = "btn btn-secondary";
    copy.textContent = "Copy password";
    copy.addEventListener("click", async () => {
        try {
            await navigator.clipboard.writeText(password);
            copy.textContent = "Copied";
        } catch {                                       // no clipboard permission: select it so Ctrl+C works
            const range = document.createRange();
            range.selectNodeContents(code);
            const selection = window.getSelection();
            selection.removeAllRanges();
            selection.addRange(range);
            copy.textContent = "Selected: press Ctrl+C";
        }
    });

    const warning = document.createElement("p");
    warning.className = "field-hint";
    warning.textContent = "This password is shown once. It is not stored anywhere in readable form, so it can not be shown again. "
        + "The user must choose their own password the first time they log in.";

    const done = document.createElement("button");
    done.type = "button";
    done.className = "btn";
    done.textContent = "Done";
    done.addEventListener("click", () => {
        panel.replaceChildren();                        // the password leaves the page
        panel.hidden = true;
        if (onDone) onDone();
    });

    const actions = document.createElement("div");
    actions.className = "form-actions";
    actions.append(copy, done);

    panel.append(heading, text, code, warning, actions);
    panel.hidden = false;
    copy.focus();
}

renderAdminNav(document.body.dataset.adminPage);
renderAdminSections();
