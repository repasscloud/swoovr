using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Swoovr.Contracts;
using Swoovr.Domain;
using Swoovr.Infrastructure.Persistence;
using static Swoovr.Api.Accounts.AccountEndpoints;

namespace Swoovr.Api.Accounts;

/// <summary>
/// Issue 389 (D382 DEFAULT, Q181): the first-sign-in profile steps after D342's account setup. Each step saves through
/// the same routes the profile pages use (`PUT /api/me/profile`, `/api/me/picture-text`, photo uploads,
/// `/api/me/modes/{id}`, `/api/me/identity`), so nothing is duplicated; this records the two optional steps the member
/// continued past and sets the completion flag once every step is done. `GET /api/me` reports the next step as
/// `setupStep` until the flag is set, and the web keeps the member on /welcome until then. The API doesn't refuse
/// other routes for these steps: the app (MAUI) has no onboarding screens yet (web first, M7).
/// </summary>
public static class OnboardingEndpoints
{
    public static void Map(WebApplication app)
    {
        var me = app.MapGroup("/api/me/onboarding").RequireAuthorization();
        me.MapGet("", async (ClaimsPrincipal user, AccountResolver accounts, TimeProvider clock, CancellationToken cancellationToken) =>
        {
            var (current, denied) = await MemberAsync(user, accounts, cancellationToken);
            if (denied is not null) return denied;
            await NextAsync(accounts.Db!, current!.Profile!, clock.GetUtcNow(), cancellationToken);
            return Results.Ok(await ViewAsync(accounts.Db!, current.Profile!, cancellationToken));
        });
        // Continues past an optional step (about, identity); the about step also records the "every mode" answer (181.4 A).
        me.MapPost("/steps/{step}", async (string step, OnboardingStepUpdate? update, ClaimsPrincipal user, AccountResolver accounts, TimeProvider clock,
            CancellationToken cancellationToken) =>
        {
            var (current, denied) = await MemberAsync(user, accounts, cancellationToken);
            if (denied is not null) return denied;
            var (db, profile, now) = (accounts.Db!, current!.Profile!, clock.GetUtcNow());
            var facts = await FactsAsync(db, profile, cancellationToken);
            if (!ProfileOnboarding.Order.Contains(step) || !ProfileOnboarding.MayAcknowledge(step, facts))
                return Problem(StatusCodes.Status409Conflict, ProblemCodes.OnboardingStepIncomplete, "Finish this step before continuing.");
            if (!profile.OnboardingSteps.Contains(step)) profile.OnboardingSteps = [.. profile.OnboardingSteps, step];
            if (step == ProfileOnboarding.About && update?.ShareAbout is { } share) profile.OnboardingShareAbout = share;
            Audit.Record(db, now, profile.AccountId, "account", "onboarding.step", "account", profile.AccountId.ToString(), step);
            await db.SaveChangesAsync(cancellationToken);
            return Results.Ok(await ViewAsync(db, profile, cancellationToken));
        });
        // Sets the completion flag once every step is done; afterwards the steps never come back (a member can still clear a
        // home city or turn modes off later, and Explore says what's missing).
        me.MapPost("/complete", async (ClaimsPrincipal user, AccountResolver accounts, TimeProvider clock, CancellationToken cancellationToken) =>
        {
            var (current, denied) = await MemberAsync(user, accounts, cancellationToken);
            if (denied is not null) return denied;
            var (db, profile, now) = (accounts.Db!, current!.Profile!, clock.GetUtcNow());
            if (profile.OnboardingCompletedAt is null)
            {
                if (await NextAsync(db, profile, now, cancellationToken) is not null)
                    return Problem(StatusCodes.Status409Conflict, ProblemCodes.OnboardingStepIncomplete, "Finish setting up your profile first.");
            }
            return Results.Ok(await ViewAsync(db, profile, cancellationToken));
        });
    }

    /// <summary>D382: the next profile step while the completion flag isn't set, else null (one query set per call, none once done).
    /// When no step remains but the flag is still unset (a member whose profile already had everything, or who left after the
    /// last save but before `/complete` succeeded), the flag is persisted here, so clearing a home city or the last mode later
    /// shows Explore's notice instead of reopening onboarding (PR #392 review).</summary>
    public static async Task<string?> NextAsync(SwoovrDbContext db, MemberProfile profile, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (profile.OnboardingCompletedAt is not null) return null;
        var next = ProfileOnboarding.Next(await FactsAsync(db, profile, cancellationToken));
        if (next is null) await CompleteAsync(db, profile, now, cancellationToken);
        return next;
    }

    /// <summary>Sets the completion flag once (a conditional update, so concurrent requests record one audit entry).</summary>
    private static async Task CompleteAsync(SwoovrDbContext db, MemberProfile profile, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var set = await db.MemberProfiles.Where(p => p.AccountId == profile.AccountId && p.OnboardingCompletedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.OnboardingCompletedAt, now), cancellationToken);
        if (set == 0)
        {
            profile.OnboardingCompletedAt = await db.MemberProfiles.AsNoTracking().Where(p => p.AccountId == profile.AccountId)
                .Select(p => p.OnboardingCompletedAt).SingleAsync(cancellationToken);
            return;
        }
        profile.OnboardingCompletedAt = now;
        if (db.Entry(profile).State != EntityState.Detached) db.Entry(profile).Property(p => p.OnboardingCompletedAt).IsModified = false;
        Audit.Record(db, now, profile.AccountId, "account", "onboarding.completed", "account", profile.AccountId.ToString());
        await db.SaveChangesAsync(cancellationToken);
    }

    public static async Task<OnboardingView> ViewAsync(SwoovrDbContext db, MemberProfile profile, CancellationToken cancellationToken)
    {
        var facts = await FactsAsync(db, profile, cancellationToken);
        var done = profile.OnboardingCompletedAt is not null;
        return new(done ? null : ProfileOnboarding.Next(facts), done ? [] : ProfileOnboarding.Pending(facts), profile.OnboardingShareAbout,
            profile.OnboardingCompletedAt, await LibraryPhotos(db, profile.AccountId).CountAsync(cancellationToken));
    }

    public static async Task<OnboardingFacts> FactsAsync(SwoovrDbContext db, MemberProfile profile, CancellationToken cancellationToken)
    {
        var photo = profile.PictureText is not null || await LibraryPhotos(db, profile.AccountId).AnyAsync(cancellationToken);
        var modes = await db.MemberModes.AsNoTracking().Where(m => m.AccountId == profile.AccountId && m.Enabled).Select(m => m.ModeId).ToListAsync(cancellationToken);
        var identity = await db.MemberIdentities.AsNoTracking().Where(i => i.AccountId == profile.AccountId)
            .Select(i => new { i.GenderId, ShowMe = i.DatingShowMe.Count }).SingleOrDefaultAsync(cancellationToken);
        return new(profile.DisplayName is not null, profile.HomeGeoNameId is not null, profile.About is not null, photo, modes.Count > 0,
            modes.Contains(ConnectionModeIds.Dating), identity?.GenderId is not null, identity?.ShowMe > 0, profile.OnboardingSteps);
    }

    /// <summary>Library photos that count for the photo step: waiting for or passed screening, not album-only, not removed.</summary>
    private static IQueryable<MediaItem> LibraryPhotos(SwoovrDbContext db, Guid account) => db.MediaItems.AsNoTracking()
        .Where(i => i.AccountId == account && i.Purpose == null && !i.AlbumOnly && i.RemovedAt == null
            && (i.Status == MediaStatus.Pending || i.Status == MediaStatus.Passed || i.Status == MediaStatus.Uncertain));
}
