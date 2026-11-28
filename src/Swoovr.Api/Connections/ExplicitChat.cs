using Microsoft.EntityFrameworkCore;
using Swoovr.Api.Accounts;
using Swoovr.Domain;
using Swoovr.Infrastructure.Persistence;

namespace Swoovr.Api.Connections;

/// <summary>
/// D423 and D428 (#546): whether an explicit chat photo from <c>sender</c> may reach <c>recipient</c> now. Both ID
/// Verified, and the recipient's Receive explicit media on (effective) for at least one mode the pair is connected in.
/// The same answer for delivery and for every view, so an opt-out, a lapse or a disconnection stops it loading (182.4).
/// </summary>
public static class ExplicitChat
{
    public static async Task<bool> AllowedAsync(SwoovrDbContext db, Guid sender, Guid recipient, CancellationToken cancellationToken)
    {
        var verified = await db.MemberProfiles.AsNoTracking().Where(p => p.AccountId == sender || p.AccountId == recipient)
            .Select(p => new { p.AccountId, Verified = p.IdStatus == VerificationStatus.Verified }).ToListAsync(cancellationToken);
        bool Verified(Guid id) => verified.Any(v => v.AccountId == id && v.Verified);
        if (!Verified(sender) || !Verified(recipient)) return false;
        var (low, high) = ConnectionRequest.Pair(sender, recipient);
        var receives = await db.ExplicitMediaPreferences.AnyAsync(p => p.AccountId == recipient && p.Receive
            && db.ConnectionRequests.Any(r => r.PairLow == low && r.PairHigh == high && r.Status == ConnectionRequestStatus.Accepted && r.EndedAt == null && r.ModeId == p.ModeId), cancellationToken);
        return ExplicitChatPolicy.Allowed(true, true, receives);
    }

    /// <summary>
    /// PR 606/607 review: decided in the same save that publishes the photo (MediaPipeline.PassCoreAsync), so an explicit
    /// photo the recipient can't receive never becomes visible to them, even for a moment, and a chat that closed or a
    /// block that came first counts as "can't receive".
    /// </summary>
    public static async Task MarkUndeliverableAsync(SwoovrDbContext db, MediaItem item, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (item.ContentRating != ContentRating.Explicit || item.Purpose != AttachmentPolicy.Purpose) return;
        var message = await db.Messages.SingleOrDefaultAsync(m => m.MediaId == item.Id, cancellationToken);
        if (message is null || message.ExplicitRefusedAt is not null || message.DeletedAt is not null) return;
        var conversation = await MessagingEndpoints.OpenAsync(db, message.ConversationId, message.SenderAccountId, cancellationToken);
        var recipient = conversation is null ? Guid.Empty : conversation.PairLow == message.SenderAccountId ? conversation.PairHigh : conversation.PairLow;
        if (conversation is not null && await AllowedAsync(db, message.SenderAccountId, recipient, cancellationToken)) return;
        message.ExplicitRefusedAt = now;
        Audit.Record(db, now, null, "system", "message.explicit-refused", "message", message.Id.ToString());
    }

    /// <summary>
    /// PR 606/607 review: the recipient's link to an explicit photo (variant "explicit") is checked again every time the
    /// image is served, not only when the link was made: refused or deleted message, closed chat, block, opt-out or ID lapse
    /// and it no longer loads.
    /// </summary>
    public static async Task<bool> ServableAsync(SwoovrDbContext db, MediaItem item, CancellationToken cancellationToken)
    {
        if (item is not { Purpose: AttachmentPolicy.Purpose, ContentRating: ContentRating.Explicit, Status: MediaStatus.Passed, RemovedAt: null }) return false;
        var message = await db.Messages.AsNoTracking().SingleOrDefaultAsync(m => m.MediaId == item.Id, cancellationToken);
        if (message is null || message.ExplicitRefusedAt is not null || message.DeletedAt is not null || message.ModerationRemovedAt is not null) return false;
        var conversation = await MessagingEndpoints.OpenAsync(db, message.ConversationId, message.SenderAccountId, cancellationToken);
        if (conversation is null) return false;
        var recipient = conversation.PairLow == message.SenderAccountId ? conversation.PairHigh : conversation.PairLow;
        return await AllowedAsync(db, message.SenderAccountId, recipient, cancellationToken);
    }
}
