
var builder = WebApplication.CreateBuilder(args);

var app = builder.Build();

app.UseDefaultFiles();

// Authentication/session middleware
app.UseAuthenticationMiddleware();

app.UseStaticFiles();

// API endpoints
app.MapAuthEndpoints();

app.Run();
