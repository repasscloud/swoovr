using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Swoovr.Api.Accounts;
using Swoovr.Contracts;
using Swoovr.Domain;
using Swoovr.Infrastructure.Persistence;
using static Swoovr.Api.Accounts.AccountEndpoints;

namespace Swoovr.Api.Notifications;

/// <summary>
/// Issue 588 (D446): a notification to every member. Admins send and withdraw; Support can see what was sent. It goes to
/// the notification centre only (no push or email yet), to every active account in the members realm, restricted ones
/// included, and opens a read-only page only a recipient can reach. It can't be edited after sending; withdrawing removes it
/// from every centre and closes the page. Audited with ids only.
/// </summary>
public static class BroadcastEndpoints
{
    public static void Map(WebApplication app)
    {
        var staff = app.MapGroup("/api/admin/broadcasts").RequireAuthorization();
        staff.MapGet("", async (ClaimsPrincipal user, AccountResolver accounts, CancellationToken token) =>
        {
            var (current, denied) = await AdminEndpoints.StaffAsync(user, accounts, AccountRole.Support, token);
            if (denied is not null) return denied;
            var rows = await accounts.Db!.Broadcasts.AsNoTracking().OrderByDescending(b => b.SentAt).Take(100).ToListAsync(token);
            return Results.Ok(await ViewsAsync(accounts.Db, rows, current!.Allows(AccountRole.Admin), token));
        });
        staff.MapPost("", async (BroadcastRequest request, ClaimsPrincipal user, AccountResolver accounts, TimeProvider clock, CancellationToken token) =>
        {
            var (current, denied) = await AdminEndpoints.StaffAsync(user, accounts, AccountRole.Admin, token);
            if (denied is not null) return denied;
            if (BroadcastPolicy.Title(request.Title) is not { } title || BroadcastPolicy.Body(request.Body) is not { } body)
                return Problem(StatusCodes.Status400BadRequest, ProblemCodes.InvalidRequest, $"Write a title (up to {BroadcastPolicy.TitleMax} characters) and a message (up to {BroadcastPolicy.BodyMax}).");
            if (!request.Confirm) return Problem(StatusCodes.Status400BadRequest, ProblemCodes.InvalidRequest, "Confirm it goes to every member and can't be edited after sending.");
            var (db, now, actor) = (accounts.Db!, clock.GetUtcNow(), current!.Account!.Id);
            var broadcast = new Broadcast { Id = Guid.NewGuid(), Title = title, Body = body, SentBy = actor, SentAt = now };
            await using var transaction = await db.Database.BeginTransactionAsync(token);
            db.Broadcasts.Add(broadcast);
            await db.SaveChangesAsync(token);
            // One centre item per active member (restricted included); never deactivated or pending-deletion accounts.
            broadcast.Recipients = await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO notifications (id, account_id, kind, subject_id, count, created_at, updated_at, dedupe_key)
                SELECT gen_random_uuid(), a.id, 'Announcement', {broadcast.Id}, 1, {now}, {now}, {"announcement:" + broadcast.Id.ToString("N")}
                FROM accounts a WHERE a.realm = 'Members' AND a.status = 'Active'
                """, token);
            Audit.Record(db, now, actor, "account", "broadcast.sent", "broadcast", broadcast.Id.ToString(), broadcast.Recipients.ToString(System.Globalization.CultureInfo.InvariantCulture));
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            return Results.Ok((await ViewsAsync(db, [broadcast], true, token)).Single());
        });
        staff.MapPost("/{id:guid}/withdraw", async (Guid id, ClaimsPrincipal user, AccountResolver accounts, TimeProvider clock, CancellationToken token) =>
        {
            var (current, denied) = await AdminEndpoints.StaffAsync(user, accounts, AccountRole.Admin, token);
            if (denied is not null) return denied;
            var (db, now, actor) = (accounts.Db!, clock.GetUtcNow(), current!.Account!.Id);
            await using var transaction = await db.Database.BeginTransactionAsync(token);
            var broadcast = await db.Broadcasts.SingleOrDefaultAsync(b => b.Id == id, token);
            if (broadcast is null) return Results.NotFound();
            if (broadcast.WithdrawnAt is null)
            {
                (broadcast.WithdrawnAt, broadcast.WithdrawnBy) = (now, actor);
                await db.Notifications.Where(n => n.Kind == NotificationKind.Announcement && n.SubjectId == id).ExecuteDeleteAsync(token);
                Audit.Record(db, now, actor, "account", "broadcast.withdrawn", "broadcast", id.ToString());
                await db.SaveChangesAsync(token);
            }
            await transaction.CommitAsync(token);
            return Results.Ok((await ViewsAsync(db, [broadcast], true, token)).Single());
        });

        // The member's page for one announcement: only if it reached their centre and hasn't been withdrawn.
        app.MapGet("/api/me/announcements/{id:guid}", async (Guid id, ClaimsPrincipal user, AccountResolver accounts, TimeProvider clock, CancellationToken token) =>
        {
            var (current, denied) = await MemberAsync(user, accounts, token);
            if (denied is not null) return denied;
            var (db, me) = (accounts.Db!, current!.Account!.Id);
            var found = await (from b in db.Broadcasts
                               where b.Id == id && b.WithdrawnAt == null
                                     && db.Notifications.Any(n => n.AccountId == me && n.Kind == NotificationKind.Announcement && n.SubjectId == b.Id)
                               select new AnnouncementView(b.Id, b.Title, b.Body, b.SentAt)).SingleOrDefaultAsync(token);
            if (found is null) return Problem(StatusCodes.Status404NotFound, ProblemCodes.NotAvailable, "This announcement isn't available.");
            await db.Notifications.Where(n => n.AccountId == me && n.Kind == NotificationKind.Announcement && n.SubjectId == id && n.ReadAt == null)
                .ExecuteUpdateAsync(u => u.SetProperty(n => n.ReadAt, clock.GetUtcNow()), token);
            return Results.Ok(found);
        }).RequireAuthorization();
    }

    private static async Task<List<BroadcastView>> ViewsAsync(SwoovrDbContext db, IReadOnlyList<Broadcast> rows, bool admin, CancellationToken token)
    {
        var ids = rows.Select(b => b.SentBy).Concat(rows.Where(b => b.WithdrawnBy != null).Select(b => b.WithdrawnBy!.Value)).Distinct().ToList();
        var staff = await db.Accounts.AsNoTracking().Where(a => ids.Contains(a.Id)).ToDictionaryAsync(a => a.Id,
            a => string.Join(" ", new[] { a.RegisteredFirstName, a.RegisteredLastName }.Where(n => !string.IsNullOrWhiteSpace(n))) is { Length: > 0 } name ? name : a.Email ?? "Staff", token);
        return rows.Select(b => new BroadcastView(b.Id, b.Title, b.Body, b.Recipients, b.SentAt, staff.GetValueOrDefault(b.SentBy), b.WithdrawnAt,
            b.WithdrawnBy is { } by ? staff.GetValueOrDefault(by) : null, admin && b.WithdrawnAt is null)).ToList();
    }
}
