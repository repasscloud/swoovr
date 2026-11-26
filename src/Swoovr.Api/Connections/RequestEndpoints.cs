using Microsoft.AspNetCore.SignalR;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Swoovr.Api.Accounts;
using Swoovr.Api.Profiles;
using Swoovr.Contracts;
using Swoovr.Domain;
using Swoovr.Infrastructure.Persistence;
using static Swoovr.Api.Accounts.AccountEndpoints;

namespace Swoovr.Api.Connections;

/// <summary>
/// Connection requests and blocks (D112, D117–D119). Per mode: send, withdraw, accept, decline; nothing connects on its
/// own; bots never initiate; one pending request per pair and mode; a rate limit per sender. A block hides both people
/// from each other everywhere, cancels their pending requests and makes every answer about the other a 404. The audit
/// log holds ids and mode ids only.
/// </summary>
public static class RequestEndpoints
{
    public const int PageSize = 50;

    public static void Map(WebApplication app)
    {
        var api = app.MapGroup("/api").RequireAuthorization();

        api.MapPost("/requests", async (NewConnectionRequest request, ClaimsPrincipal user, AccountResolver accounts, IConfiguration configuration,
            TimeProvider clock, CancellationToken cancellationToken) =>
        {
            var (current, denied) = await MemberAsync(user, accounts, cancellationToken);
            if (denied is not null) return denied;
            var (me, db, now) = (current!.Profile!, accounts.Db!, clock.GetUtcNow());
            // D133: a targeted restriction stops new requests; existing connections stay.
            if (current.Enforcement.NoNewRequests)
                return Problem(StatusCodes.Status403Forbidden, ProblemCodes.Restricted, "You can't send new requests right now.");
            var mode = await db.ConnectionModes.SingleOrDefaultAsync(m => m.Id == request.Mode && m.Available, cancellationToken);
            if (mode is null) return Problem(StatusCodes.Status404NotFound, ProblemCodes.UnknownMode, "That connection mode isn't available.");
            var target = await ProfileEndpoints.Candidates(db, configuration, me.AccountId).SingleOrDefaultAsync(c => c.Profile.PublicId == request.To, cancellationToken);
            var shared = target is null ? [] : await ProfileEndpoints.SharedModesAsync(db, me.AccountId, target, cancellationToken);
            var shown = shared.Any(m => m.Id == mode.Id);
            var other = target?.Profile.AccountId ?? Guid.Empty;
            var (low, high) = ConnectionRequest.Pair(me.AccountId, other);
            var pair = await db.ConnectionRequests.Where(r => r.PairLow == low && r.PairHigh == high).ToListAsync(cancellationToken);
            var between = pair.Where(r => r.ModeId == mode.Id).ToList();
            var trip = request.Trip;
            // D125: a request needs the same market, a pair already connected in another mode, or a local writing to a
            // traveller visiting their market (the trip is attached). Travellers can't start contact with a destination's
            // locals before arrival (D60, D142), and a profile link from another market changes nothing.
            if (shown && target is not null)
            {
                var (market, _) = await MarketEndpoints.LocalAsync(db, me, mode.Id, cancellationToken);
                var bots = target.Profile.Bot ? await MarketEndpoints.BotMarketsAsync(db, target.Profile.AccountId, cancellationToken) : null;
                var same = request.Trip is null && MarketAccess.SameMarket(market?.Id, target.Profile.HomeMarketId, target.Profile.Bot, bots);
                // D346, #279: physical presence by a fresh reading also opens an open market offering this mode (no trip).
                if (!same && request.Trip is null
                    && (await MarketEndpoints.SharedByPresenceAsync(db, me, target.Profile, bots, now, cancellationToken)).Select(m => m.Id).ToList() is { Count: > 0 } present
                    && await db.MarketModes.AnyAsync(m => present.Contains(m.MarketId) && m.ModeId == mode.Id, cancellationToken))
                    same = true;
                var connected = request.Trip is null && pair.Any(r => r.Status == ConnectionRequestStatus.Accepted);
                var visits = same || connected || market is null ? []
                    : await Travel.TravelEndpoints.VisitsToAsync(db, configuration, me.AccountId, market, await MarketEndpoints.ZonesAsync(db, cancellationToken),
                        mode.Id, now, cancellationToken, target.Profile.AccountId);
                // From the Travel feed (Q17 rule 29) the trip must be the recipient's and one the sender can see now;
                // from a profile the visit the sender can see is attached.
                var visit = request.Trip is { } tripId ? visits.FirstOrDefault(v => v.Trip.Id == tripId) : visits.FirstOrDefault();
                trip = visit?.Trip.Id;
                // D142, D149: a traveller may start contact with the destination's locals once arrival is confirmed; the
                // request carries their own trip. Before that they may browse but not request (the person is already
                // visible to them through Traveller Search, so saying why reveals nothing new).
                var travel = same || connected || visit is not null ? null : await Travel.ArrivalEndpoints.ReachAsync(db, me, target, shared, now, cancellationToken);
                if (travel?.ArrivedTrips.TryGetValue(mode.Id, out var ownTrip) == true) trip = ownTrip;
                else if (travel?.SearchModes.Contains(mode.Id) == true)
                    return Problem(StatusCodes.Status403Forbidden, ProblemCodes.ArrivalRequired, "You can send requests here once you've confirmed you've arrived.");
                shown = MarketAccess.MayRequest(same, connected, visit is not null, travel?.ArrivedTrips.ContainsKey(mode.Id) == true);
            }
            var facts = new RequestFacts(
                SenderIsBot: me.Bot,
                SameAccount: other == me.AccountId,
                // Candidates already leaves out blocked pairs; a block therefore reads as "not available".
                Separated: false,
                RecipientShownToSender: shown,
                Connected: between.Any(r => r.Status == ConnectionRequestStatus.Accepted),
                Pending: between.Any(r => r.Status == ConnectionRequestStatus.Pending),
                DeclinedAt: between.Where(r => r.Status == ConnectionRequestStatus.Declined && r.FromAccountId == me.AccountId).Max(r => r.AnsweredAt),
                SentInWindow: await db.ConnectionRequests.CountAsync(r => r.FromAccountId == me.AccountId && r.CreatedAt > now - ConnectionRequestPolicy.Window, cancellationToken),
                // D176: the recipient disconnected the sender in this mode.
                DisconnectedBySenderOf: between.Where(r => r.Status == ConnectionRequestStatus.Disconnected && r.EndedBy == other).Max(r => r.EndedAt));
            switch (ConnectionRequestPolicy.CanSend(facts, now))
            {
                case RequestRefusal.SenderIsBot:
                    return Problem(StatusCodes.Status403Forbidden, ProblemCodes.BotCannotInitiate, "Official bots can't send requests.");
                case RequestRefusal.NotAvailable:
                    return Problem(StatusCodes.Status404NotFound, ProblemCodes.NotAvailable, "That profile isn't available.");
                case RequestRefusal.AlreadyConnected:
                    return Problem(StatusCodes.Status409Conflict, ProblemCodes.AlreadyConnected, "You're already connected in this mode.");
                case RequestRefusal.AlreadyPending:
                    return Problem(StatusCodes.Status409Conflict, ProblemCodes.RequestPending, "There's already a request between you in this mode.");
                case RequestRefusal.RecentlyDeclined:
                    return Problem(StatusCodes.Status409Conflict, ProblemCodes.RecentlyDeclined, "You can't ask again in this mode yet.");
                case RequestRefusal.RecentlyDisconnected:
                    return Problem(StatusCodes.Status409Conflict, ProblemCodes.RecentlyDisconnected, "You can't ask again in this mode yet.");
                case RequestRefusal.RateLimited:
                    Audit.Record(db, now, me.AccountId, "account", "request.rate-limited", "account", me.AccountId.ToString(), mode.Id);
                    await db.SaveChangesAsync(cancellationToken);
                    return Problem(StatusCodes.Status429TooManyRequests, ProblemCodes.TooManyRequests, "You've sent a lot of requests today. Try again later.");
            }
            var created = new ConnectionRequest
            {
                Id = Guid.NewGuid(), FromAccountId = me.AccountId, ToAccountId = other, PairLow = low, PairHigh = high, ModeId = mode.Id,
                Status = ConnectionRequestStatus.Pending, CreatedAt = now, TripId = trip,
            };
            db.ConnectionRequests.Add(created);
            Audit.Record(db, now, me.AccountId, "account", "request.sent", "connection-request", created.Id.ToString(), mode.Id);
            try { await db.SaveChangesAsync(cancellationToken); }
            catch (DbUpdateException)
            {
                // Two sends at once: the unique index keeps one pending request per pair and mode.
                return Problem(StatusCodes.Status409Conflict, ProblemCodes.RequestPending, "There's already a request between you in this mode.");
            }
            // #49: the recipient hears about it unless they muted the mode, turned requests off, or either of them is barred or blocked.
            await accounts.Services.GetRequiredService<Notifications.Notifier>().NotifyAsync(other, NotificationKind.Request, me.AccountId, mode.Id, created.Id, now, cancellationToken);
            return Results.Created($"/api/requests/{created.Id}", await ViewAsync(db, created, me.AccountId, cancellationToken));
        });

        foreach (var (route, answer) in new[] { ("accept", ConnectionRequestPolicy.Answer.Accept), ("decline", ConnectionRequestPolicy.Answer.Decline),
            ("withdraw", ConnectionRequestPolicy.Answer.Withdraw) })
        {
            api.MapPost($"/requests/{{id:guid}}/{route}", async (Guid id, ClaimsPrincipal user, AccountResolver accounts, TimeProvider clock,
                CancellationToken cancellationToken) =>
            {
                var (current, denied) = await MemberAsync(user, accounts, cancellationToken);
                if (denied is not null) return denied;
                var (me, db, now) = (current!.Profile!, accounts.Db!, clock.GetUtcNow());
                var request = await db.ConnectionRequests.FindAsync([id], cancellationToken);
                // Another member's request, a blocked pair or a closed account all read as not found.
                if (request is null || !ConnectionRequestPolicy.MayAnswer(request, me.AccountId, answer)
                    || !await ReachableAsync(db, me.AccountId, request.FromAccountId == me.AccountId ? request.ToAccountId : request.FromAccountId, cancellationToken))
                    return Problem(StatusCodes.Status404NotFound, ProblemCodes.NotAvailable, "That request isn't available.");
                if (request.Status != ConnectionRequestStatus.Pending)
                    return Problem(StatusCodes.Status409Conflict, ProblemCodes.RequestSettled, "That request has already been answered.");
                (request.Status, request.AnsweredAt) = (ConnectionRequestPolicy.Outcome(answer), now);
                Audit.Record(db, now, me.AccountId, "account", "request." + route switch { "accept" => "accepted", "decline" => "declined", _ => "withdrawn" },
                    "connection-request", request.Id.ToString(), request.ModeId);
                try { await db.SaveChangesAsync(cancellationToken); }
                catch (DbUpdateException)
                {
                    return Problem(StatusCodes.Status409Conflict, ProblemCodes.AlreadyConnected, "You're already connected in this mode.");
                }
                // #49: answering a request reads its notification; the sender hears that it was accepted (never that it was declined).
                await db.Notifications.Where(n => n.AccountId == me.AccountId && n.SubjectId == request.Id && n.ReadAt == null)
                    .ExecuteUpdateAsync(u => u.SetProperty(n => n.ReadAt, now), cancellationToken);
                if (answer == ConnectionRequestPolicy.Answer.Accept)
                    await accounts.Services.GetRequiredService<Notifications.Notifier>().NotifyAsync(request.FromAccountId, NotificationKind.Connection, me.AccountId,
                        request.ModeId, request.Id, now, cancellationToken);
                return Results.Ok(await ViewAsync(db, request, me.AccountId, cancellationToken));
            });
        }

        // D176: either member ends one mode's connection without blocking. The other isn't told; it leaves both members'
        // Connections, and the conversation closes read-only once no mode connects them.
        api.MapPost("/requests/{id:guid}/disconnect", async (Guid id, ClaimsPrincipal user, AccountResolver accounts, IHubContext<MessagesHub> hub, TimeProvider clock,
            CancellationToken cancellationToken) =>
        {
            var (current, denied) = await MemberAsync(user, accounts, cancellationToken);
            if (denied is not null) return denied;
            var (me, db, now) = (current!.Profile!, accounts.Db!, clock.GetUtcNow());
            var request = await db.ConnectionRequests.FindAsync([id], cancellationToken);
            if (request is null || !ConnectionRequestPolicy.MayDisconnect(request, me.AccountId)
                || !await ReachableAsync(db, me.AccountId, request.FromAccountId == me.AccountId ? request.ToAccountId : request.FromAccountId, cancellationToken))
                return Problem(StatusCodes.Status404NotFound, ProblemCodes.NotAvailable, "That connection isn't available.");
            if (!ConnectionRequestPolicy.Disconnect(request, me.AccountId, now))
                return Problem(StatusCodes.Status409Conflict, ProblemCodes.NotConnected, "You aren't connected in this mode.");
            Audit.Record(db, now, me.AccountId, "account", "request.disconnected", "connection-request", request.Id.ToString(), request.ModeId);
            await db.SaveChangesAsync(cancellationToken);
            var closed = await MessagingEndpoints.CloseIfUnconnectedAsync(db, request.PairLow, request.PairHigh, now, cancellationToken);
            if (closed is not null)
            {
                await db.SaveChangesAsync(cancellationToken);
                await hub.Clients.Groups(MessagesHub.Group(closed.PairLow), MessagesHub.Group(closed.PairHigh)).SendAsync("closed", new ConversationClosed(closed.Id), cancellationToken);
            }
            return Results.Ok(await ViewAsync(db, request, me.AccountId, cancellationToken));
        });

        // Pending requests received or sent, newest first, paged.
        api.MapGet("/requests", async (string? box, int? page, ClaimsPrincipal user, AccountResolver accounts, TimeProvider clock, CancellationToken cancellationToken) =>
        {
            var (current, denied) = await MemberAsync(user, accounts, cancellationToken);
            if (denied is not null) return denied;
            if (box is not ("received" or "sent") || page is < 1 or > 1000)
                return Problem(StatusCodes.Status400BadRequest, ProblemCodes.InvalidRequest, "Ask for the received or sent box.");
            var (me, db) = (current!.Profile!.AccountId, accounts.Db!);
            var query = db.ConnectionRequests.Where(r => r.Status == ConnectionRequestStatus.Pending
                && (box == "received" ? r.ToAccountId == me : r.FromAccountId == me));
            return Results.Ok(await PageAsync(db, query, me, page ?? 1, cancellationToken, accounts.Services, clock.GetUtcNow()));
        });

        // Accepted connections (a request each side has agreed to), newest first, paged.
        api.MapGet("/connections", async (int? page, ClaimsPrincipal user, AccountResolver accounts, TimeProvider clock, CancellationToken cancellationToken) =>
        {
            var (current, denied) = await MemberAsync(user, accounts, cancellationToken);
            if (denied is not null) return denied;
            if (page is < 1 or > 1000) return Problem(StatusCodes.Status400BadRequest, ProblemCodes.InvalidRequest, "Check the page number.");
            var (me, db) = (current!.Profile!.AccountId, accounts.Db!);
            var query = db.ConnectionRequests.Where(r => r.Status == ConnectionRequestStatus.Accepted && (r.ToAccountId == me || r.FromAccountId == me));
            return Results.Ok(await PageAsync(db, query, me, page ?? 1, cancellationToken, accounts.Services, clock.GetUtcNow()));
        });

        api.MapPost("/blocks", async (BlockRequest request, ClaimsPrincipal user, AccountResolver accounts, IHubContext<MessagesHub> hub, TimeProvider clock,
            CancellationToken cancellationToken) =>
        {
            var (current, denied) = await MemberAsync(user, accounts, cancellationToken);
            if (denied is not null) return denied;
            var (me, db, now) = (current!.Profile!.AccountId, accounts.Db!, clock.GetUtcNow());
            var target = await db.MemberProfiles.SingleOrDefaultAsync(p => p.PublicId == request.ProfileId, cancellationToken);
            if (target is null || target.AccountId == me)
                return Problem(StatusCodes.Status404NotFound, ProblemCodes.NotAvailable, "That profile isn't available.");
            var closed = new List<Conversation>();
            if (await AddBlockAsync(db, me, target.AccountId, now, cancellationToken, closed)) await db.SaveChangesAsync(cancellationToken);
            await MessagingEndpoints.PushClosedAsync(hub, closed, cancellationToken);
            return Results.NoContent();
        });

        api.MapDelete("/blocks/{id:guid}", async (Guid id, ClaimsPrincipal user, AccountResolver accounts, TimeProvider clock, CancellationToken cancellationToken) =>
        {
            var (current, denied) = await MemberAsync(user, accounts, cancellationToken);
            if (denied is not null) return denied;
            var (me, db) = (current!.Profile!.AccountId, accounts.Db!);
            var block = await (from b in db.Blocks join p in db.MemberProfiles on b.BlockedAccountId equals p.AccountId
                               where b.BlockerAccountId == me && p.PublicId == id select b).SingleOrDefaultAsync(cancellationToken);
            if (block is null) return Problem(StatusCodes.Status404NotFound, ProblemCodes.NotAvailable, "That block isn't there.");
            db.Blocks.Remove(block);
            Audit.Record(db, clock.GetUtcNow(), me, "account", "block.removed", "account", block.BlockedAccountId.ToString());
            await db.SaveChangesAsync(cancellationToken);
            return Results.NoContent();
        });

        // Only the member's own blocks; never who blocked them.
        api.MapGet("/blocks", async (ClaimsPrincipal user, AccountResolver accounts, CancellationToken cancellationToken) =>
        {
            var (current, denied) = await MemberAsync(user, accounts, cancellationToken);
            if (denied is not null) return denied;
            var (me, db) = (current!.Profile!.AccountId, accounts.Db!);
            var list = await (from b in db.Blocks join p in db.MemberProfiles on b.BlockedAccountId equals p.AccountId
                              where b.BlockerAccountId == me orderby b.CreatedAt descending
                              select new BlockedPerson(p.PublicId, p.DisplayName, b.CreatedAt)).Take(500).ToListAsync(cancellationToken);
            return Results.Ok(list);
        });
    }

    /// <summary>Whether the other member can still be dealt with: their account is active and neither blocked the other.</summary>
    /// <summary>
    /// Adds a block (unsaved) unless it exists; blocking cancels every pending request between the two, in every mode.
    /// Also used by a report with "block" (D131). Returns whether anything changed.
    /// </summary>
    public static async Task<bool> AddBlockAsync(SwoovrDbContext db, Guid me, Guid other, DateTimeOffset now, CancellationToken cancellationToken,
        List<Conversation>? closed = null)
    {
        if (await db.Blocks.AnyAsync(b => b.BlockerAccountId == me && b.BlockedAccountId == other, cancellationToken)) return false;
        db.Blocks.Add(new Block { BlockerAccountId = me, BlockedAccountId = other, CreatedAt = now });
        var (low, high) = ConnectionRequest.Pair(me, other);
        foreach (var pending in await db.ConnectionRequests.Where(r => r.PairLow == low && r.PairHigh == high && r.Status == ConnectionRequestStatus.Pending).ToListAsync(cancellationToken))
        {
            (pending.Status, pending.AnsweredAt) = (ConnectionRequestStatus.Cancelled, now);
            Audit.Record(db, now, null, "system", "request.cancelled", "connection-request", pending.Id.ToString(), "block");
        }
        // Q42 rules 12–14: the block also ends accepted connections and closes the conversation; unblocking restores neither.
        if (await MessagingEndpoints.CloseForBlockAsync(db, me, other, now, cancellationToken) is { } conversation) closed?.Add(conversation);
        Audit.Record(db, now, me, "account", "block.added", "account", other.ToString());
        return true;
    }

    public static async Task<bool> ReachableAsync(SwoovrDbContext db, Guid me, Guid other, CancellationToken cancellationToken) =>
        await db.Accounts.AnyAsync(a => a.Id == other && a.Status == AccountStatus.Active, cancellationToken)
        && !await db.Blocks.AnyAsync(b => b.BlockerAccountId == me && b.BlockedAccountId == other || b.BlockerAccountId == other && b.BlockedAccountId == me, cancellationToken);

    /// <summary>Leave out anyone in a blocked pair or with a closed account, so lists (and the issue 540 count) never show them.</summary>
    private static IQueryable<ConnectionRequest> Listed(SwoovrDbContext db, IQueryable<ConnectionRequest> query, Guid me) =>
        query.Where(r => !db.Blocks.Any(b => b.BlockerAccountId == r.FromAccountId && b.BlockedAccountId == r.ToAccountId
                || b.BlockerAccountId == r.ToAccountId && b.BlockedAccountId == r.FromAccountId)
            && db.Accounts.Any(a => a.Id == (r.FromAccountId == me ? r.ToAccountId : r.FromAccountId) && a.Status == AccountStatus.Active));

    /// <summary>Issue 540: the received box's size, counted exactly as Connections lists it. Outgoing requests and connections never count.</summary>
    public static Task<int> PendingReceivedAsync(SwoovrDbContext db, Guid me, CancellationToken cancellationToken) =>
        Listed(db, db.ConnectionRequests.Where(r => r.Status == ConnectionRequestStatus.Pending && r.ToAccountId == me), me).CountAsync(cancellationToken);

    private static async Task<RequestPage> PageAsync(SwoovrDbContext db, IQueryable<ConnectionRequest> query, Guid me, int page, CancellationToken cancellationToken,
        IServiceProvider services, DateTimeOffset now)
    {
        query = Listed(db, query, me);
        var rows = await query.OrderByDescending(r => r.CreatedAt).ThenBy(r => r.Id).Skip((page - 1) * PageSize).Take(PageSize + 1).ToListAsync(cancellationToken);
        var shown = rows.Take(PageSize).ToList();
        var views = await ViewsAsync(db, shown, me, cancellationToken);
        return new(await WithFacesAsync(db, services, shown, views, me, now, cancellationToken), page, rows.Count > PageSize);
    }

    /// <summary>
    /// Issue 539: each row's thumbnail and age, for the whole page in one batch, exactly as an Explore card in that mode shows
    /// them: the mode's main approved photo as a short-lived link (D203), and age only where the mode and the person allow it
    /// (D138, D418). Nothing else about the person is added. PR 561 review: only while their profile would open in that mode
    /// now (the profile endpoint's rule: still a candidate and the mode still shared, D175); a mode they turned off or hid
    /// shows a name only.
    /// </summary>
    private static async Task<List<ConnectionRequestView>> WithFacesAsync(SwoovrDbContext db, IServiceProvider services, List<ConnectionRequest> rows,
        List<ConnectionRequestView> views, Guid me, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (rows.Count == 0) return views;
        var others = rows.Select(r => r.FromAccountId == me ? r.ToAccountId : r.FromAccountId).Distinct().ToList();
        var configuration = services.GetRequiredService<IConfiguration>();
        // PR 561 review: one candidate query and one batched shared-modes check for the whole page, not one per person.
        var people = await ProfileEndpoints.Candidates(db, configuration, me).Where(c => others.Contains(c.Profile.AccountId)).ToListAsync(cancellationToken);
        var open = (await ProfileEndpoints.SharedModesAsync(db, me, people, cancellationToken))
            .SelectMany(p => p.Value.Select(mode => (p.Key, mode.Id))).ToHashSet();
        var links = services.GetService<Media.MediaLinks>();
        var photos = links is null ? default : await Media.MediaEndpoints.PhotosAsync(db, links, others, rows.Select(r => r.ModeId).Distinct().ToList(), now, cancellationToken);
        var identities = await ProfileEndpoints.IdentitiesAsync(db, others, cancellationToken);
        var births = await db.MemberProfiles.Where(p => others.Contains(p.AccountId)).ToDictionaryAsync(p => p.AccountId, p => p.DateOfBirth, cancellationToken);
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        return rows.Zip(views, (row, view) =>
        {
            var other = row.FromAccountId == me ? row.ToAccountId : row.FromAccountId;
            if (!open.Contains((other, row.ModeId))) return view;
            var age = births.GetValueOrDefault(other) is { } born ? AgePolicy.YearsOn(born, today) : (int?)null;
            var shownAge = ModePrivacy.OnCard(row.ModeId, ProfileEndpoints.Public(identities.GetValueOrDefault(other), age, new Dictionary<string, string>()).Age, null, null).Age;
            return view with { Person = view.Person with { Photo = photos.Main?.GetValueOrDefault((other, row.ModeId)), Age = shownAge } };
        }).ToList();
    }

    public static async Task<ConnectionRequestView> ViewAsync(SwoovrDbContext db, ConnectionRequest request, Guid me, CancellationToken cancellationToken) =>
        (await ViewsAsync(db, [request], me, cancellationToken))[0];

    /// <summary>
    /// The rows of a page in a few queries (PR 561 review), not several per row: the other people's profiles, their names in
    /// each mode, the badge order and any trip visits load once for the whole page. Same order as <paramref name="requests"/>.
    /// </summary>
    public static async Task<List<ConnectionRequestView>> ViewsAsync(SwoovrDbContext db, IReadOnlyList<ConnectionRequest> requests, Guid me,
        CancellationToken cancellationToken)
    {
        if (requests.Count == 0) return [];
        Guid Other(ConnectionRequest r) => r.FromAccountId == me ? r.ToAccountId : r.FromAccountId;
        var others = requests.Select(Other).Distinct().ToList();
        var profiles = await db.MemberProfiles.Where(p => others.Contains(p.AccountId)).ToDictionaryAsync(p => p.AccountId, cancellationToken);
        var aliases = (await db.MemberModes.Where(m => others.Contains(m.AccountId)).Select(m => new { m.AccountId, m.ModeId, m.DisplayName }).ToListAsync(cancellationToken))
            .ToDictionary(m => (m.AccountId, m.ModeId), m => m.DisplayName);
        var order = await ProfileEndpoints.BadgeOrderAsync(db, cancellationToken);
        var tripIds = requests.Where(r => r.TripId is not null).Select(r => r.TripId!.Value).Distinct().ToList();
        var visits = tripIds.Count == 0 ? [] : await (from t in db.Trips join c in db.GeoCities on t.GeoNameId equals c.GeoNameId
                                                      where tripIds.Contains(t.Id) select new { t.Id, Visit = Travel.TravelEndpoints.Visit(t, c) })
            .ToDictionaryAsync(v => v.Id, v => v.Visit, cancellationToken);
        return requests.Select(request =>
        {
            var other = profiles[Other(request)];
            var person = new PersonSummary(other.PublicId, ModePrivacy.DisplayName(aliases.GetValueOrDefault((other.AccountId, request.ModeId)), other.DisplayName),
                ProfileEndpoints.Badges(other, order));
            var visit = request.TripId is { } tripId ? visits.GetValueOrDefault(tripId) : null;
            return new ConnectionRequestView(request.Id, request.ModeId, request.FromAccountId == me ? "sent" : "received", Wire(request.Status), person, request.CreatedAt, visit);
        }).ToList();
    }
}
