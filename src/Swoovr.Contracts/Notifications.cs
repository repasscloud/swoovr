namespace Swoovr.Contracts;

/// <summary>
/// One item in the notification centre (#49), worded for the signed-in member: Title names the person and mode where it
/// helps (the member is signed in; lock screens and emails never do by default). Link is the member page it opens.
/// </summary>
public sealed record NotificationView(Guid Id, string Kind, string Title, string Link, int Count, DateTimeOffset At, bool Read);

public sealed record NotificationCentre(IReadOnlyList<NotificationView> Items, int Unread);

/// <summary>
/// The member's notification settings (#49): kinds on or off, push and email, private previews (on by default), quiet
/// hours as local "HH:mm" times in TimeZone (both or neither), modes muted, and whether photos in chat are accepted.
/// UnansweredLimit (#300, D380): the most consecutive unanswered messages someone may send this member, 3, 5 or 10, or 0
/// for Off (the default).
/// </summary>
public sealed record NotificationSettingsView(bool Messages, bool Requests, bool Connections, bool Travel, bool Push, bool Email, bool PrivatePreviews,
    string? QuietFrom, string? QuietUntil, string TimeZone, IReadOnlyList<string> MutedModes, bool ChatPhotos, int Devices, int UnansweredLimit = 0);

/// <summary>
/// A full replacement of the settings; omitted switches keep their defaults. UnansweredLimit is the exception: 0 turns
/// it Off, 3, 5 or 10 set it, and omitted (null) keeps the member's current choice, so a client that predates #300
/// never switches someone's limit off by saving another setting.
/// </summary>
public sealed record NotificationSettingsUpdate(bool? Messages, bool? Requests, bool? Connections, bool? Travel, bool? Push, bool? Email, bool? PrivatePreviews,
    string? QuietFrom, string? QuietUntil, string? TimeZone, List<string>? MutedModes, bool? ChatPhotos, int? UnansweredLimit = null);

/// <summary>A device for push notifications: platform "ios" or "android" and the provider's token; optional installation id supports rotation without breaking older date clients.</summary>
public sealed record PushDeviceRegistration(string? Platform, string? Token, Guid? InstallationId = null);
public sealed record PushDeviceView(Guid Id, string Platform, DateTimeOffset CreatedAt, bool Simulated);
