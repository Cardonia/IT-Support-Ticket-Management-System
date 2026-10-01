using System.Threading.RateLimiting;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

// Connection string comes from appsettings.json or the env var ConnectionStrings__Default
var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? throw new InvalidOperationException("Missing ConnectionStrings:Default");

builder.Services.AddSingleton(NpgsqlDataSource.Create(connectionString));

// Reverse-proxy support, only when ForwardedHeaders:Enabled is true (HttpsSetup.cs)
builder.AddProxySupport();

// Don't announce "Server: Kestrel" on every response
builder.WebHost.ConfigureKestrel(o => o.AddServerHeader = false);

// Removes expired sessions at startup and then every hour (SessionCleanupService.cs)
builder.Services.AddHostedService<SessionCleanupService>();

// Brute-force protection for login/register: 10 requests per minute per IP
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("auth", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 10,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0
        }));
});

var app = builder.Build();

// First of all: believe X-Forwarded-For / X-Forwarded-Proto from a trusted proxy (if enabled)
app.UseProxySupport();

// Any unhandled exception becomes a clean JSON 500 instead of a stack trace / HTML
app.UseExceptionHandler(errorApp => errorApp.Run(async context =>
{
    context.Response.StatusCode = StatusCodes.Status500InternalServerError;
    SecurityHeaders.Apply(context.Response);    // the handler clears the response first, so set them again
    await context.Response.WriteAsJsonAsync(new { error = "Server error." });
}));

// Security headers (CSP, nosniff, frame and referrer rules) on every response (SecurityHeaders.cs)
app.UseSecurityHeaders();

// Outside Development: http -> https redirect and HSTS (HttpsSetup.cs)
app.UseHttpsPolicy();

// CSRF header check for every state-changing request (CsrfMiddleware.cs). It runs before the
// session lookup, so a request without the header costs no database work.
app.UseCsrfCheck();

// Auth MUST run before UseDefaultFiles, otherwise "/" is already rewritten
// to "/index.html" and the "/" redirect logic never fires.
app.UseAuthenticationMiddleware();

app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions { OnPrepareResponse = SecurityHeaders.RevalidateStaticFiles });

app.UseRateLimiter();

app.MapAuthEndpoints();
app.MapTicketEndpoints();

app.Run();
