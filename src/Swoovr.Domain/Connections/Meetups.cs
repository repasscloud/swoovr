namespace Swoovr.Domain;

/// <summary>Issue 545 (D430): where a private 1-on-1 meet-up invitation stands.</summary>
public enum MeetupStatus { Pending, Accepted, Declined, Withdrawn, Superseded, Cancelled }

/// <summary>
/// Issue 545 (Q193, D430): a private 1-on-1 meet-up planned from a chat between two connected members. One row per
/// proposal: suggesting changes supersedes the proposal and sends a new one the other way. The plan is between the two
/// members only; Swoovr books nothing and starts no meeting-safety session by itself. Shown in the thread as the card of
/// the <see cref="Message"/> that carried it; the optional note is that message's body (moderated like any message).
/// </summary>
public sealed class Meetup
{
    public Guid Id { get; set; }
    public Guid ConversationId { get; set; }
    public Guid MessageId { get; set; }
    /// <summary>The mode the conversation started in (its categories).</summary>
    public string ModeId { get; set; } = "";
    public Guid ProposerAccountId { get; set; }
    public Guid RecipientAccountId { get; set; }
    public string Category { get; set; } = "";
    /// <summary>Free text (D430: a lookup of admin and partner venues comes once venue records exist, #42).</summary>
    public string Venue { get; set; } = "";
    public DateTimeOffset StartsAt { get; set; }
    public int? DurationMinutes { get; set; }
    public MeetupStatus Status { get; set; }
    /// <summary>The proposal this one replaced (suggest changes).</summary>
    public Guid? SupersedesId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? AnsweredAt { get; set; }
    public Guid? AnsweredBy { get; set; }
}

public enum MeetupRefusal { None, InvalidCategory, InvalidVenue, InvalidTime, InvalidDuration, InvalidNote }

/// <summary>Issue 545 (D430): the rules for planning and answering a private meet-up. Checked again on every request.</summary>
public static class MeetupPolicy
{
    public const int VenueMax = 120, NoteMax = 280, MaxDaysAhead = 90;
    public static readonly TimeSpan MinLead = TimeSpan.FromMinutes(15);
    public static readonly int[] Durations = [30, 60, 90, 120, 180, 240];

    /// <summary>D430 Modes (A): each mode has its own categories; "other" is always offered.</summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<(string Id, string Label)>> Categories = new Dictionary<string, IReadOnlyList<(string, string)>>
    {
        [ConnectionModeIds.Dating] = [("coffee", "Coffee"), ("drinks", "Drinks"), ("dinner", "Dinner"), ("lunch", "Lunch"), ("walk", "Walk"), ("movie", "Movie"), ("live-music", "Live music"), ("other", "Something else")],
        [ConnectionModeIds.Friends] = [("coffee", "Coffee"), ("lunch", "Lunch"), ("drinks", "Drinks"), ("walk", "Walk"), ("movie", "Movie"), ("games", "Games"), ("sport", "Sport"), ("other", "Something else")],
        [ConnectionModeIds.Activities] = [("walk", "Walk"), ("hike", "Hike"), ("run", "Run"), ("gym", "Gym"), ("sport", "Sport"), ("class", "Class"), ("live-music", "Live music"), ("other", "Something else")],
        [ConnectionModeIds.Business] = [("coffee", "Coffee"), ("lunch", "Lunch"), ("meeting", "Meeting"), ("networking", "Networking event"), ("other", "Something else")],
    };

    public static IReadOnlyList<(string Id, string Label)> For(string? mode) =>
        mode is not null && Categories.TryGetValue(mode, out var list) ? list : [("coffee", "Coffee"), ("lunch", "Lunch"), ("walk", "Walk"), ("other", "Something else")];

    public static string? Label(string? mode, string category) => For(mode).FirstOrDefault(c => c.Id == category).Label;

    /// <summary>A proposal's details: a category of the chat's mode, a venue or area, a time from 15 minutes to 90 days ahead,
    /// an optional duration from the list and an optional note.</summary>
    public static MeetupRefusal Check(string? mode, string? category, string? venue, DateTimeOffset startsAt, int? duration, string? note, DateTimeOffset now)
    {
        if (category is null || Label(mode, category) is null) return MeetupRefusal.InvalidCategory;
        if (Clean(venue) is not { } place || place.Length > VenueMax) return MeetupRefusal.InvalidVenue;
        if (startsAt < now + MinLead || startsAt > now.AddDays(MaxDaysAhead)) return MeetupRefusal.InvalidTime;
        if (duration is { } minutes && !Durations.Contains(minutes)) return MeetupRefusal.InvalidDuration;
        if (note is not null && (Clean(note) is { Length: > NoteMax } || note.Any(c => char.IsControl(c) && c is not ('\n' or '\t')))) return MeetupRefusal.InvalidNote;
        return MeetupRefusal.None;
    }

    /// <summary>Trimmed, single-spaced, no control characters; null when blank.</summary>
    public static string? Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(c => char.IsControl(c) && c is not ('\n' or '\t'))) return null;
        return string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    // Who may do what. Every answer also needs the conversation open (the API checks that).
    public static bool MayAccept(Meetup m, Guid me, DateTimeOffset now) => m.Status == MeetupStatus.Pending && m.RecipientAccountId == me && m.StartsAt > now;
    public static bool MayDecline(Meetup m, Guid me) => m.Status == MeetupStatus.Pending && m.RecipientAccountId == me;
    public static bool MaySuggest(Meetup m, Guid me, DateTimeOffset now) => m.Status == MeetupStatus.Pending && m.RecipientAccountId == me && m.StartsAt > now;
    public static bool MayWithdraw(Meetup m, Guid me) => m.Status == MeetupStatus.Pending && m.ProposerAccountId == me;
    public static bool MayCancel(Meetup m, Guid me, DateTimeOffset now) => m.Status == MeetupStatus.Accepted && (m.ProposerAccountId == me || m.RecipientAccountId == me) && m.StartsAt > now;

    /// <summary>What the card says: a pending invitation whose time has passed reads as expired; an accepted one as past.</summary>
    public static string State(Meetup m, DateTimeOffset now) => m.Status switch
    {
        MeetupStatus.Pending when m.StartsAt <= now => "expired",
        MeetupStatus.Accepted when m.StartsAt <= now => "past",
        var s => s.ToString().ToLowerInvariant(),
    };
}
