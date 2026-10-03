using System.Text;
using Npgsql;

// What the Admin ticket pages receive. Ticket and note text is data for the page (shown with textContent only).
// Passwords, hashes and tokens are never selected here.
public record AdminTicketUser(long Id, string Username);

public record AdminTicketRow(
    long Id, string Title, string Priority, string Status,
    long CreatorId, string Creator, long? AssigneeId, string? Assignee,
    DateTime CreatedAt, DateTime UpdatedAt, bool Deleted);

public record AdminTicketPage(long Total, int Limit, int Offset, List<AdminTicketRow> Items);

public record AdminTicketDetail(
    long Id, string Title, string Description, string Priority, string Status,
    AdminTicketUser Creator, AdminTicketUser? Assignee,
    DateTime CreatedAt, DateTime UpdatedAt, DateTime? ResolvedAt,
    bool Deleted, DateTime? DeletedAt, string? DeletedBy);

public record AdminTicketNote(long Id, long AuthorId, string Author, string Body, DateTime CreatedAt);

// What can be done to the ticket right now (the page shows only these; the server checks again on every action)
public record AdminTicketActions(bool Edit, bool Assign, bool Reassign, bool Unassign, bool Resolve, bool Reopen, bool Delete);

public record AdminTicketView(
    AdminTicketDetail Ticket, long NoteCount, List<AdminTicketNote> Notes, List<AdminEventBrief> Events,
    AdminTicketActions Actions, List<AdminTicketUser> Technicians);

// The list request after AdminTickets.TryParseQuery has validated it.
public record TicketQuery(
    string? Text, string? Status, string? Priority, string? Creator, string? Assignee,
    string Deleted, string Sort, bool Desc, int Limit, int Offset);

// Just enough of a ticket to decide what an action may do (404 / deleted / state checks before the change)
public record AdminTicketState(long Id, string Status, long? AssigneeId, bool Deleted);

public record TicketChangeResult(bool Changed, string? NewStatus);

// All SQL for the Admin ticket pages lives here. Endpoints never build SQL.
//
// Every change is ONE statement that also inserts its audit row (Level 29 rule), and its WHERE repeats the state the
// admin saw (compare and set): a second admin, a technician or the terminal changing the ticket in between makes the
// statement match nothing and the caller answers 409. The ticket row is locked first (SELECT ... FOR UPDATE inside the
// statement), so the old values used for the audit row are exactly the ones that were replaced. A technician who is
// assigned must be an active Technician at that very moment: that row is read with FOR SHARE, the lock partner of the
// role-change / deactivation lock (AdminUsersRepository.LockUserAsync), so a demotion and an assignment can not cross.
// Admin changes are overrides with their own events: admin.ticket_created / _updated / _assigned / _reassigned /
// _unassigned / _resolved / _reopened / _deleted. Ticket and note TEXT is not copied into the audit log; a title is
// cut to 100 characters, a description is only named as "changed".
public static class AdminTicketsRepository
{
    public const int MaxTextLength = 50;
    public const int MaxNotesShown = 200;
    public const int MaxTechnicians = 200;

    const string RowColumns = """
        t.id, t.title, t.priority, t.status, t.created_by, c.first_name, t.assigned_to, a.first_name,
        t.created_at, t.updated_at, (t.deleted_at IS NOT NULL)
        """;

    const string RowFrom = """
        tickets t
        JOIN users c ON c.id = t.created_by
        LEFT JOIN users a ON a.id = t.assigned_to
        """;

    static string EscapeLike(string text) =>
        text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    // ---------------------------------------------------------------- reads

    public static async Task<AdminTicketPage> ListAsync(NpgsqlDataSource db, TicketQuery q, CancellationToken ct)
    {
        var where = new StringBuilder("WHERE true");
        var values = new List<(string Name, object Value)>();

        if (!string.IsNullOrEmpty(q.Text))
        {
            // title text, or the exact ticket number when only digits were typed
            where.Append(" AND (t.title ILIKE @pattern ESCAPE '\\'");
            values.Add(("pattern", "%" + EscapeLike(q.Text) + "%"));
            if (q.Text.All(char.IsAsciiDigit) && q.Text.Length <= 18)
            {
                where.Append(" OR t.id = @number");
                values.Add(("number", long.Parse(q.Text)));
            }
            where.Append(')');
        }
        if (q.Status is not null) { where.Append(" AND t.status = @status"); values.Add(("status", q.Status)); }
        if (q.Priority is not null) { where.Append(" AND t.priority = @priority"); values.Add(("priority", q.Priority)); }
        if (q.Creator is not null) { where.Append(" AND lower(c.first_name) = lower(@creator)"); values.Add(("creator", q.Creator)); }
        if (q.Assignee is not null) { where.Append(" AND lower(a.first_name) = lower(@assignee)"); values.Add(("assignee", q.Assignee)); }
        if (q.Deleted == "exclude") where.Append(" AND t.deleted_at IS NULL");
        else if (q.Deleted == "only") where.Append(" AND t.deleted_at IS NOT NULL");

        var dir = q.Desc ? "DESC" : "ASC";
        // Sort keys come from this fixed list only; "id" is the last key so paging is stable.
        var order = q.Sort switch
        {
            "id" => $"t.id {dir}",
            "created" => $"t.created_at {dir}, t.id {dir}",
            "updated" => $"t.updated_at {dir}, t.id {dir}",
            "status" => $"CASE t.status WHEN 'Open' THEN 1 WHEN 'In Progress' THEN 2 ELSE 3 END {dir}, t.id {dir}",
            "priority" => $"CASE t.priority WHEN 'High' THEN 1 WHEN 'Medium' THEN 2 ELSE 3 END {dir}, t.id {dir}",
            _ => throw new ArgumentException("Unknown sort key.", nameof(q)),
        };

        await using var conn = await db.OpenConnectionAsync(ct);

        long total;
        await using (var count = new NpgsqlCommand($"SELECT count(*) FROM {RowFrom} {where}", conn))
        {
            foreach (var (name, value) in values) count.Parameters.AddWithValue(name, value);
            total = Convert.ToInt64(await count.ExecuteScalarAsync(ct));
        }

        var items = new List<AdminTicketRow>();
        await using (var page = new NpgsqlCommand(
            $"SELECT {RowColumns} FROM {RowFrom} {where} ORDER BY {order} LIMIT @limit OFFSET @offset", conn))
        {
            foreach (var (name, value) in values) page.Parameters.AddWithValue(name, value);
            page.Parameters.AddWithValue("limit", q.Limit);
            page.Parameters.AddWithValue("offset", q.Offset);
            await using var r = await page.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                items.Add(new AdminTicketRow(
                    r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3),
                    r.GetInt64(4), r.GetString(5),
                    r.IsDBNull(6) ? null : r.GetInt64(6), r.IsDBNull(7) ? null : r.GetString(7),
                    r.GetDateTime(8), r.GetDateTime(9), r.GetBoolean(10)));
        }

        return new AdminTicketPage(total, q.Limit, q.Offset, items);
    }

    public static async Task<AdminTicketState?> GetStateAsync(NpgsqlDataSource db, long id, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand(
            "SELECT id, status, assigned_to, (deleted_at IS NOT NULL) FROM tickets WHERE id = @id");
        cmd.Parameters.AddWithValue("id", id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return new AdminTicketState(r.GetInt64(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetInt64(2), r.GetBoolean(3));
    }

    public static async Task<bool> IsActiveTechnicianAsync(NpgsqlDataSource db, long id, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand(
            "SELECT 1 FROM users WHERE id = @id AND role = 'Technician' AND is_active");
        cmd.Parameters.AddWithValue("id", id);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    public static async Task<AdminTicketView?> GetAsync(NpgsqlDataSource db, long id, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);

        AdminTicketDetail? ticket = null;
        await using (var cmd = new NpgsqlCommand("""
            SELECT t.id, t.title, t.description, t.priority, t.status,
                   t.created_by, c.first_name, t.assigned_to, a.first_name,
                   t.created_at, t.updated_at, t.resolved_at,
                   (t.deleted_at IS NOT NULL), t.deleted_at, d.first_name
            FROM tickets t
            JOIN users c ON c.id = t.created_by
            LEFT JOIN users a ON a.id = t.assigned_to
            LEFT JOIN users d ON d.id = t.deleted_by
            WHERE t.id = @id
            """, conn))
        {
            cmd.Parameters.AddWithValue("id", id);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct))
                ticket = new AdminTicketDetail(
                    r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4),
                    new AdminTicketUser(r.GetInt64(5), r.GetString(6)),
                    r.IsDBNull(7) ? null : new AdminTicketUser(r.GetInt64(7), r.GetString(8)),
                    r.GetDateTime(9), r.GetDateTime(10), r.IsDBNull(11) ? null : r.GetDateTime(11),
                    r.GetBoolean(12), r.IsDBNull(13) ? null : r.GetDateTime(13), r.IsDBNull(14) ? null : r.GetString(14));
        }
        if (ticket is null) return null;

        long noteCount;
        await using (var cmd = new NpgsqlCommand("SELECT count(*) FROM ticket_notes WHERE ticket_id = @id", conn))
        {
            cmd.Parameters.AddWithValue("id", id);
            noteCount = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
        }

        var notes = new List<AdminTicketNote>();
        await using (var cmd = new NpgsqlCommand("""
            SELECT n.id, n.author_id, u.first_name, n.body, n.created_at
            FROM ticket_notes n JOIN users u ON u.id = n.author_id
            WHERE n.ticket_id = @id
            ORDER BY n.created_at, n.id
            LIMIT @max
            """, conn))
        {
            cmd.Parameters.AddWithValue("id", id);
            cmd.Parameters.AddWithValue("max", MaxNotesShown);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                notes.Add(new AdminTicketNote(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3), r.GetDateTime(4)));
        }

        // The ticket's history: every event whose target is this ticket, newest first (one index probe)
        var events = new List<AdminEventBrief>();
        await using (var cmd = new NpgsqlCommand("""
            SELECT a.id, a.at, a.action, a.summary, a.actor_name, a.actor_role
            FROM audit_log a
            WHERE a.target_type = 'ticket' AND a.target_id = @id
            ORDER BY a.id DESC
            LIMIT 50
            """, conn))
        {
            cmd.Parameters.AddWithValue("id", id);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                events.Add(new AdminEventBrief(
                    r.GetInt64(0), r.GetDateTime(1), r.GetString(2), r.GetString(3),
                    r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5)));
        }

        var live = !ticket.Deleted;
        var actions = new AdminTicketActions(
            Edit: live,
            Assign: live && ticket.Status == "Open",
            Reassign: live && ticket.Status == "In Progress",
            Unassign: live && ticket.Status == "In Progress",
            Resolve: live && ticket.Status == "In Progress",
            Reopen: live && ticket.Status == "Resolved",
            Delete: live);

        var technicians = new List<AdminTicketUser>();
        if (actions.Assign || actions.Reassign)
        {
            await using var cmd = new NpgsqlCommand("""
                SELECT id, first_name FROM users
                WHERE role = 'Technician' AND is_active
                ORDER BY lower(first_name), id
                LIMIT @max
                """, conn);
            cmd.Parameters.AddWithValue("max", MaxTechnicians);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) technicians.Add(new AdminTicketUser(r.GetInt64(0), r.GetString(1)));
        }

        return new AdminTicketView(ticket, noteCount, notes, events, actions, technicians);
    }

    // ---------------------------------------------------------------- changes

    // Ticket for an active Employee (looked up by username). null = no such active Employee. admin.ticket_created.
    public static async Task<long?> CreateAsync(
        NpgsqlDataSource db, long actorId, string? ip, string createdFor,
        string title, string description, string priority, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("""
            WITH c AS (
                SELECT id, first_name FROM users
                WHERE lower(first_name) = lower(@created_for) AND role = 'Employee' AND is_active
            ), t AS (
                INSERT INTO tickets (title, description, priority, status, created_by)
                SELECT @title, @description, @priority, 'Open', c.id FROM c
                RETURNING id, title, priority, created_by
            ), a AS (
                INSERT INTO audit_log (actor_id, actor_name, actor_role, action, target_type, target_id, summary, detail, ip)
                SELECT adm.id, adm.first_name, adm.role, 'admin.ticket_created', 'ticket', t.id,
                       left('Created ticket #' || t.id || ' for ' || c.first_name, 500),
                       jsonb_build_object('title', left(t.title, 100), 'priority', t.priority, 'created_for', c.first_name),
                       NULLIF(@ip, '')::inet
                FROM t JOIN c ON c.id = t.created_by JOIN users adm ON adm.id = @actor_id
            )
            SELECT id FROM t
            """);
        cmd.Parameters.AddWithValue("actor_id", actorId);
        cmd.Parameters.AddWithValue("created_for", createdFor);
        cmd.Parameters.AddWithValue("title", title);
        cmd.Parameters.AddWithValue("description", description);
        cmd.Parameters.AddWithValue("priority", priority);
        AuditLog.AddIp(cmd, ip);
        return await cmd.ExecuteScalarAsync(ct) is { } v ? Convert.ToInt64(v) : null;
    }

    // Edit title / description / priority (null = leave as is). false = the ticket is gone, deleted, or nothing differs.
    public static async Task<bool> EditAsync(
        NpgsqlDataSource db, long actorId, string? ip, long id,
        string? title, string? description, string? priority, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("""
            WITH old AS (
                SELECT t.id, t.title, t.description, t.priority FROM tickets t
                WHERE t.id = @id AND t.deleted_at IS NULL FOR UPDATE
            ), t AS (
                UPDATE tickets x
                SET title = COALESCE(@title::text, x.title),
                    description = COALESCE(@description::text, x.description),
                    priority = COALESCE(@priority::text, x.priority),
                    updated_at = now()
                FROM old
                WHERE x.id = old.id
                  AND (COALESCE(@title::text, old.title) IS DISTINCT FROM old.title
                       OR COALESCE(@description::text, old.description) IS DISTINCT FROM old.description
                       OR COALESCE(@priority::text, old.priority) IS DISTINCT FROM old.priority)
                RETURNING x.id, x.title, x.priority, old.title AS old_title, old.priority AS old_priority,
                          (x.description IS DISTINCT FROM old.description) AS description_changed
            ), a AS (
                INSERT INTO audit_log (actor_id, actor_name, actor_role, action, target_type, target_id, summary, detail, ip)
                SELECT adm.id, adm.first_name, adm.role, 'admin.ticket_updated', 'ticket', t.id,
                       'Edited ticket #' || t.id,
                       jsonb_strip_nulls(jsonb_build_object(
                           'fields', to_jsonb(array_remove(ARRAY[
                               CASE WHEN t.title <> t.old_title THEN 'title' END,
                               CASE WHEN t.description_changed THEN 'description' END,
                               CASE WHEN t.priority <> t.old_priority THEN 'priority' END], NULL)),
                           'title', CASE WHEN t.title <> t.old_title
                                         THEN jsonb_build_object('from', left(t.old_title, 100), 'to', left(t.title, 100)) END,
                           'priority', CASE WHEN t.priority <> t.old_priority
                                            THEN jsonb_build_object('from', t.old_priority, 'to', t.priority) END)),
                       NULLIF(@ip, '')::inet
                FROM t JOIN users adm ON adm.id = @actor_id
            )
            SELECT count(*) FROM t
            """);
        cmd.Parameters.AddWithValue("actor_id", actorId);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("title", (object?)title ?? DBNull.Value);
        cmd.Parameters.AddWithValue("description", (object?)description ?? DBNull.Value);
        cmd.Parameters.AddWithValue("priority", (object?)priority ?? DBNull.Value);
        AuditLog.AddIp(cmd, ip);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct)) == 1;
    }

    // The part every lifecycle statement starts with: lock the ticket if it is still in the state the admin saw.
    const string OldTicket = """
        old AS (
            SELECT t.id, t.status, t.assigned_to FROM tickets t
            WHERE t.id = @id AND t.deleted_at IS NULL
              AND t.status = @expected_status
              AND t.assigned_to IS NOT DISTINCT FROM @expected_assignee::bigint
            FOR UPDATE
        )
        """;

    static async Task<TicketChangeResult> RunAsync(NpgsqlCommand cmd, CancellationToken ct)
    {
        await using var r = await cmd.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        return new TicketChangeResult(r.GetInt64(0) == 1, r.IsDBNull(1) ? null : r.GetString(1));
    }

    static NpgsqlCommand StateCommand(
        NpgsqlDataSource db, string sql, long actorId, string? ip, long id, string expectedStatus, long? expectedAssignee)
    {
        var cmd = db.CreateCommand(sql);
        cmd.Parameters.AddWithValue("actor_id", actorId);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("expected_status", expectedStatus);
        cmd.Parameters.AddWithValue("expected_assignee", (object?)expectedAssignee ?? DBNull.Value);
        AuditLog.AddIp(cmd, ip);
        return cmd;
    }

    // Open -> In Progress (assigned) or In Progress -> In Progress (another technician: reassigned)
    public static async Task<TicketChangeResult> AssignAsync(
        NpgsqlDataSource db, long actorId, string? ip, long id, string expectedStatus, long? expectedAssignee,
        long technicianId, CancellationToken ct)
    {
        await using var cmd = StateCommand(db, $"""
            WITH {OldTicket},
            tech AS (
                SELECT w.id, w.first_name FROM users w
                WHERE w.id = @technician_id AND w.role = 'Technician' AND w.is_active
                FOR SHARE
            ), t AS (
                UPDATE tickets x
                SET status = 'In Progress', assigned_to = tech.id, resolved_at = NULL, updated_at = now()
                FROM old, tech
                WHERE x.id = old.id AND old.assigned_to IS DISTINCT FROM tech.id
                RETURNING x.id, x.title, x.status, old.status AS old_status, old.assigned_to AS previous_id,
                          tech.first_name AS tech_name
            ), a AS (
                INSERT INTO audit_log (actor_id, actor_name, actor_role, action, target_type, target_id, summary, detail, ip)
                SELECT adm.id, adm.first_name, adm.role,
                       CASE WHEN t.old_status = 'Open' THEN 'admin.ticket_assigned' ELSE 'admin.ticket_reassigned' END,
                       'ticket', t.id,
                       left(CASE WHEN t.old_status = 'Open'
                                 THEN 'Assigned ticket #' || t.id || ' to ' || t.tech_name
                                 ELSE 'Reassigned ticket #' || t.id || ' to ' || t.tech_name END, 500),
                       jsonb_strip_nulls(jsonb_build_object(
                           'title', left(t.title, 100), 'to', t.tech_name,
                           'from', (SELECT p.first_name FROM users p WHERE p.id = t.previous_id))),
                       NULLIF(@ip, '')::inet
                FROM t JOIN users adm ON adm.id = @actor_id
            )
            SELECT count(*), max(status) FROM t
            """, actorId, ip, id, expectedStatus, expectedAssignee);
        cmd.Parameters.AddWithValue("technician_id", technicianId);
        return await RunAsync(cmd, ct);
    }

    // In Progress -> Open, no assignee
    public static async Task<TicketChangeResult> UnassignAsync(
        NpgsqlDataSource db, long actorId, string? ip, long id, string expectedStatus, long? expectedAssignee, CancellationToken ct)
    {
        await using var cmd = StateCommand(db, $"""
            WITH {OldTicket},
            t AS (
                UPDATE tickets x
                SET status = 'Open', assigned_to = NULL, updated_at = now()
                FROM old
                WHERE x.id = old.id AND old.status = 'In Progress'
                RETURNING x.id, x.title, x.status, old.assigned_to AS previous_id
            ), a AS (
                INSERT INTO audit_log (actor_id, actor_name, actor_role, action, target_type, target_id, summary, detail, ip)
                SELECT adm.id, adm.first_name, adm.role, 'admin.ticket_unassigned', 'ticket', t.id,
                       'Returned ticket #' || t.id || ' to Open',
                       jsonb_strip_nulls(jsonb_build_object(
                           'title', left(t.title, 100),
                           'from', (SELECT p.first_name FROM users p WHERE p.id = t.previous_id))),
                       NULLIF(@ip, '')::inet
                FROM t JOIN users adm ON adm.id = @actor_id
            )
            SELECT count(*), max(status) FROM t
            """, actorId, ip, id, expectedStatus, expectedAssignee);
        return await RunAsync(cmd, ct);
    }

    // In Progress -> Resolved (sets resolved_at); the assignee stays as the one who is credited
    public static async Task<TicketChangeResult> ResolveAsync(
        NpgsqlDataSource db, long actorId, string? ip, long id, string expectedStatus, long? expectedAssignee, CancellationToken ct)
    {
        await using var cmd = StateCommand(db, $"""
            WITH {OldTicket},
            t AS (
                UPDATE tickets x
                SET status = 'Resolved', resolved_at = now(), updated_at = now()
                FROM old
                WHERE x.id = old.id AND old.status = 'In Progress'
                RETURNING x.id, x.title, x.status, old.assigned_to AS assignee_id
            ), a AS (
                INSERT INTO audit_log (actor_id, actor_name, actor_role, action, target_type, target_id, summary, detail, ip)
                SELECT adm.id, adm.first_name, adm.role, 'admin.ticket_resolved', 'ticket', t.id,
                       'Resolved ticket #' || t.id,
                       jsonb_strip_nulls(jsonb_build_object(
                           'title', left(t.title, 100),
                           'assignee', (SELECT p.first_name FROM users p WHERE p.id = t.assignee_id))),
                       NULLIF(@ip, '')::inet
                FROM t JOIN users adm ON adm.id = @actor_id
            )
            SELECT count(*), max(status) FROM t
            """, actorId, ip, id, expectedStatus, expectedAssignee);
        return await RunAsync(cmd, ct);
    }

    // Resolved -> In Progress with the same technician, when he is still an active Technician; otherwise Open, unassigned
    public static async Task<TicketChangeResult> ReopenAsync(
        NpgsqlDataSource db, long actorId, string? ip, long id, string expectedStatus, long? expectedAssignee, CancellationToken ct)
    {
        await using var cmd = StateCommand(db, $"""
            WITH {OldTicket},
            keep AS (
                SELECT w.id, w.first_name FROM users w
                WHERE w.id = (SELECT old.assigned_to FROM old) AND w.role = 'Technician' AND w.is_active
                FOR SHARE
            ), t AS (
                UPDATE tickets x
                SET status = CASE WHEN EXISTS (SELECT 1 FROM keep) THEN 'In Progress' ELSE 'Open' END,
                    assigned_to = (SELECT keep.id FROM keep),
                    resolved_at = NULL, updated_at = now()
                FROM old
                WHERE x.id = old.id AND old.status = 'Resolved'
                RETURNING x.id, x.title, x.status, (SELECT keep.first_name FROM keep) AS assignee_name
            ), a AS (
                INSERT INTO audit_log (actor_id, actor_name, actor_role, action, target_type, target_id, summary, detail, ip)
                SELECT adm.id, adm.first_name, adm.role, 'admin.ticket_reopened', 'ticket', t.id,
                       'Reopened ticket #' || t.id,
                       jsonb_strip_nulls(jsonb_build_object(
                           'title', left(t.title, 100), 'to_status', t.status, 'assignee', t.assignee_name)),
                       NULLIF(@ip, '')::inet
                FROM t JOIN users adm ON adm.id = @actor_id
            )
            SELECT count(*), max(status) FROM t
            """, actorId, ip, id, expectedStatus, expectedAssignee);
        return await RunAsync(cmd, ct);
    }

    // Soft delete: the ticket and its notes stay in the database, hidden from every normal page and API.
    // false = already deleted or gone. The tickets_deleted_guard trigger (db/006) keeps it deleted afterwards.
    public static async Task<bool> DeleteAsync(
        NpgsqlDataSource db, long actorId, string? ip, long id, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("""
            WITH t AS (
                UPDATE tickets x
                SET deleted_at = now(), deleted_by = @actor_id
                WHERE x.id = @id AND x.deleted_at IS NULL
                RETURNING x.id, x.title, x.status
            ), a AS (
                INSERT INTO audit_log (actor_id, actor_name, actor_role, action, target_type, target_id, summary, detail, ip)
                SELECT adm.id, adm.first_name, adm.role, 'admin.ticket_deleted', 'ticket', t.id,
                       'Deleted ticket #' || t.id,
                       jsonb_build_object('title', left(t.title, 100), 'status', t.status),
                       NULLIF(@ip, '')::inet
                FROM t JOIN users adm ON adm.id = @actor_id
            )
            SELECT count(*) FROM t
            """);
        cmd.Parameters.AddWithValue("actor_id", actorId);
        cmd.Parameters.AddWithValue("id", id);
        AuditLog.AddIp(cmd, ip);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct)) == 1;
    }
}
