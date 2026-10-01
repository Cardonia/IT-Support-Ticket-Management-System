using Microsoft.AspNetCore.StaticFiles;

// Browser security headers on every response (Level 24).
//
// The Content-Security-Policy is strict because the pages are: all script and CSS come from this site's
// own files, and there is no inline <script>, inline style="" or inline onclick / onsubmit (script.js
// attaches the handlers with addEventListener). So even if an attacker ever got markup into a page, the
// browser would refuse to run it.
public static class SecurityHeaders
{
    public const string ContentSecurityPolicy =
        "default-src 'self'; " +          // everything not listed below: only from this site (fetch calls too)
        "script-src 'self'; " +           // our script.js only: no inline script, no eval
        "style-src 'self'; " +            // our style.css only: no inline <style>, no style="" attributes
        "img-src 'self'; " +
        "object-src 'none'; " +           // no plugins
        "base-uri 'self'; " +             // an injected <base> cannot redirect our relative URLs
        "form-action 'self'; " +          // forms can only post to this site
        "frame-ancestors 'none'";         // nobody may put our pages in a frame (clickjacking)

    // Sets the headers on one response. Also called by the exception handler in Program.cs, because
    // UseExceptionHandler clears the response (headers included) before it writes the 500.
    public static void Apply(HttpResponse response)
    {
        var headers = response.Headers;
        headers["Content-Security-Policy"] = ContentSecurityPolicy;
        headers["X-Content-Type-Options"] = "nosniff";                  // never guess a file type: a .txt stays text
        headers["X-Frame-Options"] = "DENY";                            // same as frame-ancestors, for old browsers
        headers["Referrer-Policy"] = "no-referrer";                     // ticket ids in the address never leave the site
        headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
    }

    // Runs first, before anything can answer, so redirects, 401 / 403 / 429 and static files all carry the headers.
    public static void UseSecurityHeaders(this WebApplication app)
    {
        app.Use((context, next) =>
        {
            Apply(context.Response);
            return next();
        });
    }

    // For UseStaticFiles: the browser may keep a copy of a static file, but must ask the server whether it is
    // still current before using it (a cheap 304 when nothing changed). Without this a browser can keep an old
    // script.js for hours after a deploy. Responses that already chose a Cache-Control (the guest and protected
    // pages get "no-store" from AuthenticationMiddleware) are left alone.
    public static void RevalidateStaticFiles(StaticFileResponseContext context)
    {
        var headers = context.Context.Response.Headers;
        if (!headers.ContainsKey("Cache-Control"))
            headers["Cache-Control"] = "no-cache";
    }
}
