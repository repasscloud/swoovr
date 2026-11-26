namespace Swoovr.Contracts;

/// <summary>
/// Issue 535 (D425): why an admin opens a member's Didit result or ID document during a date of birth review. Stable codes are
/// recorded in the sensitive-access log (`identity_accesses.reason_code`); the label, plus any optional detail, is the recorded
/// purpose (D403, D413). Staff choose one instead of typing a reason every time.
/// </summary>
public static class IdentityAccessReasons
{
    /// <summary>The Didit result inside a review: the review itself is the purpose, so details are optional.</summary>
    public const string DiscrepancyReview = "identity-discrepancy-review";
    public const string DateOfBirth = "dob-verification";
    public const string LegalName = "legal-name-verification";
    public const string Both = "dob-and-legal-name";
    public const string Document = "document-discrepancy";
    /// <summary>Needs a detail.</summary>
    public const string Other = "other";
    public const int DetailMax = 300;

    /// <summary>The ID document's dropdown, in order.</summary>
    public static readonly IReadOnlyList<string> IdDocument = [DateOfBirth, LegalName, Both, Document, Other];
    /// <summary>The Didit result: only the review's own purpose.</summary>
    public static readonly IReadOnlyList<string> DiditResult = [DiscrepancyReview];

    public static string Label(string code) => code switch
    {
        DiscrepancyReview => "Identity discrepancy review",
        DateOfBirth => "Date of birth verification",
        LegalName => "Legal name verification",
        Both => "Date of birth and legal name",
        Document => "Document discrepancy investigation",
        Other => "Other",
        _ => code,
    };

    /// <summary>The purpose to record for a chosen code and optional detail; null when the code isn't allowed here, the detail
    /// is too long, or Other has no detail.</summary>
    public static string? Purpose(string? code, string? detail, IReadOnlyList<string> allowed)
    {
        if (code is null || !allowed.Contains(code)) return null;
        var more = detail?.Trim() ?? "";
        if (more.Length > DetailMax || code == Other && more.Length == 0) return null;
        return more.Length == 0 ? Label(code) : $"{Label(code)}: {more}";
    }
}
