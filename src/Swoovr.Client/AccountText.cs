using Swoovr.Contracts;
namespace Swoovr.Client;

/// <summary>
/// Wording for the signed-in account screen, shared by the web page (Web.Client) and the app (Mobile) so both say
/// the same thing about the same API answer. Labels only: the API decides badges, beta and access (D55).
/// </summary>
public static class AccountText
{
    // Issue 439: the date of birth follow-up after an ID check, in plain words. Neither date is ever shown (D166).
    public const string DobReviewTitle = "Your ID is verified.";
    public const string DobReview = "Someone from Swoovr will check that the date of birth on your account matches your ID. You don't need to do anything, and we'll contact you if we need more.";
    public const string DobLockedCorrection = "Use the prompt above to set your date of birth to the one on your ID.";
    public const string DobLockedReview = "Swoovr is checking this against your ID, so you can't change it for now.";
    public static string DobCorrected(bool matches) => matches
        ? "Thanks. Your date of birth now matches your ID."
        : "Thanks. Someone from Swoovr will check it against your ID. Your ID Verified badge stays.";
    public const string BetaUrl = "https://swoovr.com/beta/";
    public const string EmailChangeHeading = "Your email change needs attention";
    public const string EmailChangeHelp = "Your previously accepted email remains on this account. Your account standing and existing access stay the same. Contact Support for help, or sign in again after correcting the email on your sign-in account.";
    public static string EmailChangeMessage(string code) => code switch
    {
        ProblemCodes.IdentifierRestricted => "This email address cannot currently be used with Swoovr. If you believe this is incorrect, please contact Swoovr Support for assistance.",
        ProblemCodes.TooManyRequests => "We haven't changed your Swoovr email. Too many identifier checks were made; try again in an hour.",
        "email-session-refresh-required" => "Sign in again before changing your Swoovr email.",
        _ => "We can't check your new email right now. Your Swoovr email hasn't changed. Please try again later.",
    };

    /// <summary>D77: the same words whatever the reason, so the screen never says whether an address was ever enrolled.</summary>
    public static bool BetaBlocked(SessionSummary? session) => session?.Beta is "notEnrolled" or "unavailable";

    public static string BadgeKind(string badge) => badge switch
    {
        "founder" => "founding", "photoVerified" => "photo", "idVerified" => "id", "staff" => "staff", _ => "bot",
    };

    /// <summary>Issue 494 (D406): Explore cards show badges in this order whatever the configured order is; a bot's only badge is the bot badge.</summary>
    private static readonly string[] CardOrder = ["idVerified", "photoVerified", "founder", "staff", "bot"];
    public static IReadOnlyList<Swoovr.Contracts.BadgeView> CardBadges(IReadOnlyList<Swoovr.Contracts.BadgeView> badges) =>
        badges.OrderBy(b => Array.IndexOf(CardOrder, b.Badge) is >= 0 and var i ? i : CardOrder.Length).ToList();

    /// <summary>What a badge means, for the explanation on a card.</summary>
    public static string BadgeMeaning(string badge) => badge switch
    {
        "idVerified" => "Their ID document was checked.", "photoVerified" => "Their profile photos were checked against them.",
        "founder" => "One of Swoovr's founding members.", "staff" => "Works at Swoovr.", _ => "An official Swoovr bot, not a person.",
    };

    public static string BadgeLabel(string badge) => badge switch
    {
        "founder" => "Swoovr Founder", "photoVerified" => "Photo verified", "idVerified" => "ID verified",
        "staff" => "Swoovr staff", _ => "Swoovr guide (bot)",
    };

    public static string MethodLabel(string method) => method switch
    {
        // Keycloak 26.8.1 reports a passkey used on the sign-in form as the password method.
        "emailPassword" => "Password or passkey",
        "passkey" => "Passkey",
        "emailLink" => "Email sign-in link",
        "apple" => "Apple",
        "google" => "Google",
        // Keycloak records no method for the session it starts right after email-first sign-up.
        _ => "New account (just created)",
    };

    public static string AgeSaved(MemberAccount? account) =>
        account?.Age is { } years ? $"Saved. Your age shows as {years}." : "Saved. No age is shown.";

    public const string InvalidAge = "Check the date or age: it can't be in the future or over 120.";

    /// <summary>Result kind (for tests and markup hooks) and message for a coupon attempt.</summary>
    public static (string Result, string Message) Coupon(ApiResult<MemberAccount> result) => result.Code switch
    {
        null when result.Value is not null => ("ok", result.Value.Badges.Any(b => b.Badge == "founder") ? "Your founding status is restored." : "Code applied."),
        ProblemCodes.TooManyAttempts => ("limited", "Too many attempts. Try again in an hour."),
        _ => ("invalid", "That code can't be used."),
    };
}
