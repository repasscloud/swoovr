namespace Swoovr.Domain;

/// <summary>
/// Issue 572 (D448): a member privately saves another member. Only the member who saved it ever sees it: the other member is
/// never told and no count is shown anywhere. A block either way removes it, and it drops out of the list while the profile
/// isn't open to the member who saved it.
/// </summary>
public sealed class MemberFavourite
{
    public Guid AccountId { get; set; }
    public Guid TargetAccountId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public static class FavouritePolicy
{
    /// <summary>DEFAULT: a member can keep up to 500; the list shows the latest 100 that are open to them.</summary>
    public const int Max = 500, Listed = 100;
}
