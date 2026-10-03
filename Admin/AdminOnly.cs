using System.Security.Claims;
using Npgsql;

// Page and API gate for the Admin area (Level 28).
//
// AuthenticationMiddleware has already answered guests (redirect to "/" for pages, 401 for the API).
// This filter decides what a signed-in user who is NOT an Admin sees: a plain 404, exactly what any
// unknown address answers, so the admin area does not even reveal that it exists. The role is the one
// loaded from the database for this request (AuthHelpers.FindUserAsync), never a value from the browser.
//
// Use it on pages:      app.MapGroup("/admin").AdminOnly()
// The JSON API under /api/admin (a later level) uses RequireRole("Admin") instead, which answers 403.
public static class AdminOnly
{
    public static TBuilder AdminOnlyPages<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        return builder.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;

            if (http.User.IsInRole(AuthHelpers.AdminRole))
                return await next(context);

            // Recorded as security.admin_denied (at most one row per user per minute, AuditLog.cs) and in the server log.
            // Only the numeric id and the request line are logged (structured, so no log-line injection).
            var logger = http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("AdminArea");
            logger.LogWarning("Admin area refused: user {UserId} ({Role}) {Method} {Path}",
                http.User.FindFirstValue(ClaimTypes.NameIdentifier),
                http.User.FindFirstValue(ClaimTypes.Role),
                http.Request.Method,
                http.Request.Path.Value);

            if (long.TryParse(http.User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
                await AuditLog.WriteAdminDeniedAsync(
                    http.RequestServices.GetRequiredService<NpgsqlDataSource>(), userId,
                    http.Request.Method, http.Request.Path.Value ?? "", AuditLog.ClientIp(http),
                    logger, http.RequestAborted);

            return Results.NotFound();
        });
    }
}
