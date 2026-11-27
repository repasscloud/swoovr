using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Swoovr.Api.Accounts;
using Swoovr.Contracts;
using Swoovr.Domain;
using Swoovr.Infrastructure.Persistence;
using static Swoovr.Api.Accounts.AccountEndpoints;

namespace Swoovr.Api.Profiles;

/// <summary>
/// Identity details and Dating's "show me" (D138), and per-mode filters (D137) with Plus filters answered by the
/// entitlement service (D151). The matching itself (reciprocal rules and filters) runs in ProfileEndpoints through
/// <see cref="MatchPolicy"/>; this holds the member's own settings.
/// </summary>
public static class MatchingEndpoints
{
    public static void Map(WebApplication app)
    {
        var api = app.MapGroup("/api").RequireAuthorization();
        api.MapGet("/profile-options", async (ClaimsPrincipal user, AccountResolver accounts, CancellationToken cancellationToken) =>
        {
            var (_, denied) = await MemberAsync(user, accounts, cancellationToken);
            if (denied is not null) return denied;
            var options = await accounts.Db!.ProfileOptions.Where(o => o.Available).OrderBy(o => o.SortOrder).ToListAsync(cancellationToken);
            return Results.Ok(new ProfileOptionsView(
                options.Where(o => o.Kind == "gender").Select(o => new OptionView(o.Id, o.Label)).ToList(),
                options.Where(o => o.Kind == "orientation").Select(o => new OptionView(o.Id, o.Label)).ToList(),
                // Issue 389 (D382): the pronoun list, and the Dating "show me" choices (Q181.1 A).
                options.Where(o => o.Kind == "pronoun").Select(o => new OptionView(o.Id, o.Label)).ToList(),
                options.Where(o => o.Kind == "gender" && MatchPolicy.ShowMeOption(o.Id)).Select(o => new OptionView(o.Id, o.Label)).ToList()));
        });
        api.MapGet("/me/identity", async (ClaimsPrincipal user, AccountResolver accounts, CancellationToken cancellationToken) =>
        {
            var (current, denied) = await MemberAsync(user, accounts, cancellationToken);
            if (denied is not null) return denied;
            return Results.Ok(View(await IdentityAsync(accounts.Db!, current!.Profile!.AccountId, cancellationToken), await ProfileEndpoints.GenderLabelsAsync(accounts.Db!, cancellationToken)));
        });
        api.MapPut("/me/identity", async (IdentityUpdate update, ClaimsPrincipal user, AccountResolver accounts, TimeProvider clock, CancellationToken cancellationToken) =>
        {
            var (current, denied) = await MemberAsync(user, accounts, cancellationToken);
            if (denied is not null) return denied;
            var (db, now, id) = (accounts.Db!, clock.GetUtcNow(), current!.Profile!.AccountId);
            var options = await db.ProfileOptions.Where(o => o.Available).ToListAsync(cancellationToken);
            var identity = await db.MemberIdentities.FindAsync([id], cancellationToken);
            if (identity is null) db.MemberIdentities.Add(identity = new MemberIdentity { AccountId = id });
            // Issue 389 (D382): a pronoun option, with own words only for Self-describe. An older client sends free text only,
            // which maps to the matching option or Self-describe (Q181.6 A).
            var (pronounId, pronouns) = Blank(update.PronounId) is { } chosen
                ? (chosen, chosen == MatchPolicy.SelfDescribe ? Blank(update.Pronouns) : null)
                : MatchPolicy.Pronoun(update.Pronouns, options);
            var candidate = new MemberIdentity
            {
                AccountId = id, GenderId = Blank(update.Gender), GenderDescription = update.Gender == "another" ? Blank(update.GenderDescription) : null,
                PronounId = pronounId, Pronouns = pronouns, OrientationId = Blank(update.Orientation), ShowGender = update.ShowGender, ShowOrientation = update.ShowOrientation,
                ShowAge = update.ShowAge, DatingShowMe = (update.DatingShowMe ?? []).Distinct().ToList(),
                // D382: a client that predates the pronoun switch (null) had one "Show my gender and pronouns" toggle, so turning it
                // off hides pronouns too and turning it on keeps the current switch (PR #392 review).
                ShowPronouns = update.ShowPronouns ?? (update.ShowGender && identity.ShowPronouns),
            };
            var check = MatchPolicy.Check(candidate, options.Where(o => o.Kind == "gender").Select(o => o.Id).ToHashSet(),
                options.Where(o => o.Kind == "orientation").Select(o => o.Id).ToHashSet(), options.Where(o => o.Kind == "pronoun").Select(o => o.Id).ToHashSet());
            if (check == FilterError.TextTooLong)
                return Problem(StatusCodes.Status400BadRequest, ProblemCodes.InvalidProfile, $"Keep your pronouns to {MatchPolicy.PronounsMax} characters.");
            if (check != FilterError.None)
                return Problem(StatusCodes.Status400BadRequest, ProblemCodes.InvalidProfile, "Check the options you picked.");
            // Issue 568 (PR 582 review): "Profile updated" moves only when what others see changes, never for a hidden value.
            var labels = await ProfileEndpoints.GenderLabelsAsync(db, cancellationToken);
            var orientations = await db.ProfileOptions.Where(o => o.Kind == "orientation").ToDictionaryAsync(o => o.Id, o => o.Label, cancellationToken);
            if (ProfileEndpoints.Public(identity, 0, labels) != ProfileEndpoints.Public(candidate, 0, labels)) identity.PresentationChangedAt = now;
            if (ProfileEndpoints.OrientationIn("dating", identity, orientations) != ProfileEndpoints.OrientationIn("dating", candidate, orientations)) identity.OrientationChangedAt = now;
            (identity.GenderId, identity.GenderDescription, identity.PronounId, identity.Pronouns, identity.OrientationId, identity.ShowGender, identity.ShowOrientation, identity.ShowAge,
                identity.ShowPronouns, identity.DatingShowMe, identity.UpdatedAt) = (candidate.GenderId, candidate.GenderDescription, candidate.PronounId, candidate.Pronouns,
                candidate.OrientationId, candidate.ShowGender, candidate.ShowOrientation, candidate.ShowAge, candidate.ShowPronouns, candidate.DatingShowMe, now);
            // The audit says the identity changed, never what it says (gender and orientation are sensitive).
            Audit.Record(db, now, id, "account", "identity.updated", "account", id.ToString());
            await db.SaveChangesAsync(cancellationToken);
            return Results.Ok(View(identity, options.Where(o => o.Kind == "pronoun").ToDictionary(o => ProfileEndpoints.PronounKey + o.Id, o => o.Label)));
        });
        api.MapGet("/me/modes/{modeId}/filters", async (string modeId, ClaimsPrincipal user, AccountResolver accounts, TimeProvider clock, CancellationToken cancellationToken) =>
        {
            var (current, denied) = await MemberAsync(user, accounts, cancellationToken);
            if (denied is not null) return denied;
            var db = accounts.Db!;
            if (!await db.ConnectionModes.AnyAsync(m => m.Id == modeId && m.Available, cancellationToken))
                return Problem(StatusCodes.Status404NotFound, ProblemCodes.UnknownMode, "That connection mode isn't available.");
            var filter = await db.ModeFilters.FindAsync([current!.Profile!.AccountId, modeId], cancellationToken);
            return Results.Ok(View(filter, modeId, await Moderation.Entitlements.TierAsync(db, current.Profile, clock.GetUtcNow(), cancellationToken)));
        });
        api.MapPut("/me/modes/{modeId}/filters", async (string modeId, FilterUpdate update, ClaimsPrincipal user, AccountResolver accounts, TimeProvider clock,
            CancellationToken cancellationToken) =>
        {
            var (current, denied) = await MemberAsync(user, accounts, cancellationToken);
            if (denied is not null) return denied;
            var (db, now, id) = (accounts.Db!, clock.GetUtcNow(), current!.Profile!.AccountId);
            if (!await db.ConnectionModes.AnyAsync(m => m.Id == modeId && m.Available, cancellationToken))
                return Problem(StatusCodes.Status404NotFound, ProblemCodes.UnknownMode, "That connection mode isn't available.");
            // #90: filter changes are limited per hour so they can't be used to narrow down where someone lives.
            if (await Searches.LimitedAsync(db, id, "filter", now, cancellationToken) is { } limited) return limited;
            if (!Enum.TryParse<ChipMatch>(update.RequiredMatch ?? "any", true, out var match) || !Enum.IsDefined(match)
                || !Enum.TryParse<VisitorVisibility>(update.Visitors ?? "both", true, out var visitors) || !Enum.IsDefined(visitors)
                || !Enum.TryParse<ExploreSort>(update.Sort ?? "recent", true, out var sort) || !Enum.IsDefined(sort)
                || new[] { update.RequiredMatch, update.Visitors, update.Sort }.Any(v => int.TryParse(v, out _)))
                return Problem(StatusCodes.Status400BadRequest, ProblemCodes.InvalidFilter, "Check the filter values.");
            var candidate = new ModeFilter
            {
                AccountId = id, ModeId = modeId, MinAge = update.MinAge, MaxAge = update.MaxAge, RequiredChips = (update.RequiredChips ?? []).Distinct().ToList(),
                RequiredMatch = match, ExcludedChips = (update.ExcludedChips ?? []).Distinct().ToList(), IdVerifiedOnly = update.IdVerifiedOnly, Visitors = visitors,
                // #90: a distance snaps to its band (5, 10, 25, 50, 100, 250 or 500 km).
                MaxDistanceKm = update.MaxDistanceKm is { } km and >= 1 and <= MatchPolicy.MaxDistanceKm ? LocationPrivacy.Snap(km) : update.MaxDistanceKm, Sort = sort, UpdatedAt = now,
            };
            var chips = (await db.ModeIntentions.Where(i => i.ModeId == modeId && i.Available).Select(i => i.Id).ToListAsync(cancellationToken)).ToHashSet();
            var tier = await Moderation.Entitlements.TierAsync(db, current.Profile, now, cancellationToken);
            var saved = await db.ModeFilters.AsNoTracking().Where(f => f.AccountId == id && f.ModeId == modeId).Select(f => f.MaxDistanceKm).FirstOrDefaultAsync(cancellationToken);
            switch (MatchPolicy.Check(candidate, chips, tier, saved))
            {
                case FilterError.None: break;
                case FilterError.PlusRequired: return Problem(StatusCodes.Status403Forbidden, ProblemCodes.PlusRequired,
                    candidate.MaxDistanceKm > LocationPrivacy.MaxFor(tier) ? $"Distances over {LocationPrivacy.FreeMaxKm} km are part of Plus." : "That filter is part of Plus.");
                default: return Problem(StatusCodes.Status400BadRequest, ProblemCodes.InvalidFilter, "Check the filter values.");
            }
            var filter = await db.ModeFilters.FindAsync([id, modeId], cancellationToken);
            if (filter is null) db.ModeFilters.Add(filter = new ModeFilter { AccountId = id, ModeId = modeId });
            (filter.MinAge, filter.MaxAge, filter.RequiredChips, filter.RequiredMatch, filter.ExcludedChips, filter.IdVerifiedOnly, filter.Visitors, filter.MaxDistanceKm,
                filter.Sort, filter.UpdatedAt) = (candidate.MinAge, candidate.MaxAge, candidate.RequiredChips, match, candidate.ExcludedChips, candidate.IdVerifiedOnly,
                visitors, candidate.MaxDistanceKm, sort, now);
            Audit.Record(db, now, id, "account", "filters.updated", "connection-mode", modeId);
            await db.SaveChangesAsync(cancellationToken);
            return Results.Ok(View(filter, modeId, tier));
        });
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public static async Task<MemberIdentity> IdentityAsync(SwoovrDbContext db, Guid account, CancellationToken cancellationToken) =>
        await db.MemberIdentities.FindAsync([account], cancellationToken) ?? new MemberIdentity { AccountId = account };

    private static IdentityView View(MemberIdentity i, IReadOnlyDictionary<string, string> labels) =>
        new(i.GenderId, i.GenderDescription, ProfileEndpoints.PronounText(i, labels), i.OrientationId, i.ShowGender, i.ShowOrientation, i.ShowAge, i.DatingShowMe, i.PronounId, i.ShowPronouns);

    private static FilterView View(ModeFilter? f, string modeId, PlanTier tier) => f is null
        ? new(modeId, null, null, [], "any", [], false, "both", null, "recent", tier >= PlanTier.Plus, LocationPrivacy.FilterOptions(tier))
        : new(modeId, f.MinAge, f.MaxAge, f.RequiredChips, Wire(f.RequiredMatch), f.ExcludedChips, f.IdVerifiedOnly, Wire(f.Visitors), f.MaxDistanceKm, Wire(f.Sort), tier >= PlanTier.Plus,
            LocationPrivacy.FilterOptions(tier));
}
