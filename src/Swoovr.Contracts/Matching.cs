namespace Swoovr.Contracts;

/// <summary>Configurable option lists (D138).</summary>
public sealed record OptionView(string Id, string Label);
/// <param name="Pronouns">Issue 389 (D382): the pronoun list; "self" is Self-describe (own words, up to 50 characters).</param>
/// <param name="ShowMe">D382 (Q181.1 A): the Dating "show me" choices (Man, Woman, Non-binary, Another gender). "Man" includes Trans man
/// and "Woman" includes Trans woman.</param>
public sealed record ProfileOptionsView(IReadOnlyList<OptionView> Genders, IReadOnlyList<OptionView> Orientations,
    IReadOnlyList<OptionView>? Pronouns = null, IReadOnlyList<OptionView>? ShowMe = null);

/// <summary>
/// The member's identity details (D138): gender (description only for "another"), pronouns, orientation, what shows
/// publicly, whether the age shows (D139), and Dating's reciprocal "show me" (gender ids).
/// </summary>
/// <param name="Pronouns">The pronouns as others see them (the option's label, or the member's own words with "self").</param>
/// <param name="PronounId">Issue 389 (D382): the pronoun option id; "self" for Self-describe.</param>
/// <param name="ShowPronouns">D382 (Q181.6 A): pronouns have their own switch.</param>
public sealed record IdentityView(string? Gender, string? GenderDescription, string? Pronouns, string? Orientation, bool ShowGender, bool ShowOrientation,
    bool ShowAge, IReadOnlyList<string> DatingShowMe, string? PronounId = null, bool ShowPronouns = true);
/// <summary>
/// D382: PronounId picks an option; with "self", Pronouns holds the member's own words. An older client that sends only
/// Pronouns (free text) gets the matching option, else Self-describe. ShowPronouns null keeps the current switch.
/// </summary>
public sealed record IdentityUpdate(string? Gender, string? GenderDescription, string? Pronouns, string? Orientation, bool ShowGender, bool ShowOrientation,
    bool ShowAge, IReadOnlyList<string>? DatingShowMe, string? PronounId = null, bool? ShowPronouns = null);

/// <summary>
/// One mode's filters (D137, D347). Every tier: age range, required chips ("any" or "all"), excluded chips (reciprocal,
/// D140), ID Verified only, visitors ("both", "current", "upcoming", "neither") and MaxDistanceKm from DistanceOptions
/// (5, 10, 25 and 50 km; plus 100, 250 and 500 km on paid tiers). Plus: Sort "compatibility". PlusAvailable says
/// whether the member's plan includes Plus (entitlement service, D151).
/// </summary>
public sealed record FilterView(string Mode, int? MinAge, int? MaxAge, IReadOnlyList<Guid> RequiredChips, string RequiredMatch, IReadOnlyList<Guid> ExcludedChips,
    bool IdVerifiedOnly, string Visitors, int? MaxDistanceKm, string Sort, bool PlusAvailable, IReadOnlyList<int>? DistanceOptions = null);
public sealed record FilterUpdate(int? MinAge, int? MaxAge, IReadOnlyList<Guid>? RequiredChips, string? RequiredMatch, IReadOnlyList<Guid>? ExcludedChips,
    bool IdVerifiedOnly, string? Visitors, int? MaxDistanceKm, string? Sort);
