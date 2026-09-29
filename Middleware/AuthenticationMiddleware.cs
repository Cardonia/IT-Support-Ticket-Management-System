using Npgsql;

public static class AuthenticationMiddleware
{
    // Public static assets: no DB lookup, no auth
    static readonly string[] PublicAssetFolders = { "/css", "/js", "/img", "/fonts" };
    static readonly HashSet<string> PublicAssetFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "/style.css", "/script.js", "/favicon.ico"
    };

    // Pages only for guests: logged-in users are sent to /home.html
    static readonly HashSet<string> GuestPages = new(StringComparer.OrdinalIgnoreCase)
    {
        "/", "/index.html", "/login.html", "/register.html"
    };

    // API routes reachable without a session
    static readonly string[] PublicApi = { "/api/login", "/api/register", "/api/logout" };

    public static void UseAuthenticationMiddleware(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            var path = context.Request.Path;

            // 1) Public assets: skip everything (no DB hit)
            if (IsPublicAsset(path))
            {
                await next();
                return;
            }

            // 2) Resolve the session (if any) and attach the user to the request
            SessionUser? user = null;
            var token = context.Request.Cookies[AuthHelpers.CookieName];

            if (!string.IsNullOrEmpty(token))
            {
                try
                {
                    var db = context.RequestServices.GetRequiredService<NpgsqlDataSource>();
                    user = await AuthHelpers.FindUserAsync(db, token, context.RequestAborted);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    app.Logger.LogError(ex, "Session lookup failed");
                    context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                    await context.Response.WriteAsync("Service temporarily unavailable.");
                    return;
                }
            }

            if (user is not null)
            {
                context.User = user.ToPrincipal();   // endpoints can now know who is calling
            }

            bool validSession = user is not null;

            // 3) Guest-only pages
            if (GuestPages.Contains(path.Value ?? "/"))
            {
                if (validSession)
                {
                    context.Response.Redirect("/home.html");
                    return;
                }
                await next();
                return;
            }

            // 4) Public API (login / register / logout)
            if (PublicApi.Any(p => path.StartsWithSegments(p)))
            {
                await next();
                return;
            }

            // 5) Everything else needs a valid session
            if (!validSession)
            {
                if (path.StartsWithSegments("/api"))
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    await context.Response.WriteAsJsonAsync(new { error = "Not authenticated." });
                }
                else
                {
                    context.Response.Redirect("/");
                }
                return;
            }

            // Don't let the back button show protected pages after logout
            context.Response.Headers.CacheControl = "no-store";

            await next();
        });
    }

    static bool IsPublicAsset(PathString path) =>
        PublicAssetFiles.Contains(path.Value ?? "") ||
        PublicAssetFolders.Any(f => path.StartsWithSegments(f));
}
