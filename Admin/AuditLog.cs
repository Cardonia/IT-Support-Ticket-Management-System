using System.Net;
using Npgsql;

// Helpers for the audit log (Level 29). The table itself is db/005_admin.sql.
//
// The rule that matters: an audit row about a change is written in THE SAME SQL STATEMENT as the change
// (WITH changed AS (UPDATE ... RETURNING ...) INSERT INTO audit_log ... SELECT ... FROM changed). So
//   - the row exists only if the change really happened (a lost take-race writes nothing), and
//   - the change can not happen without its row (one statement is atomic), and
//   - nothing can be added "later by mistake" in a code path that forgets to call a logger.
// Those statements live next to the change they describe (TicketRepository, AuthHelpers, AuthEndpoints).
// This file holds only what is shared, plus the two events that are not part of a change:
// a failed login and a refused visit to the Admin area.
//
// What is NEVER written: passwords, hashes, session tokens, cookies, temporary passwords. `summary` is a short
// fixed sentence, `detail` is a small object built here or in SQL from a fixed list of fields (the table also
// refuses obviously secret keys). Ticket and note TEXT is not copied except a ticket title cut to 100 characters.
public static class AuditLog
{
    // The caller's address as text for the `ip` (inet) column; null when unknown.
    // IPv4-in-IPv6 ("::ffff:1.2.3.4") becomes plain IPv4; an IPv6 zone ("%eth0") is dropped because inet can not hold it.
    // Behind a proxy this is the address UseForwardedHeaders restored (HttpsSetup.cs), else the proxy's own.
    public static string? ClientIp(HttpContext ctx)
    {
        var ip = ctx.Connection.RemoteIpAddress;
        if (ip is null) return null;
        if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6) return ip.ToString();   // ScopeId throws for IPv4
        if (ip.IsIPv4MappedToIPv6) return ip.MapToIPv4().ToString();
        if (ip.ScopeId != 0) ip = new IPAddress(ip.GetAddressBytes());
        return ip.ToString();
    }

    // The parameter every audit statement uses as  NULLIF(@ip, '')::inet . An empty string (never a C# null,
    // which Npgsql would refuse) means "unknown".
    public static void AddIp(NpgsqlCommand cmd, string? ip) =>
        cmd.Parameters.AddWithValue("ip", ip ?? "");

    // A login attempt for a username that EXISTS but failed (wrong password, or the account is deactivated).
    // Unknown usernames are not logged on purpose: bots, and passwords typed into the name box, never enter the log.
    // The actor is unknown (nobody proved who they are), so actor_* stay NULL; the target is the account.
    // This is the only audit write that is not in the same statement as a change, because nothing changes.
    // A failure to write it is logged and swallowed: it must never change the answer to the login request.
    public static async Task WriteLoginFailedAsync(
        NpgsqlDataSource db, long userId, string reason, string? ip, ILogger logger, CancellationToken ct)
    {
        try
        {
            await using var cmd = db.CreateCommand("""
                INSERT INTO audit_log (action, target_type, target_id, summary, detail, ip)
                SELECT 'auth.login_failed', 'user', u.id,
                       left('Failed login for ' || u.first_name, 500),
                       jsonb_build_object('reason', @reason::text), NULLIF(@ip, '')::inet
                FROM users u
                WHERE u.id = @user_id
                """);
            cmd.Parameters.AddWithValue("user_id", userId);
            cmd.Parameters.AddWithValue("reason", reason);
            AddIp(cmd, ip);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Could not write the failed-login audit row");
        }
    }

    // A signed-in non-admin asked for an Admin address (AdminOnly.cs). At most one row per user per minute, so a
    // script that hammers /admin can not fill the log: the newest such row of this user is read through the index
    // (actor_id, action, id DESC) and the new row is only inserted when it is older than a minute.
    // Failures are logged and swallowed: the visitor still gets the plain 404.
    public static async Task WriteAdminDeniedAsync(
        NpgsqlDataSource db, long userId, string method, string path, string? ip, ILogger logger, CancellationToken ct)
    {
        try
        {
            await using var cmd = db.CreateCommand("""
                INSERT INTO audit_log (actor_id, actor_name, actor_role, action, summary, detail, ip)
                SELECT u.id, u.first_name, u.role, 'security.admin_denied',
                       'Refused access to the Admin area',
                       jsonb_build_object('method', @method::text, 'path', left(@path::text, 200)),
                       NULLIF(@ip, '')::inet
                FROM users u
                WHERE u.id = @user_id
                  AND COALESCE((SELECT a.at FROM audit_log a
                                WHERE a.actor_id = @user_id AND a.action = 'security.admin_denied'
                                ORDER BY a.id DESC LIMIT 1), '-infinity'::timestamptz)
                      < now() - interval '1 minute'
                """);
            cmd.Parameters.AddWithValue("user_id", userId);
            cmd.Parameters.AddWithValue("method", method);
            cmd.Parameters.AddWithValue("path", path);
            AddIp(cmd, ip);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Could not write the admin-denied audit row");
        }
    }
}
