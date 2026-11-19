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
        me.MapGet("", async (ClaimsPrincipal user, AccountResolver accounts, CancellationToken cancellationToken) =>
        {
            var (current, denied) = await MemberAsync(user, accounts, cancellationToken);
            if (denied is not null) return denied;
            return Results.Ok(await ViewAsync(accounts.Db!, current!.Profile!, cancellationToken));
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
                if (ProfileOnboarding.Next(await FactsAsync(db, profile, cancellationToken)) is not null)
                    return Problem(StatusCodes.Status409Conflict, ProblemCodes.OnboardingStepIncomplete, "Finish setting up your profile first.");
                profile.OnboardingCompletedAt = now;
                Audit.Record(db, now, profile.AccountId, "account", "onboarding.completed", "account", profile.AccountId.ToString());
                await db.SaveChangesAsync(cancellationToken);
            }
            return Results.Ok(await ViewAsync(db, profile, cancellationToken));
        });
    }

    /// <summary>D382: the next profile step while the completion flag isn't set, else null (one query set per call, none once done).</summary>
    public static async Task<string?> NextAsync(SwoovrDbContext db, MemberProfile profile, CancellationToken cancellationToken) =>
        profile.OnboardingCompletedAt is not null ? null : ProfileOnboarding.Next(await FactsAsync(db, profile, cancellationToken));

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
