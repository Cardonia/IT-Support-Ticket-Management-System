using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Npgsql;

public sealed record SessionUser(long Id, string Username, string Role)
{
    public ClaimsPrincipal ToPrincipal() => new(new ClaimsIdentity(
        new[]
        {
            new Claim(ClaimTypes.NameIdentifier, Id.ToString()),
            new Claim(ClaimTypes.Name, Username),
            new Claim(ClaimTypes.Role, Role),
        },
        authenticationType: "session"));
}

public static class AuthHelpers
{
    public const string CookieName = "session";
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromDays(7);

    public static string NewToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    public static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public static async Task<SessionUser?> FindUserAsync(
        NpgsqlDataSource db, string token, CancellationToken ct)
    {
        const string sql = """
            SELECT u.id, u.first_name, u.role
            FROM sessions s
            JOIN users u ON u.id = s.user_id
            WHERE s.token_hash = @hash AND s.expires_at > now()
            """;

        await using var cmd = db.CreateCommand(sql);
        cmd.Parameters.AddWithValue("hash", Hash(token));

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;

        return new SessionUser(
            Convert.ToInt64(reader.GetValue(0)),
            reader.GetString(1),
            reader.IsDBNull(2) ? "Employee" : reader.GetString(2));
    }

    public static async Task<(string Token, DateTime ExpiresAt)> CreateSessionAsync(
        NpgsqlConnection conn, NpgsqlTransaction? tx, long userId, CancellationToken ct)
    {
        var token = NewToken();
        var expiresAt = DateTime.UtcNow + SessionLifetime;

        await using var cmd = new NpgsqlCommand(
            "INSERT INTO sessions (token_hash, user_id, expires_at) VALUES (@h, @u, @e)",
            conn, tx);
        cmd.Parameters.AddWithValue("h", Hash(token));
        cmd.Parameters.AddWithValue("u", userId);
        cmd.Parameters.AddWithValue("e", expiresAt);
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
