using System.Globalization;
using Npgsql;

// Housekeeping, not security: a session whose expires_at has passed is already refused by the
// middleware (it only accepts expires_at > now()), so an expired row does no harm. This service
// just removes those rows so the sessions table does not grow forever.
//
// It runs once when the app starts and then every SessionCleanup:Interval (default one hour).
// A failure (for example the database is down for a minute) is logged and retried next cycle;
// it never stops the web app.
//
// Setting (optional), in appsettings.json or as the env var SessionCleanup__Interval:
//   "SessionCleanup": { "Interval": "01:00:00" }      <- hours:minutes:seconds, from 00:00:01 up to 1.00:00:00 (one day)
// Careful: .NET reads a bare number or "24:00:00" as DAYS, so those are refused (and replaced, see ParseInterval).
public sealed class SessionCleanupService : BackgroundService
{
    // sessions_expires_at_idx (expires_at) makes this cheap even when the table is large
    const string DeleteExpiredSql = "DELETE FROM sessions WHERE expires_at < now()";

    static readonly TimeSpan DefaultInterval = TimeSpan.FromHours(1);
    static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(1);
    static readonly TimeSpan MaxInterval = TimeSpan.FromHours(24);     // PeriodicTimer's own limit is about 24 days

    readonly NpgsqlDataSource _db;
    readonly ILogger<SessionCleanupService> _log;
    readonly TimeSpan _interval;

    public SessionCleanupService(
        NpgsqlDataSource db, IConfiguration config, ILogger<SessionCleanupService> log)
    {
        _db = db;
        _log = log;

        _interval = ParseInterval(config["SessionCleanup:Interval"], out var problem);
        if (problem is not null) _log.LogWarning("{Problem} Using {Interval}.", problem, _interval);
    }

    // Never throws: a missing setting gives the default silently; a bad one gives a usable value
    // plus a message, so a typo in the config cannot stop the app from starting.
    internal static TimeSpan ParseInterval(string? raw, out string? problem)
    {
        problem = null;
        if (string.IsNullOrWhiteSpace(raw)) return DefaultInterval;

        if (!TimeSpan.TryParse(raw, CultureInfo.InvariantCulture, out var value))
        {
            problem = $"SessionCleanup:Interval \"{raw}\" is not a time span (use hours:minutes:seconds, e.g. 01:00:00).";
            return DefaultInterval;
        }
        if (value < MinInterval)
        {
            problem = $"SessionCleanup:Interval \"{raw}\" is shorter than {MinInterval}.";
            return DefaultInterval;
        }
        if (value > MaxInterval)
        {
            problem = $"SessionCleanup:Interval \"{raw}\" is longer than {MaxInterval} (.NET reads a bare number or 24:00:00 as days; one day is 1.00:00:00).";
            return MaxInterval;
        }
        return value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("Expired-session cleanup runs at startup and then every {Interval}.", _interval);

        using var timer = new PeriodicTimer(_interval);
        try
        {
            do
            {
                await CleanOnceAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // the app is shutting down: leave quietly
        }
    }

    async Task CleanOnceAsync(CancellationToken ct)
    {
        try
        {
            await using var cmd = _db.CreateCommand(DeleteExpiredSql);
            var removed = await cmd.ExecuteNonQueryAsync(ct);

            if (removed > 0) _log.LogInformation("Removed {Count} expired session(s).", removed);
            else _log.LogDebug("No expired sessions to remove.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;                       // shutting down, not a failure
        }
        catch (Exception ex)
        {
            // An unhandled exception in a BackgroundService would stop the whole host, so never let one out
            _log.LogWarning(ex, "Expired-session cleanup failed; trying again in {Interval}.", _interval);
        }
    }
}
