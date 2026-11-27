namespace Swoovr.Contracts;

/// <summary>
/// A conversation as one member sees it (D143): the other person, when it last moved, and whether the member may write
/// now (CanSend; the API checks again on every send). No read receipts, presence or typing (Q42 rules 6–7).
/// AwaitingReply (#300, D380; set when one conversation is opened): the member reached the other person's
/// unanswered-message limit and can write again once they reply. It never says what the limit is.
/// Issue 541: Unread counts the other person's delivered messages since this member last opened the conversation (their own
/// state only; never shown to the sender). ModeId is the mode the conversation started in (OriginModeId), so the list can
/// name it truthfully; null for conversations from before modes were recorded.
/// </summary>
public sealed record ConversationView(Guid Id, PersonSummary Person, DateTimeOffset CreatedAt, DateTimeOffset? LastMessageAt, bool CanSend, bool Closed = false,
    bool AwaitingReply = false, int Unread = 0, string? ModeId = null);

/// <summary>
/// A message as one member sees it. Kind removed-by-moderation carries neutral text and cannot be unsent; it is distinct from member deletion. CanDelete: theirs and within five minutes (delete for both, rule 8). Deleted: shown
/// as deleted, with no text (Q59, D167); the text stays on the server for 30 days for investigations. A photo message
/// (#41) carries Photo (a short-lived link) once it passed screening; the sender also sees PhotoState: checking,
/// in-review, approved or not-approved (a blocked photo reads as not-approved). The recipient never sees a photo message before it passed.
/// </summary>
public sealed record MessageView(Guid Id, Guid ConversationId, bool Mine, string Kind, string Text, DateTimeOffset SentAt, bool CanDelete, bool Deleted = false,
    PhotoView? Photo = null, string? PhotoState = null);

/// <summary>Newest first; Before is the cursor for the next page (the oldest message's time).</summary>
/// <param name="FirstUnread">PR 579 review: on the newest page only, the first message that was unread before this read
/// (the unread count's own rule), which may be on an earlier page; null when nothing was unread.</param>
public sealed record MessagePage(IReadOnlyList<MessageView> Items, bool More, DateTimeOffset? Before, Guid? FirstUnread = null);

/// <summary>Opens (or returns) the conversation with a connected member, by their public profile id.</summary>
public sealed record StartConversation(Guid To, string? Mode = null);

public sealed record NewMessage(string? Text);

/// <summary>#41, Q85: ends the conversation; Closing is "thanks", "not-a-match", "take-care" or null for none.</summary>
public sealed record EndConversation(string? Closing);

/// <summary>#41: a suggested first line from an intention chip both members picked in a mode they share. Fills the box; never sent by itself.</summary>
public sealed record ConversationStarter(string Chip, string Text);

/// <summary>
/// What the messaging hub pushes (SignalR, D143): "message" with a MessageView, "deleted" with the conversation and
/// message ids, "closed" with the conversation id when a block closes it, "released" (#300, D380) with the conversation
/// id when the other member loosened their unanswered-message limit enough for this member to write again (only to a
/// member it frees; clients read the conversation again). Clients send through the REST routes.
/// </summary>
public sealed record MessageDeleted(Guid ConversationId, Guid MessageId);
public sealed record ConversationClosed(Guid ConversationId);
public sealed record ConversationReleased(Guid ConversationId);
