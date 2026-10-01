using System.Security.Claims;

// Role check for minimal-API endpoints (AddAuthorization is not registered, so
// RequireAuthorization() does not work in this project).
//
// Usage:
//   app.MapGet("/api/tickets", Handler).RequireRole("Technician");
//   var techs = app.MapGroup("/api/tech").RequireRole("Technician");
//
// The authentication middleware already answers 401 for callers without a valid
// session. This filter adds the 403 for a valid session with the wrong role.
// Role names are case-sensitive and must match users.role: "Employee", "Technician".
public static class RoleGuard
{
    public static TBuilder RequireRole<TBuilder>(this TBuilder builder, params string[] roles)
        where TBuilder : IEndpointConventionBuilder
    {
        if (roles.Length == 0)
            throw new ArgumentException("Pass at least one role.", nameof(roles));

        return builder.AddEndpointFilter(async (context, next) =>
        {
            var user = context.HttpContext.User;

            // Safety net: normally the middleware has already returned 401 before we get here
            if (user.Identity?.IsAuthenticated != true)
                return Results.Json(new { error = "Not authenticated." },
                    statusCode: StatusCodes.Status401Unauthorized);

            if (!roles.Any(user.IsInRole))
                return Results.Json(new { error = "Forbidden." },
                    statusCode: StatusCodes.Status403Forbidden);

            return await next(context);
        });
    }
}
