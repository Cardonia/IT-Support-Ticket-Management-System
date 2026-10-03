using System.Text;
using Npgsql;

// One line of the activity page. The sentence is `Summary` (written by the application, never user free text beyond
// a 100-character ticket title); only these fields leave the audit table: no `detail`, no address.
public record AdminActivityItem(
    long Id, DateTime At, string Action, string Summary,
    long? ActorId, string? ActorName, string? ActorRole, bool Terminal,
    string? TargetType, long? TargetId, long? TicketId);

public record AdminActivityPage(int Limit, bool HasMore, long? NextBefore, List<AdminActivityItem> Items);

public record ActivityQuery(
    string? Group, string? Action, string? User, string? TargetType, long? TargetId,
    DateTime? From, DateTime? To, long? Before, int Limit);

// All SQL of the Admin activity page (the audit log, read only). Newest first by id, keyset paging with `before`.
// "A user's activity" = events done BY the user plus events done TO the user: two index probes that are merged
// (UNION), instead of one OR that would scan.
public static class AdminActivityRepository
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 100;

    // Same list as the audit_log_action_check constraint (db/005_admin.sql). A new event needs a migration AND this list.
    public static readonly string[] Actions =
    {
        "user.registered", "auth.login", "auth.logout", "auth.login_failed", "user.password_changed",
        "ticket.created", "ticket.taken", "ticket.resolved", "ticket.released", "note.added",
        "admin.user_created", "admin.user_updated", "admin.role_changed", "admin.user_deactivated",
        "admin.user_reactivated", "admin.password_reset",
        "admin.ticket_created", "admin.ticket_updated", "admin.ticket_assigned", "admin.ticket_reassigned",
        "admin.ticket_unassigned", "admin.ticket_resolved", "admin.ticket_reopened", "admin.ticket_deleted",
        "security.admin_denied", "security.reauth_failed",
    };

    // The part of an action before the dot
    public static readonly string[] Groups = { "auth", "user", "ticket", "note", "admin", "security" };
    public static readonly string[] TargetTypes = { "user", "ticket", "note" };

    const string Columns = """
        a.id, a.at, a.action, a.summary, a.actor_id, a.actor_name, a.actor_role,
        COALESCE(a.actor_id IS NULL AND a.detail->>'via' = 'terminal', false) AS terminal,
        a.target_type, a.target_id,
        CASE WHEN a.target_type = 'ticket' THEN a.target_id
             WHEN a.target_type = 'note' AND a.detail->>'ticket_id' ~ '^[0-9]{1,18}$' THEN (a.detail->>'ticket_id')::bigint
        END AS ticket_id
        """;

    public static async Task<AdminActivityPage> ListAsync(NpgsqlDataSource db, ActivityQuery q, CancellationToken ct)
    {
        var empty = new AdminActivityPage(q.Limit, false, null, new List<AdminActivityItem>());

        long? userId = null;
        if (q.User is not null)
        {
            userId = await AdminNotesRepository.FindUserIdAsync(db, q.User, ct);
            if (userId is null) return empty;                      // no such user: no events, not an error
        }

        var common = new StringBuilder();
        var values = new List<(string Name, object Value)>();
        if (q.Group is not null) { common.Append(" AND a.action LIKE @group_pattern"); values.Add(("group_pattern", q.Group + ".%")); }
        if (q.Action is not null) { common.Append(" AND a.action = @action"); values.Add(("action", q.Action)); }
        if (q.TargetType is not null) { common.Append(" AND a.target_type = @target_type"); values.Add(("target_type", q.TargetType)); }
        if (q.TargetId is not null) { common.Append(" AND a.target_id = @target_id"); values.Add(("target_id", q.TargetId.Value)); }
        if (q.From is not null) { common.Append(" AND a.at >= @from"); values.Add(("from", q.From.Value)); }
        if (q.To is not null) { common.Append(" AND a.at < @to"); values.Add(("to", q.To.Value)); }
        if (q.Before is not null) { common.Append(" AND a.id < @before"); values.Add(("before", q.Before.Value)); }

        string Probe(string own) =>
            $"(SELECT {Columns} FROM audit_log a WHERE {own}{common} ORDER BY a.id DESC LIMIT @limit_plus)";

        string sql;
        if (userId is null) sql = $"SELECT * FROM {Probe("true")} e ORDER BY id DESC";
        else
        {
            values.Add(("user_id", userId.Value));
            sql = $"""
                SELECT * FROM (
                    {Probe("a.actor_id = @user_id")}
                    UNION
                    {Probe("a.target_type = 'user' AND a.target_id = @user_id")}
                ) e ORDER BY id DESC LIMIT @limit_plus
                """;
        }

        var items = new List<AdminActivityItem>();
        await using var cmd = db.CreateCommand(sql);
        foreach (var (name, value) in values) cmd.Parameters.AddWithValue(name, value);
        cmd.Parameters.AddWithValue("limit_plus", q.Limit + 1);

        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
                items.Add(new AdminActivityItem(
                    r.GetInt64(0), r.GetDateTime(1), r.GetString(2), r.GetString(3),
                    r.IsDBNull(4) ? null : r.GetInt64(4), r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6),
                    r.GetBoolean(7),
                    r.IsDBNull(8) ? null : r.GetString(8), r.IsDBNull(9) ? null : r.GetInt64(9), r.IsDBNull(10) ? null : r.GetInt64(10)));

        var more = items.Count > q.Limit;
        if (more) items.RemoveAt(items.Count - 1);
        return new AdminActivityPage(q.Limit, more, more ? items[^1].Id : null, items);
    }
}
