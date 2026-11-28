namespace Swoovr.Contracts;

/// <summary>
/// D424/D427 (#530): the member's Photo Verified state. Available: the host offers it (off until Privacy Notice 0.22).
/// State: none, issued (Code and WrittenDate to write on paper, until CodeExpiresAt), submitted (waiting for a person),
/// verified (until VerifiedUntil), rejected (Reason in member words; they can try again while AttemptsLeft &gt; 0) or expired.
/// </summary>
public sealed record PhotoVerificationView(bool Available, string State, int AttemptsLeft, string? Code = null, DateOnly? WrittenDate = null,
    DateTimeOffset? CodeExpiresAt = null, DateTimeOffset? VerifiedAt = null, DateTimeOffset? VerifiedUntil = null, string? Reason = null, bool HasProfilePhoto = false);

/// <summary>D424: ask for a code; Date is the member's own local date (it goes on the paper).</summary>
public sealed record PhotoVerificationCodeRequest(DateOnly Date);

/// <summary>D424: one request in the staff Photo Identified queue. Evidence is a short-lived staff link; ProfilePhotos are
/// the public profile photos the member had when they sent it, as member links.</summary>
public sealed record PhotoIdentifiedItem(Guid Id, Guid AccountId, string? DisplayName, string Code, DateOnly WrittenDate, DateTimeOffset SubmittedAt,
    string? Evidence, IReadOnlyList<PhotoView> ProfilePhotos, bool ProfilePhotosChanged);

/// <summary>D424: approve, or reject with one of the reasons (PhotoVerificationPolicy.Reasons).</summary>
public sealed record PhotoIdentifiedDecision(string? Decision, string? Reason);
