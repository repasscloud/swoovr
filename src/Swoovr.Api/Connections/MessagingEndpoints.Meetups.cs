using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Swoovr.Api.Accounts;
using Swoovr.Contracts;
using Swoovr.Domain;
using Swoovr.Infrastructure.Persistence;
using static Swoovr.Api.Accounts.AccountEndpoints;

namespace Swoovr.Api.Connections;

/// <summary>
/// Issue 545 (Q193, D430): private 1-on-1 meet-ups planned from a chat. A proposal travels as a "meetup" message, so it
/// gets every send rule of the chat (connected, open, rate and unanswered limits, restrictions), live delivery, the
/// notification, unread counts and the message report flow; its state lives on the <see cref="Meetup"/> row and every
/// change re-sends the card to both members. Swoovr books nothing and never starts meeting safety by itself.
/// </summary>
public static partial class MessagingEndpoints
{
    public static void MapMeetups(WebApplication app)
    {
        var api = app.MapGroup("/api").RequireAuthorization();
        api.MapGet("/meetups/categories", (string? mode) => Results.Ok(MeetupPolicy.For(mode).Select(c => new MeetupCategoryView(c.Id, c.Label)).ToList()));
        api.MapPost("/conversations/{id:guid}/meetups", (Guid id, MeetupRequest request, ClaimsPrincipal user, AccountResolver accounts, TimeProvider clock, CancellationToken cancellationToken) =>
            ProposeAsync(id, null, request, user, accounts, clock, cancellationToken));
        // The invited member proposes something else: the invitation is superseded and the new one goes the other way.
        api.MapPost("/meetups/{id:guid}/suggest", async (Guid id, MeetupRequest request, ClaimsPrincipal user, AccountResolver accounts, TimeProvider clock, CancellationToken cancellationToken) =>
        {
            var (current, denied) = await MemberAsync(user, accounts, cancellationToken);
            if (denied is not null) return denied;
            var me = current!.Account!.Id;
            var meetup = await accounts.Db!.Meetups.AsNoTracking().SingleOrDefaultAsync(m => m.Id == id && (m.ProposerAccountId == me || m.RecipientAccountId == me), cancellationToken);
            return meetup is null ? NotAvailable() : await ProposeAsync(meetup.ConversationId, meetup.Id, request, user, accounts, clock, cancellationToken);
        });
        api.MapPost("/meetups/{id:guid}/{action:regex(^(accept|decline|withdraw|cancel)$)}", AnswerAsync);
        api.MapGet("/me/meetups", ListAsync);
    }

    private static async Task<IResult> ProposeAsync(Guid id, Guid? supersedes, MeetupRequest request, ClaimsPrincipal user, AccountResolver accounts, TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var (current, denied) = await MemberAsync(user, accounts, cancellationToken);
        if (denied is not null) return denied;
        if (current!.Enforcement.NoMessages) return Problem(StatusCodes.Status403Forbidden, ProblemCodes.Restricted, "You can't send messages right now.");
        var (db, me, now) = (accounts.Db!, current.Account!.Id, clock.GetUtcNow());
        await using var lifecycle = await AccountLifecycleLease.AcquireAsync(db, me, cancellationToken);
        if (!await db.Accounts.AsNoTracking().AnyAsync(a => a.Id == me && a.Status == AccountStatus.Active, cancellationToken)) return NotAvailable();
        await using var barrier = await Retention.PreservationLease.SharedAsync(db, cancellationToken);
        await using var transaction = await LockAsync(db, id, me, cancellationToken);
        now = clock.GetUtcNow();
        var conversation = await OpenAsync(db, id, me, cancellationToken);
        if (conversation is null) return NotAvailable();
        var other = conversation.PairLow == me ? conversation.PairHigh : conversation.PairLow;
        switch (MessagingPolicy.CanSend(await SendFactsAsync(db, current.Profile!.Bot, me, other, id, now, cancellationToken), "Meet-up"))
        {
            case SendRefusal.None: break;
            case SendRefusal.AwaitingReply: return AwaitingReply();
            case SendRefusal.ArrivalRequired: return Problem(StatusCodes.Status403Forbidden, ProblemCodes.ArrivalRequired, "You can reply, or write first once you've confirmed you've arrived.");
            case SendRefusal.RateLimited: return Problem(StatusCodes.Status429TooManyRequests, ProblemCodes.TooManyRequests, "That's a lot of messages. Slow down a little.");
            default: return NotAvailable();
        }
        Meetup? previous = null;
        if (supersedes is { } replaced)
        {
            previous = await db.Meetups.SingleOrDefaultAsync(m => m.Id == replaced && m.ConversationId == id, cancellationToken);
            if (previous is null || !MeetupPolicy.MaySuggest(previous, me, now)) return Problem(StatusCodes.Status409Conflict, ProblemCodes.MeetupNotOpen, "This invitation can't be changed any more.");
        }
        // One invitation waits at a time in a chat; the other member answers it (or suggests changes) first.
        else if (await db.Meetups.AnyAsync(m => m.ConversationId == id && m.Status == MeetupStatus.Pending && m.StartsAt > now, cancellationToken))
            return Problem(StatusCodes.Status409Conflict, ProblemCodes.MeetupPending, "There's already an invitation waiting in this chat.");
        var mode = conversation.OriginModeId;
        switch (MeetupPolicy.Check(mode, request.Category, request.Venue, request.StartsAt, request.DurationMinutes, request.Note, now))
        {
            case MeetupRefusal.None: break;
            case MeetupRefusal.InvalidTime: return Problem(StatusCodes.Status400BadRequest, ProblemCodes.InvalidMeetup, "Choose a time from 15 minutes to 90 days from now.");
            case MeetupRefusal.InvalidVenue: return Problem(StatusCodes.Status400BadRequest, ProblemCodes.InvalidMeetup, $"Say where, in up to {MeetupPolicy.VenueMax} characters.");
            case MeetupRefusal.InvalidNote: return Problem(StatusCodes.Status400BadRequest, ProblemCodes.InvalidMeetup, $"Keep the message to {MeetupPolicy.NoteMax} characters.");
            default: return Problem(StatusCodes.Status400BadRequest, ProblemCodes.InvalidMeetup, "Choose what you'd like to do.");
        }
        var note = MeetupPolicy.Clean(request.Note);
        var message = new Message { Id = Guid.NewGuid(), ConversationId = id, SenderAccountId = me, Kind = MessageKind.Meetup, Body = note ?? "", SentAt = now };
        var meetup = new Meetup
        {
            Id = Guid.NewGuid(), ConversationId = id, MessageId = message.Id, ModeId = mode ?? "", ProposerAccountId = me, RecipientAccountId = other,
            Category = request.Category!, Venue = MeetupPolicy.Clean(request.Venue)!, StartsAt = request.StartsAt.ToUniversalTime(), DurationMinutes = request.DurationMinutes,
            Status = MeetupStatus.Pending, SupersedesId = previous?.Id, CreatedAt = now,
        };
        message.MeetupId = meetup.Id;
        if (note is not null) accounts.Services.GetRequiredService<Moderation.TextHarmQueue>().Enqueue(me, TextHarmSource.Message, message.Id, null, note, now);
        if (previous is not null) (previous.Status, previous.AnsweredAt, previous.AnsweredBy) = (MeetupStatus.Superseded, now, me);
        db.Messages.Add(message);
        db.Meetups.Add(meetup);
        conversation.LastMessageAt = now;
        // The audit names the action only, never the place, time or note.
        Audit.Record(db, now, me, "account", previous is null ? "meetup.proposed" : "meetup.suggested", "meetup", meetup.Id.ToString());
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await barrier.DisposeAsync();
        await lifecycle.DisposeAsync();
        await MemberMutationAuthority.ReleaseForEffectsAsync(accounts.Services);
        if (previous is not null) await SendCardAsync(accounts.Services, db, previous, conversation, now, cancellationToken);
        await SendCardAsync(accounts.Services, db, meetup, conversation, now, cancellationToken);
        await accounts.Services.GetRequiredService<Notifications.Notifier>().NotifyAsync(other, NotificationKind.Message, me, null, id, now, cancellationToken,
            previous is null ? "Meet-up invitation" : "Suggested changes to a meet-up");
        return Results.Created($"/api/messages/{message.Id}", View(message, me, now, meetup: Card(meetup, message, me, now, true)));
    }

    private static async Task<IResult> AnswerAsync(Guid id, string action, ClaimsPrincipal user, AccountResolver accounts, TimeProvider clock, CancellationToken cancellationToken)
    {
        var (current, denied) = await MemberAsync(user, accounts, cancellationToken);
        if (denied is not null) return denied;
        var (db, me, now) = (accounts.Db!, current!.Account!.Id, clock.GetUtcNow());
        var found = await db.Meetups.AsNoTracking().SingleOrDefaultAsync(m => m.Id == id && (m.ProposerAccountId == me || m.RecipientAccountId == me), cancellationToken);
        if (found is null) return NotAvailable();
        await using var lifecycle = await AccountLifecycleLease.AcquireAsync(db, me, cancellationToken);
        await using var barrier = await Retention.PreservationLease.SharedAsync(db, cancellationToken);
        await using var transaction = await LockAsync(db, found.ConversationId, me, cancellationToken);
        now = clock.GetUtcNow();
        // Answers need the chat open: a block or disconnect ends every plan in it.
        var conversation = await OpenAsync(db, found.ConversationId, me, cancellationToken);
        if (conversation is null) return NotAvailable();
        var meetup = await db.Meetups.SingleAsync(m => m.Id == id, cancellationToken);
        var (allowed, next) = action switch
        {
            "accept" => (MeetupPolicy.MayAccept(meetup, me, now), MeetupStatus.Accepted),
            "decline" => (MeetupPolicy.MayDecline(meetup, me), MeetupStatus.Declined),
            "withdraw" => (MeetupPolicy.MayWithdraw(meetup, me), MeetupStatus.Withdrawn),
            _ => (MeetupPolicy.MayCancel(meetup, me, now), MeetupStatus.Cancelled),
        };
        if (!allowed) return Problem(StatusCodes.Status409Conflict, ProblemCodes.MeetupNotOpen, "This invitation can't be changed any more.");
        (meetup.Status, meetup.AnsweredAt, meetup.AnsweredBy) = (next, now, me);
        Audit.Record(db, now, me, "account", "meetup." + action, "meetup", meetup.Id.ToString());
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await barrier.DisposeAsync();
        await lifecycle.DisposeAsync();
        await MemberMutationAuthority.ReleaseForEffectsAsync(accounts.Services);
        await SendCardAsync(accounts.Services, db, meetup, conversation, now, cancellationToken);
        var other = meetup.ProposerAccountId == me ? meetup.RecipientAccountId : meetup.ProposerAccountId;
        await accounts.Services.GetRequiredService<Notifications.Notifier>().NotifyAsync(other, NotificationKind.Message, me, null, conversation.Id, now, cancellationToken,
            action switch { "accept" => "Meet-up accepted", "decline" => "Meet-up declined", "withdraw" => "Meet-up invitation withdrawn", _ => "Meet-up cancelled" });
        var message = await db.Messages.AsNoTracking().SingleAsync(m => m.Id == meetup.MessageId, cancellationToken);
        return Results.Ok(View(message, me, now, meetup: Card(meetup, message, me, now, true)));
    }

    /// <summary>Issue 545: "My meet-ups": every plan in a chat the member still lists; never another's, never a blocked pair's.</summary>
    private static async Task<IResult> ListAsync(ClaimsPrincipal user, AccountResolver accounts, TimeProvider clock, CancellationToken cancellationToken)
    {
        var (current, denied) = await MemberAsync(user, accounts, cancellationToken);
        if (denied is not null) return denied;
        var (db, me, now) = (accounts.Db!, current!.Account!.Id, clock.GetUtcNow());
        var all = from m in db.Meetups
                  join c in Listed(db, me) on m.ConversationId equals c.Id
                  join msg in db.Messages on m.MessageId equals msg.Id
                  where (m.ProposerAccountId == me || m.RecipientAccountId == me) && m.Status != MeetupStatus.Superseded
                  select new { m, msg, Open = c.ClosedAt == null };
        // PR 583 review: upcoming and waiting plans are fetched apart from the history (latest first), so old plans never
        // crowd current ones out. Each part is bounded on its own.
        var upcoming = await all.Where(r => (r.m.Status == MeetupStatus.Pending || r.m.Status == MeetupStatus.Accepted) && r.m.StartsAt > now)
            .OrderBy(r => r.m.StartsAt).Take(200).ToListAsync(cancellationToken);
        var history = await all.Where(r => !((r.m.Status == MeetupStatus.Pending || r.m.Status == MeetupStatus.Accepted) && r.m.StartsAt > now))
            .OrderByDescending(r => r.m.StartsAt).Take(100).ToListAsync(cancellationToken);
        var rows = upcoming.Concat(history.OrderBy(r => r.m.StartsAt)).ToList();
        var others = rows.Select(r => r.m.ProposerAccountId == me ? r.m.RecipientAccountId : r.m.ProposerAccountId).Distinct().ToList();
        var names = await db.MemberProfiles.Where(p => others.Contains(p.AccountId)).ToDictionaryAsync(p => p.AccountId, p => p.DisplayName, cancellationToken);
        var aliases = await db.MemberModes.Where(m => others.Contains(m.AccountId)).Select(m => new { m.AccountId, m.ModeId, m.DisplayName }).ToListAsync(cancellationToken);
        return Results.Ok(rows.Select(r =>
        {
            var other = r.m.ProposerAccountId == me ? r.m.RecipientAccountId : r.m.ProposerAccountId;
            var name = ModePrivacy.DisplayName(aliases.FirstOrDefault(a => a.AccountId == other && a.ModeId == r.m.ModeId)?.DisplayName, names.GetValueOrDefault(other)) ?? "Deleted member";
            return Card(r.m, r.msg, me, now, r.Open) with { WithName = name };
        }).ToList());
    }

    /// <summary>The cards for the meet-up messages on one page of a thread.</summary>
    private static async Task<Dictionary<Guid, MeetupView>> MeetupsForAsync(SwoovrDbContext db, Conversation conversation, IReadOnlyList<Message> page, Guid me,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        var ids = page.Where(m => m.MeetupId is not null).Select(m => m.MeetupId!.Value).ToList();
        if (ids.Count == 0) return [];
        var meetups = await db.Meetups.AsNoTracking().Where(m => ids.Contains(m.Id)).ToDictionaryAsync(m => m.Id, cancellationToken);
        return page.Where(m => m.MeetupId is { } id && meetups.ContainsKey(id))
            .ToDictionary(m => m.Id, m => Card(meetups[m.MeetupId!.Value], m, me, now, conversation.ClosedAt is null));
    }

    /// <summary>One meet-up as <paramref name="viewer"/> sees it. The note is the carrying message's visible text.</summary>
    private static MeetupView Card(Meetup m, Message message, Guid viewer, DateTimeOffset now, bool open) => new(
        m.Id, m.ConversationId, m.ModeId, m.Category, MeetupPolicy.Label(m.ModeId, m.Category) ?? "Meet-up", m.Venue, m.StartsAt, m.DurationMinutes,
        MessagingPolicy.VisibleText(message) is { Length: > 0 } note ? note : null, MeetupPolicy.State(m, now), m.ProposerAccountId == viewer,
        open && MeetupPolicy.MayAccept(m, viewer, now), open && MeetupPolicy.MayDecline(m, viewer), open && MeetupPolicy.MaySuggest(m, viewer, now),
        open && MeetupPolicy.MayWithdraw(m, viewer), open && MeetupPolicy.MayCancel(m, viewer, now));

    /// <summary>Re-sends a meet-up's card to both members, so open chats show the new state at once.</summary>
    private static async Task SendCardAsync(IServiceProvider services, SwoovrDbContext db, Meetup meetup, Conversation conversation, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var message = await db.Messages.AsNoTracking().SingleOrDefaultAsync(m => m.Id == meetup.MessageId, cancellationToken);
        if (message is null) return;
        // PR 583 review: a state change never brings back a card the viewer hid ("Hide for me", or hiding the chat after it
        // was sent), the same rule as the thread list.
        var hidden = await db.MessageHides.Where(h => h.MessageId == message.Id).Select(h => h.AccountId)
            .Union(db.ConversationHides.Where(h => h.ConversationId == conversation.Id && h.HiddenAt >= message.SentAt).Select(h => h.AccountId))
            .ToListAsync(cancellationToken);
        foreach (var viewer in new[] { conversation.PairLow, conversation.PairHigh }.Where(v => !hidden.Contains(v)))
            await BetaMessageAudience.SendAsync(services, viewer, View(message, viewer, now, meetup: Card(meetup, message, viewer, now, conversation.ClosedAt is null)), cancellationToken);
    }

    /// <summary>Issue 545 DEFAULT: a block or a disconnect closes the chat and calls off every plan still open in it.</summary>
    public static Task<int> CallOffMeetupsAsync(SwoovrDbContext db, Guid conversation, DateTimeOffset now, CancellationToken cancellationToken) =>
        db.Meetups.Where(m => m.ConversationId == conversation && (m.Status == MeetupStatus.Pending || m.Status == MeetupStatus.Accepted))
            .ExecuteUpdateAsync(u => u.SetProperty(m => m.Status, MeetupStatus.Cancelled).SetProperty(m => m.AnsweredAt, now), cancellationToken);
}
