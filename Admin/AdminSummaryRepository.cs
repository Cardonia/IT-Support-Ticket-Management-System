using Npgsql;

// The numbers and short lists of the Admin dashboard (Level 34). Read only; every statement names its columns, so no
// password, hash, token or address can reach the answer.
public record AdminCounts(
    int Users, int Employees, int Technicians, int Admins, int ActiveUsers, int DeactivatedUsers, int MustChange,
    int Tickets, int Open, int InProgress, int Resolved, int High, int Medium, int Low, int UnassignedOpen, int DeletedTickets,
    int Notes,
    int Logins24h, int FailedLogins24h, int AdminActions24h, int Security24h);

public record AdminRecentUser(long Id, string Username, string Role, bool Active, DateTime? CreatedAt);
public record AdminRecentTicket(long Id, string Title, string Status, string Priority, long CreatorId, string Creator, DateTime CreatedAt);
public record AdminSummary(AdminCounts Counts, List<AdminActivityItem> Activity, List<AdminRecentUser> Users, List<AdminRecentTicket> Tickets);

public static class AdminSummaryRepository
{
    public const int RecentRows = 8;

    // One round trip for all the counters. Tickets that were soft deleted are counted apart and are not in the other
    // ticket numbers. "24h" counters read the audit log by time (index audit_log_at_idx, db/007).
    const string CountsSql = """
        SELECT
            u.total, u.employees, u.technicians, u.admins, u.active, u.must_change,
            t.total, t.open, t.in_progress, t.resolved, t.high, t.medium, t.low, t.unassigned_open, t.deleted,
            (SELECT count(*) FROM ticket_notes),
            a.logins, a.failed, a.admin_actions, a.security
        FROM (SELECT count(*) AS total,
                     count(*) FILTER (WHERE role = 'Employee')   AS employees,
                     count(*) FILTER (WHERE role = 'Technician') AS technicians,
                     count(*) FILTER (WHERE role = 'Admin')      AS admins,
                     count(*) FILTER (WHERE is_active)           AS active,
                     count(*) FILTER (WHERE must_change_password AND is_active) AS must_change
              FROM users) u,
             (SELECT count(*) FILTER (WHERE deleted_at IS NULL) AS total,
                     count(*) FILTER (WHERE deleted_at IS NULL AND status = 'Open')        AS open,
                     count(*) FILTER (WHERE deleted_at IS NULL AND status = 'In Progress') AS in_progress,
                     count(*) FILTER (WHERE deleted_at IS NULL AND status = 'Resolved')    AS resolved,
                     count(*) FILTER (WHERE deleted_at IS NULL AND priority = 'High')      AS high,
                     count(*) FILTER (WHERE deleted_at IS NULL AND priority = 'Medium')    AS medium,
                     count(*) FILTER (WHERE deleted_at IS NULL AND priority = 'Low')       AS low,
                     count(*) FILTER (WHERE deleted_at IS NULL AND status = 'Open' AND assigned_to IS NULL) AS unassigned_open,
                     count(*) FILTER (WHERE deleted_at IS NOT NULL) AS deleted
              FROM tickets) t,
             (SELECT count(*) FILTER (WHERE action = 'auth.login')        AS logins,
                     count(*) FILTER (WHERE action = 'auth.login_failed') AS failed,
                     count(*) FILTER (WHERE action LIKE 'admin.%')        AS admin_actions,
                     count(*) FILTER (WHERE action LIKE 'security.%')     AS security
              FROM audit_log WHERE at >= now() - interval '24 hours') a
        """;

    const string RecentUsersSql =
        "SELECT id, first_name, role, is_active, created_at FROM users ORDER BY id DESC LIMIT @n";

    const string RecentTicketsSql = """
        SELECT t.id, t.title, t.status, t.priority, t.created_by, c.first_name, t.created_at
        FROM tickets t JOIN users c ON c.id = t.created_by
        WHERE t.deleted_at IS NULL
        ORDER BY t.id DESC LIMIT @n
        """;

    public static async Task<AdminSummary> GetAsync(NpgsqlDataSource db, CancellationToken ct)
    {
        AdminCounts counts;
        await using (var cmd = db.CreateCommand(CountsSql))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            await r.ReadAsync(ct);
            int I(int i) => (int)r.GetInt64(i);
            var users = I(0); var active = I(4);
            counts = new AdminCounts(users, I(1), I(2), I(3), active, users - active, I(5),
                I(6), I(7), I(8), I(9), I(10), I(11), I(12), I(13), I(14),
                I(15),
                I(16), I(17), I(18), I(19));
        }

        var recentUsers = new List<AdminRecentUser>();
        await using (var cmd = db.CreateCommand(RecentUsersSql))
        {
            cmd.Parameters.AddWithValue("n", RecentRows);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                recentUsers.Add(new AdminRecentUser(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetBoolean(3),
                    r.IsDBNull(4) ? null : r.GetDateTime(4)));
        }

        var recentTickets = new List<AdminRecentTicket>();
        await using (var cmd = db.CreateCommand(RecentTicketsSql))
        {
            cmd.Parameters.AddWithValue("n", RecentRows);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                recentTickets.Add(new AdminRecentTicket(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3),
                    r.GetInt64(4), r.GetString(5), r.GetDateTime(6)));
        }

        var activity = await AdminActivityRepository.ListAsync(db,
            new ActivityQuery(null, null, null, null, null, null, null, null, RecentRows), ct);

        return new AdminSummary(counts, activity.Items, recentUsers, recentTickets);
    }
}
