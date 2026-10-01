// CSRF protection by custom header, on top of the SameSite=Lax session cookie.
//
// Every request that can change something (anything except GET, HEAD, OPTIONS and TRACE) must carry
//     X-Requested-With: fetch
// A page on another site cannot add a custom header to a request it makes to us without a CORS
// preflight, and this app sends no CORS headers, so the browser never lets that request go out.
// A plain <form> post from another site cannot set the header at all. Our own pages add it in api()
// (wwwroot/script.js).
//
// It runs before the authentication middleware, so a request without the header is turned away
// without any database lookup, and login / register / logout are covered too (login CSRF and
// logout CSRF are real attacks). The rule is by HTTP method, not by path, so a state-changing
// endpoint added later is covered automatically (fail closed).
public static class CsrfMiddleware
{
    public const string HeaderName = "X-Requested-With";
    public const string HeaderValue = "fetch";

    public static void UseCsrfCheck(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            var method = context.Request.Method;

            if (HttpMethods.IsGet(method) || HttpMethods.IsHead(method) ||
                HttpMethods.IsOptions(method) || HttpMethods.IsTrace(method))
            {
                await next();
                return;
            }

            // Exactly one header value, exactly "fetch" (case-sensitive)
            var values = context.Request.Headers[HeaderName];
            if (values.Count == 1 && string.Equals(values[0], HeaderValue, StringComparison.Ordinal))
            {
                await next();
                return;
            }

            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = "Missing or invalid X-Requested-With header." });
        });
    }
}
