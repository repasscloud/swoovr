using System.Text.RegularExpressions;

namespace Swoovr.Domain;

/// <summary>
/// Issue 389 (D382): what the first-sign-in profile steps can see about the member. Every value comes from the
/// member's own records; nothing is inferred from email or the identity provider.
/// </summary>
/// <param name="HasPhotoOrPicture">At least one library photo that is waiting for or passed screening (not album-only,
/// not removed or refused), or the "What would you like your picture to say?" text.</param>
/// <param name="Acknowledged">The optional steps the member continued past (about, identity), stored on the profile.</param>
public sealed record OnboardingFacts(bool HasDisplayName, bool HasHomeCity, bool HasAbout, bool HasPhotoOrPicture, bool AnyModeOn, bool DatingOn,
    bool HasGender, bool HasShowMe, IReadOnlyCollection<string> Acknowledged);

/// <summary>
/// Issue 389 (D382 DEFAULT, Q181): the profile steps that follow D342's account setup (names, mobile, policies, date
/// of birth), in the owner's order: 1 display name and home city, 2 about, 3 photos or a text picture, 4 modes,
/// 5 gender, pronouns and orientation, 6 Dating "show me" (only while Dating is on). A step is done when its data is
/// there (181.7 A: existing accounts see only what is missing), or, for the two optional steps, when the member
/// continued past it. The API records each one, so leaving and coming back resumes at the first step not done. Once
/// every step is done the profile's completion flag is set and these steps never come back.
/// </summary>
public static class ProfileOnboarding
{
    public const string Profile = "profile", About = "about", Photos = "photos", Modes = "modes", Identity = "identity", DatingShowMe = "datingShowMe";

    public static readonly IReadOnlyList<string> Order = [Profile, About, Photos, Modes, Identity, DatingShowMe];

    /// <summary>The onboarding step allows at most this many uploads; the library itself keeps up to <see cref="MediaPolicy.MaxKept"/>.</summary>
    public const int OnboardingUploads = 10;

    public static bool Done(string step, OnboardingFacts facts) => step switch
    {
        Profile => facts.HasDisplayName && facts.HasHomeCity,
        About => facts.HasAbout || facts.Acknowledged.Contains(About),
        Photos => facts.HasPhotoOrPicture,
        // 181.8 A: at least one mode.
        Modes => facts.AnyModeOn,
        // 181.8 A: gender is required only when Dating is on ("Prefer not to say" counts, D161); otherwise optional.
        Identity => (!facts.DatingOn || facts.HasGender) && (facts.HasGender || facts.Acknowledged.Contains(Identity)),
        DatingShowMe => !facts.DatingOn || facts.HasShowMe,
        _ => true,
    };

    /// <summary>The first step not done, or null when every step is done.</summary>
    public static string? Next(OnboardingFacts facts) => Order.FirstOrDefault(step => !Done(step, facts));

    /// <summary>The steps still to do, in order ("Step 2 of 4"). Dating's "show me" is listed only while Dating is on.</summary>
    public static IReadOnlyList<string> Pending(OnboardingFacts facts) => Order.Where(step => !Done(step, facts)).ToList();

    /// <summary>Only the optional steps can be continued past without data, and identity only when Dating doesn't need a gender.</summary>
    public static bool MayAcknowledge(string step, OnboardingFacts facts) =>
        step == About || step == Identity && (!facts.DatingOn || facts.HasGender);
}

public enum PictureTextError { None, TooLong, ControlCharacters, ContactDetails }

/// <summary>
/// Issue 389 (D382, Q181.5 A): "What would you like your picture to say?" The member's own words (up to 100
/// characters, one line), drawn by the app as a text tile. Swoovr never generates an image from them. They're
/// moderated like other public profile text (the text-harm queue and content removal), and contact details are
/// refused when saved: email addresses, links, social handles and phone numbers (eight or more digits).
/// </summary>
public static partial class PictureTextPolicy
{
    public const int Max = 100;

    public static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public static PictureTextError Check(string? text)
    {
        if (text is null) return PictureTextError.None;
        if (text.Any(char.IsControl)) return PictureTextError.ControlCharacters;
        if (text.Length > Max) return PictureTextError.TooLong;
        return ContactDetails(text) ? PictureTextError.ContactDetails : PictureTextError.None;
    }

    /// <summary>Deliberately broad: a false positive costs a rewording, a miss puts contact details on a public tile.</summary>
    public static bool ContactDetails(string text) =>
        Email().IsMatch(text) || Link().IsMatch(text) || Handle().IsMatch(text) || Phone().Matches(text).Any(m => m.Value.Count(char.IsAsciiDigit) >= 8);

    [GeneratedRegex(@"[^\s@]+@[^\s@]+\.[^\s@]+", RegexOptions.CultureInvariant)]
    private static partial Regex Email();

    [GeneratedRegex(@"(https?://|www\.|\b[a-z0-9-]+\.(com|net|org|io|app|me|au|co|uk|info|biz|ly|gg)\b)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Link();

    [GeneratedRegex(@"(^|\s)@[A-Za-z0-9_.]{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex Handle();

    [GeneratedRegex(@"\+?\d[\d\s().\-]{6,}\d", RegexOptions.CultureInvariant)]
    private static partial Regex Phone();
}
