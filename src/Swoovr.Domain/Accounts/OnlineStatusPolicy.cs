namespace Swoovr.Domain;

/// <summary>
/// Issue 499 (D410): online status is opt-in and mutual. Only two members who both turned it on see each other, and only
/// "active in the last five minutes": never a time. Off, nobody sees theirs and they see nobody's. Never for official bots.
/// </summary>
public static class OnlineStatusPolicy
{
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(5);
    /// <summary>Activity is written at most this often, and only while the member has it on.</summary>
    public static readonly TimeSpan WriteEvery = TimeSpan.FromMinutes(1);

    public static bool Shows(bool available, MemberProfile viewer, MemberProfile person, DateTimeOffset now) =>
        available && viewer.ShowOnline && person.ShowOnline && !person.Bot && viewer.AccountId != person.AccountId
        && person.ActiveAt is { } at && at > now - Window;

    /// <summary>
    /// Requests an open page makes on its own (the unread count every 30 seconds, meeting safety every 15) aren't the member
    /// doing anything, so a tab left open doesn't keep them online. Any other automatic refresh says so with the background
    /// header (PR 526 review: a support ticket refreshing every 5 seconds while a file is checked), so opening it still counts.
    /// </summary>
    public static bool Counts(string method, string path, bool background = false) => !background && !(method == "GET"
        && (path.EndsWith("/me/notifications", StringComparison.Ordinal)
            || path.EndsWith("/me/meeting-safety", StringComparison.Ordinal) || path.EndsWith("/session", StringComparison.Ordinal)));
}
