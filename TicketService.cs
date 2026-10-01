using Npgsql;

public enum TakeResult { Taken, AlreadyTaken, NotFound }
public enum ResolveResult { Resolved, NotFound, NotAssignee, WrongState }

// Business rules for tickets. Endpoints call this; this calls the repository.
// A rule that must hold even when two people act at the same moment lives inside the SQL
// statement itself (its WHERE clause), never in "read first, then decide".
public static class TicketService
{
    // Rule: a ticket can be taken only while it is Open and nobody is assigned.
    // Taking it sets status = In Progress, assigned_to = the technician and updated_at, in ONE statement.
    public static async Task<TakeResult> TakeAsync(
        NpgsqlDataSource db, long ticketId, long technicianId, CancellationToken ct)
    {
        if (await TicketRepository.TryTakeAsync(db, ticketId, technicianId, ct))
            return TakeResult.Taken;

        // Nothing changed: either there is no such ticket, or someone else got there first.
        // This second query only chooses which message to send; it decides nothing.
        return await TicketRepository.ExistsAsync(db, ticketId, ct)
            ? TakeResult.AlreadyTaken
            : TakeResult.NotFound;
    }

    // Rule: only the technician who took a ticket can resolve it, and only while it is In Progress.
    // Resolving sets status = Resolved, resolved_at and updated_at in ONE statement.
    public static async Task<ResolveResult> ResolveAsync(
        NpgsqlDataSource db, long ticketId, long technicianId, CancellationToken ct)
    {
        if (await TicketRepository.TryResolveAsync(db, ticketId, technicianId, ct))
            return ResolveResult.Resolved;

        // Nothing changed. This second query only chooses which message to send; it decides nothing.
        var state = await TicketRepository.GetStateAsync(db, ticketId, ct);
        if (state is null) return ResolveResult.NotFound;
        if (state.AssignedTo is not null && state.AssignedTo != technicianId) return ResolveResult.NotAssignee;
        return ResolveResult.WrongState;     // still Open, or already resolved
    }
}
