using System.Text;
using Npgsql;

public enum ReauthResult
{
    Ok,        // the password is right
    Missing,   // nothing was typed
    Wrong,     // wrong password (a security.reauth_failed row has been written)
    Locked,    // too many recent failures: the password was NOT even checked
}

// "Type your password again" for dangerous actions (Level 30: deactivate, reactivate, reset password, own password
// change; later levels: role change, ticket delete). The password is checked with bcrypt on THIS request and is never
// stored, logged or audited.
//
// Guessing is limited from the audit table, not from memory, so it survives a restart and can not be dodged by changing
// IP address: when the newest 5 `security.reauth_failed` rows of the account are all younger than 10 minutes, the
// account is locked and the answer is 429 without running bcrypt. Each wrong password adds one row, so the lock lifts by
// itself when the 5th-newest failure is 10 minutes old.
//
// One attempt at a time per account: the check runs in a transaction that first takes an advisory lock for the account
// (released at COMMIT). Two parallel guesses can not both pass the "fewer than 5 failures" check before either has
// written its failure row; the second waits, then counts again and sees the first one's row.
public static class Reauth
{
    public const int MaxFailures = 5;
    public const int WindowMinutes = 10;

    public const string LockedMessage = "Too many wrong passwords. Wait a few minutes and try again.";

    // `purpose` is a fixed word chosen by the calling endpoint (never taken from the request); it ends up in the audit detail.
    public static async Task<ReauthResult> CheckAsync(
        NpgsqlDataSource db, long userId, string? password, string purpose, string? ip, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(password)) return ReauthResult.Missing;

        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await using (var lockCmd = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtextextended('it_db.reauth:' || @user_id::text, 0))", conn, tx))
        {
            lockCmd.Parameters.AddWithValue("user_id", userId);
            await lockCmd.ExecuteNonQueryAsync(ct);
        }

        string hash;
        long recentFailures;
        await using (var cmd = new NpgsqlCommand("""
            SELECT u.password,
                   (SELECT count(*) FROM (
                        SELECT f.at FROM audit_log f
                        WHERE f.actor_id = @user_id AND f.action = 'security.reauth_failed'
                        ORDER BY f.id DESC
                        LIMIT @max_failures) r
                    WHERE r.at > clock_timestamp() - make_interval(mins => @window_minutes))
            FROM users u
            WHERE u.id = @user_id AND u.is_active
            """, conn, tx))
        {
            cmd.Parameters.AddWithValue("user_id", userId);
            cmd.Parameters.AddWithValue("max_failures", MaxFailures);
            cmd.Parameters.AddWithValue("window_minutes", WindowMinutes);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return ReauthResult.Wrong;     // the account is gone or deactivated
            hash = reader.GetString(0);
            recentFailures = reader.GetInt64(1);
        }

        if (recentFailures >= MaxFailures) return ReauthResult.Locked;

        // bcrypt only reads 72 bytes; a longer text can never be the password (login refuses it the same way)
        bool ok = Encoding.UTF8.GetByteCount(password) <= 72 && PasswordHash.Verify(password, hash);
        if (ok)
        {
            await tx.CommitAsync(ct);
            return ReauthResult.Ok;
        }

        await using (var fail = new NpgsqlCommand("""
            INSERT INTO audit_log (actor_id, actor_name, actor_role, action, target_type, target_id, summary, detail, ip)
            SELECT u.id, u.first_name, u.role, 'security.reauth_failed', 'user', u.id,
                   'Password confirmation failed', jsonb_build_object('for', @purpose::text), NULLIF(@ip, '')::inet
            FROM users u
            WHERE u.id = @user_id
            """, conn, tx))
        {
            fail.Parameters.AddWithValue("user_id", userId);
            fail.Parameters.AddWithValue("purpose", purpose);
            AuditLog.AddIp(fail, ip);
            await fail.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return ReauthResult.Wrong;
    }
}
