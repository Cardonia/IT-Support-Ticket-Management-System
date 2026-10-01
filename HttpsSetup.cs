using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

// HTTPS and reverse-proxy support (Level 24). Everything here is controlled by settings
// (appsettings.json, or environment variables with "__" for ":").
//
//   ForwardedHeaders:Enabled        true  = the app sits behind a reverse proxy (nginx, a cloud load balancer, ...)
//                                           that sends X-Forwarded-For / X-Forwarded-Proto. Default false.
//   ForwardedHeaders:KnownProxies   the IP addresses allowed to send those headers (a list, or one comma-separated
//                                   value). Empty = only a proxy on the same machine (127.0.0.1 / ::1).
//   Security:RequireHttps           false = no HTTPS redirect and no HSTS outside Development. Default true.
//   HTTPS_PORT                      the public https port for the redirect (443 behind a proxy), when the app
//                                   itself does not listen on https.
public static class HttpsSetup
{
    // Registers the forwarded-headers options. Call before builder.Build().
    public static void AddProxySupport(this WebApplicationBuilder builder)
    {
        var section = builder.Configuration.GetSection("ForwardedHeaders");
        if (!section.GetValue<bool>("Enabled")) return;

        // The list can be written as a JSON array (appsettings.json), as indexed variables
        // (ForwardedHeaders__KnownProxies__0=..., __1=...) or as one comma-separated value.
        var raws = new List<string>(section.GetSection("KnownProxies").Get<string[]>() ?? Array.Empty<string>());
        var single = section["KnownProxies"];
        if (!string.IsNullOrWhiteSpace(single))
            raws.AddRange(single.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        var known = new List<IPAddress>();
        foreach (var raw in raws)
        {
            // A typo in a security setting must stop the app, not silently trust nobody or everybody
            if (!IPAddress.TryParse(raw?.Trim(), out var address))
                throw new InvalidOperationException(
                    $"ForwardedHeaders:KnownProxies contains '{raw}', which is not an IP address.");
            known.Add(address);
        }

        builder.Services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.ForwardLimit = 1;                        // believe only the last proxy in front of us

            if (known.Count > 0)
            {
                options.KnownNetworks.Clear();               // replace the defaults (loopback) with exactly the list
                options.KnownProxies.Clear();
                foreach (var address in known) options.KnownProxies.Add(address);
            }
        });
    }

    // MUST be the first component: it rewrites the client address (rate limiter) and the scheme
    // (request.IsHttps, so the session cookie gets "Secure" behind a TLS-terminating proxy).
    // Headers from any other sender are ignored, so a client cannot fake its address or "https".
    public static void UseProxySupport(this WebApplication app)
    {
        if (app.Configuration.GetValue<bool>("ForwardedHeaders:Enabled"))
            app.UseForwardedHeaders();
    }

    // Outside Development: send http requests to https and tell browsers to use https only (HSTS, 30 days,
    // sent only on https responses and never for localhost). If the app does not know an https port the
    // redirect does nothing (and logs a warning), so plain-http setups keep working.
    public static void UseHttpsPolicy(this WebApplication app)
    {
        if (app.Environment.IsDevelopment()) return;
        if (!app.Configuration.GetValue("Security:RequireHttps", true)) return;

        app.UseHsts();
        app.UseHttpsRedirection();
    }
}
