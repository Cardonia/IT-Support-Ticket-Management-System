// /admin: the dashboard. Counters (each one links to the page that lists those rows) and three short lists.
// Served by Admin/AdminEndpoints.cs after script.js (api, setMessage, statusBadge, priorityLabel) and admin-common.js
// (adminLink, roleBadge, activityLine, formatWhen). One request: GET /api/admin/summary.
// Everything is drawn with textContent / createElement, never innerHTML: names and titles are other people's text.

// One counter: a link when the rows behind it have a page, a plain tile when not
function statTile(label, value, href) {
    const number = document.createElement("strong");
    number.className = "stat-num";
    number.textContent = String(value);

    const text = document.createElement("span");
    text.className = "stat-label";
    text.textContent = label;

    const li = document.createElement("li");
    const box = href ? document.createElement("a") : document.createElement("span");
    box.className = "stat";
    if (href) box.href = href;
    box.append(number, text);
    li.append(box);
    return li;
}

function fillStats(id, tiles) {
    document.getElementById(id).replaceChildren(...tiles.map(([label, value, href]) => statTile(label, value, href)));
}

function emptyItem(list, text) {
    const li = document.createElement("li");
    li.className = "mini-empty";
    li.textContent = text;
    list.replaceChildren(li);
}

function renderCounts(c) {
    fillStats("dash-people", [
        ["Users", c.users, "/admin/users"],
        ["Employees", c.employees, "/admin/users?role=Employee"],
        ["Technicians", c.technicians, "/admin/users?role=Technician"],
        ["Admins", c.admins, "/admin/users?role=Admin"],
        ["Active", c.activeUsers, "/admin/users?status=active"],
        ["Deactivated", c.deactivatedUsers, "/admin/users?status=disabled"],
        ["Must choose a new password", c.mustChange, null],
    ]);
    fillStats("dash-tickets", [
        ["Tickets", c.tickets, "/admin/tickets"],
        ["Open", c.open, "/admin/tickets?status=Open"],
        ["In progress", c.inProgress, "/admin/tickets?status=In%20Progress"],
        ["Resolved", c.resolved, "/admin/tickets?status=Resolved"],
        ["High priority", c.high, "/admin/tickets?priority=High"],
        ["Medium priority", c.medium, "/admin/tickets?priority=Medium"],
        ["Low priority", c.low, "/admin/tickets?priority=Low"],
        ["Open and unassigned", c.unassignedOpen, "/admin/tickets?status=Open"],
        ["Deleted tickets", c.deletedTickets, "/admin/tickets?deleted=only"],
        ["Notes", c.notes, "/admin/notes"],
    ]);
    fillStats("dash-day", [
        ["Logins", c.logins24h, "/admin/activity?action=auth.login"],
        ["Failed logins", c.failedLogins24h, "/admin/activity?action=auth.login_failed"],
        ["Admin actions", c.adminActions24h, "/admin/activity?group=admin"],
        ["Security events", c.security24h, "/admin/activity?group=security"],
    ]);
}

function renderActivity(items) {
    const list = document.getElementById("dash-activity");
    if (items.length === 0) return emptyItem(list, "Nothing has been recorded yet.");
    list.replaceChildren(...items.map(e => activityLine(e, formatWhen(e.at))));
}

function renderRecentUsers(users) {
    const list = document.getElementById("dash-users");
    if (users.length === 0) return emptyItem(list, "No users yet.");
    list.replaceChildren(...users.map(u => {
        const main = document.createElement("span");
        main.className = "mini-main";
        main.append(adminLink("users", u.id, u.username));

        const meta = document.createElement("span");
        meta.className = "mini-meta";
        meta.append(roleBadge(u.role));
        if (!u.active) {
            const off = document.createElement("span");
            off.className = "badge badge-off";
            off.textContent = "Deactivated";
            meta.append(off);
        }
        const when = document.createElement("span");
        when.textContent = formatWhen(u.createdAt, "Before tracking");
        meta.append(when);

        const li = document.createElement("li");
        li.append(main, meta);
        return li;
    }));
}

function renderRecentTickets(tickets) {
    const list = document.getElementById("dash-newtickets");
    if (tickets.length === 0) return emptyItem(list, "No tickets yet.");
    list.replaceChildren(...tickets.map(t => {
        const main = document.createElement("span");
        main.className = "mini-main";
        main.append(adminLink("tickets", t.id, `#${t.id} ${t.title}`));

        const by = document.createElement("span");
        by.append("by ", adminLink("users", t.creatorId, t.creator));

        const meta = document.createElement("span");
        meta.className = "mini-meta";
        meta.append(statusBadge(t.status), priorityLabel(t.priority), by, formatWhen(t.createdAt));

        const li = document.createElement("li");
        li.append(main, meta);
        return li;
    }));
}

async function loadDashboard() {
    const res = await api("/api/admin/summary");
    if (!res.ok) {
        setMessage("message", res.message, "error");
        return;
    }
    setMessage("message", "");
    renderCounts(res.data.counts);
    renderActivity(res.data.activity);
    renderRecentUsers(res.data.users);
    renderRecentTickets(res.data.tickets);
}

loadDashboard();
