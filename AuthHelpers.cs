using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Npgsql;

public sealed record SessionUser(long Id, string Username, string Role, bool MustChangePassword = false)
{
    public ClaimsPrincipal ToPrincipal()
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, Id.ToString()),
            new(ClaimTypes.Name, Username),
            new(ClaimTypes.Role, Role),
        };
        if (MustChangePassword) claims.Add(new Claim(AuthHelpers.MustChangeClaim, "true"));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "session"));
    }
}

public static class AuthHelpers
{
    public const string CookieName = "session";
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromDays(7);

    public const string AdminRole = "Admin";
    public const string MustChangeClaim = "must_change_password";

    // Admin sessions are short and absolute (no sliding): at most this many minutes after login.
    // Configure with Admin:SessionMinutes. A missing, invalid or out-of-range value (5-480)
    // falls back to the default, so a typo can never make the limit longer than intended.
    public const int DefaultAdminSessionMinutes = 120;

    public static int AdminSessionMinutes(IConfiguration config) =>
        int.TryParse(config["Admin:SessionMinutes"], out var m) && m >= 5 && m <= 480
            ? m : DefaultAdminSessionMinutes;

    public static string NewToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    public static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    // The one place a session cookie becomes a user. Besides "session exists and has not expired":
    //  - the account must be active (a deactivated user is locked out on the very next request);
    //  - an Admin's session must be younger than the admin limit, whatever its expires_at says
    //    (so a long session of someone who became Admin later cannot outlive the admin policy).
    // IS DISTINCT FROM keeps rows whose role is NULL (treated as Employee below).
    public static async Task<SessionUser?> FindUserAsync(
        NpgsqlDataSource db, string token, CancellationToken ct,
        int adminSessionMinutes = DefaultAdminSessionMinutes)
    {
        const string sql = """
            SELECT u.id, u.first_name, u.role, u.must_change_password
            FROM sessions s
            JOIN users u ON u.id = s.user_id
            WHERE s.token_hash = @hash AND s.expires_at > now()
              AND u.is_active
              AND (u.role IS DISTINCT FROM 'Admin'
                   OR s.created_at > now() - make_interval(mins => @admin_minutes))
            """;

        await using var cmd = db.CreateCommand(sql);
        cmd.Parameters.AddWithValue("hash", Hash(token));
        cmd.Parameters.AddWithValue("admin_minutes", adminSessionMinutes);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;

        return new SessionUser(
            Convert.ToInt64(reader.GetValue(0)),
            reader.GetString(1),
            reader.IsDBNull(2) ? "Employee" : reader.GetString(2),
            !reader.IsDBNull(3) && reader.GetBoolean(3));
    }

    // Creates the session AND its audit row (auth.login) in one statement: no session without a login record,
    // no record without a session. `via` is "password" for a login and "registration" when register starts the session.
    public static async Task<(string Token, DateTime ExpiresAt)> CreateSessionAsync(
        NpgsqlConnection conn, NpgsqlTransaction? tx, long userId, CancellationToken ct,
        TimeSpan? lifetime = null, string? ip = null, string via = "password")
    {
        var token = NewToken();
        var expiresAt = DateTime.UtcNow + (lifetime ?? SessionLifetime);

        await using var cmd = new NpgsqlCommand("""
            WITH s AS (
                INSERT INTO sessions (token_hash, user_id, expires_at) VALUES (@h, @u, @e)
                RETURNING user_id
            ), a AS (
                INSERT INTO audit_log (actor_id, actor_name, actor_role, action, target_type, target_id, summary, detail, ip)
                SELECT x.id, x.first_name, x.role, 'auth.login', 'user', x.id, 'Logged in',
                       jsonb_build_object('via', @via::text), NULLIF(@ip, '')::inet
                FROM s JOIN users x ON x.id = s.user_id
            )
            SELECT user_id FROM s
            """, conn, tx);
        cmd.Parameters.AddWithValue("h", Hash(token));
        cmd.Parameters.AddWithValue("u", userId);
        cmd.Parameters.AddWithValue("e", expiresAt);
        cmd.Parameters.AddWithValue("via", via);
        AuditLog.AddIp(cmd, ip);
        await cmd.ExecuteNonQueryAsync(ct);

        return (token, expiresAt);
    }

    public static CookieOptions CookieFor(HttpRequest request, DateTime? expiresUtc = null) => new()
    {
        HttpOnly = true,
        Secure = request.IsHttps,          // true automatically on HTTPS, works on http://localhost in dev
        SameSite = SameSiteMode.Lax,       // Strict drops the cookie on links from email/chat
        Path = "/",
        IsEssential = true,
        Expires = expiresUtc.HasValue
            ? new DateTimeOffset(DateTime.SpecifyKind(expiresUtc.Value, DateTimeKind.Utc))
            : null
    };
}
