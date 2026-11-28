using System.Security.Cryptography;

namespace Swoovr.Domain;

/// <summary>D424 (#530): where one Photo Verified request stands.</summary>
public enum PhotoCheckState { Issued, Submitted, Approved, Rejected, Expired }

/// <summary>
/// D424 and D427 (#530): one Photo Verified request. The member gets a single-use, account-bound four-character code and
/// the date to write on paper, then uploads one photo of themselves holding it. Staff compare it with the public profile
/// photos the member had when they sent it (<see cref="ProfilePhotos"/>); approval refuses if those changed since. The
/// evidence photo is deleted 30 days after the decision; this record (who, when, outcome, which photos) stays.
/// </summary>
public sealed class PhotoVerificationAttempt
{
    public Guid Id { get; set; }
    public Guid AccountId { get; set; }
    public string Code { get; set; } = "";
    /// <summary>The date the member writes under the code: their own local date when the code was issued.</summary>
    public DateOnly WrittenDate { get; set; }
    public DateTimeOffset IssuedAt { get; set; }
    public PhotoCheckState State { get; set; }
    public Guid? MediaId { get; set; }
    public DateTimeOffset? SubmittedAt { get; set; }
    /// <summary>The member's public profile photo ids when they sent the photo, comma separated, sorted.</summary>
    public string? ProfilePhotos { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public Guid? DecidedBy { get; set; }
    public string? Reason { get; set; }
    public DateTimeOffset? EvidenceDeletedAt { get; set; }
}

/// <summary>D424: a public profile photo that was matched when Photo Verified was approved. Changing it removes the badge.</summary>
public sealed class PhotoVerificationMatch
{
    public Guid AccountId { get; set; }
    public Guid MediaId { get; set; }
    public DateTimeOffset VerifiedAt { get; set; }
}

/// <summary>D424 and D427 (owner, 26 November 2026; every A of Q190).</summary>
public static class PhotoVerificationPolicy
{
    /// <summary>The media purpose of an evidence photo; never a library, profile, album or chat photo.</summary>
    public const string Purpose = "verification";
    /// <summary>No 0/O, 1/I/L, 2/Z, 5/S, 6/G, 8/B: easy to write by hand and to read back.</summary>
    public const string Alphabet = "ACDEFHJKMNPQRTUVWXY3479";
    public const int CodeLength = 4;
    /// <summary>D427: up to 3 submissions in 30 days, each with a fresh code.</summary>
    public const int MaxSubmissions = 3;
    public static readonly TimeSpan Window = TimeSpan.FromDays(30);
    /// <summary>DEFAULT: a code is good for a day; a new one can be asked for any time before sending.</summary>
    public static readonly TimeSpan CodeLifetime = TimeSpan.FromHours(24);
    /// <summary>D427: the evidence photo is deleted 30 days after the decision.</summary>
    public static readonly TimeSpan EvidenceRetention = TimeSpan.FromDays(30);
    /// <summary>D427: Photo Verified lasts 12 months.</summary>
    public static DateTimeOffset Until(DateTimeOffset verified) => verified.AddMonths(12);

    public static string NewCode() => string.Create(CodeLength, 0, (chars, _) =>
    {
        for (var i = 0; i < chars.Length; i++) chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
    });

    /// <summary>The member's local date can be a day either side of the server's.</summary>
    public static bool DateAccepted(DateOnly written, DateTimeOffset now)
    {
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        return written >= today.AddDays(-1) && written <= today.AddDays(1);
    }

    public static bool CodeLive(PhotoVerificationAttempt attempt, DateTimeOffset now) =>
        attempt.State == PhotoCheckState.Issued && attempt.IssuedAt + CodeLifetime > now;

    public static string Snapshot(IEnumerable<Guid> photos) => string.Join(',', photos.Distinct().Order().Select(p => p.ToString("N")));

    public static IReadOnlyList<Guid> Photos(string? snapshot) =>
        string.IsNullOrEmpty(snapshot) ? [] : snapshot.Split(',').Select(p => Guid.ParseExact(p, "N")).ToList();

    /// <summary>Staff reasons for not awarding it; each has member wording that never sounds like an accusation.</summary>
    public static readonly IReadOnlyDictionary<string, string> Reasons = new Dictionary<string, string>
    {
        ["face-not-matched"] = "We couldn't match the photo to your profile photos.",
        ["code-not-readable"] = "We couldn't read the code or the date on the paper.",
        ["code-or-date-wrong"] = "The code or the date on the paper didn't match.",
        ["photo-unclear"] = "Your face, the code and the date need to be clearly visible.",
        ["profile-photos-changed"] = "Your profile photos changed after you sent it. Send a new one.",
        ["other"] = "We couldn't award Photo Verified this time.",
    };
}
