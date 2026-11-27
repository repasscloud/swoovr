using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Swoovr.Api.Accounts;
using Swoovr.Contracts;
using Swoovr.Domain;
using static Swoovr.Api.Accounts.AccountEndpoints;

namespace Swoovr.Api.Profiles;

/// <summary>
/// Issue 572 (D448): private favourites. A member saves another from their profile and sees their own list; the other
/// member is never told, and no count exists anywhere. Saving needs the profile to be open to them; the list leaves out
/// anyone whose profile isn't open to them now (a block either way removes the favourite outright).
/// </summary>
public static class FavouriteEndpoints
{
    public static void Map(WebApplication app)
    {
        var api = app.MapGroup("/api").RequireAuthorization();
        api.MapPut("/people/{id:guid}/favourite", async (Guid id, ClaimsPrincipal user, AccountResolver accounts, IConfiguration configuration, TimeProvider clock,
            CancellationToken token) =>
        {
            var (current, denied) = await MemberAsync(user, accounts, token);
            if (denied is not null) return denied;
            var (db, me, now) = (accounts.Db!, current!.Profile!.AccountId, clock.GetUtcNow());
            var person = (await ProfileEndpoints.OpensAsync(db, configuration, current.Profile!, id, now, token))?.Person;
            if (person is null || person.Profile.AccountId == me) return Problem(StatusCodes.Status404NotFound, ProblemCodes.NotAvailable, "That profile isn't available.");
            var target = person.Profile.AccountId;
            if (!await db.MemberFavourites.AnyAsync(f => f.AccountId == me && f.TargetAccountId == target, token))
            {
                if (await db.MemberFavourites.CountAsync(f => f.AccountId == me, token) >= FavouritePolicy.Max)
                    return Problem(StatusCodes.Status409Conflict, ProblemCodes.InvalidRequest, $"You can keep up to {FavouritePolicy.Max} favourites. Remove one first.");
                db.MemberFavourites.Add(new MemberFavourite { AccountId = me, TargetAccountId = target, CreatedAt = now });
                try { await db.SaveChangesAsync(token); }
                catch (DbUpdateException) when (!token.IsCancellationRequested) { } // saved twice at once: it's saved
            }
            return Results.Ok(new FavouriteState(true));
        });
        api.MapDelete("/people/{id:guid}/favourite", async (Guid id, ClaimsPrincipal user, AccountResolver accounts, CancellationToken token) =>
        {
            var (current, denied) = await MemberAsync(user, accounts, token);
            if (denied is not null) return denied;
            var (db, me) = (accounts.Db!, current!.Profile!.AccountId);
            // Works even when the profile no longer opens, so a member can always tidy their own list.
            var target = await db.MemberProfiles.Where(p => p.PublicId == id).Select(p => (Guid?)p.AccountId).SingleOrDefaultAsync(token);
            if (target is { } account) await db.MemberFavourites.Where(f => f.AccountId == me && f.TargetAccountId == account).ExecuteDeleteAsync(token);
            return Results.Ok(new FavouriteState(false));
        });
        api.MapGet("/me/favourites", async (ClaimsPrincipal user, AccountResolver accounts, IConfiguration configuration, TimeProvider clock, CancellationToken token) =>
        {
            var (current, denied) = await MemberAsync(user, accounts, token);
            if (denied is not null) return denied;
            var (db, me, now) = (accounts.Db!, current!.Profile!.AccountId, clock.GetUtcNow());
            var saved = await (from f in db.MemberFavourites join p in db.MemberProfiles on f.TargetAccountId equals p.AccountId
                               where f.AccountId == me orderby f.CreatedAt descending select new { p.PublicId, f.CreatedAt })
                .Take(FavouritePolicy.Max).ToListAsync(token);
            var order = await ProfileEndpoints.BadgeOrderAsync(db, token);
            // One batched check for the whole list, then the shown mode's alias for everyone in one query.
            var opened = await ProfileEndpoints.OpensManyAsync(db, configuration, current.Profile!, saved.Select(r => r.PublicId).ToList(), now, token);
            var listed = saved.Where(r => opened.ContainsKey(r.PublicId)).Take(FavouritePolicy.Listed).Select(r => (Row: r, Open: opened[r.PublicId])).ToList();
            var accountsShown = listed.Select(x => x.Open.Person.Profile.AccountId).ToList();
            var aliases = (await db.MemberModes.Where(m => accountsShown.Contains(m.AccountId)).Select(m => new { m.AccountId, m.ModeId, m.DisplayName }).ToListAsync(token))
                .ToDictionary(m => (m.AccountId, m.ModeId), m => m.DisplayName);
            var list = listed.Select(x => new FavouriteView(new PersonSummary(x.Open.Person.Profile.PublicId,
                // The name the linked profile shows: that mode's alias, else the main name (as Explore and GET /api/profiles/{id}).
                ModePrivacy.DisplayName(aliases.GetValueOrDefault((x.Open.Person.Profile.AccountId, x.Open.ShownMode)), x.Open.Person.Profile.DisplayName)!,
                ProfileEndpoints.Badges(x.Open.Person.Profile, order)), x.Row.CreatedAt)).ToList();
            return Results.Ok(list);
        });
    }
}
