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
/// A full replacement of the notification settings; omitted switches keep their defaults. The two chat settings are the
/// exception: omitted (null) ChatPhotos or UnansweredLimit keeps the member's current choice (0 turns the limit Off; 3, 5
/// or 10 set it). Since #415 (D390) the web edits them on Privacy & safety, so the Notifications page omits them and can
/// never put back an older value.
/// </summary>
public sealed record NotificationSettingsUpdate(bool? Messages, bool? Requests, bool? Connections, bool? Travel, bool? Push, bool? Email, bool? PrivatePreviews,
    string? QuietFrom, string? QuietUntil, string? TimeZone, List<string>? MutedModes, bool? ChatPhotos, int? UnansweredLimit = null);

/// <summary>
/// Only the chat settings (#415, D390), saved from Privacy & safety: accept photos in chat and the unanswered-message
/// limit (0 Off, 3, 5 or 10). Omitted (null) keeps the current choice; nothing else in the notification settings changes.
/// </summary>
public sealed record ChatSettingsUpdate(bool? ChatPhotos, int? UnansweredLimit);

/// <summary>A device for push notifications: platform "ios" or "android" and the provider's token; optional installation id supports rotation without breaking older date clients.</summary>
public sealed record PushDeviceRegistration(string? Platform, string? Token, Guid? InstallationId = null);
public sealed record PushDeviceView(Guid Id, string Platform, DateTimeOffset CreatedAt, bool Simulated);
