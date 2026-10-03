using System.Text;
using Npgsql;

// What the Admin user pages receive. No field here can carry a password, a hash or a token:
// the SQL below names its columns and never selects those.
public record AdminUserRow(
    long Id, string Username, string Role, bool IsActive, bool MustChangePassword,
    DateTime? CreatedAt,     // null = the account is older than the creation date column ("before tracking")
    DateTime? LastLogin);    // null = no login recorded (Level 29 started recording; older logins are unknown)

public record AdminUserPage(long Total, int Limit, int Offset, List<AdminUserRow> Items);

// Just enough of a user to decide what an action may do (404 / Admin / state checks before the change)
public record AdminUserTarget(long Id, string Username, string Role, bool IsActive);

public record AdminTicketBrief(long Id, string Title, string Status, string Priority, DateTime CreatedAt);
public record AdminNoteBrief(long Id, long TicketId, string Excerpt, DateTime CreatedAt);
public record AdminEventBrief(long Id, DateTime At, string Action, string Summary, string? ActorName, string? ActorRole);
public record AdminUserCounts(long TicketsCreated, long TicketsAssigned, long InProgress, long Notes);

public record AdminUserDetail(
    AdminUserRow User, long ActiveSessions, AdminUserCounts Counts,
    List<AdminTicketBrief> LatestCreated, List<AdminTicketBrief> LatestAssigned,
    List<AdminNoteBrief> LatestNotes, List<AdminEventBrief> Events);

// The list request after AdminUsers.TryParseQuery has validated it. Sort is one of name / role / created / lastlogin.
public record UserQuery(string? Text, string? Role, string? Status, string Sort, bool Desc, int Limit, int Offset);

// Outcome of deactivating a user
public record DeactivateResult(bool Changed, long ReleasedTickets, long EndedSessions);

// Outcome of a role change (Technician -> Employee releases the In Progress tickets)
public record RoleChangeResult(bool Changed, long ReleasedTickets);

// All SQL for the Admin user pages lives here. Endpoints never build SQL.
//
// Every change is ONE statement:
//  - the audit row for the action is inserted by the same statement (WITH ... INSERT INTO audit_log), or, for
//    rename / deactivate / reactivate, by the users_audit trigger of db/005_admin.sql. To make that trigger know WHO
//    acted, the statement starts with  WITH ctx AS (SELECT set_config('app.actor_id', ...), set_config('app.actor_ip', ...))
//    and the UPDATE reads FROM ctx, so ctx is always evaluated first. Both settings are transaction-local, so nothing
//    leaks to the next user of the pooled connection;
//  - every UPDATE keeps its guard in the WHERE (not an Admin, expected state), so a stale page or a second Admin acting
//    at the same moment changes nothing and the caller answers 409.
public static class AdminUsersRepository
{
    public const int MaxTextLength = 50;

    // Same columns everywhere a user row is shown. last_login = newest auth.login row of the account, read through
    // audit_log_actor_action_idx (actor_id, action, id DESC): one index probe per user.
    const string RowColumns = """
        u.id, u.first_name, u.role, u.is_active, u.must_change_password, u.created_at,
        (SELECT a.at FROM audit_log a
         WHERE a.actor_id = u.id AND a.action = 'auth.login'
         ORDER BY a.id DESC LIMIT 1) AS last_login
        """;

    static AdminUserRow ReadRow(NpgsqlDataReader r) => new(
        r.GetInt64(0),
        r.GetString(1),
        r.IsDBNull(2) ? "Employee" : r.GetString(2),
        !r.IsDBNull(3) && r.GetBoolean(3),
        !r.IsDBNull(4) && r.GetBoolean(4),
        r.IsDBNull(5) ? null : r.GetDateTime(5),
        r.IsDBNull(6) ? null : r.GetDateTime(6));

    // % and _ typed by the Admin are letters, not wildcards (backslash is the escape character of the ILIKE below)
    static string EscapeLike(string text) =>
        text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    // ---------------------------------------------------------------- reads

    public static async Task<AdminUserPage> ListAsync(NpgsqlDataSource db, UserQuery q, CancellationToken ct)
    {
        // The WHERE is built from fixed pieces; every value travels as a parameter.
        var where = new StringBuilder("WHERE true");
        var values = new List<(string Name, object Value)>();

        if (!string.IsNullOrEmpty(q.Text))
        {
            where.Append(" AND u.first_name ILIKE @pattern ESCAPE '\\'");
            values.Add(("pattern", "%" + EscapeLike(q.Text) + "%"));
        }
        if (q.Role is not null)
        {
            where.Append(" AND u.role = @role");
            values.Add(("role", q.Role));
        }
        if (q.Status == "active") where.Append(" AND u.is_active");
        else if (q.Status == "disabled") where.Append(" AND NOT u.is_active");

        var dir = q.Desc ? "DESC" : "ASC";
        // Sort keys come from this fixed list only; "id" is the last key so paging is stable.
        var order = q.Sort switch
        {
            "name" => $"lower(u.first_name) {dir}, u.id {dir}",
            "role" => $"u.role {dir}, lower(u.first_name) ASC, u.id ASC",
            "created" => $"u.created_at {dir} NULLS LAST, u.id {dir}",
            "lastlogin" => $"last_login {dir} NULLS LAST, u.id {dir}",
            _ => throw new ArgumentException("Unknown sort key.", nameof(q)),
        };

        await using var conn = await db.OpenConnectionAsync(ct);

        long total;
        await using (var count = new NpgsqlCommand($"SELECT count(*) FROM users u {where}", conn))
        {
            foreach (var (name, value) in values) count.Parameters.AddWithValue(name, value);
            total = Convert.ToInt64(await count.ExecuteScalarAsync(ct));
        }

        var items = new List<AdminUserRow>();
        await using (var page = new NpgsqlCommand(
            $"SELECT {RowColumns} FROM users u {where} ORDER BY {order} LIMIT @limit OFFSET @offset", conn))
        {
            foreach (var (name, value) in values) page.Parameters.AddWithValue(name, value);
            page.Parameters.AddWithValue("limit", q.Limit);
            page.Parameters.AddWithValue("offset", q.Offset);
            await using var reader = await page.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) items.Add(ReadRow(reader));
        }

        return new AdminUserPage(total, q.Limit, q.Offset, items);
    }

    // Used before every action: what is this account? (null = no such user)
    public static async Task<AdminUserTarget?> GetTargetAsync(NpgsqlDataSource db, long id, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand(
            "SELECT id, first_name, role, is_active FROM users WHERE id = @id");
        cmd.Parameters.AddWithValue("id", id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return new AdminUserTarget(
            r.GetInt64(0), r.GetString(1),
            r.IsDBNull(2) ? "Employee" : r.GetString(2),
            !r.IsDBNull(3) && r.GetBoolean(3));
    }

    public static async Task<AdminUserDetail?> GetAsync(NpgsqlDataSource db, long id, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);

        AdminUserRow? user = null;
        await using (var cmd = new NpgsqlCommand($"SELECT {RowColumns} FROM users u WHERE u.id = @id", conn))
        {
            cmd.Parameters.AddWithValue("id", id);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct)) user = ReadRow(r);
        }
        if (user is null) return null;

        long sessions, created, assigned, inProgress, notes;
        await using (var cmd = new NpgsqlCommand("""
            SELECT (SELECT count(*) FROM sessions s WHERE s.user_id = @id AND s.expires_at > now()),
                   (SELECT count(*) FROM tickets t WHERE t.created_by = @id AND t.deleted_at IS NULL),
                   (SELECT count(*) FROM tickets t WHERE t.assigned_to = @id AND t.deleted_at IS NULL),
                   (SELECT count(*) FROM tickets t WHERE t.assigned_to = @id AND t.status = 'In Progress' AND t.deleted_at IS NULL),
                   (SELECT count(*) FROM ticket_notes n JOIN tickets t ON t.id = n.ticket_id WHERE n.author_id = @id AND t.deleted_at IS NULL)
            """, conn))
        {
            cmd.Parameters.AddWithValue("id", id);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            sessions = r.GetInt64(0); created = r.GetInt64(1); assigned = r.GetInt64(2);
            inProgress = r.GetInt64(3); notes = r.GetInt64(4);
        }

        var createdList = await TicketsAsync(conn,
            "WHERE t.created_by = @id AND t.deleted_at IS NULL ORDER BY t.created_at DESC, t.id DESC", id, ct);
        var assignedList = await TicketsAsync(conn,
            "WHERE t.assigned_to = @id AND t.deleted_at IS NULL ORDER BY t.updated_at DESC, t.id DESC", id, ct);

        var noteList = new List<AdminNoteBrief>();
        await using (var cmd = new NpgsqlCommand("""
            SELECT n.id, n.ticket_id, left(n.body, 200), n.created_at
            FROM ticket_notes n
            JOIN tickets t ON t.id = n.ticket_id
            WHERE n.author_id = @id AND t.deleted_at IS NULL
            ORDER BY n.id DESC
            LIMIT 5
            """, conn))
        {
            cmd.Parameters.AddWithValue("id", id);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                noteList.Add(new AdminNoteBrief(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetDateTime(3)));
        }

        // Things this account did, and things done to it (changes by an Admin, failed logins). Two index probes, merged.
        var events = new List<AdminEventBrief>();
        await using (var cmd = new NpgsqlCommand("""
            SELECT e.id, e.at, e.action, e.summary, e.actor_name, e.actor_role
            FROM (
                (SELECT a.id, a.at, a.action, a.summary, a.actor_name, a.actor_role
                 FROM audit_log a WHERE a.actor_id = @id ORDER BY a.id DESC LIMIT 10)
                UNION
                (SELECT a.id, a.at, a.action, a.summary, a.actor_name, a.actor_role
                 FROM audit_log a WHERE a.target_type = 'user' AND a.target_id = @id ORDER BY a.id DESC LIMIT 10)
            ) e
            ORDER BY e.id DESC
            LIMIT 10
            """, conn))
        {
            cmd.Parameters.AddWithValue("id", id);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                events.Add(new AdminEventBrief(
                    r.GetInt64(0), r.GetDateTime(1), r.GetString(2), r.GetString(3),
                    r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5)));
        }

        return new AdminUserDetail(user, sessions, new AdminUserCounts(created, assigned, inProgress, notes),
            createdList, assignedList, noteList, events);
    }

    static async Task<List<AdminTicketBrief>> TicketsAsync(
        NpgsqlConnection conn, string whereAndOrder, long id, CancellationToken ct)
    {
        var list = new List<AdminTicketBrief>();
        await using var cmd = new NpgsqlCommand(
            $"SELECT t.id, t.title, t.status, t.priority, t.created_at FROM tickets t {whereAndOrder} LIMIT 5", conn);
        cmd.Parameters.AddWithValue("id", id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new AdminTicketBrief(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetDateTime(4)));
        return list;
    }

    // ---------------------------------------------------------------- changes

    // Creates an Employee or Technician with a temporary password (the role is validated by the endpoint; the
    // users_role_check and the Admin guard trigger refuse anything else). Throws PostgresException 23505 when the
    // name is taken (users_first_name_lower_uq, case-insensitive), as register does.
    public static async Task<long> CreateAsync(
        NpgsqlDataSource db, long actorId, string? ip, string username, string passwordHash, string role, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("""
            WITH u AS (
                INSERT INTO users (first_name, password, role, must_change_password)
                VALUES (@username, @password_hash, @role, true)
                RETURNING id, first_name, role
            ), a AS (
                INSERT INTO audit_log (actor_id, actor_name, actor_role, action, target_type, target_id, summary, detail, ip)
                SELECT adm.id, adm.first_name, adm.role, 'admin.user_created', 'user', u.id,
                       left('Created user ' || u.first_name, 500),
                       jsonb_build_object('role', u.role), NULLIF(@ip, '')::inet
                FROM u JOIN users adm ON adm.id = @actor_id
            )
            SELECT id FROM u
            """);
        cmd.Parameters.AddWithValue("username", username);
        cmd.Parameters.AddWithValue("password_hash", passwordHash);
        cmd.Parameters.AddWithValue("role", role);
        cmd.Parameters.AddWithValue("actor_id", actorId);
        AuditLog.AddIp(cmd, ip);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
    }

    // The first lines of every statement below that must tell the users_audit trigger who is acting
    const string ActorContext = """
        ctx AS (
            SELECT set_config('app.actor_id', @actor_id::text, true) AS actor,
                   set_config('app.actor_ip', @ip, true) AS ip
        )
        """;

    // true = renamed. Throws PostgresException 23505 when the new name is taken.
    public static async Task<bool> RenameAsync(
        NpgsqlDataSource db, long actorId, string? ip, long id, string username, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand($"""
            WITH {ActorContext},
            u AS (
                UPDATE users x SET first_name = @username
                FROM ctx
                WHERE x.id = @id AND x.role IS DISTINCT FROM 'Admin' AND x.first_name <> @username
                RETURNING x.id
            )
            SELECT count(*) FROM u
            """);
        cmd.Parameters.AddWithValue("actor_id", actorId);
        cmd.Parameters.AddWithValue("username", username);
        cmd.Parameters.AddWithValue("id", id);
        AuditLog.AddIp(cmd, ip);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct)) == 1;
    }

    // Deactivation, the whole rule in one statement inside the lock transaction (ADMIN_PLAN.md 3.7, 3.8):
    //  1. the account becomes inactive            -> users_audit writes admin.user_deactivated
    //  2. its In Progress tickets go back to Open, unassigned (Resolved tickets keep their assignee)
    //                                              -> one ticket.released row per ticket
    //  3. all its sessions are deleted             -> it is locked out on its very next request
    public static async Task<DeactivateResult> DeactivateAsync(
        NpgsqlDataSource db, long actorId, string? ip, long id, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await LockUserAsync(conn, tx, id, ct);

        await using var cmd = new NpgsqlCommand($"""
            WITH {ActorContext},
            u AS (
                UPDATE users x SET is_active = false
                FROM ctx
                WHERE x.id = @id AND x.is_active AND x.role IS DISTINCT FROM 'Admin'
                RETURNING x.id, x.first_name
            ), rel AS (
                UPDATE tickets t SET status = 'Open', assigned_to = NULL, updated_at = now()
                FROM u
                WHERE t.assigned_to = u.id AND t.status = 'In Progress' AND t.deleted_at IS NULL
                RETURNING t.id, u.first_name AS tech_name
            ), s AS (
                DELETE FROM sessions WHERE user_id IN (SELECT id FROM u) RETURNING 1
            ), a AS (
                INSERT INTO audit_log (actor_id, actor_name, actor_role, action, target_type, target_id, summary, detail, ip)
                SELECT adm.id, adm.first_name, adm.role, 'ticket.released', 'ticket', rel.id,
                       'Ticket #' || rel.id || ' returned to Open',
                       jsonb_build_object('reason', 'user_deactivated', 'from_user', rel.tech_name), NULLIF(@ip, '')::inet
                FROM rel JOIN users adm ON adm.id = @actor_id
            )
            SELECT (SELECT count(*) FROM u), (SELECT count(*) FROM rel), (SELECT count(*) FROM s)
            """, conn, tx);
        cmd.Parameters.AddWithValue("actor_id", actorId);
        cmd.Parameters.AddWithValue("id", id);
        AuditLog.AddIp(cmd, ip);

        DeactivateResult result;
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            await r.ReadAsync(ct);
            result = new DeactivateResult(r.GetInt64(0) == 1, r.GetInt64(1), r.GetInt64(2));
        }
        await tx.CommitAsync(ct);
        return result;
    }

    // Level 31. Changes Employee <-> Technician, the whole rule in one statement (inside the lock transaction):
    //  1. the role changes only if it is still the one the admin saw (compare and set) and the target is no Admin
    //                                              -> users_audit writes admin.role_changed (from / to / actor / ip)
    //  2. Technician -> Employee: its In Progress tickets go back to Open, unassigned  -> one ticket.released row each
    //     (Resolved tickets keep their assignee; Employee -> Technician touches no ticket)
    // Sessions are NOT deleted: the role is read from the database on every request, so the new role is in force at
    // the user's very next request, and the user does not have to log in again.
    public static async Task<RoleChangeResult> ChangeRoleAsync(
        NpgsqlDataSource db, long actorId, string? ip, long id, string expectedRole, string newRole, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await LockUserAsync(conn, tx, id, ct);

        await using var cmd = new NpgsqlCommand($"""
            WITH {ActorContext},
            u AS (
                UPDATE users x SET role = @new_role
                FROM ctx
                WHERE x.id = @id AND x.role IS DISTINCT FROM 'Admin'
                  AND COALESCE(x.role, 'Employee') = @expected_role AND @expected_role <> @new_role
                RETURNING x.id, x.first_name
            ), rel AS (
                UPDATE tickets t SET status = 'Open', assigned_to = NULL, updated_at = now()
                FROM u
                WHERE @new_role = 'Employee' AND t.assigned_to = u.id AND t.status = 'In Progress' AND t.deleted_at IS NULL
                RETURNING t.id, u.first_name AS tech_name
            ), a AS (
                INSERT INTO audit_log (actor_id, actor_name, actor_role, action, target_type, target_id, summary, detail, ip)
                SELECT adm.id, adm.first_name, adm.role, 'ticket.released', 'ticket', rel.id,
                       'Ticket #' || rel.id || ' returned to Open',
                       jsonb_build_object('reason', 'role_changed', 'from_user', rel.tech_name), NULLIF(@ip, '')::inet
                FROM rel JOIN users adm ON adm.id = @actor_id
            )
            SELECT (SELECT count(*) FROM u), (SELECT count(*) FROM rel)
            """, conn, tx);
        cmd.Parameters.AddWithValue("actor_id", actorId);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("expected_role", expectedRole);
        cmd.Parameters.AddWithValue("new_role", newRole);
        AuditLog.AddIp(cmd, ip);

        RoleChangeResult result;
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            await r.ReadAsync(ct);
            result = new RoleChangeResult(r.GetInt64(0) == 1, r.GetInt64(1));
        }
        await tx.CommitAsync(ct);
        return result;
    }

    // Locks the user's row until the transaction ends. Why: a technician's "take ticket" (TicketRepository.TakeSql) takes a
    // share lock on the same row. Whichever comes first, the other waits: a take that began before the change is finished
    // (and committed) before the statement below starts, so that statement's fresh snapshot SEES the ticket it just took
    // and releases it; a take that begins after the change waits, then sees the new role / state and takes nothing.
    // (Without the lock one statement could miss a ticket that was taken a moment earlier.)
    static async Task LockUserAsync(NpgsqlConnection conn, NpgsqlTransaction tx, long id, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT id FROM users WHERE id = @id FOR UPDATE", conn, tx);
        cmd.Parameters.AddWithValue("id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // true = reactivated (users_audit writes admin.user_reactivated). The user logs in with the password they had.
    public static async Task<bool> ReactivateAsync(
        NpgsqlDataSource db, long actorId, string? ip, long id, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand($"""
            WITH {ActorContext},
            u AS (
                UPDATE users x SET is_active = true
                FROM ctx
                WHERE x.id = @id AND NOT x.is_active AND x.role IS DISTINCT FROM 'Admin'
                RETURNING x.id
            )
            SELECT count(*) FROM u
            """);
        cmd.Parameters.AddWithValue("actor_id", actorId);
        cmd.Parameters.AddWithValue("id", id);
        AuditLog.AddIp(cmd, ip);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct)) == 1;
    }

    // New temporary password (already hashed by the caller) + must change at next login + every session ended,
    // and admin.password_reset written, in one statement. (changed, endedSessions)
    public static async Task<(bool Changed, long EndedSessions)> ResetPasswordAsync(
        NpgsqlDataSource db, long actorId, string? ip, long id, string passwordHash, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("""
            WITH u AS (
                UPDATE users x SET password = @password_hash, must_change_password = true
                WHERE x.id = @id AND x.is_active AND x.role IS DISTINCT FROM 'Admin'
                RETURNING x.id, x.first_name
            ), s AS (
                DELETE FROM sessions WHERE user_id IN (SELECT id FROM u) RETURNING 1
            ), a AS (
                INSERT INTO audit_log (actor_id, actor_name, actor_role, action, target_type, target_id, summary, detail, ip)
                SELECT adm.id, adm.first_name, adm.role, 'admin.password_reset', 'user', u.id,
                       left('Reset the password of ' || u.first_name, 500),
                       jsonb_build_object('sessions_ended', (SELECT count(*) FROM s)), NULLIF(@ip, '')::inet
                FROM u JOIN users adm ON adm.id = @actor_id
            )
            SELECT (SELECT count(*) FROM u), (SELECT count(*) FROM s)
            """);
        cmd.Parameters.AddWithValue("password_hash", passwordHash);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("actor_id", actorId);
        AuditLog.AddIp(cmd, ip);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        return (r.GetInt64(0) == 1, r.GetInt64(1));
    }
}
