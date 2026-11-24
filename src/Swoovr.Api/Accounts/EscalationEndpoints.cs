using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Swoovr.Contracts;
using Swoovr.Domain;
using Swoovr.Infrastructure.Persistence;
using static Swoovr.Api.Accounts.AccountEndpoints;

namespace Swoovr.Api.Accounts;

/// <summary>
/// Issue 455 (D404): staff escalate an account to an admin instead of deciding. Support and above raise; only an admin who
/// didn't raise it resolves. Internal only, audited with ids, and closed to staff with a conflict of interest.
/// </summary>
public static class EscalationEndpoints
{
    private const string BadText = "Write a reason (up to 1,000 characters) without dates or document details.";

    public static void Map(WebApplication app)
    {
        var account = app.MapGroup("/api/admin/accounts/{id:guid}/escalation").RequireAuthorization();
        account.MapGet("", async (Guid id, ClaimsPrincipal user, AccountResolver accounts, CancellationToken token) =>
        {
            var (current, denied) = await StaffAsync(user, accounts, id, AccountRole.Support, token);
            if (denied is not null) return denied;
            var rows = await ViewsAsync(accounts.Db!, accounts.Db!.AccountEscalations.Where(e => e.AccountId == id && e.ResolvedAt == null), current!, token);
            return Results.Ok(new EscalationState(rows.FirstOrDefault()));
        });
        account.MapPost("", async (Guid id, EscalationRequest request, ClaimsPrincipal user, AccountResolver accounts, TimeProvider clock, CancellationToken token) =>
        {
            var (current, denied) = await StaffAsync(user, accounts, id, AccountRole.Support, token);
            if (denied is not null) return denied;
            var (db, now) = (accounts.Db!, clock.GetUtcNow());
            if (EscalationPolicy.Text(request.Reason) is not { } reason) return Problem(StatusCodes.Status400BadRequest, ProblemCodes.InvalidRequest, BadText);
            if (!await db.MemberProfiles.AnyAsync(p => p.AccountId == id, token)) return Results.NotFound();
            if (await db.AccountEscalations.AnyAsync(e => e.AccountId == id && e.ResolvedAt == null, token)) return Open();
            // An open date of birth review is escalated with its account, so the queue shows it.
            var review = await db.DobReviews.Where(r => r.AccountId == id && r.ResolvedAt == null).Select(r => (Guid?)r.Id).FirstOrDefaultAsync(token);
            var escalation = new AccountEscalation { Id = Guid.NewGuid(), AccountId = id, DobReviewId = review, Reason = reason, CreatedBy = current!.Account!.Id, CreatedAt = now };
            db.AccountEscalations.Add(escalation);
            Audit.Record(db, now, current.Account.Id, "account", "account.escalated", "account", id.ToString(), escalation.Id.ToString());
            try { await db.SaveChangesAsync(token); }
            catch (DbUpdateException) when (!token.IsCancellationRequested) { return Open(); } // two staff at once: one open escalation per account
            return Results.Ok((await ViewsAsync(db, db.AccountEscalations.Where(e => e.Id == escalation.Id), current, token)).Single());
        });

        var queue = app.MapGroup("/api/admin/escalations").RequireAuthorization();
        queue.MapGet("", async (bool? open, ClaimsPrincipal user, AccountResolver accounts, CancellationToken token) =>
        {
            var (current, denied) = await AdminEndpoints.StaffAsync(user, accounts, AccountRole.Admin, token);
            if (denied is not null) return denied;
            var rows = VisibleTo(accounts.Db!, current!.Account!.Id).Where(e => open == false ? e.ResolvedAt != null : e.ResolvedAt == null);
            return Results.Ok(await ViewsAsync(accounts.Db!, (open == false ? rows.OrderByDescending(e => e.ResolvedAt) : rows.OrderBy(e => e.CreatedAt)).Take(200), current, token));
        });
        queue.MapPost("/{escalationId:guid}/resolve", async (Guid escalationId, EscalationResolution request, ClaimsPrincipal user, AccountResolver accounts, TimeProvider clock,
            CancellationToken token) =>
        {
            var (current, denied) = await AdminEndpoints.StaffAsync(user, accounts, AccountRole.Admin, token);
            if (denied is not null) return denied;
            var (db, now) = (accounts.Db!, clock.GetUtcNow());
            var escalation = await db.AccountEscalations.AsNoTracking().SingleOrDefaultAsync(e => e.Id == escalationId, token);
            if (escalation is null) return Results.NotFound();
            if (await StaffAccessEndpoints.ExcludedAsync(db, current!.Account!.Id, [escalation.AccountId], token)) return StaffAccessEndpoints.Conflict();
            if (EscalationPolicy.Text(request.Note) is not { } note) return Problem(StatusCodes.Status400BadRequest, ProblemCodes.InvalidRequest, BadText);
            if (escalation.ResolvedAt is null && !EscalationPolicy.MayResolve(escalation, current.Account.Id, true))
                return Problem(StatusCodes.Status403Forbidden, ProblemCodes.ModerationNotPermitted, "Another admin has to resolve an escalation you raised.");
            await using var transaction = await db.Database.BeginTransactionAsync(token);
            if (await db.AccountEscalations.Where(e => e.Id == escalationId && e.ResolvedAt == null)
                    .ExecuteUpdateAsync(u => u.SetProperty(e => e.ResolvedAt, now).SetProperty(e => e.ResolvedBy, current.Account.Id).SetProperty(e => e.ResolutionNote, note), token) == 0)
                return Problem(StatusCodes.Status409Conflict, ProblemCodes.InvalidRequest, "This escalation is already resolved.");
            Audit.Record(db, now, current.Account.Id, "account", "account.escalation-resolved", "account", escalation.AccountId.ToString(), escalationId.ToString());
            await db.SaveChangesAsync(token); await transaction.CommitAsync(token);
            return Results.Ok((await ViewsAsync(db, db.AccountEscalations.Where(e => e.Id == escalationId), current, token)).Single());
        });
    }

    /// <summary>Escalations about members this staff member has no conflict of interest with (D134, procedure §5).</summary>
    public static IQueryable<AccountEscalation> VisibleTo(SwoovrDbContext db, Guid staff)
    {
        var excluded = StaffAccessEndpoints.ExcludedSubjects(db, staff);
        return db.AccountEscalations.AsNoTracking().Where(e => !excluded.Contains(e.AccountId) && e.AccountId != staff);
    }

    /// <summary>The open escalation of each date of birth review in view.</summary>
    public static async Task<Dictionary<Guid, EscalationView>> ForReviewsAsync(SwoovrDbContext db, IReadOnlyCollection<Guid> reviews, CurrentAccount current, CancellationToken token) =>
        (await ViewsAsync(db, VisibleTo(db, current.Account!.Id).Where(e => e.ResolvedAt == null && e.DobReviewId != null && reviews.Contains(e.DobReviewId.Value)), current, token))
            .ToDictionary(v => v.DobReviewId!.Value);

    private static async Task<List<EscalationView>> ViewsAsync(SwoovrDbContext db, IQueryable<AccountEscalation> query, CurrentAccount current, CancellationToken token)
    {
        var rows = await query.AsNoTracking().ToListAsync(token);
        var staffIds = rows.Select(e => e.CreatedBy).Concat(rows.Where(e => e.ResolvedBy != null).Select(e => e.ResolvedBy!.Value)).Distinct().ToList();
        var staff = await db.Accounts.AsNoTracking().Where(a => staffIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id,
            a => string.Join(" ", new[] { a.RegisteredFirstName, a.RegisteredLastName }.Where(n => !string.IsNullOrWhiteSpace(n))) is { Length: > 0 } name ? name : a.Email ?? "Staff", token);
        var ids = rows.Select(e => e.AccountId).ToList();
        var names = await db.MemberProfiles.Where(p => ids.Contains(p.AccountId)).ToDictionaryAsync(p => p.AccountId, p => p.DisplayName, token);
        return rows.Select(e => new EscalationView(e.Id, e.AccountId, names.GetValueOrDefault(e.AccountId), e.DobReviewId, e.Reason, staff.GetValueOrDefault(e.CreatedBy, "Staff"), e.CreatedAt,
            e.ResolvedBy is { } by ? staff.GetValueOrDefault(by, "Staff") : null, e.ResolvedAt, e.ResolutionNote,
            EscalationPolicy.MayResolve(e, current.Account!.Id, current.Allows(AccountRole.Admin)))).ToList();
    }

    private static IResult Open() => Problem(StatusCodes.Status409Conflict, ProblemCodes.InvalidRequest, "This account is already escalated.");

    private static async Task<(CurrentAccount? Current, IResult? Denied)> StaffAsync(ClaimsPrincipal user, AccountResolver accounts, Guid id, AccountRole role, CancellationToken token)
    {
        var (current, denied) = await AdminEndpoints.StaffAsync(user, accounts, role, token);
        if (denied is not null) return (null, denied);
        return await StaffAccessEndpoints.ExcludedAsync(accounts.Db!, current!.Account!.Id, [id], token) ? (null, StaffAccessEndpoints.Conflict()) : (current, null);
    }
}
