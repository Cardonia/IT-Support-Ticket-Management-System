using System.Security.Claims;
using System.Text;
using System.Text.RegularExpressions;
using Npgsql;

public static class AuthEndpoints
{
    static readonly Regex UsernameRx = new("^[A-Za-z0-9_]{4,14}$", RegexOptions.Compiled);

    // Used so a login for an unknown user costs the same time as a wrong password
    static readonly string DummyHash = BCrypt.Net.BCrypt.HashPassword("dummy-password-for-timing");

    static string? Validate(string? rawUsername, string? password, out string username)
    {
        username = (rawUsername ?? "").Trim();

        if (!UsernameRx.IsMatch(username))
            return "Username must be 4-14 characters: letters, numbers or underscore.";

        if (string.IsNullOrEmpty(password) || password.Length < 8)
            return "Password must be at least 8 characters.";

        if (Encoding.UTF8.GetByteCount(password) > 72)   // bcrypt limit
            return "Password is too long (max 72 bytes).";

        return null;
    }

    public static void MapAuthEndpoints(this WebApplication app)
    {
        // ---------------- REGISTER ----------------
        app.MapPost("/api/register", async (
            RegisterRequest? data, HttpContext ctx, NpgsqlDataSource db) =>
        {
            if (ctx.User.Identity?.IsAuthenticated == true)
                return Results.Conflict("You are already logged in. Log out first.");

            var error = Validate(data?.Username, data?.Password, out var username);
            if (error is not null) return Results.BadRequest(error);

            var ct = ctx.RequestAborted;
            var passwordHash = BCrypt.Net.BCrypt.HashPassword(data!.Password);

            await using var conn = await db.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);

            long userId;
            try
            {
                await using var cmd = new NpgsqlCommand("""
                    INSERT INTO users (first_name, password, role)
                    VALUES (@first_name, @password, 'Employee')
                    RETURNING id
                    """, conn, tx);
                cmd.Parameters.AddWithValue("first_name", username);
                cmd.Parameters.AddWithValue("password", passwordHash);
                userId = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                return Results.Conflict("Username is already registered.");
            }

            var (token, expiresAt) = await AuthHelpers.CreateSessionAsync(conn, tx, userId, ct);
            await tx.CommitAsync(ct);

            ctx.Response.Cookies.Append(
                AuthHelpers.CookieName, token, AuthHelpers.CookieFor(ctx.Request, expiresAt));

            return Results.Ok("User registered successfully.");
        }).RequireRateLimiting("auth");

        // ---------------- LOGIN ----------------
        app.MapPost("/api/login", async (
            LoginRequest? data, HttpContext ctx, NpgsqlDataSource db) =>
        {
            var username = (data?.Username ?? "").Trim();
            var password = data?.Password ?? "";

            if (username.Length == 0 || password.Length == 0 ||
                Encoding.UTF8.GetByteCount(password) > 72)
                return Results.BadRequest("Username and password are required.");

            var ct = ctx.RequestAborted;

            await using var conn = await db.OpenConnectionAsync(ct);

            long? userId = null;
            string hash = DummyHash;

            await using (var cmd = new NpgsqlCommand(
                "SELECT id, password FROM users WHERE lower(first_name) = lower(@u)", conn))
            {
                cmd.Parameters.AddWithValue("u", username);
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                if (await reader.ReadAsync(ct))
                {
                    userId = Convert.ToInt64(reader.GetValue(0));
                    hash = reader.GetString(1);
                }
            }

            bool ok = BCrypt.Net.BCrypt.Verify(password, hash) && userId.HasValue;
            if (!ok) return Results.Json(
                "Invalid username or password.", statusCode: StatusCodes.Status401Unauthorized);

            // Housekeeping: drop expired sessions
            await using (var cleanup = new NpgsqlCommand(
                "DELETE FROM sessions WHERE expires_at < now()", conn))
            {
                await cleanup.ExecuteNonQueryAsync(ct);
            }

            var (token, expiresAt) = await AuthHelpers.CreateSessionAsync(conn, null, userId!.Value, ct);

            ctx.Response.Cookies.Append(
                AuthHelpers.CookieName, token, AuthHelpers.CookieFor(ctx.Request, expiresAt));

            return Results.Ok("Logged in.");
        }).RequireRateLimiting("auth");

        // ---------------- LOGOUT ----------------
        app.MapPost("/api/logout", async (HttpContext ctx, NpgsqlDataSource db) =>
        {
            var token = ctx.Request.Cookies[AuthHelpers.CookieName];

            if (!string.IsNullOrEmpty(token))
            {
                await using var cmd = db.CreateCommand(
                    "DELETE FROM sessions WHERE token_hash = @h");
                cmd.Parameters.AddWithValue("h", AuthHelpers.Hash(token));
                await cmd.ExecuteNonQueryAsync(ctx.RequestAborted);
            }

            ctx.Response.Cookies.Delete(
                AuthHelpers.CookieName, AuthHelpers.CookieFor(ctx.Request));

            return Results.Ok("Logged out.");
        });

        // ---------------- WHO AM I (protected by middleware) ----------------
        app.MapGet("/api/me", (HttpContext ctx) => Results.Ok(new
        {
            username = ctx.User.Identity?.Name,
            role = ctx.User.FindFirstValue(ClaimTypes.Role)
        }));
    }
}
