using System.Security.Claims;
using System.Security.Cryptography;

// The JSON API of the Admin area (Level 30 on). Everything lives under /api/admin and is behind RequireRole("Admin"):
// a guest gets 401 from the authentication middleware, a signed-in Employee or Technician gets 403 here, and the role
// is the one read from the database for this request, never a value from the browser.
//
// Rules every handler follows (ADMIN_PLAN.md section 3.5):
//  - the page is never trusted: each handler loads the target and checks it again (exists, not an Admin, state);
//  - an Admin account is untouchable through the app, and so is the caller itself (they are an Admin);
//  - queries name their columns: no password, hash or token is ever selected or returned;
//  - every change writes its audit row in the same statement (Level 29).
public static class AdminApi
{
    public const string AdminTargetMessage = "Admin accounts can only be changed from the database terminal.";

    public static void MapAdminApi(this WebApplication app)
    {
        var api = app.MapGroup("/api/admin").RequireRole(AuthHelpers.AdminRole);
        api.MapAdminUsers();                     // Admin/AdminUsers.cs
        api.MapAdminTickets();                   // Admin/AdminTickets.cs
        api.MapAdminNotes();                     // Admin/AdminNotes.cs
        api.MapAdminActivity();                  // Admin/AdminActivity.cs
        api.MapAdminSummary();                   // Admin/AdminSummaryEndpoint.cs
    }

    // The caller's id comes from the session claims built by the middleware, never from the request
    public static bool TryGetActor(HttpContext ctx, out long actorId) =>
        long.TryParse(ctx.User.FindFirstValue(ClaimTypes.NameIdentifier), out actorId);

    public static IResult Error(int status, string message, string? code = null) =>
        code is null
            ? Results.Json(new { error = message }, statusCode: status)
            : Results.Json(new { error = message, code }, statusCode: status);

    // The answer for a re-authentication that did not succeed (null = it succeeded and the handler goes on)
    public static IResult? ReauthFailure(ReauthResult result) => result switch
    {
        ReauthResult.Ok => null,
        ReauthResult.Missing => Error(StatusCodes.Status400BadRequest, "Enter your password to confirm."),
        ReauthResult.Locked => Error(StatusCodes.Status429TooManyRequests, Reauth.LockedMessage, "reauth_locked"),
        _ => Error(StatusCodes.Status403Forbidden, "Wrong password.", "reauth_failed"),
    };
}

// One-time passwords for accounts an Admin creates or resets. 14 characters from 56 symbols (about 81 bits),
// chosen with the system's secure random generator. Look-alikes (0 O o, 1 l I) are left out because a person has to
// read this off a screen and type it once. It is shown once, stored only as a bcrypt hash, and never logged.
public static class TemporaryPassword
{
    const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz23456789";
    public const int Length = 14;

    public static string New()
    {
        var chars = new char[Length];
        for (int i = 0; i < chars.Length; i++)
            chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        return new string(chars);
    }
}
