namespace Swoovr.Admin;

/// <summary>One-line role summaries for the staff role picker (#347), from D322 (docs/product/04-IDENTITY-TRUST-AND-SAFETY.md).</summary>
public static class StaffRoleText
{
    public static string Describe(string role) => role switch
    {
        "support" => "Routine member services and minor enforcement, including restrictions up to 30 days.",
        "trustSafety" => "Support, plus specialist investigations, protective interventions and enforcement up to 90 days.",
        "admin" => "Support, plus platform administration, staff management, permanent terminations and appeals.",
        "legal" => "Legal compliance, evidence preservation, law enforcement requests, disclosures and legal holds. Only the root admin grants it.",
        "business" => "Business workspace only. On its own it opens no member, moderation or staff tools in Admin.",
        _ => "",
    };
}
