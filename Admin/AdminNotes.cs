using Npgsql;

// GET /api/admin/notes  (Level 33). Every technician note, newest first, read only.
// ?q=&author=&ticket=&from=&to=&before=&limit=   (from inclusive, to exclusive; before = keyset cursor)
public static class AdminNotes
{
    public static void MapAdminNotes(this RouteGroupBuilder api)
    {
        api.MapGet("/notes", async (HttpContext ctx, NpgsqlDataSource db) =>
        {
            var error = TryParseQuery(ctx.Request.Query, out var query);
            if (error is not null) return AdminApi.Error(StatusCodes.Status400BadRequest, error);
            return Results.Ok(await AdminNotesRepository.ListAsync(db, query!, ctx.RequestAborted));
        });
    }

    public static string? TryParseQuery(IQueryCollection q, out NoteQuery? query)
    {
        query = null;

        var text = q["q"].ToString().Trim();
        if (text.Length > AdminParams.MaxTextLength)
            return $"Search text must be at most {AdminParams.MaxTextLength} characters.";

        var author = q["author"].ToString();
        if (author.Length > 0 && !AdminParams.IsName(author)) return "Author must be a username.";

        var error = AdminParams.TryId(q["ticket"].ToString(), "Ticket", out var ticket);
        if (error is not null) return error;
        error = AdminParams.TryInstant(q["from"].ToString(), "From", out var from);
        if (error is not null) return error;
        error = AdminParams.TryInstant(q["to"].ToString(), "To", out var to);
        if (error is not null) return error;
        error = AdminParams.TryId(q["before"].ToString(), "Before", out var before);
        if (error is not null) return error;
        error = AdminParams.TryLimit(q["limit"].ToString(), AdminNotesRepository.DefaultLimit, AdminNotesRepository.MaxLimit, out var limit);
        if (error is not null) return error;

        query = new NoteQuery(text.Length == 0 ? null : text, author.Length == 0 ? null : author, ticket, from, to, before, limit);
        return null;
    }
}
