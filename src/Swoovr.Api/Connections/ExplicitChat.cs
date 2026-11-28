using Microsoft.EntityFrameworkCore;
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
}
