using System.Threading.RateLimiting;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

// Connection string comes from appsettings.json or the env var ConnectionStrings__Default
var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? throw new InvalidOperationException("Missing ConnectionStrings:Default");

builder.Services.AddSingleton(NpgsqlDataSource.Create(connectionString));

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

// Any unhandled exception becomes a clean JSON 500 instead of a stack trace / HTML
app.UseExceptionHandler(errorApp => errorApp.Run(async context =>
{
    context.Response.StatusCode = StatusCodes.Status500InternalServerError;
    await context.Response.WriteAsJsonAsync(new { error = "Server error." });
}));

// Auth MUST run before UseDefaultFiles, otherwise "/" is already rewritten
// to "/index.html" and the "/" redirect logic never fires.
app.UseAuthenticationMiddleware();

app.UseDefaultFiles();
app.UseStaticFiles();

app.UseRateLimiter();

app.MapAuthEndpoints();
app.MapTicketEndpoints();

app.Run();
