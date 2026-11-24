namespace Swoovr.Domain;

/// <summary>Issue 498 (D409): how long someone counts as a new member. It ends on its own and never affects order or eligibility.</summary>
public static class NewMemberPolicy
{
    public const int DefaultDays = 14, MaxDays = 90;
    /// <summary>Accounts created after this are new; null turns the indicator off (0 or less). Never longer than 90 days.</summary>
    public static DateTimeOffset? From(int days, DateTimeOffset now) => days <= 0 ? null : now.AddDays(-Math.Min(days, MaxDays));
}
