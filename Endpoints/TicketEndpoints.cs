using System.Security.Claims;
using Npgsql;

// Only these three fields are read from the body. Anything else the client sends
// (created_by, status, id ...) is ignored.
public record CreateTicketRequest(string? Title, string? Description, string? Priority);

// Body of PATCH /api/tickets/{id}/status. Only "Resolved" is accepted (Take sets In Progress).
public record ChangeStatusRequest(string? Status);

// Body of POST /api/tickets/{id}/notes. Only "body" is read (author and ticket come from the session / path).
public record CreateNoteRequest(string? Body);

public static class TicketEndpoints
{
    const int TitleMax = 100;          // keep in sync with tickets_title_check
    const int DescriptionMax = 2000;   // keep in sync with tickets_description_check
    const int NoteMax = 2000;          // keep in sync with ticket_notes_body_check
    static readonly string[] Priorities = { "Low", "Medium", "High" };
    static readonly string[] Statuses = { "Open", "In Progress", "Resolved" };   // keep in sync with tickets_status_check

    static string? ValidateCreate(
        CreateTicketRequest? data, out string title, out string description, out string priority)
    {
        title = (data?.Title ?? "").Trim();
        description = (data?.Description ?? "").Trim();
        priority = "Medium";                                   // default when not sent

        if (title.Length == 0) return "Title is required.";
        if (title.Length > TitleMax) return $"Title must be at most {TitleMax} characters.";

        if (description.Length == 0) return "Description is required.";
        if (description.Length > DescriptionMax)
            return $"Description must be at most {DescriptionMax} characters.";

        var raw = (data?.Priority ?? "").Trim();
        if (raw.Length > 0)
        {
            var match = Priorities.FirstOrDefault(
                p => p.Equals(raw, StringComparison.OrdinalIgnoreCase));
            if (match is null) return "Priority must be Low, Medium or High.";
            priority = match;                                  // canonical spelling
        }

        return null;
    }

    // The caller's id comes from the session claims built by the middleware, never from the request
    static bool TryGetUserId(HttpContext ctx, out long userId) =>
        long.TryParse(ctx.User.FindFirstValue(ClaimTypes.NameIdentifier), out userId);

    static IResult TakeResponse(long id, TakeResult result) => result switch
    {
        TakeResult.Taken => Results.Ok(new { id, status = "In Progress" }),
        TakeResult.AlreadyTaken => Results.Json(new { error = "This ticket was already taken." },
            statusCode: StatusCodes.Status409Conflict),
        _ => Results.Json(new { error = "Ticket not found." },
            statusCode: StatusCodes.Status404NotFound),
    };

    // null = valid. The note is trimmed (the database refuses an empty or over-long one as well)
    static string? ValidateNote(CreateNoteRequest? data, out string body)
    {
        body = (data?.Body ?? "").Trim();
        if (body.Length == 0) return "Note is required.";
        if (body.Length > NoteMax) return $"Note must be at most {NoteMax} characters.";
        return null;
    }

    static IResult ResolveResponse(long id, ResolveResult result) => result switch
    {
        ResolveResult.Resolved => Results.Ok(new { id, status = "Resolved" }),
        ResolveResult.NotAssignee => Results.Json(
            new { error = "Only the technician who took this ticket can resolve it." },
            statusCode: StatusCodes.Status403Forbidden),
        ResolveResult.WrongState => Results.Json(
            new { error = "Only an In Progress ticket can be resolved." },
            statusCode: StatusCodes.Status409Conflict),
        _ => Results.Json(new { error = "Ticket not found." },
            statusCode: StatusCodes.Status404NotFound),
    };

    public static void MapTicketEndpoints(this WebApplication app)
    {
        var tickets = app.MapGroup("/api/tickets");

        // ---------------- CREATE (Employees only) ----------------
        // No session -> 401 (middleware). Technician -> 403 (RequireRole).
        tickets.MapPost("", async (
            CreateTicketRequest? data, HttpContext ctx, NpgsqlDataSource db) =>
        {
            var error = ValidateCreate(data, out var title, out var description, out var priority);
            if (error is not null) return Results.BadRequest(error);

            if (!TryGetUserId(ctx, out var userId))
                return Results.Json(new { error = "Not authenticated." },
                    statusCode: StatusCodes.Status401Unauthorized);

            var id = await TicketRepository.CreateAsync(
                db, userId, title, description, priority, ctx.RequestAborted);

            return Results.Created($"/api/tickets/{id}", new { id });
        }).RequireRole("Employee");

        // ---------------- ALL TICKETS (Technicians only) ----------------
        // Same path as the POST above ("/api/tickets"); the HTTP method tells them apart.
        // Optional ?status=Open|In Progress|Resolved. Missing = all tickets. Any other value
        // (including an empty one) is a 400, never silently ignored.
        tickets.MapGet("", async (string? status, NpgsqlDataSource db, CancellationToken ct) =>
        {
            string? wanted = null;
            if (status is not null)
            {
                wanted = Statuses.FirstOrDefault(
                    s => s.Equals(status.Trim(), StringComparison.OrdinalIgnoreCase));
                if (wanted is null) return Results.BadRequest("Status must be Open, In Progress or Resolved.");
            }

            return Results.Ok(await TicketRepository.ListAllAsync(db, wanted, ct));
        }).RequireRole("Technician");

        // ---------------- MY TICKETS (Employees only) ----------------
        // Only the caller's own tickets, newest first. "/mine" is a fixed segment, so the
        // GET /api/tickets/{id} route added later will not swallow it.
        tickets.MapGet("/mine", async (HttpContext ctx, NpgsqlDataSource db) =>
        {
            if (!TryGetUserId(ctx, out var userId))
                return Results.Json(new { error = "Not authenticated." },
                    statusCode: StatusCodes.Status401Unauthorized);

            var list = await TicketRepository.ListByCreatorAsync(db, userId, ctx.RequestAborted);
            return Results.Ok(list);
        }).RequireRole("Employee");

        // ---------------- TICKET DETAILS (owner or any Technician) ----------------
        // Someone else's ticket and a ticket that does not exist give the same 404,
        // so ticket ids cannot be probed. ":long" means a non-number never reaches the handler.
        tickets.MapGet("/{id:long}", async (long id, HttpContext ctx, NpgsqlDataSource db) =>
        {
            if (!TryGetUserId(ctx, out var userId))
                return Results.Json(new { error = "Not authenticated." },
                    statusCode: StatusCodes.Status401Unauthorized);

            var seeAll = ctx.User.IsInRole("Technician");
            var ticket = await TicketRepository.GetByIdAsync(db, id, userId, seeAll, ctx.RequestAborted);

            if (ticket is null)
                return Results.Json(new { error = "Ticket not found." },
                    statusCode: StatusCodes.Status404NotFound);

            // The page shows the Take button only when this is true; the take endpoint re-checks anyway
            var canTake = seeAll && ticket.Status == "Open" && ticket.AssignedTo is null;
            // CanResolve already comes from the SQL (In Progress and assigned to this caller)
            return Results.Ok(ticket with { CanTake = canTake, CanResolve = seeAll && ticket.CanResolve,
                CanAddNote = seeAll && ticket.CanAddNote });
        }).RequireRole("Employee", "Technician");

        // ---------------- TAKE A TICKET (Technicians only) ----------------
        // 200 taken | 404 no such ticket | 409 not Open any more (someone else took it first)
        tickets.MapPost("/{id:long}/take", async (long id, HttpContext ctx, NpgsqlDataSource db) =>
        {
            if (!TryGetUserId(ctx, out var userId))
                return Results.Json(new { error = "Not authenticated." },
                    statusCode: StatusCodes.Status401Unauthorized);

            var result = await TicketService.TakeAsync(db, id, userId, ctx.RequestAborted);
            return TakeResponse(id, result);
        }).RequireRole("Technician");

        // ---------------- RESOLVE A TICKET (the assigned Technician only) ----------------
        // Body {"status":"Resolved"}. 200 resolved | 400 any other status value |
        // 403 someone else took it | 404 no such ticket | 409 not In Progress (still Open, or already Resolved)
        tickets.MapPatch("/{id:long}/status", async (
            long id, ChangeStatusRequest? data, HttpContext ctx, NpgsqlDataSource db) =>
        {
            if (!"Resolved".Equals((data?.Status ?? "").Trim(), StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest("Status must be Resolved.");

            if (!TryGetUserId(ctx, out var userId))
                return Results.Json(new { error = "Not authenticated." },
                    statusCode: StatusCodes.Status401Unauthorized);

            var result = await TicketService.ResolveAsync(db, id, userId, ctx.RequestAborted);
            return ResolveResponse(id, result);
        }).RequireRole("Technician");

        // ---------------- NOTES OF A TICKET (owner or any Technician) ----------------
        // Same visibility rule and the same 404 as the ticket details: someone else's ticket and a
        // ticket that does not exist look identical. Employees DO see technician notes on their own tickets.
        // Oldest first. A ticket without notes gives 200 [].
        tickets.MapGet("/{id:long}/notes", async (long id, HttpContext ctx, NpgsqlDataSource db) =>
        {
            if (!TryGetUserId(ctx, out var userId))
                return Results.Json(new { error = "Not authenticated." },
                    statusCode: StatusCodes.Status401Unauthorized);

            var notes = await TicketRepository.GetNotesAsync(
                db, id, userId, ctx.User.IsInRole("Technician"), ctx.RequestAborted);

            return notes is null
                ? Results.Json(new { error = "Ticket not found." }, statusCode: StatusCodes.Status404NotFound)
                : Results.Ok(notes);
        }).RequireRole("Employee", "Technician");

        // ---------------- ADD A NOTE (the assigned Technician only) ----------------
        // Body {"body":"..."}. 201 {"id":N} | 400 empty / too long | 403 not the technician who took it
        // (or nobody took it yet) | 404 no such ticket. The notes are read with GET on the same path.
        tickets.MapPost("/{id:long}/notes", async (
            long id, CreateNoteRequest? data, HttpContext ctx, NpgsqlDataSource db) =>
        {
            var error = ValidateNote(data, out var body);
            if (error is not null) return Results.BadRequest(error);

            if (!TryGetUserId(ctx, out var userId))
                return Results.Json(new { error = "Not authenticated." },
                    statusCode: StatusCodes.Status401Unauthorized);

            var (result, noteId) = await TicketService.AddNoteAsync(db, id, userId, body, ctx.RequestAborted);
            return result switch
            {
                NoteResult.Added => Results.Json(new { id = noteId }, statusCode: StatusCodes.Status201Created),
                NoteResult.NotAssignee => Results.Json(
                    new { error = "Only the technician who took this ticket can add notes." },
                    statusCode: StatusCodes.Status403Forbidden),
                _ => Results.Json(new { error = "Ticket not found." },
                    statusCode: StatusCodes.Status404NotFound),
            };
        }).RequireRole("Technician");
    }
}
