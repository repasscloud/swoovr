namespace Swoovr.Admin;

/// <summary>Short staff-facing labels for media review codes and decisions. Codes stay internal and are not displayed.</summary>
public static class MediaText
{
    /// <summary>Issues 536 and 537: the Destination row. A private album is a green pill whatever the signal; a photo library upload
    /// with a nudity signal is an amber pill, so it isn't mistaken for a main profile photo; anything else is plain text. It shows the
    /// destination the API supplies, never one inferred from the signal. Tone null: plain text.
    /// D423 (#546): a photo sent straight to chat is a blue Chat pill, whatever the signal.</summary>
    public static (string Label, string? Tone) Destination(string? destination, string? signal) => destination switch
    {
        "album" => ("Private album", "album"),
        "chat" => ("Chat", "chat"),
        _ => ("Photo library", signal == "nudity-signal" ? "library" : null),
    };

    public static string Reason(string code) => code switch
    {
        "APPROVED_STANDARD" => "Meets photo rules",
        "APPROVED_NON_EXPLICIT" => "Non-explicit, permitted",
        "APPROVED_AFTER_REVIEW" => "Acceptable after review",
        "APPROVED_FALSE_POSITIVE" => "Automated flag cleared",
        "APPROVED_ALBUM_EXPLICIT" => "Adult content: private albums only",
        "APPROVED_CHAT_EXPLICIT" => "Adult content: chat, only if the recipient accepts it",
        "REJECT_EXPLICIT_NO_ID" => "Explicit content not allowed without ID verification",
        "REJECT_EXPLICIT_BETA" => "Explicit content not allowed here",
        "REJECT_IMPERSONATION" => "Impersonation or unauthorised use",
        "REJECT_PRIVACY" => "Personal or location information",
        "REJECT_VIOLENCE" => "Graphic violence",
        "REJECT_HATE" => "Hate or extremist imagery",
        "REJECT_SPAM" => "Spam or promotion",
        "REJECT_IMAGE_QUALITY" => "Image quality",
        "REJECT_OTHER" => "Other",
        "ESCALATE_CHILD_SAFETY" => "Child-safety concern",
        "ESCALATE_NON_CONSENSUAL" => "Possible non-consensual image",
        "ESCALATE_OTHER" => "Uncertain serious concern",
        "LEGACY_APPROVE" => "Approved (uncategorised)",
        "LEGACY_REJECT" => "Rejected (uncategorised)",
        "LEGACY_ESCALATE" => "Escalated (uncategorised)",
        _ => code,
    };

    public static string Decision(string decision) => decision switch
    {
        "approve" => "Approved",
        "reject" => "Rejected",
        "escalate" => "Escalated",
        "appeal-upheld" => "Appeal upheld",
        "appeal-approved" => "Approved on appeal",
        "reconsider-approved" => "Approved on reconsideration",
        "appeal-unavailable" => "Appeal unavailable",
        _ => decision,
    };

    /// <summary>Pill tone: approve (outlined dot), reject (solid), escalate, quiet.</summary>
    public static string Tone(string decision) => decision switch
    {
        "approve" or "appeal-approved" or "reconsider-approved" => "approve",
        "reject" or "appeal-upheld" => "reject",
        "escalate" => "escalate",
        _ => "quiet",
    };

    public static string Signal(string signal) => signal;

    public static string Kind(string? purpose) => purpose == "attachment" ? "Chat attachment" : "Profile photo";

    /// <summary>Issue 232: appeals against this Admin's own decision, which a different Admin decides (D298).</summary>
    public static string AwaitingOtherAdmin(int count) => count == 1 ? "1 appeal waiting for another Admin" : $"{count} appeals waiting for another Admin";
}
