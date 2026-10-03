using Npgsql;

// GET /api/admin/summary  (Level 34). The dashboard: counters and three short lists. Read only, no parameters.
public static class AdminSummaryEndpoint
{
    public static void MapAdminSummary(this RouteGroupBuilder api)
    {
        api.MapGet("/summary", async (HttpContext ctx, NpgsqlDataSource db) =>
        {
            // The dashboard takes no input: any query string is ignored, nothing in it reaches SQL
            var summary = await AdminSummaryRepository.GetAsync(db, ctx.RequestAborted);
            return Results.Ok(summary);
        });
    }
}
