using System.Collections.Frozen;

// The Admin area: pages and scripts (Level 28). Everything here is served by code, from the private
// folder AdminSite/ next to the program, NOT from wwwroot. So the static file server can never hand
// these files to anybody, and the only way to get one is through an endpoint behind AdminOnlyPages().
//
// Routes
//   /admin                      the dashboard page (AdminSite/admin.html)
//   /admin/users                the user list, /admin/users/<id> one user
//   /admin/tickets              the ticket list, /admin/tickets/<id> one ticket (Level 32)
//   /admin/assets/<name>        a script from AdminSite/ (only the names listed below)
//   /api/admin/...              the JSON API of the area (AdminApi.cs), mapped from here so Program.cs stays untouched
// Later levels add the other pages here, one line each in Pages.
//
// The file names are a fixed table. The URL never becomes part of a file path, so there is nothing to
// traverse: /admin/assets/..%2f..%2fappsettings.json is just "a name that is not in the table" -> 404.
public static class AdminEndpoints
{
    const string Html = "text/html; charset=utf-8";
    const string Script = "text/javascript; charset=utf-8";

    // route -> file (pages)
    static readonly FrozenDictionary<string, string> Pages = new Dictionary<string, string>
    {
        [""] = "admin.html",                   // "" under the /admin group = /admin
        ["/users"] = "admin-users.html",
        ["/users/{id:long}"] = "admin-user.html",   // the number is read by the page script from the address bar
        ["/tickets"] = "admin-tickets.html",
        ["/tickets/{id:long}"] = "admin-ticket.html",
        ["/notes"] = "admin-notes.html",
        ["/activity"] = "admin-activity.html",
    }.ToFrozenDictionary();

    // asset name in the URL -> file (scripts)
    static readonly FrozenDictionary<string, string> Assets = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["admin-common.js"] = "admin-common.js",
        ["admin-dashboard.js"] = "admin-dashboard.js",
        ["admin-users.js"] = "admin-users.js",
        ["admin-user.js"] = "admin-user.js",
        ["admin-tickets.js"] = "admin-tickets.js",
        ["admin-ticket.js"] = "admin-ticket.js",
        ["admin-notes.js"] = "admin-notes.js",
        ["admin-activity.js"] = "admin-activity.js",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    static readonly string[] ReadMethods = { "GET", "HEAD" };
    static readonly string[] AllMethods = { "GET", "HEAD", "POST", "PUT", "PATCH", "DELETE", "OPTIONS" };

    public static void MapAdmin(this WebApplication app)
    {
        var dir = Path.Combine(app.Environment.ContentRootPath, "AdminSite");
        if (!Directory.Exists(dir))
            app.Logger.LogError("Admin pages folder not found: {Dir}. The Admin area will answer 404 until it is deployed with the program (see ARCHITECTURE.md, section 12).", dir);

        app.MapAdminApi();                // /api/admin/... (Admin/AdminApi.cs)

        var admin = app.MapGroup("/admin").AdminOnlyPages();

        foreach (var (route, file) in Pages)
            admin.MapMethods(route, ReadMethods, () => ServeAsync(dir, file, Html));

        admin.MapMethods("/assets/{name}", ReadMethods, (string name) =>
            Assets.TryGetValue(name, out var file)
                ? ServeAsync(dir, file, Script)
                : Task.FromResult(Results.NotFound()));

        // Everything else under /admin (an address that is not a page, or POST / PUT / ... on any address) is the
        // same plain 404 as an address that does not exist, for every method. Without this catch-all the router
        // would answer 405 Method Not Allowed for the methods a route does not handle, and a non-admin could use
        // that to tell "exists" from "does not exist".
        admin.MapMethods("/{**rest}", AllMethods, () => Results.NotFound());
    }

    static async Task<IResult> ServeAsync(string dir, string file, string contentType)
    {
        var path = Path.Combine(dir, file);
        if (!File.Exists(path)) return Results.NotFound();
        // Results.Bytes: no ETag / Range / Last-Modified; the middleware has already set Cache-Control: no-store
        return Results.Bytes(await File.ReadAllBytesAsync(path), contentType);
    }
}
