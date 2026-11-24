using System.Text.RegularExpressions;

namespace Swoovr.Domain;

/// <summary>
/// Issue 455 (D404): staff ask an admin to look at an account (and its open date of birth review, if any) instead of deciding.
/// Internal only: never shown to the member, and never a date or document data in either text.
/// </summary>
public sealed class AccountEscalation
{
    public Guid Id { get; set; }
    public Guid AccountId { get; set; }
    public Guid? DobReviewId { get; set; }
    public string Reason { get; set; } = "";
    public Guid CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid? ResolvedBy { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
    public string? ResolutionNote { get; set; }
}

public static partial class EscalationPolicy
{
    public const int TextMax = 1000;
    [GeneratedRegex(@"\b\d{1,2}[/.\-]\d{1,2}[/.\-]\d{2,4}\b|\b(19|20)\d{2}[/.\-]\d{1,2}[/.\-]\d{1,2}\b|\b\d{1,2}(st|nd|rd|th)?\s+(jan|feb|mar|apr|may|jun|jul|aug|sep|oct|nov|dec)[a-z]*\.?,?\s+\d{4}\b|\b(jan|feb|mar|apr|may|jun|jul|aug|sep|oct|nov|dec)[a-z]*\.?\s+\d{1,2}(st|nd|rd|th)?,?\s+\d{4}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 250)]
    private static partial Regex Date();

    /// <summary>Text staff wrote, trimmed; null when it is empty, too long or holds a full date.</summary>
    public static string? Text(string? value) =>
        value?.Trim() is { Length: > 0 and <= TextMax } text && !Date().IsMatch(text) ? text : null;

    /// <summary>Only an admin resolves, and never their own escalation (DEFAULT, D404).</summary>
    public static bool MayResolve(AccountEscalation escalation, Guid staff, bool admin) => admin && escalation.ResolvedAt is null && escalation.CreatedBy != staff;
}
