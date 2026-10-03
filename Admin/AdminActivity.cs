using Npgsql;

// GET /api/admin/activity  (Level 33). The audit log, newest first, read only.
// ?group=&action=&user=&targetType=&targetId=&from=&to=&before=&limit=
public static class AdminActivity
{
    public static void MapAdminActivity(this RouteGroupBuilder api)
    {
        api.MapGet("/activity", async (HttpContext ctx, NpgsqlDataSource db) =>
        {
            var error = TryParseQuery(ctx.Request.Query, out var query);
            if (error is not null) return AdminApi.Error(StatusCodes.Status400BadRequest, error);
            return Results.Ok(await AdminActivityRepository.ListAsync(db, query!, ctx.RequestAborted));
        });
    }

    public static string? TryParseQuery(IQueryCollection q, out ActivityQuery? query)
    {
        query = null;

        string? group = null;
        var rawGroup = q["group"].ToString();
        if (rawGroup.Length > 0)
        {
            group = AdminActivityRepository.Groups.FirstOrDefault(g => g == rawGroup);
            if (group is null) return "Group must be auth, user, ticket, note, admin or security.";
        }

        string? action = null;
        var rawAction = q["action"].ToString();
        if (rawAction.Length > 0)
        {
            action = AdminActivityRepository.Actions.FirstOrDefault(a => a == rawAction);
            if (action is null) return "Unknown action.";
        }

        var user = q["user"].ToString();
        if (user.Length > 0 && !AdminParams.IsName(user)) return "User must be a username.";

        string? targetType = null;
        var rawType = q["targetType"].ToString();
        if (rawType.Length > 0)
        {
            targetType = AdminActivityRepository.TargetTypes.FirstOrDefault(t => t == rawType);
            if (targetType is null) return "Target type must be user, ticket or note.";
        }

        var error = AdminParams.TryId(q["targetId"].ToString(), "Target number", out var targetId);
        if (error is not null) return error;
        if (targetId is not null && targetType is null) return "Choose a target type together with the target number.";
        error = AdminParams.TryInstant(q["from"].ToString(), "From", out var from);
        if (error is not null) return error;
        error = AdminParams.TryInstant(q["to"].ToString(), "To", out var to);
        if (error is not null) return error;
        error = AdminParams.TryId(q["before"].ToString(), "Before", out var before);
        if (error is not null) return error;
        error = AdminParams.TryLimit(q["limit"].ToString(), AdminActivityRepository.DefaultLimit, AdminActivityRepository.MaxLimit, out var limit);
        if (error is not null) return error;

        query = new ActivityQuery(group, action, user.Length == 0 ? null : user, targetType, targetId, from, to, before, limit);
        return null;
    }
}
