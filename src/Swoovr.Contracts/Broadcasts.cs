namespace Swoovr.Contracts;

/// <summary>Issue 588 (D446): staff send a notification to every member. Confirm must be true: it can't be edited after.</summary>
public sealed record BroadcastRequest(string? Title, string? Body, bool Confirm);

public sealed record BroadcastView(Guid Id, string Title, string Body, int Recipients, DateTimeOffset SentAt, string? SentBy, DateTimeOffset? WithdrawnAt,
    string? WithdrawnBy, bool CanWithdraw);

/// <summary>What the member sees on the announcement's own page.</summary>
public sealed record AnnouncementView(Guid Id, string Title, string Body, DateTimeOffset SentAt);
