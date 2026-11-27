namespace Swoovr.Domain;

/// <summary>
/// Where a member's date of birth stands after an approved ID check (Q58, issue #33, D166): nothing to do; one
/// correction open (the dates differed); or waiting for an admin's manual review (they still differed after the
/// correction, or Didit gave no date). ID Verified is kept in every state; only the date of birth's mark waits.
/// </summary>
public enum DobCheck { None, CorrectionNeeded, ManualReview }

public enum DobReviewOutcome { Verified, NotVerified }

/// <summary>
/// A manual review of a date of birth that didn't match the verified ID (D166), in the admin queue. Ids, times and the
/// outcome only: never either date; the admin reads the document's date live from Didit (kept there 30 days, D147; D403).
/// </summary>
public sealed class DobReview
{
    public Guid Id { get; set; }
    public Guid AccountId { get; set; }
    public Guid AttemptId { get; set; }
    /// <summary>"still-different", "no-document-date", "document-not-approved" (issue 488), "admin-correction-mismatch" (D299),
    /// or after a live read "document-date-differs" / "document-date-matches" (issue 506).</summary>
    public string Reason { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
    public Guid? ResolvedBy { get; set; }
    public DobReviewOutcome? Outcome { get; set; }
    /// <summary>Staff reason recorded with the outcome (no dates). Null for reviews resolved before it was kept.</summary>
    public string? ResolutionNote { get; set; }
    /// <summary>Issue 488 (D413): when an admin, having looked at the ID, approved it together with the date of birth.</summary>
    public DateTimeOffset? IdApprovedAt { get; set; }
}

/// <summary>Issue 488 (D413): an admin was shown the ID document for this review. Ids and the time only.</summary>
public sealed class DobReviewIdView
{
    public long Id { get; set; }
    public Guid ReviewId { get; set; }
    public Guid ActorAccountId { get; set; }
    public DateTimeOffset At { get; set; }
}

public enum IdApprovalRefusal { None, NotViewed, NotVerified }

public enum DobCorrectionResult { Verified, ManualReview }

/// <summary>
/// Q58 (owner, 26 October 2026, D166): Swoovr keeps the member's own date of birth and never stores the document's.
/// When an approved check's date differs, the member gets one chance (a one-time prompt) to set their date to the one on
/// their ID; the API then reads Didit's decision again and compares. A match marks the date verified; anything else
/// opens a manual review for an admin. A mismatch is never treated as fraud by itself.
/// </summary>
public static class DobCorrectionPolicy
{
    /// <summary>After an approved check: verified on a match, one correction when Didit reported a different date, review when it reported none.</summary>
    public static DobCheck AfterApproval(DateOnly? profile, DateOnly? document) =>
        IdVerificationPolicy.DateOfBirthMatches(profile, document) ? DobCheck.None
        : document is null ? DobCheck.ManualReview : DobCheck.CorrectionNeeded;

    /// <summary>After the member's one correction, compared with Didit's decision read again.</summary>
    public static DobCorrectionResult AfterCorrection(DateOnly corrected, DateOnly? document) =>
        IdVerificationPolicy.DateOfBirthMatches(corrected, document) ? DobCorrectionResult.Verified : DobCorrectionResult.ManualReview;

    /// <summary>Issue 488 (D413): why a review opens when an approved session gave no date of birth.</summary>
    public static string ReviewReason(ProviderSession session) => session.DocumentNotApproved ? "document-not-approved" : "no-document-date";

    /// <summary>
    /// Issue 506 (D416): a review opened as "no date from Didit" says what an admin's live read shows instead, and follows later
    /// reads. Only an Approved document's date counts; every other reason, and a read without both dates, stays as it was.
    /// </summary>
    public static string AfterLiveRead(string reason, DateOnly? profile, IEnumerable<ProviderDocument> documents) =>
        reason is not ("no-document-date" or "document-date-differs" or "document-date-matches") || profile is null
            || documents.FirstOrDefault(d => string.Equals(d.Status, "Approved", StringComparison.OrdinalIgnoreCase) && d.DateOfBirth is not null)?.DateOfBirth is not { } read ? reason
        : read == profile ? "document-date-matches" : "document-date-differs";

    /// <summary>Why a review opens after the member's one correction.</summary>
    public static string CorrectionReason(ProviderSession session) => session.DocumentNotApproved ? "document-not-approved" : "still-different";

    /// <summary>
    /// Q186 A (D413): the same admin has to have seen the ID since the date last changed, and approving keeps ID Verified
    /// but never restores it. Nothing about age, the verified identity or the provider's result changes.
    /// </summary>
    public static IdApprovalRefusal ApproveId(VerificationStatus status, DateTimeOffset? viewedAt, DateTimeOffset changedAt) =>
        status != VerificationStatus.Verified ? IdApprovalRefusal.NotVerified : viewedAt > changedAt ? IdApprovalRefusal.None : IdApprovalRefusal.NotViewed;

    /// <summary>The ordinary date-of-birth form is closed while a correction or a review is open, so the prompt is the only way.</summary>
    public static bool OrdinaryChangeAllowed(DobCheck state) => state == DobCheck.None;
}

public enum NameReviewOutcome { Matches, Mismatch }

/// <summary>
/// Q125 A (owner, 4 November 2026; D317): an ID-verified member changed their registered name. Staff compare the new
/// name with the member's verified identity record (opened through the existing restricted, logged access) and decide.
/// ID Verified stays until then; "mismatch" removes it. Ids, times and the outcome only: never a name.
/// </summary>
public sealed class NameChangeReview
{
    public Guid Id { get; set; }
    public Guid AccountId { get; set; }
    /// <summary>The registered-names revision under review; a newer change replaces the open review's revision.</summary>
    public long Revision { get; set; }
    /// <summary>Null for a name change (D317); "id-name-differs" or "id-name-middle" when the member asked for a review instead of
    /// taking the name on their approved ID (issue 533, D429).</summary>
    public string? Reason { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
    public Guid? ResolvedBy { get; set; }
    public NameReviewOutcome? Outcome { get; set; }
    public string? ResolutionNote { get; set; }
}
