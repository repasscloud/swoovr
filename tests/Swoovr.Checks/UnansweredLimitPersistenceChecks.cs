using Microsoft.EntityFrameworkCore;
using Swoovr.Api.Connections;
using Swoovr.Domain;
using Swoovr.Infrastructure.Persistence;

/// <summary>
/// #300 (D380) on a real PostgreSQL: the stored setting (Off by default, 3/5/10 only), and the unanswered run the send
/// routes read from the stored messages under the conversation lock. Fictional adults only.
/// </summary>
static class UnansweredLimitPersistenceChecks
{
    public static async Task Run(DbContextOptions<SwoovrDbContext> options, Action<string, bool> check)
    {
        var now = DateTimeOffset.Parse("2026-11-18T09:00:00Z");
        var (a, b, c) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        Guid ab, ac;
        await using (var db = new SwoovrDbContext(options))
        {
            foreach (var id in new[] { a, b, c })
                db.Accounts.Add(new() { Id = id, Issuer = "https://unanswered-fixture.example.test/realms/members", Subject = id.ToString(), Email = $"fictional.{id:N}@example.test",
                    Realm = AccountRealm.Members, Status = AccountStatus.Active, CreatedAt = now.AddYears(-2), LastSeenAt = now });
            ab = Conversation(db, a, b); ac = Conversation(db, a, c);
            await db.SaveChangesAsync();
        }

        Guid Conversation(SwoovrDbContext db, Guid x, Guid y)
        {
            var pair = ConnectionRequest.Pair(x, y);
            var id = Guid.NewGuid();
            db.Conversations.Add(new() { Id = id, PairLow = pair.Low, PairHigh = pair.High, CreatedAt = now.AddYears(-2) });
            return id;
        }
        async Task<Message> Send(Guid conversation, Guid from, DateTimeOffset at, MessageKind kind = MessageKind.Text)
        {
            await using var db = new SwoovrDbContext(options);
            var message = new Message { Id = Guid.NewGuid(), ConversationId = conversation, SenderAccountId = from, Kind = kind, Body = kind == MessageKind.Photo ? "" : "Fictional line", SentAt = at };
            db.Messages.Add(message);
            await db.SaveChangesAsync();
            return message;
        }
        async Task SetLimit(Guid account, int? limit)
        {
            await using var db = new SwoovrDbContext(options);
            var row = await db.NotificationSettings.SingleOrDefaultAsync(s => s.AccountId == account);
            if (row is null) db.NotificationSettings.Add(row = new NotificationSettings { AccountId = account, UpdatedAt = now });
            row.UnansweredLimit = limit;
            await db.SaveChangesAsync();
        }
        async Task<(int? Limit, int Run)> Facts(Guid conversation, Guid sender, Guid recipient)
        {
            await using var db = new SwoovrDbContext(options);
            return await MessagingEndpoints.UnansweredAsync(db, conversation, sender, recipient, default);
        }
        async Task<bool> Awaiting(Guid conversation, Guid sender, Guid recipient) => await Facts(conversation, sender, recipient) is var (limit, run) && MessagingPolicy.AwaitingReply(limit, run);

        // Settings: Off by default, with or without a row; only 3, 5 or 10 are stored.
        check("unanswered limit: no settings row reads as Off", await Facts(ab, a, b) == (null, 0));
        await SetLimit(b, null);
        await using (var db = new SwoovrDbContext(options))
            check("unanswered limit: a new settings row is Off", (await db.NotificationSettings.SingleAsync(s => s.AccountId == b)).UnansweredLimit is null);
        foreach (var bad in new[] { 0, 4, 11, -1 })
        {
            var refused = false;
            try { await SetLimit(b, bad); }
            catch (DbUpdateException) { refused = true; }
            check($"unanswered limit: the database refuses {bad}", refused);
        }
        foreach (var good in new[] { 3, 5, 10 }) { await SetLimit(b, good); check($"unanswered limit: {good} persists", (await Facts(ab, a, b)).Limit == good); }

        // Scenario A/B: five over a year with no reply; time never resets it.
        await SetLimit(b, 5);
        var sent = new List<Message>();
        for (var n = 0; n < 4; n++) sent.Add(await Send(ab, a, now.AddDays(-365 + n * 60)));
        check("unanswered limit: below the limit may send", !await Awaiting(ab, a, b) && (await Facts(ab, a, b)).Run == 4);
        sent.Add(await Send(ab, a, now.AddDays(-30), MessageKind.Photo));
        check("unanswered limit: the fifth (a photo) reaches it; the next waits, a month later", await Awaiting(ab, a, b));

        // Deleting, hiding, screening or moderation removal never shortens the run; farewells and system events from B aren't replies.
        await using (var db = new SwoovrDbContext(options))
        {
            var first = await db.Messages.SingleAsync(m => m.Id == sent[0].Id); first.DeletedAt = now;
            var second = await db.Messages.SingleAsync(m => m.Id == sent[1].Id); second.ModerationRemovedAt = now;
            db.MessageHides.Add(new MessageHide { MessageId = sent[2].Id, AccountId = a, HiddenAt = now });
            db.MessageHides.Add(new MessageHide { MessageId = sent[3].Id, AccountId = b, HiddenAt = now });
            await db.SaveChangesAsync();
        }
        check("unanswered limit: delete for both, hiding and moderation removal don't bypass it", await Awaiting(ab, a, b));
        await Send(ab, b, now.AddMinutes(-10), MessageKind.System);
        check("unanswered limit: a system event isn't a reply", await Awaiting(ab, a, b));
        check("unanswered limit: the run is counted only up to the limit", (await Facts(ab, a, b)).Run == 5);

        // Scenario C: other conversations keep their own run.
        check("unanswered limit: another conversation is independent", !await Awaiting(ac, a, c) && (await Facts(ac, a, c)) == (null, 0));
        await SetLimit(c, 3);
        check("unanswered limit: and follows that recipient's own setting", (await Facts(ac, a, c)) == (3, 0));

        // Scenario E and toggling: 10 → 3, Off, on again with the same history.
        await SetLimit(b, 10);
        check("unanswered limit: raising it to 10 lets them send again", !await Awaiting(ab, a, b));
        await SetLimit(b, 3);
        check("unanswered limit: lowering it restricts a run already above it", await Awaiting(ab, a, b));
        await SetLimit(b, null);
        check("unanswered limit: Off lifts it", !await Awaiting(ab, a, b));
        await SetLimit(b, 5);
        check("unanswered limit: turning it on again counts the run already there", await Awaiting(ab, a, b));

        // Scenario D: one reply resets A, and starts B's own run, judged by A's setting.
        var hi = await Send(ab, b, now.AddMinutes(-5));
        check("unanswered limit: a one-word reply resets the sender", !await Awaiting(ab, a, b) && (await Facts(ab, a, b)).Run == 0);
        check("unanswered limit: the other direction uses the other member's setting (Off)", await Facts(ab, b, a) == (null, 0));
        await SetLimit(a, 3);
        check("unanswered limit: the reply counts in the other direction", await Facts(ab, b, a) == (3, 1));
        await using (var db = new SwoovrDbContext(options)) { (await db.Messages.SingleAsync(m => m.Id == hi.Id)).DeletedAt = now; await db.SaveChangesAsync(); }
        check("unanswered limit: deleting the reply doesn't undo the reset", (await Facts(ab, a, b)).Run == 0);

        // Concurrency: sends hold the conversation row lock (as LockAsync does) while they check and insert, so twelve
        // concurrent sends against a limit of 5 store exactly five; the refused ones store nothing.
        await SetLimit(b, 5);
        await Send(ab, b, now.AddMinutes(-1));
        var attempts = await Task.WhenAll(Enumerable.Range(0, 12).Select(async n =>
        {
            await using var db = new SwoovrDbContext(options);
            await using var transaction = await db.Database.BeginTransactionAsync();
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM conversations WHERE id = {ab} AND (pair_low = {a} OR pair_high = {a}) FOR UPDATE");
            var (limit, run) = await MessagingEndpoints.UnansweredAsync(db, ab, a, b, default);
            if (MessagingPolicy.AwaitingReply(limit, run)) { await transaction.RollbackAsync(); return false; }
            db.Messages.Add(new Message { Id = Guid.NewGuid(), ConversationId = ab, SenderAccountId = a, Kind = MessageKind.Text, Body = "Fictional race " + n, SentAt = now.AddSeconds(n + 1) });
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
            return true;
        }));
        await using (var db = new SwoovrDbContext(options))
        {
            var stored = await db.Messages.CountAsync(m => m.ConversationId == ab && m.SenderAccountId == a && m.Body.StartsWith("Fictional race"));
            check("unanswered limit: concurrent sends can't pass it, and refused sends store nothing", attempts.Count(x => x) == 5 && stored == 5);
        }
    }
}
