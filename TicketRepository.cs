using Npgsql;

// One row of a ticket list: only what a list needs (no description)
public record TicketListItem(long Id, string Title, string Priority, string Status, DateTime CreatedAt);

// One row of the technician's list: like TicketListItem plus who created the ticket (username)
public record TechTicketListItem(
    long Id, string Title, string Priority, string Status, string CreatedBy, DateTime CreatedAt);

// Full ticket for the details page. CreatedBy / AssignedTo are usernames (users.first_name).
public record TicketDetail(
    long Id, string Title, string Description, string Priority, string Status,
    string CreatedBy, string? AssignedTo,
    DateTime CreatedAt, DateTime UpdatedAt, DateTime? ResolvedAt,
    bool CanTake = false,      // set by the endpoint: this caller may press "Take Ticket" right now
    bool CanResolve = false,   // from the SQL: In Progress and assigned to this caller ("Mark Resolved")
    bool CanAddNote = false);  // from the SQL: assigned to this caller (note form)

// One note of a ticket. Author is the technician's username (users.first_name).
public record NoteItem(long Id, string Author, string Body, DateTime CreatedAt);

// Just enough of a ticket to explain why a take/resolve changed nothing
public record TicketState(string Status, long? AssignedTo);

// All SQL for tickets lives here. Endpoints never build SQL.
public static class TicketRepository
{
    // status is written explicitly; created_by always comes from the session, never from the request body
    const string InsertSql = """
        INSERT INTO tickets (title, description, priority, status, created_by)
        VALUES (@title, @description, @priority, 'Open', @created_by)
        RETURNING id
        """;

    // Newest first; id breaks ties between tickets created in the same instant.
    // Not paginated yet (see ARCHITECTURE section 16).
    const string ListByCreatorSql = """
        SELECT id, title, priority, status, created_at
        FROM tickets
        WHERE created_by = @user_id
        ORDER BY created_at DESC, id DESC
        """;

    // Every ticket from every employee, newest first. Technicians only (the endpoint enforces that).
    // Not paginated yet (see ARCHITECTURE section 16).
    const string ListAllSql = """
        SELECT t.id, t.title, t.priority, t.status, c.first_name, t.created_at
        FROM tickets t
        JOIN users c ON c.id = t.created_by
        ORDER BY t.created_at DESC, t.id DESC
        """;

    // Same list, only one status. A separate statement (instead of "@status IS NULL OR ...") keeps
    // the SQL simple and lets PostgreSQL use tickets_status_idx (status, created_at DESC).
    const string ListByStatusSql = """
        SELECT t.id, t.title, t.priority, t.status, c.first_name, t.created_at
        FROM tickets t
        JOIN users c ON c.id = t.created_by
        WHERE t.status = @status
        ORDER BY t.created_at DESC, t.id DESC
        """;

    // Taking a ticket, the whole rule in ONE statement: it only changes a ticket that is still
    // Open and unassigned. PostgreSQL checks the WHERE and writes the change atomically: when two
    // technicians take the same ticket at the same moment, the second waits for the first row lock,
    // re-checks the WHERE against the updated row (no longer Open), and gets no row back.
    const string TakeSql = """
        UPDATE tickets
        SET status = 'In Progress', assigned_to = @user_id, updated_at = now()
        WHERE id = @id AND status = 'Open' AND assigned_to IS NULL
        RETURNING id
        """;

    const string ExistsSql = "SELECT 1 FROM tickets WHERE id = @id";

    // Resolving, the whole rule in ONE statement (same idea as TakeSql): only a ticket that is
    // In Progress AND assigned to the caller changes. Sets resolved_at and updated_at together.
    const string ResolveSql = """
        UPDATE tickets
        SET status = 'Resolved', resolved_at = now(), updated_at = now()
        WHERE id = @id AND status = 'In Progress' AND assigned_to = @user_id
        RETURNING id
        """;

    // Adding a note, the whole rule in ONE statement: the note is only inserted when the ticket exists
    // and is assigned to the caller (INSERT ... SELECT ... WHERE). author_id is the session user.
    // Works for In Progress and Resolved tickets (an Open ticket has no assignee, so it never matches).
    const string AddNoteSql = """
        INSERT INTO ticket_notes (ticket_id, author_id, body)
        SELECT id, @user_id, @body
        FROM tickets
        WHERE id = @id AND assigned_to = @user_id
        RETURNING id
        """;

    // Only used after ResolveSql (or AddNoteSql) changed nothing, to choose the right error message
    const string StateSql = "SELECT status, assigned_to FROM tickets WHERE id = @id";

    // One ticket, but only if the caller may see it: @see_all is true for Technicians,
    // everybody else only gets tickets they created. A ticket that does not exist and a
    // ticket that is not yours both return no row, so the caller cannot tell them apart.
    const string GetByIdSql = """
        SELECT t.id, t.title, t.description, t.priority, t.status,
               c.first_name, a.first_name, t.created_at, t.updated_at, t.resolved_at,
               COALESCE(t.status = 'In Progress' AND t.assigned_to = @user_id, false),
               COALESCE(t.assigned_to = @user_id, false)
        FROM tickets t
        JOIN users c ON c.id = t.created_by
        LEFT JOIN users a ON a.id = t.assigned_to
        WHERE t.id = @id AND (@see_all OR t.created_by = @user_id)
        """;

    // The notes of one ticket, oldest first, but only if the caller may see the ticket (same rule as
    // GetByIdSql). Starting from tickets with a LEFT JOIN makes one statement answer both questions:
    //   no row at all        = no such ticket, or not yours  -> the endpoint answers 404
    //   one row, n.id NULL   = ticket visible, no notes yet  -> empty list
    // id breaks ties between notes written in the same instant.
    const string GetNotesSql = """
        SELECT n.id, u.first_name, n.body, n.created_at
        FROM tickets t
        LEFT JOIN ticket_notes n ON n.ticket_id = t.id
        LEFT JOIN users u ON u.id = n.author_id
        WHERE t.id = @id AND (@see_all OR t.created_by = @user_id)
        ORDER BY n.created_at, n.id
        """;

    public static async Task<long> CreateAsync(
        NpgsqlDataSource db, long createdBy, string title, string description,
        string priority, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand(InsertSql);
        cmd.Parameters.AddWithValue("title", title);
        cmd.Parameters.AddWithValue("description", description);
        cmd.Parameters.AddWithValue("priority", priority);
        cmd.Parameters.AddWithValue("created_by", createdBy);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
    }

    // Only the tickets created by this user
    public static async Task<List<TicketListItem>> ListByCreatorAsync(
        NpgsqlDataSource db, long userId, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand(ListByCreatorSql);
        cmd.Parameters.AddWithValue("user_id", userId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var list = new List<TicketListItem>();
        while (await reader.ReadAsync(ct))
        {
            list.Add(new TicketListItem(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetDateTime(4)));
        }
        return list;
    }

    // Returns null when there is no such ticket or the caller may not see it
    public static async Task<TicketDetail?> GetByIdAsync(
        NpgsqlDataSource db, long id, long userId, bool seeAll, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand(GetByIdSql);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("user_id", userId);
        cmd.Parameters.AddWithValue("see_all", seeAll);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;

        return new TicketDetail(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.GetDateTime(7),
            reader.GetDateTime(8),
            reader.IsDBNull(9) ? null : reader.GetDateTime(9),
            CanResolve: reader.GetBoolean(10),
            CanAddNote: reader.GetBoolean(11));
    }

    // All tickets, or only those with the given status (already validated by the endpoint),
    // for the technician list
    public static async Task<List<TechTicketListItem>> ListAllAsync(
        NpgsqlDataSource db, string? status, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand(status is null ? ListAllSql : ListByStatusSql);
        if (status is not null) cmd.Parameters.AddWithValue("status", status);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var list = new List<TechTicketListItem>();
        while (await reader.ReadAsync(ct))
        {
            list.Add(new TechTicketListItem(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetDateTime(5)));
        }
        return list;
    }

    // true = this call took the ticket; false = nothing changed (taken already, or no such ticket)
    public static async Task<bool> TryTakeAsync(
        NpgsqlDataSource db, long ticketId, long technicianId, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand(TakeSql);
        cmd.Parameters.AddWithValue("id", ticketId);
        cmd.Parameters.AddWithValue("user_id", technicianId);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    // true = this call resolved the ticket; false = nothing changed (not In Progress, not yours, or no such ticket)
    public static async Task<bool> TryResolveAsync(
        NpgsqlDataSource db, long ticketId, long technicianId, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand(ResolveSql);
        cmd.Parameters.AddWithValue("id", ticketId);
        cmd.Parameters.AddWithValue("user_id", technicianId);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    // null = no such ticket or the caller may not see it; otherwise the notes (possibly none)
    public static async Task<List<NoteItem>?> GetNotesAsync(
        NpgsqlDataSource db, long ticketId, long userId, bool seeAll, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand(GetNotesSql);
        cmd.Parameters.AddWithValue("id", ticketId);
        cmd.Parameters.AddWithValue("user_id", userId);
        cmd.Parameters.AddWithValue("see_all", seeAll);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;          // ticket missing or not visible

        var list = new List<NoteItem>();
        do
        {
            if (reader.IsDBNull(0)) break;                     // the ticket has no notes
            list.Add(new NoteItem(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetDateTime(3)));
        } while (await reader.ReadAsync(ct));
        return list;
    }

    // The new note's id, or null when nothing was inserted (no such ticket, or not assigned to this caller)
    public static async Task<long?> TryAddNoteAsync(
        NpgsqlDataSource db, long ticketId, long authorId, string body, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand(AddNoteSql);
        cmd.Parameters.AddWithValue("id", ticketId);
        cmd.Parameters.AddWithValue("user_id", authorId);
        cmd.Parameters.AddWithValue("body", body);
        var id = await cmd.ExecuteScalarAsync(ct);
        return id is null ? null : Convert.ToInt64(id);
    }

    // null = no such ticket
    public static async Task<TicketState?> GetStateAsync(
        NpgsqlDataSource db, long ticketId, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand(StateSql);
        cmd.Parameters.AddWithValue("id", ticketId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new TicketState(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetInt64(1));
    }

    public static async Task<bool> ExistsAsync(
        NpgsqlDataSource db, long ticketId, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand(ExistsSql);
        cmd.Parameters.AddWithValue("id", ticketId);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }
}
