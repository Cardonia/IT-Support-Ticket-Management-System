using Npgsql;

// Bodies of the user endpoints. Only these fields are read; anything else the client sends is ignored.
public record CreateUserRequest(string? Username, string? Role);
public record RenameUserRequest(string? Username);
public record ConfirmPasswordRequest(string? Password);     // the ADMIN's own password, typed again
public record ChangeRoleRequest(string? Role, string? ExpectedRole, string? Password);   // Password = the ADMIN's own

// /api/admin/users  (Level 30). The list, one user, create, rename, deactivate, reactivate, reset password.
// Role changes are the next level; nothing here can create, change or remove an Admin.
public static class AdminUsers
{
    // Roles an Admin may give out. "Admin" is deliberately not in this list: it is never an option.
    static readonly string[] AssignableRoles = { "Employee", "Technician" };
    static readonly string[] AllRoles = { "Employee", "Technician", "Admin" };   // for the list filter only
    static readonly string[] SortKeys = { "name", "role", "created", "lastlogin" };

    public const int DefaultLimit = 25;
    public const int MaxLimit = 100;
    const int MaxOffset = 1_000_000;

    public static void MapAdminUsers(this RouteGroupBuilder api)
    {
        // ---- list: ?q=&role=&status=&sort=&dir=&limit=&offset=
        api.MapGet("/users", async (HttpContext ctx, NpgsqlDataSource db) =>
        {
            var error = TryParseQuery(ctx.Request.Query, out var query);
            if (error is not null) return AdminApi.Error(StatusCodes.Status400BadRequest, error);
            return Results.Ok(await AdminUsersRepository.ListAsync(db, query!, ctx.RequestAborted));
        });

        // ---- one user
        api.MapGet("/users/{id:long}", async (long id, NpgsqlDataSource db, HttpContext ctx) =>
        {
            var detail = await AdminUsersRepository.GetAsync(db, id, ctx.RequestAborted);
            return detail is null ? NotFound() : Results.Ok(detail);
        });

        // ---- create: {username, role}; role is Employee or Technician; the answer carries the one-time password
        api.MapPost("/users", async (CreateUserRequest? data, HttpContext ctx, NpgsqlDataSource db) =>
        {
            if (!AdminApi.TryGetActor(ctx, out var actor)) return Unauthenticated();

            var error = AuthEndpoints.ValidateUsername(data?.Username, out var username);
            if (error is not null) return AdminApi.Error(StatusCodes.Status400BadRequest, error);

            var role = AssignableRoles.FirstOrDefault(
                r => r.Equals((data?.Role ?? "").Trim(), StringComparison.OrdinalIgnoreCase));
            if (role is null)
                return AdminApi.Error(StatusCodes.Status400BadRequest, "Role must be Employee or Technician.");

            var temporary = TemporaryPassword.New();
            var hash = BCrypt.Net.BCrypt.HashPassword(temporary);

            long id;
            try
            {
                id = await AdminUsersRepository.CreateAsync(
                    db, actor, AuditLog.ClientIp(ctx), username, hash, role, ctx.RequestAborted);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                return AdminApi.Error(StatusCodes.Status409Conflict, "Username is already registered.");
            }
            catch (PostgresException ex) when (IsDatabaseRefusal(ex))
            {
                return AdminApi.Error(StatusCodes.Status403Forbidden, AdminApi.AdminTargetMessage);
            }

            // The temporary password is in this response and nowhere else: not in the database (only its hash),
            // not in the audit log, not in the server log.
            return Results.Json(new { id, username, role, temporaryPassword = temporary },
                statusCode: StatusCodes.Status201Created);
        });

        // ---- rename: {username}
        api.MapPatch("/users/{id:long}", async (long id, RenameUserRequest? data, HttpContext ctx, NpgsqlDataSource db) =>
        {
            if (!AdminApi.TryGetActor(ctx, out var actor)) return Unauthenticated();
            var ct = ctx.RequestAborted;

            var target = await AdminUsersRepository.GetTargetAsync(db, id, ct);
            if (target is null) return NotFound();
            if (IsUntouchable(target, actor)) return AdminTarget();

            var error = AuthEndpoints.ValidateUsername(data?.Username, out var username);
            if (error is not null) return AdminApi.Error(StatusCodes.Status400BadRequest, error);
            if (username == target.Username)
                return AdminApi.Error(StatusCodes.Status400BadRequest, "That is already this user's username.");

            try
            {
                var changed = await AdminUsersRepository.RenameAsync(db, actor, AuditLog.ClientIp(ctx), id, username, ct);
                if (!changed) return Stale();
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                return AdminApi.Error(StatusCodes.Status409Conflict, "Username is already registered.");
            }
            catch (PostgresException ex) when (IsDatabaseRefusal(ex))
            {
                return AdminTarget();
            }

            return Results.Ok(new { id, username });
        });

        // ---- change role: {role, expectedRole, password}. Employee <-> Technician only; Admin is never an option.
        // expectedRole is the role the admin SAW: if someone changed it meanwhile (another admin, the terminal) the answer
        // is 409 and nothing changes, so two admins can not overwrite each other blindly.
        api.MapPatch("/users/{id:long}/role", async (long id, ChangeRoleRequest? data, HttpContext ctx, NpgsqlDataSource db) =>
        {
            if (!AdminApi.TryGetActor(ctx, out var actor)) return Unauthenticated();
            var ct = ctx.RequestAborted;
            var ip = AuditLog.ClientIp(ctx);

            var target = await AdminUsersRepository.GetTargetAsync(db, id, ct);
            if (target is null) return NotFound();
            if (IsUntouchable(target, actor)) return AdminTarget();

            var role = AssignableRole(data?.Role);
            var expected = AssignableRole(data?.ExpectedRole);
            if (role is null || expected is null)
                return AdminApi.Error(StatusCodes.Status400BadRequest, "Role must be Employee or Technician.");
            if (role == expected)
                return AdminApi.Error(StatusCodes.Status400BadRequest, "Choose a different role than the current one.");
            if (target.Role != expected) return StaleRole();

            var denied = AdminApi.ReauthFailure(await Reauth.CheckAsync(db, actor, data?.Password, "role_change", ip, ct));
            if (denied is not null) return denied;

            try
            {
                var result = await AdminUsersRepository.ChangeRoleAsync(db, actor, ip, id, expected, role, ct);
                if (!result.Changed) return StaleRole();
                return Results.Ok(new { id, role, releasedTickets = result.ReleasedTickets });
            }
            catch (PostgresException ex) when (IsDatabaseRefusal(ex))
            {
                return AdminTarget();
            }
        });

        // ---- deactivate: {password}. Ends the sessions, returns In Progress tickets to Open.
        api.MapPost("/users/{id:long}/deactivate", async (long id, ConfirmPasswordRequest? data, HttpContext ctx, NpgsqlDataSource db) =>
        {
            if (!AdminApi.TryGetActor(ctx, out var actor)) return Unauthenticated();
            var ct = ctx.RequestAborted;
            var ip = AuditLog.ClientIp(ctx);

            var target = await AdminUsersRepository.GetTargetAsync(db, id, ct);
            if (target is null) return NotFound();
            if (IsUntouchable(target, actor)) return AdminTarget();
            if (!target.IsActive) return AdminApi.Error(StatusCodes.Status409Conflict, "This user is already deactivated.");

            var denied = AdminApi.ReauthFailure(await Reauth.CheckAsync(db, actor, data?.Password, "deactivate", ip, ct));
            if (denied is not null) return denied;

            try
            {
                var result = await AdminUsersRepository.DeactivateAsync(db, actor, ip, id, ct);
                if (!result.Changed) return Stale();
                return Results.Ok(new { id, isActive = false, releasedTickets = result.ReleasedTickets, endedSessions = result.EndedSessions });
            }
            catch (PostgresException ex) when (IsDatabaseRefusal(ex))
            {
                return AdminTarget();
            }
        });

        // ---- reactivate: {password}
        api.MapPost("/users/{id:long}/reactivate", async (long id, ConfirmPasswordRequest? data, HttpContext ctx, NpgsqlDataSource db) =>
        {
            if (!AdminApi.TryGetActor(ctx, out var actor)) return Unauthenticated();
            var ct = ctx.RequestAborted;
            var ip = AuditLog.ClientIp(ctx);

            var target = await AdminUsersRepository.GetTargetAsync(db, id, ct);
            if (target is null) return NotFound();
            if (IsUntouchable(target, actor)) return AdminTarget();
            if (target.IsActive) return AdminApi.Error(StatusCodes.Status409Conflict, "This user is already active.");

            var denied = AdminApi.ReauthFailure(await Reauth.CheckAsync(db, actor, data?.Password, "reactivate", ip, ct));
            if (denied is not null) return denied;

            try
            {
                if (!await AdminUsersRepository.ReactivateAsync(db, actor, ip, id, ct)) return Stale();
                return Results.Ok(new { id, isActive = true });
            }
            catch (PostgresException ex) when (IsDatabaseRefusal(ex))
            {
                return AdminTarget();
            }
        });

        // ---- reset password: {password} = the admin's. The answer carries the one-time password.
        api.MapPost("/users/{id:long}/reset-password", async (long id, ConfirmPasswordRequest? data, HttpContext ctx, NpgsqlDataSource db) =>
        {
            if (!AdminApi.TryGetActor(ctx, out var actor)) return Unauthenticated();
            var ct = ctx.RequestAborted;
            var ip = AuditLog.ClientIp(ctx);

            var target = await AdminUsersRepository.GetTargetAsync(db, id, ct);
            if (target is null) return NotFound();
            if (IsUntouchable(target, actor)) return AdminTarget();
            if (!target.IsActive)
                return AdminApi.Error(StatusCodes.Status409Conflict, "This user is deactivated. Reactivate the account first.");

            var denied = AdminApi.ReauthFailure(await Reauth.CheckAsync(db, actor, data?.Password, "reset_password", ip, ct));
            if (denied is not null) return denied;

            var temporary = TemporaryPassword.New();
            var hash = BCrypt.Net.BCrypt.HashPassword(temporary);

            try
            {
                var (changed, ended) = await AdminUsersRepository.ResetPasswordAsync(db, actor, ip, id, hash, ct);
                if (!changed) return Stale();
                return Results.Json(new { id, username = target.Username, temporaryPassword = temporary, endedSessions = ended });
            }
            catch (PostgresException ex) when (IsDatabaseRefusal(ex))
            {
                return AdminTarget();
            }
        });
    }

    // ---------------------------------------------------------------- helpers

    // An Admin (and so the caller too, who is one) can not be changed through the app
    static bool IsUntouchable(AdminUserTarget target, long actorId) =>
        target.Role == AuthHelpers.AdminRole || target.Id == actorId;

    // 42501 = the Admin guard trigger said no, 23514 = a CHECK or the last-Admin rule said no. The SQL already
    // excludes Admin rows, so this only fires if the code and the triggers ever disagree: refuse, never crash.
    static bool IsDatabaseRefusal(PostgresException ex) => ex.SqlState is "42501" or "23514";

    static IResult NotFound() => AdminApi.Error(StatusCodes.Status404NotFound, "User not found.");
    static IResult AdminTarget() => AdminApi.Error(StatusCodes.Status403Forbidden, AdminApi.AdminTargetMessage);
    static IResult StaleRole() => AdminApi.Error(StatusCodes.Status409Conflict, "This user's role changed in the meantime. Reload the page.");

    // "employee" / " Technician " -> "Employee" / "Technician"; anything else (Admin included) -> null
    static string? AssignableRole(string? text) =>
        AssignableRoles.FirstOrDefault(r => r.Equals((text ?? "").Trim(), StringComparison.OrdinalIgnoreCase));

    static IResult Stale() => AdminApi.Error(StatusCodes.Status409Conflict, "This user changed in the meantime. Reload the page.");
    static IResult Unauthenticated() => AdminApi.Error(StatusCodes.Status401Unauthorized, "Not authenticated.");

    // Reads and checks the list parameters. Returns the error text (400) or null. Unknown values are refused, never
    // guessed; numbers are clamped (limit 1-100, offset 0 or more).
    public static string? TryParseQuery(IQueryCollection q, out UserQuery? query)
    {
        query = null;

        var text = q["q"].ToString().Trim();
        if (text.Length > AdminUsersRepository.MaxTextLength)
            return $"Search text must be at most {AdminUsersRepository.MaxTextLength} characters.";
        if (InputText.HasAnyControl(text)) return "Search text " + InputText.TextMessage;

        string? role = null;
        var rawRole = q["role"].ToString();
        if (rawRole.Length > 0)
        {
            role = AllRoles.FirstOrDefault(r => r == rawRole);
            if (role is null) return "Role must be Employee, Technician or Admin.";
        }

        string? status = null;
        var rawStatus = q["status"].ToString();
        if (rawStatus.Length > 0)
        {
            if (rawStatus != "active" && rawStatus != "disabled") return "Status must be active or disabled.";
            status = rawStatus;
        }

        var sort = q["sort"].ToString();
        if (sort.Length == 0) sort = "name";
        if (!SortKeys.Contains(sort)) return "Sort must be name, role, created or lastlogin.";

        var dir = q["dir"].ToString();
        if (dir.Length == 0) dir = "asc";
        if (dir != "asc" && dir != "desc") return "Direction must be asc or desc.";

        var limit = DefaultLimit;
        var rawLimit = q["limit"].ToString();
        if (rawLimit.Length > 0)
        {
            if (!long.TryParse(rawLimit, out var l)) return "Limit must be a number.";
            limit = (int)Math.Clamp(l, 1, MaxLimit);
        }

        var offset = 0;
        var rawOffset = q["offset"].ToString();
        if (rawOffset.Length > 0)
        {
            if (!long.TryParse(rawOffset, out var o)) return "Offset must be a number.";
            offset = (int)Math.Clamp(o, 0, MaxOffset);
        }

        query = new UserQuery(text.Length == 0 ? null : text, role, status, sort, dir == "desc", limit, offset);
        return null;
    }
}
