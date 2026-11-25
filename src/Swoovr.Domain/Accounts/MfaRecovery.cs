namespace Swoovr.Domain;

/// <summary>Issue 374 (D373, D405): how an independent Admin verified a staff member before clearing their MFA. A video call alone is never enough.</summary>
public static class MfaRecovery
{
    public const string VideoTrustedChannel = "video-trusted-channel", VideoStaffRecords = "video-staff-records";
    public static readonly IReadOnlyList<string> Methods = [VideoTrustedChannel, VideoStaffRecords];
    public static bool Valid(string? method) => method is not null && Methods.Contains(method);
    public static string Detail(string caseReference, string? method) => method is null ? caseReference : caseReference + ";method:" + method;
    /// <summary>The method a reset's audit detail holds, or null (resets before this was recorded, and member resets).</summary>
    public static string? From(string? detail) => detail?.LastIndexOf(";method:", StringComparison.Ordinal) is int at and >= 0 && Valid(detail[(at + 8)..]) ? detail[(at + 8)..] : null;
}
