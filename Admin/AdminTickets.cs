using Npgsql;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;

// Bodies of the ticket endpoints. Only these fields are read; anything else the client sends is ignored.
public record AdminCreateTicketRequest(string? Title, string? Description, string? Priority, string? CreatedFor);
public record AdminEditTicketRequest(string? Title, string? Description, string? Priority);
// action: assign | reassign | unassign | resolve | reopen. expectedStatus / expectedAssigneeId = what the admin SAW.
public record AdminTicketStateRequest(string? Action, string? ExpectedStatus, long? ExpectedAssigneeId, long? TechnicianId);
public record AdminDeleteTicketRequest(string? Password, string? ConfirmId);   // Password = the ADMIN's own

// /api/admin/tickets  (Level 32). All tickets, one ticket, create, edit, lifecycle overrides, soft delete.
// The page is never trusted: every handler loads the ticket again and checks that it exists, is not deleted and is
// still in the state the admin saw. Every change is one SQL statement with its audit row (AdminTicketsRepository).
public static partial class AdminTickets
{
    static readonly string[] Statuses = { "Open", "In Progress", "Resolved" };
    static readonly string[] Priorities = { "Low", "Medium", "High" };
    static readonly string[] SortKeys = { "id", "created", "updated", "status", "priority" };
    static readonly string[] DeletedModes = { "exclude", "only", "include" };
    static readonly string[] Actions = { "assign", "reassign", "unassign", "resolve", "reopen" };

    public const int DefaultLimit = 25;
    public const int MaxLimit = 100;
    const int MaxOffset = 1_000_000;

    [GeneratedRegex("^[A-Za-z0-9_]{1,14}$")]
    private static partial Regex UsernameShape();

    public static void MapAdminTickets(this RouteGroupBuilder api)
    {
        // ---- list: ?q=&status=&priority=&creator=&assignee=&deleted=&sort=&dir=&limit=&offset=
        api.MapGet("/tickets", async (HttpContext ctx, NpgsqlDataSource db) =>
        {
            var error = TryParseQuery(ctx.Request.Query, out var query);
            if (error is not null) return AdminApi.Error(StatusCodes.Status400BadRequest, error);
            return Results.Ok(await AdminTicketsRepository.ListAsync(db, query!, ctx.RequestAborted));
        });

        // ---- one ticket (a deleted one too: the admin can see what was removed)
        api.MapGet("/tickets/{id:long}", async (long id, NpgsqlDataSource db, HttpContext ctx) =>
        {
            var view = await AdminTicketsRepository.GetAsync(db, id, ctx.RequestAborted);
            return view is null ? NotFound() : Results.Ok(view);
        });

        // ---- create for an active Employee: {title, description, priority, createdFor}
        api.MapPost("/tickets", async (AdminCreateTicketRequest? data, HttpContext ctx, NpgsqlDataSource db) =>
        {
            if (!AdminApi.TryGetActor(ctx, out var actor)) return Unauthenticated();

            var error = TicketEndpoints.ValidateCreate(
                data is null ? null : new CreateTicketRequest(data.Title, data.Description, data.Priority),
                out var title, out var description, out var priority);
            if (error is not null) return AdminApi.Error(StatusCodes.Status400BadRequest, error);

            var createdFor = (data?.CreatedFor ?? "").Trim();
            if (!UsernameShape().IsMatch(createdFor))
                return AdminApi.Error(StatusCodes.Status400BadRequest, CreatedForMessage);

            var id = await AdminTicketsRepository.CreateAsync(
                db, actor, AuditLog.ClientIp(ctx), createdFor, title, description, priority, ctx.RequestAborted);
            if (id is null) return AdminApi.Error(StatusCodes.Status400BadRequest, CreatedForMessage);

            return Results.Json(new { id }, statusCode: StatusCodes.Status201Created);
        });

        // ---- edit: {title?, description?, priority?}
        api.MapPatch("/tickets/{id:long}", async (long id, AdminEditTicketRequest? data, HttpContext ctx, NpgsqlDataSource db) =>
        {
            if (!AdminApi.TryGetActor(ctx, out var actor)) return Unauthenticated();
            var ct = ctx.RequestAborted;

            var state = await AdminTicketsRepository.GetStateAsync(db, id, ct);
            if (state is null) return NotFound();
            if (state.Deleted) return DeletedTicket();

            string? title = null, description = null, priority = null;
            if (data?.Title is not null)
            {
                title = data.Title.Trim();
                if (title.Length == 0) return AdminApi.Error(StatusCodes.Status400BadRequest, "Title is required.");
                if (title.Length > TicketEndpoints.TitleMax)
                    return AdminApi.Error(StatusCodes.Status400BadRequest, $"Title must be at most {TicketEndpoints.TitleMax} characters.");
                if (InputText.HasForbiddenControl(title))
                    return AdminApi.Error(StatusCodes.Status400BadRequest, "Title " + InputText.TextMessage);
            }
            if (data?.Description is not null)
            {
                description = data.Description.Trim();
                if (description.Length == 0) return AdminApi.Error(StatusCodes.Status400BadRequest, "Description is required.");
                if (description.Length > TicketEndpoints.DescriptionMax)
                    return AdminApi.Error(StatusCodes.Status400BadRequest, $"Description must be at most {TicketEndpoints.DescriptionMax} characters.");
                if (InputText.HasForbiddenControl(description))
                    return AdminApi.Error(StatusCodes.Status400BadRequest, "Description " + InputText.TextMessage);
            }
            if (data?.Priority is not null)
            {
                priority = Priorities.FirstOrDefault(p => p.Equals(data.Priority.Trim(), StringComparison.OrdinalIgnoreCase));
                if (priority is null) return AdminApi.Error(StatusCodes.Status400BadRequest, "Priority must be Low, Medium or High.");
            }
            if (title is null && description is null && priority is null)
                return AdminApi.Error(StatusCodes.Status400BadRequest, "Nothing to change.");

            if (!await AdminTicketsRepository.EditAsync(db, actor, AuditLog.ClientIp(ctx), id, title, description, priority, ct))
            {
                // nothing matched: either the values are identical, or the ticket was deleted a moment ago
                var again = await AdminTicketsRepository.GetStateAsync(db, id, ct);
                if (again is null || again.Deleted) return DeletedTicket();
                return AdminApi.Error(StatusCodes.Status400BadRequest, "Nothing to change.");
            }
            return Results.Ok(new { id });
        });

        // ---- lifecycle: {action, expectedStatus, expectedAssigneeId, technicianId}
        api.MapPost("/tickets/{id:long}/state", async (long id, AdminTicketStateRequest? data, HttpContext ctx, NpgsqlDataSource db) =>
        {
            if (!AdminApi.TryGetActor(ctx, out var actor)) return Unauthenticated();
            var ct = ctx.RequestAborted;
            var ip = AuditLog.ClientIp(ctx);

            var state = await AdminTicketsRepository.GetStateAsync(db, id, ct);
            if (state is null) return NotFound();
            if (state.Deleted) return DeletedTicket();

            var action = Actions.FirstOrDefault(a => a == (data?.Action ?? "").Trim());
            if (action is null)
                return AdminApi.Error(StatusCodes.Status400BadRequest, "Action must be assign, reassign, unassign, resolve or reopen.");

            var expected = Statuses.FirstOrDefault(s => s == data?.ExpectedStatus);
            if (expected is null)
                return AdminApi.Error(StatusCodes.Status400BadRequest, "expectedStatus must be Open, In Progress or Resolved.");

            if (state.Status != expected || state.AssigneeId != data?.ExpectedAssigneeId) return StaleTicket();

            var needs = action switch
            {
                "assign" => "Open",
                "reopen" => "Resolved",
                _ => "In Progress",
            };
            if (state.Status != needs)
                return AdminApi.Error(StatusCodes.Status409Conflict, $"A ticket that is {state.Status} can't be changed this way.");

            long technicianId = 0;
            if (action is "assign" or "reassign")
            {
                if (data?.TechnicianId is not { } tid)
                    return AdminApi.Error(StatusCodes.Status400BadRequest, "Choose a technician.");
                technicianId = tid;
                if (action == "reassign" && technicianId == state.AssigneeId)
                    return AdminApi.Error(StatusCodes.Status400BadRequest, "Choose a different technician.");
                if (!await AdminTicketsRepository.IsActiveTechnicianAsync(db, technicianId, ct))
                    return AdminApi.Error(StatusCodes.Status400BadRequest, TechnicianMessage);
            }

            var result = action switch
            {
                "assign" or "reassign" => await AdminTicketsRepository.AssignAsync(
                    db, actor, ip, id, expected, state.AssigneeId, technicianId, ct),
                "unassign" => await AdminTicketsRepository.UnassignAsync(db, actor, ip, id, expected, state.AssigneeId, ct),
                "resolve" => await AdminTicketsRepository.ResolveAsync(db, actor, ip, id, expected, state.AssigneeId, ct),
                _ => await AdminTicketsRepository.ReopenAsync(db, actor, ip, id, expected, state.AssigneeId, ct),
            };

            if (!result.Changed)
            {
                // Matched nothing. If the ticket is exactly as it was, the technician must have lost the role in the
                // meantime (400); otherwise somebody changed the ticket first (409).
                var again = await AdminTicketsRepository.GetStateAsync(db, id, ct);
                if (again is not null && !again.Deleted && again.Status == state.Status && again.AssigneeId == state.AssigneeId
                    && action is "assign" or "reassign")
                    return AdminApi.Error(StatusCodes.Status400BadRequest, TechnicianMessage);
                return StaleTicket();
            }
            return Results.Ok(new { id, status = result.NewStatus });
        });

        // ---- soft delete: {password, confirmId}. confirmId = the ticket number typed again.
        api.MapDelete("/tickets/{id:long}", async (long id, [FromBody] AdminDeleteTicketRequest? data, HttpContext ctx, NpgsqlDataSource db) =>
        {
            if (!AdminApi.TryGetActor(ctx, out var actor)) return Unauthenticated();
            var ct = ctx.RequestAborted;
            var ip = AuditLog.ClientIp(ctx);

            var state = await AdminTicketsRepository.GetStateAsync(db, id, ct);
            if (state is null) return NotFound();
            if (state.Deleted) return AdminApi.Error(StatusCodes.Status409Conflict, "This ticket is already deleted.");

            if ((data?.ConfirmId ?? "").Trim().TrimStart('#') != id.ToString())
                return AdminApi.Error(StatusCodes.Status400BadRequest, "Type the ticket number to confirm.");

            var denied = AdminApi.ReauthFailure(await Reauth.CheckAsync(db, actor, data?.Password, "ticket_delete", ip, ct));
            if (denied is not null) return denied;

            if (!await AdminTicketsRepository.DeleteAsync(db, actor, ip, id, ct))
                return AdminApi.Error(StatusCodes.Status409Conflict, "This ticket is already deleted.");
            return Results.Ok(new { id, deleted = true });
        });
    }

    // ---------------------------------------------------------------- helpers

    const string CreatedForMessage = "createdFor must be the username of an active Employee.";
    const string TechnicianMessage = "Choose an active Technician.";

    static IResult NotFound() => AdminApi.Error(StatusCodes.Status404NotFound, "Ticket not found.");
    static IResult DeletedTicket() => AdminApi.Error(StatusCodes.Status409Conflict, "This ticket is deleted.");
    static IResult StaleTicket() => AdminApi.Error(StatusCodes.Status409Conflict, "This ticket changed in the meantime. Reload the page.");
    static IResult Unauthenticated() => AdminApi.Error(StatusCodes.Status401Unauthorized, "Not authenticated.");

    // Reads and checks the list parameters. Returns the error text (400) or null. Unknown values are refused.
    public static string? TryParseQuery(IQueryCollection q, out TicketQuery? query)
    {
        query = null;

        var text = q["q"].ToString().Trim();
        if (text.Length > AdminTicketsRepository.MaxTextLength)
            return $"Search text must be at most {AdminTicketsRepository.MaxTextLength} characters.";
        if (InputText.HasAnyControl(text)) return "Search text " + InputText.TextMessage;

        string? status = null;
        var rawStatus = q["status"].ToString();
        if (rawStatus.Length > 0)
        {
            status = Statuses.FirstOrDefault(s => s == rawStatus);
            if (status is null) return "Status must be Open, In Progress or Resolved.";
        }

        string? priority = null;
        var rawPriority = q["priority"].ToString();
        if (rawPriority.Length > 0)
        {
            priority = Priorities.FirstOrDefault(p => p == rawPriority);
            if (priority is null) return "Priority must be Low, Medium or High.";
        }

        var creator = q["creator"].ToString();
        if (creator.Length > 0 && !UsernameShape().IsMatch(creator)) return "Creator must be a username.";
        var assignee = q["assignee"].ToString();
        if (assignee.Length > 0 && !UsernameShape().IsMatch(assignee)) return "Assignee must be a username.";

        var deleted = q["deleted"].ToString();
        if (deleted.Length == 0) deleted = "exclude";
        if (!DeletedModes.Contains(deleted)) return "Deleted must be exclude, only or include.";

        var sort = q["sort"].ToString();
        if (sort.Length == 0) sort = "id";
        if (!SortKeys.Contains(sort)) return "Sort must be id, created, updated, status or priority.";

        var dir = q["dir"].ToString();
        if (dir.Length == 0) dir = "desc";
        if (dir != "asc" && dir != "desc") return "Direction must be asc or desc.";

        var limit = DefaultLimit;
        var rawLimit = q["limit"].ToString();
        if (rawLimit.Length > 0)
        {
            if (!long.TryParse(rawLimit, out var l)) return "Limit must be a number.";
            limit = (int)Math.Clamp(l, 1, MaxLimit);
        }

        var offset = 0;
        var rawOffset = q["offset"].ToString();
        if (rawOffset.Length > 0)
        {
            if (!long.TryParse(rawOffset, out var o)) return "Offset must be a number.";
            offset = (int)Math.Clamp(o, 0, MaxOffset);
        }

        query = new TicketQuery(
            text.Length == 0 ? null : text, status, priority,
            creator.Length == 0 ? null : creator, assignee.Length == 0 ? null : assignee,
            deleted, sort, dir == "desc", limit, offset);
        return null;
    }
}
