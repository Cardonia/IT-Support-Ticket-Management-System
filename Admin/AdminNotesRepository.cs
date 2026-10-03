using System.Text;
using Npgsql;

// What the Admin notes page receives. Note text is data for the page (shown with textContent only).
public record AdminNoteRow(
    long Id, long TicketId, string TicketTitle, bool TicketDeleted,
    long AuthorId, string Author, string Body, DateTime CreatedAt);

// Keyset paging: `NextBefore` is the id to send as ?before= for the next (older) page, null on the last page.
public record AdminNotePage(int Limit, bool HasMore, long? NextBefore, List<AdminNoteRow> Items);

public record NoteQuery(
    string? Text, string? Author, long? Ticket, DateTime? From, DateTime? To, long? Before, int Limit);

// All SQL of the Admin notes page. Read only: the admin never writes, edits or deletes a note.
// Newest first by id (ids grow with time), `before` = "ids smaller than this", so a note added while the admin
// reads can not shift or repeat a row, which offset paging would do. Notes of a soft-deleted ticket are included
// and flagged: the admin sees everything, the normal pages do not.
public static class AdminNotesRepository
{
    public const int MaxTextLength = AdminParams.MaxTextLength;
    public const int DefaultLimit = 25;
    public const int MaxLimit = 100;

    static string EscapeLike(string text) =>
        text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    public static async Task<long?> FindUserIdAsync(NpgsqlDataSource db, string username, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("SELECT id FROM users WHERE lower(first_name) = lower(@name)");
        cmd.Parameters.AddWithValue("name", username);
        return await cmd.ExecuteScalarAsync(ct) is { } v ? Convert.ToInt64(v) : null;
    }

    public static async Task<AdminNotePage> ListAsync(NpgsqlDataSource db, NoteQuery q, CancellationToken ct)
    {
        var empty = new AdminNotePage(q.Limit, false, null, new List<AdminNoteRow>());

        var where = new StringBuilder("WHERE true");
        var values = new List<(string Name, object Value)>();

        if (q.Author is not null)
        {
            var author = await FindUserIdAsync(db, q.Author, ct);
            if (author is null) return empty;                      // no such user: no notes, not an error
            where.Append(" AND n.author_id = @author");
            values.Add(("author", author.Value));
        }
        if (q.Ticket is not null) { where.Append(" AND n.ticket_id = @ticket"); values.Add(("ticket", q.Ticket.Value)); }
        if (!string.IsNullOrEmpty(q.Text))
        {
            where.Append(" AND n.body ILIKE @pattern ESCAPE '\\'");
            values.Add(("pattern", "%" + EscapeLike(q.Text) + "%"));
        }
        if (q.From is not null) { where.Append(" AND n.created_at >= @from"); values.Add(("from", q.From.Value)); }
        if (q.To is not null) { where.Append(" AND n.created_at < @to"); values.Add(("to", q.To.Value)); }
        if (q.Before is not null) { where.Append(" AND n.id < @before"); values.Add(("before", q.Before.Value)); }

        var items = new List<AdminNoteRow>();
        await using var cmd = db.CreateCommand($"""
            SELECT n.id, n.ticket_id, t.title, (t.deleted_at IS NOT NULL), n.author_id, u.first_name, n.body, n.created_at
            FROM ticket_notes n
            JOIN tickets t ON t.id = n.ticket_id
            JOIN users u ON u.id = n.author_id
            {where}
            ORDER BY n.id DESC
            LIMIT @limit_plus
            """);
        foreach (var (name, value) in values) cmd.Parameters.AddWithValue(name, value);
        cmd.Parameters.AddWithValue("limit_plus", q.Limit + 1);       // one extra row tells whether there is an older page

        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
                items.Add(new AdminNoteRow(
                    r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetBoolean(3),
                    r.GetInt64(4), r.GetString(5), r.GetString(6), r.GetDateTime(7)));

        var more = items.Count > q.Limit;
        if (more) items.RemoveAt(items.Count - 1);
        return new AdminNotePage(q.Limit, more, more ? items[^1].Id : null, items);
    }
}
