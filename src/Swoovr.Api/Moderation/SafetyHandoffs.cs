using Microsoft.EntityFrameworkCore;
using Swoovr.Api.Accounts;
using Swoovr.Api.Ticketing;
using Swoovr.Api.Travel;
using Swoovr.Domain;
using Swoovr.Infrastructure.Persistence;

namespace Swoovr.Api.Moderation;

public sealed record SafetyHandoffRun(int Created, int Waiting, int Failed);

/// <summary>
/// Issue 188 (Q128, D411, D420): raises each sensitive report's item in the helpdesk's "Trust &amp; Safety" group, as an internal
/// note from the `swoovr-safety-reports` identity, which is also the item's customer. The row id is the operation id: a
/// create that may have reached Zammad is found again by search and never sent twice. Logs counts and codes only.
/// </summary>
public sealed class SafetyHandoffJob(SwoovrDbContext db, TicketingActorFactory actors, IConfiguration configuration, ILogger<SafetyHandoffJob> log)
{
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(10);

    public async Task<SafetyHandoffRun> RunAsync(DateTimeOffset now, CancellationToken ct)
    {
        int created = 0, waiting = 0, failed = 0;
        var due = await db.SafetyHandoffs.Where(h => (h.State == SafetyHandoffState.Pending || h.State == SafetyHandoffState.Sending) && h.NextAttemptAt != null && h.NextAttemptAt <= now)
            .OrderBy(h => h.CreatedAt).Select(h => h.Id).Take(50).ToListAsync(ct);
        foreach (var id in due)
        {
            // A lease, so two replicas never work one row at once.
            if (await db.SafetyHandoffs.Where(h => h.Id == id && h.NextAttemptAt != null && h.NextAttemptAt <= now
                    && (h.State == SafetyHandoffState.Pending || h.State == SafetyHandoffState.Sending))
                    .ExecuteUpdateAsync(u => u.SetProperty(h => h.NextAttemptAt, now + Lease), ct) != 1) continue;
            var row = await db.SafetyHandoffs.SingleAsync(h => h.Id == id, ct);
            // The lease was written outside the change tracker: read the row as it is now, not as an earlier pass saw it.
            await db.Entry(row).ReloadAsync(ct);
            await DeliverAsync(row, now, ct);
            switch (row.State)
            {
                case SafetyHandoffState.Created: created++; break;
                case SafetyHandoffState.Failed: failed++; break;
                default: waiting++; break;
            }
        }
        if (created + waiting + failed > 0)
            log.LogInformation("Safety report hand-off: {Created} items created, {Waiting} waiting, {Failed} failed.", created, waiting, failed);
        return new(created, waiting, failed);
    }

    private async Task DeliverAsync(SafetyHandoff row, DateTimeOffset now, CancellationToken ct)
    {
        var provider = actors.SafetyReports();
        if (provider is null || !actors.Configured)
        {
            // Not set up on this host: the report is already in Swoovr's own queue; the item waits without using up attempts.
            (row.NextAttemptAt, row.FailureCode) = (now + TimeSpan.FromMinutes(15), "ticketing-unavailable");
            await db.SaveChangesAsync(ct);
            return;
        }
        // PR 528 review: only a create sent in this pass and certainly refused may be sent again. A failed read while
        // reconciling (which Zammad's transport reports as certain) must leave the row Sending, or the next pass would duplicate it.
        var creating = false;
        try
        {
            var me = await provider.ActorAsync(ct);
            if (me.Login != SafetyHandoffPolicy.Login || !me.Active) { Defer(row, now, "identity-mismatch"); await db.SaveChangesAsync(ct); return; }
            if (row.State == SafetyHandoffState.Sending)
            {
                var matches = await provider.SearchAsync("swoovr_operation:" + row.Id.ToString("D"), 1, 2, ct);
                if (matches.Count == 1 && matches[0].CustomerId == me.Id && matches[0].OperationReference == row.Id.ToString("D")) await CreatedAsync(row, matches[0].Id, now, ct);
                else { Defer(row, now, "reconciliation-required"); await db.SaveChangesAsync(ct); }
                return;
            }
            var groups = await provider.GroupNamesAsync(ct);
            if (groups.Where(g => g.Value == SafetyHandoffPolicy.Group).Select(g => (long?)g.Key).SingleOrDefault() is not { } group)
            { Defer(row, now, "group-missing"); await db.SaveChangesAsync(ct); return; }
            var label = await db.ReportCategories.Where(c => c.Id == row.CategoryId).Select(c => c.Label).SingleOrDefaultAsync(ct) ?? row.CategoryId;
            var note = SafetyHandoffPolicy.Note(label, row.CaseId, HomeCityCheckSettings.Read(configuration).AdminBaseUrl);
            // Recorded before the write, so a lost answer is reconciled rather than sent again.
            row.State = SafetyHandoffState.Sending;
            await db.SaveChangesAsync(ct);
            creating = true;
            var ticket = await provider.CreateTicketAsync(new SupportTicketDraft(SafetyHandoffPolicy.Title, note, me.Id, group, row.Id.ToString("D"), TicketingPolicy.SystemSource), ct);
            if (ticket.CustomerId != me.Id || ticket.GroupId != group) { Defer(row, now, "provider-identity-mismatch"); await db.SaveChangesAsync(CancellationToken.None); return; }
            await CreatedAsync(row, ticket.Id, now, ct);
        }
        catch (TicketingProviderException error)
        {
            // A refusal Zammad certainly didn't apply can be created again; anything uncertain stays to be reconciled.
            if (creating && !error.Uncertain) row.State = SafetyHandoffState.Pending;
            Defer(row, now, error.Code); await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (TicketingAccessException error) { Defer(row, now, error.Code); await db.SaveChangesAsync(CancellationToken.None); }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        { Defer(row, now, "provider-unreachable"); await db.SaveChangesAsync(CancellationToken.None); }
    }

    private async Task CreatedAsync(SafetyHandoff row, long ticketId, DateTimeOffset now, CancellationToken ct)
    {
        (row.State, row.TicketId, row.FailureCode, row.NextAttemptAt) = (SafetyHandoffState.Created, ticketId, null, null);
        // Linked to its case, so Swoovr Admin applies the case's queue and conflict-of-interest rules to the item too.
        if (!await db.Set<TicketingCaseLink>().AnyAsync(l => l.ProviderTicketId == ticketId && l.CaseId == row.CaseId && l.AppealId == null, ct))
            db.Add(new TicketingCaseLink { Id = Guid.NewGuid(), ProviderTicketId = ticketId, CaseId = row.CaseId, LinkedBy = Guid.Empty, LinkedAt = now });
        Audit.Record(db, now, null, "system", "safety-report.handed-off", "moderation-case", row.CaseId.ToString(), TicketingProjection.Reference(ticketId));
        await db.SaveChangesAsync(CancellationToken.None);
    }

    private void Defer(SafetyHandoff row, DateTimeOffset now, string code)
    {
        row.Attempts++;
        row.FailureCode = code.Length > 64 ? code[..64] : code;
        if (row.Attempts < SafetyHandoffPolicy.MaximumAttempts) { row.NextAttemptAt = now + SafetyHandoffPolicy.Backoff(row.Attempts); return; }
        (row.State, row.NextAttemptAt) = (SafetyHandoffState.Failed, null);
        Audit.Record(db, now, null, "system", "safety-report.handoff-failed", "moderation-case", row.CaseId.ToString(), row.FailureCode);
        log.LogError("Safety report hand-off for case {Case} failed after {Attempts} attempts: {Code}.", row.CaseId, row.Attempts, row.FailureCode);
    }
}

/// <summary>Issue 188: runs the hand-off every minute (`Safety:HandoffWorker`, on by default where there is a database).</summary>
public sealed class SafetyHandoffWorker(IServiceScopeFactory scopes, TimeProvider clock, ILogger<SafetyHandoffWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(30), clock, stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<SafetyHandoffJob>().RunAsync(clock.GetUtcNow(), stoppingToken);
            }
            catch (Exception error) when (!stoppingToken.IsCancellationRequested)
            { log.LogError("Safety report hand-off pass failed ({ErrorType}); it retries in a minute.", error.GetType().Name); }
            await Task.Delay(TimeSpan.FromMinutes(1), clock, stoppingToken);
        }
    }
}

/// <summary>
/// PR 528 review (D420): which cases a helpdesk item answers to in Swoovr Admin. Its case links, and for a sensitive report's
/// item its case through the hand-off row (the item's operation reference), so the case's queue and conflict-of-interest rules
/// apply even while a lost create answer waits to be reconciled and the link isn't saved yet.
/// </summary>
public static class SafetyHandoffAccess
{
    public static async Task<Guid[]> CasesAsync(SwoovrDbContext db, SupportTicket ticket, CancellationToken ct)
    {
        var linked = await db.Set<TicketingCaseLink>().Where(l => l.ProviderTicketId == ticket.Id).Select(l => l.CaseId).Distinct().ToArrayAsync(ct);
        var handedOff = ticket.OperationReference is { } operation && Guid.TryParse(operation, out var id)
            ? await db.SafetyHandoffs.Where(h => h.Id == id).Select(h => h.CaseId).ToArrayAsync(ct) : [];
        return linked.Concat(handedOff).Distinct().ToArray();
    }

    /// <summary>An item Swoovr raised with no member or guest behind it is shown only when it is tied to a case.</summary>
    public static bool Refused(bool systemOnly, IReadOnlyCollection<Guid> cases) => systemOnly && cases.Count == 0;
}
