using System.Security.Claims;
using System.Text;
using System.Text.RegularExpressions;
using Npgsql;

public static class AuthEndpoints
{
    static readonly Regex UsernameRx = new("^[A-Za-z0-9_]{4,14}$", RegexOptions.Compiled);

    // Used so a login for an unknown user costs the same time as a wrong password
    static readonly string DummyHash = BCrypt.Net.BCrypt.HashPassword("dummy-password-for-timing");

    // The username and password rules, shared by register, "create user" in the Admin area and "change password".
    // Each returns the error text, or null when the value is fine.
    internal static string? ValidateUsername(string? rawUsername, out string username)
    {
        username = (rawUsername ?? "").Trim();

        if (!UsernameRx.IsMatch(username))
            return "Username must be 4-14 characters: letters, numbers or underscore.";

        return null;
    }

    internal static string? ValidatePassword(string? password)
    {
        if (string.IsNullOrEmpty(password) || password.Length < 8)
            return "Password must be at least 8 characters.";

        if (Encoding.UTF8.GetByteCount(password) > 72)   // bcrypt limit
            return "Password is too long (max 72 bytes).";

        if (InputText.HasAnyControl(password)) return "Password " + InputText.TextMessage;

        return null;
    }

    // Register: the username is checked first, then the password (same order and texts as always)
    static string? Validate(string? rawUsername, string? password, out string username) =>
        ValidateUsername(rawUsername, out username) ?? ValidatePassword(password);

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
            var ip = AuditLog.ClientIp(ctx);
            var passwordHash = BCrypt.Net.BCrypt.HashPassword(data!.Password);

            await using var conn = await db.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);

            long userId;
            try
            {
                // The audit row (user.registered) is written in the same statement as the new account
                await using var cmd = new NpgsqlCommand("""
                    WITH u AS (
                        INSERT INTO users (first_name, password, role)
                        VALUES (@first_name, @password, 'Employee')
                        RETURNING id, first_name, role
                    ), a AS (
                        INSERT INTO audit_log (actor_id, actor_name, actor_role, action, target_type, target_id, summary, ip)
                        SELECT u.id, u.first_name, u.role, 'user.registered', 'user', u.id, 'Registered', NULLIF(@ip, '')::inet
                        FROM u
                    )
                    SELECT id FROM u
                    """, conn, tx);
                cmd.Parameters.AddWithValue("first_name", username);
                cmd.Parameters.AddWithValue("password", passwordHash);
                AuditLog.AddIp(cmd, ip);
                userId = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                return Results.Conflict("Username is already registered.");
            }

            var (token, expiresAt) = await AuthHelpers.CreateSessionAsync(
                conn, tx, userId, ct, ip: ip, via: "registration");
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

            // A name with a control character can not exist (usernames are letters, digits and underscore) and the
            // database can not even compare it: same generic answer as any other unknown name, no query.
            if (InputText.HasAnyControl(username) || username.Length > 100)
                return Results.Json("Invalid username or password.", statusCode: StatusCodes.Status401Unauthorized);

            var ct = ctx.RequestAborted;

            await using var conn = await db.OpenConnectionAsync(ct);

            long? userId = null;
            string hash = DummyHash;
            string role = "Employee";
            bool active = false;

            await using (var cmd = new NpgsqlCommand(
                "SELECT id, password, role, is_active FROM users WHERE lower(first_name) = lower(@u)", conn))
            {
                cmd.Parameters.AddWithValue("u", username);
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                if (await reader.ReadAsync(ct))
                {
                    userId = Convert.ToInt64(reader.GetValue(0));
                    hash = reader.GetString(1);
                    role = reader.IsDBNull(2) ? "Employee" : reader.GetString(2);
                    active = !reader.IsDBNull(3) && reader.GetBoolean(3);
                }
            }

            // The password is always verified (same time for unknown user, wrong password and
            // deactivated account), and a deactivated account gets the same generic answer,
            // so the response never tells an outsider which usernames exist or are disabled.
            bool passwordOk = PasswordHash.Verify(password, hash) && userId.HasValue;
            bool ok = passwordOk && active;
            var ip = AuditLog.ClientIp(ctx);
            if (!ok && userId.HasValue)       // only for a username that exists; wrong password and deactivated account end here
                await AuditLog.WriteLoginFailedAsync(db, userId.Value, passwordOk ? "inactive" : "wrong_password",
                    ip, ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Auth"), ct);
            if (!ok) return Results.Json(
                "Invalid username or password.", statusCode: StatusCodes.Status401Unauthorized);

            // Housekeeping: drop expired sessions
            await using (var cleanup = new NpgsqlCommand(
                "DELETE FROM sessions WHERE expires_at < now()", conn))
            {
                await cleanup.ExecuteNonQueryAsync(ct);
            }

            // Admin sessions are short: the cookie and the session row both end after
            // Admin:SessionMinutes (default 120). Everyone else keeps the 7 days.
            TimeSpan? lifetime = role == AuthHelpers.AdminRole
                ? TimeSpan.FromMinutes(AuthHelpers.AdminSessionMinutes(
                    ctx.RequestServices.GetRequiredService<IConfiguration>()))
                : null;

            var (token, expiresAt) = await AuthHelpers.CreateSessionAsync(
                conn, null, userId!.Value, ct, lifetime, ip);

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
                // auth.logout is written in the same statement, and only when a session was really deleted
                await using var cmd = db.CreateCommand("""
                    WITH d AS (
                        DELETE FROM sessions WHERE token_hash = @h RETURNING user_id
                    ), a AS (
                        INSERT INTO audit_log (actor_id, actor_name, actor_role, action, target_type, target_id, summary, ip)
                        SELECT x.id, x.first_name, x.role, 'auth.logout', 'user', x.id, 'Logged out', NULLIF(@ip, '')::inet
                        FROM d JOIN users x ON x.id = d.user_id
                    )
                    SELECT user_id FROM d
                    """);
                cmd.Parameters.AddWithValue("h", AuthHelpers.Hash(token));
                AuditLog.AddIp(cmd, AuditLog.ClientIp(ctx));
                await cmd.ExecuteNonQueryAsync(ctx.RequestAborted);
            }

            ctx.Response.Cookies.Delete(
                AuthHelpers.CookieName, AuthHelpers.CookieFor(ctx.Request));

            return Results.Ok("Logged out.");
        });

        // ---------------- CHANGE OWN PASSWORD (every role) ----------------
        // Also the only way out of a temporary password (AuthenticationMiddleware lets this path through while
        // users.must_change_password is true). The current password is checked again here (Reauth.cs, which also
        // limits wrong guesses: 5 per 10 minutes per account, counted in the audit log). On success, in ONE statement:
        // the new hash is stored, the must-change flag is cleared, every OTHER session of this account is ended
        // (this one stays) and user.password_changed is written. Neither password is ever logged or audited.
        app.MapPost("/api/me/password", async (
            ChangePasswordRequest? data, HttpContext ctx, NpgsqlDataSource db) =>
        {
            if (!long.TryParse(ctx.User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
                return Results.Json(new { error = "Not authenticated." }, statusCode: StatusCodes.Status401Unauthorized);

            var current = data?.CurrentPassword ?? "";
            var next = data?.NewPassword ?? "";

            if (current.Length == 0)
                return Results.BadRequest(new { error = "Enter your current password." });

            var error = ValidatePassword(next);
            if (error is not null) return Results.BadRequest(new { error });

            if (next == current)
                return Results.BadRequest(new { error = "The new password must be different from the current one." });

            var ct = ctx.RequestAborted;
            var ip = AuditLog.ClientIp(ctx);

            switch (await Reauth.CheckAsync(db, userId, current, "password_change", ip, ct))
            {
                case ReauthResult.Wrong:
                    return Results.Json(new { error = "Your current password is wrong.", code = "reauth_failed" },
                        statusCode: StatusCodes.Status403Forbidden);
                case ReauthResult.Locked:
                    return Results.Json(new { error = Reauth.LockedMessage, code = "reauth_locked" },
                        statusCode: StatusCodes.Status429TooManyRequests);
                case ReauthResult.Missing:
                    return Results.BadRequest(new { error = "Enter your current password." });
            }

            var hash = BCrypt.Net.BCrypt.HashPassword(next);
            var token = ctx.Request.Cookies[AuthHelpers.CookieName] ?? "";

            await using var cmd = db.CreateCommand("""
                WITH u AS (
                    UPDATE users SET password = @password_hash, must_change_password = false
                    WHERE id = @user_id AND is_active
                    RETURNING id, first_name, role
                ), s AS (
                    DELETE FROM sessions
                    WHERE user_id IN (SELECT id FROM u) AND token_hash <> @token_hash
                    RETURNING 1
                ), a AS (
                    INSERT INTO audit_log (actor_id, actor_name, actor_role, action, target_type, target_id, summary, detail, ip)
                    SELECT u.id, u.first_name, u.role, 'user.password_changed', 'user', u.id, 'Changed own password',
                           jsonb_build_object(
                               'forced', (SELECT x.must_change_password FROM users x WHERE x.id = u.id),   -- the value before this statement
                               'sessions_ended', (SELECT count(*) FROM s)),
                           NULLIF(@ip, '')::inet
                    FROM u
                )
                SELECT (SELECT count(*) FROM u)
                """);
            cmd.Parameters.AddWithValue("password_hash", hash);
            cmd.Parameters.AddWithValue("user_id", userId);
            cmd.Parameters.AddWithValue("token_hash", AuthHelpers.Hash(token));
            AuditLog.AddIp(cmd, ip);
            var changed = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));

            if (changed == 0)    // the account was deactivated while the request ran
                return Results.Json(new { error = "Your account is not active." }, statusCode: StatusCodes.Status403Forbidden);

            return Results.Ok(new { changed = true });
        }).RequireRateLimiting("auth");

        // ---------------- WHO AM I (protected by middleware) ----------------
        app.MapGet("/api/me", (HttpContext ctx) => Results.Ok(new
        {
            username = ctx.User.Identity?.Name,
            role = ctx.User.FindFirstValue(ClaimTypes.Role),
            mustChangePassword = ctx.User.HasClaim(AuthHelpers.MustChangeClaim, "true")
        }));
    }
}
