using Swoovr.Contracts;

namespace Swoovr.Admin;

/// <summary>Readable staff labels for account wire values (#156); data attributes keep the raw values.</summary>
public static class AccountLabels
{
    /// <summary>A staff account's display name: first and last name, else the email (SSO and break-glass accounts may
    /// have no registered name), so every staff row stays openable.</summary>
    public static string StaffName(string? first, string? last, string? email) =>
        string.Join(' ', new[] { first, last }.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!.Trim())) is { Length: > 0 } name
            ? name : string.IsNullOrWhiteSpace(email) ? "Unnamed staff account" : email.Trim();
    /// <summary>Up to two initials for a staff avatar, from the name or else the email's local part (#347).</summary>
    public static string StaffInitials(string? first, string? last, string? email)
    {
        var parts = new[] { first, last }.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!.Trim()).ToList();
        if (parts.Count == 0 && !string.IsNullOrWhiteSpace(email)) parts = email.Trim().Split('@')[0].Split('.', '-', '_').Where(p => p.Length > 0).ToList();
        var letters = string.Concat(parts.Take(2).Select(p => char.ToUpperInvariant(p[0])));
        return letters.Length > 0 ? letters : "?";
    }
    /// <summary>Readable staff audit actions; the raw action stays in a data attribute (#347).</summary>
    public static readonly string[] RecoveryMethods = ["video-trusted-channel", "video-staff-records"];
    /// <summary>Issue 374: how a staff member's identity was verified before an MFA reset (D373).</summary>
    public static string RecoveryMethod(string method) => method switch
    {
        "video-trusted-channel" => "Live video call + trusted channel", "video-staff-records" => "Live video call + staff records", _ => method,
    };
    public static string StaffAction(string action) => action switch
    {
        "staff.created" => "Account created",
        "staff.provider-activated" => "Sign-in enabled",
        "staff.provider-activation-unconfirmed" => "Sign-in activation not confirmed",
        "staff.update-requested" => "Change requested",
        "staff.updated" => "Details and access updated",
        "staff.provider-unconfirmed" => "Change not confirmed by sign-in service",
        "staff.security-requested" => "Security action requested",
        "staff.security-completed" => "Security action completed",
        "staff.security-unconfirmed" => "Security action not confirmed",
        "staff.roles-synced" => "Roles updated from Microsoft Entra",
        "staff.revoked" => "Access revoked",
        "staff.affiliated" => "Member link updated",
        "mfa.reset-requested" => "MFA reset requested",
        "mfa.credentials-cleared" => "MFA reset finished",
        _ => Title(action.StartsWith("staff.", StringComparison.Ordinal) ? action[6..].Replace('-', ' ') : action),
    };
    /// <summary>Where a staff session signed in (#347): Admin is the staff realm's web client.</summary>
    public static string StaffDevice(string device) => device switch { "web" => "Admin portal", "app" => "Mobile app", _ => "Other Swoovr service" };
    /// <summary>One consistent staff timestamp: "17 Nov 2026, 01:46 UTC".</summary>
    public static string When(DateTimeOffset at) => at.ToUniversalTime().ToString("d MMM yyyy, HH:mm", System.Globalization.CultureInfo.InvariantCulture) + " UTC";
    public static string When(DateTimeOffset? at, string none) => at is { } value ? When(value) : none;
    public static string Status(string? status) => status switch
    {
        "active" => "Active", "suspended" => "Suspended", "restricted" => "Restricted", "terminated" => "Terminated",
        "pendingDeletion" => "Deletion pending", "deleted" => "Deleted", "disconnected" => "Disconnected", "locked" => "Locked", null => "Unknown", _ => Title(status),
    };
    public static string Age(string? status) => status switch
    {
        "unconfirmed" => "Not confirmed", "attested" => "18+ confirmed at sign-up", "selfDeclaredAdult" => "Self-declared adult",
        "underage" => "Under 18 (locked)", "needsVerification" => "Age check required", "verified" => "Verified by ID", "adultOverride" => "Adult override by staff",
        null => "Not recorded", _ => Title(status),
    };
    public static string Id(string? status) => status switch
    {
        "notStarted" => "ID not verified", "pending" => "ID check pending", "inReview" => "ID check in review", "verified" => "ID verified",
        "declined" => "ID check declined", "rejected" => "ID check rejected", "revoked" => "ID verified revoked", "expired" => "ID check expired",
        null => "ID not verified", _ => "ID " + Title(status).ToLowerInvariant(),
    };
    public static string Plan(string? plan) => plan switch { "free" => "Free", "plus" => "Plus", "premium" => "Premium", null => "Free", _ => Title(plan) };
    public static string Realm(string realm) => realm switch { "members" => "Member", "venues" => "Venue partner", "staff" => "Staff", _ => Title(realm) };
    public static string Source(string source) => source switch { "adminGrant" => "Staff grant", "founder" => "Founder", "subscription" => "Paid subscription", _ => Title(source) };
    public static string Attempt(string state) => state switch
    {
        "created" => "Created", "inProgress" => "In progress", "inReview" => "In review", "approved" => "Approved", "declined" => "Declined",
        "abandoned" => "Abandoned", "expired" => "Expired", _ => Title(state),
    };
    public static string Sanction(string kind) => kind switch { "warning" => "Warning", "restriction" => "Restricted", "suspension" => "Suspended", "termination" => "Terminated", _ => Title(kind) };
    public static string Reason(string? code) => AccountReasons.Label(code);
    public static string Role(string? role) => role switch { "business" => "Business", "admin" => "Admin", "trustSafety" => "Trust & Safety", "support" => "Support", "legal" => "Legal", null => "", _ => Title(role) };
    private static string Title(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        var spaced = string.Concat(value.Select((c, i) => i > 0 && char.IsUpper(c) ? " " + char.ToLowerInvariant(c) : c.ToString()));
        return char.ToUpperInvariant(spaced[0]) + spaced[1..];
    }
}
