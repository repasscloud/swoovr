using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Swoovr.Api.Moderation;
using Swoovr.Api.Ticketing;
using Swoovr.Domain;
using Swoovr.Infrastructure.Persistence;

/// <summary>
/// Issue 188 (D411, D420): a sensitive report's helpdesk item, against a loopback stand-in for Zammad (no real helpdesk). Waits
/// unconfigured without using attempts; a lost answer is found by search and never sent twice; the item carries the case and
/// category only, as an internal note in Trust &amp; Safety with the integration identity as customer; then it fails visibly.
/// </summary>
static class SafetyHandoffChecks
{
    sealed class FakeZammad : IAsyncDisposable
    {
        private readonly TcpListener probe = new(IPAddress.Loopback, 0);
        private readonly HttpListener listener = new();
        private readonly CancellationTokenSource stop = new();
        public readonly List<string> Posts = [];
        public bool LoseNextAnswer, GroupMissing, SearchFails;
        public string? Applied;
        public long TicketId = 4100;
        public Uri Root { get; }
        public FakeZammad()
        {
            probe.Start(); var port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
            Root = new Uri($"http://127.0.0.1:{port}/");
            listener.Prefixes.Add(Root.ToString()); listener.Start();
            _ = Task.Run(LoopAsync);
        }
        private string Ticket(string operation) => JsonSerializer.Serialize(new { id = TicketId, number = "41001", title = SafetyHandoffPolicy.Title, customer_id = 77, group_id = 5,
            owner_id = 1, state_id = 1, priority_id = 2, updated_at = DateTimeOffset.UtcNow, swoovr_operation = operation, swoovr_source = "system" });
        private async Task LoopAsync()
        {
            while (!stop.IsCancellationRequested)
            {
                HttpListenerContext context;
                try { context = await listener.GetContextAsync(); } catch { return; }
                var path = context.Request.Url!.PathAndQuery; var status = 200; string body;
                if (path.StartsWith("/api/v1/users/me")) body = """{"id":77,"login":"swoovr-safety-reports","active":true,"roles":["Swoovr safety reports"],"group_ids":{"5":["create","read"]}}""";
                else if (path == "/api/v1/groups") body = GroupMissing ? """[{"id":6,"name":"General Support"}]""" : """[{"id":5,"name":"Trust & Safety"},{"id":6,"name":"General Support"}]""";
                else if (path.StartsWith("/api/v1/tickets/search") && SearchFails) { status = 503; body = "{}"; }
                else if (path.StartsWith("/api/v1/tickets/search")) body = Applied is { } op && Uri.UnescapeDataString(path).Contains(op) ? "[" + Ticket(op) + "]" : "[]";
                else if (path == "/api/v1/tickets" && context.Request.HttpMethod == "POST")
                {
                    using var reader = new StreamReader(context.Request.InputStream); var posted = await reader.ReadToEndAsync(); Posts.Add(posted);
                    var operation = JsonDocument.Parse(posted).RootElement.GetProperty("swoovr_operation").GetString()!;
                    Applied = operation;
                    if (LoseNextAnswer) { LoseNextAnswer = false; status = 503; body = "{}"; } else body = Ticket(operation);
                }
                else { status = 404; body = "{}"; }
                var bytes = Encoding.UTF8.GetBytes(body);
                context.Response.StatusCode = status; context.Response.ContentType = "application/json";
                await context.Response.OutputStream.WriteAsync(bytes); context.Response.Close();
            }
        }
        public ValueTask DisposeAsync() { stop.Cancel(); listener.Stop(); listener.Close(); return ValueTask.CompletedTask; }
    }

    public static async Task Run(DbContextOptions<SwoovrDbContext> options, Action<string, bool> check)
    {
        // Pure rules first.
        check("safety hand-off backoff doubles from a minute to an hour", SafetyHandoffPolicy.Backoff(1) == TimeSpan.FromMinutes(1) && SafetyHandoffPolicy.Backoff(3) == TimeSpan.FromMinutes(4)
            && SafetyHandoffPolicy.Backoff(30) == TimeSpan.FromHours(1));
        var note = SafetyHandoffPolicy.Note("Threats to safety", Guid.Empty, "https://admin.example.test/");
        check("the note links the case and names the category only", note.Contains("https://admin.example.test/cases/00000000-0000-0000-0000-000000000000") && note.Contains("Threats to safety"));

        await using var db = new SwoovrDbContext(options);
        await db.Database.MigrateAsync();
        var now = DateTimeOffset.UtcNow; var subject = Guid.NewGuid(); var reporter = Guid.NewGuid();
        Account Account(Guid id) => new() { Id = id, Issuer = "https://issuer.example.test/members", Subject = "fixture-" + id, Realm = AccountRealm.Members, Status = AccountStatus.Active, CreatedAt = now, LastSeenAt = now };
        db.Accounts.AddRange(Account(subject), Account(reporter));
        var category = await db.ReportCategories.Where(c => c.Serious).OrderBy(c => c.SortOrder).FirstAsync();
        var item = new ModerationCase { Id = Guid.NewGuid(), SubjectAccountId = subject, Status = CaseStatus.Open, Queue = CaseQueue.Admin, CreatedAt = now, UpdatedAt = now };
        db.ModerationCases.Add(item);
        SafetyHandoff Queue()
        {
            var report = new Report { Id = Guid.NewGuid(), CaseId = item.Id, ReporterAccountId = reporter, SubjectAccountId = subject, SubjectType = ReportSubject.Profile,
                SubjectRef = Guid.NewGuid().ToString(), CategoryId = category.Id, CreatedAt = now, Text = "Fictional private report wording" };
            db.Reports.Add(report);
            var row = new SafetyHandoff { Id = Guid.NewGuid(), ReportId = report.Id, CaseId = item.Id, CategoryId = category.Id, State = SafetyHandoffState.Pending, NextAttemptAt = now, CreatedAt = now };
            db.SafetyHandoffs.Add(row);
            return row;
        }
        var first = Queue(); await db.SaveChangesAsync();
        await using var zammad = new FakeZammad();
        SafetyHandoffJob Job(bool configured) => new(db, new TicketingActorFactory(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Ticketing:Zammad:BaseUrl"] = zammad.Root.ToString(), ["Ticketing:Zammad:IdentityToken"] = "fixture-identity",
            ["Ticketing:Zammad:SafetyReportToken"] = configured ? "fixture-safety" : "", ["Location:HomeCityCheck:AdminBaseUrl"] = "https://admin.example.test/",
        }).Build(), TimeProvider.System), new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Location:HomeCityCheck:AdminBaseUrl"] = "https://admin.example.test/" }).Build(),
            NullLogger<SafetyHandoffJob>.Instance);
        async Task<SafetyHandoff> Fresh(Guid id) { db.ChangeTracker.Clear(); return await db.SafetyHandoffs.SingleAsync(h => h.Id == id); }
        async Task Due(Guid id) => await db.SafetyHandoffs.Where(h => h.Id == id).ExecuteUpdateAsync(u => u.SetProperty(h => h.NextAttemptAt, now.AddMinutes(-1)));

        await Job(false).RunAsync(now, default);
        var row = await Fresh(first.Id);
        check("unconfigured, a sensitive report waits without using attempts", row is { State: SafetyHandoffState.Pending, Attempts: 0, FailureCode: "ticketing-unavailable" } && zammad.Posts.Count == 0);

        zammad.LoseNextAnswer = true; await Due(first.Id);
        await Job(true).RunAsync(now, default);
        row = await Fresh(first.Id);
        check("a lost answer leaves the item to reconcile", row is { State: SafetyHandoffState.Sending, Attempts: 1, TicketId: null } && zammad.Posts.Count == 1);
        // PR 528 review: a failed search while reconciling keeps it Sending, so the next pass searches again instead of creating.
        zammad.SearchFails = true; await Due(first.Id);
        await Job(true).RunAsync(now, default);
        row = await Fresh(first.Id);
        check("a failed reconciliation read keeps the item Sending", row is { State: SafetyHandoffState.Sending, Attempts: 2, TicketId: null } && zammad.Posts.Count == 1);
        zammad.SearchFails = false;
        await Due(first.Id);
        await Job(true).RunAsync(now, default);
        row = await Fresh(first.Id);
        check("reconciled by search, never sent twice", row is { State: SafetyHandoffState.Created, TicketId: 4100 } && zammad.Posts.Count == 1);
        check("the item is linked to its case", await db.Set<TicketingCaseLink>().AnyAsync(l => l.ProviderTicketId == 4100 && l.CaseId == item.Id));
        check("the hand-off is audited with the reference only", await db.AuditEntries.AnyAsync(a => a.Action == "safety-report.handed-off" && a.TargetId == item.Id.ToString()));

        var posted = JsonDocument.Parse(zammad.Posts[0]).RootElement;
        var article = posted.GetProperty("article");
        check("an internal note in Trust & Safety, the identity as its own customer", posted.GetProperty("group_id").GetInt64() == 5 && posted.GetProperty("customer_id").GetInt64() == 77
            && article.GetProperty("internal").GetBoolean() && article.GetProperty("sender").GetString() == "Agent" && posted.GetProperty("swoovr_operation").GetString() == first.Id.ToString("D"));
        var body = article.GetProperty("body").GetString()!;
        check("the note has the case and category, never the report's words or the people", body.Contains(item.Id.ToString("D")) && body.Contains(category.Label)
            && !body.Contains("Fictional private report wording") && !body.Contains(reporter.ToString()) && !body.Contains(subject.ToString()));

        var second = Queue(); await db.SaveChangesAsync();
        zammad.GroupMissing = true;
        await Job(true).RunAsync(now, default);
        row = await Fresh(second.Id);
        check("a missing group waits and retries", row is { State: SafetyHandoffState.Pending, Attempts: 1, FailureCode: "group-missing" } && zammad.Posts.Count == 1);
        await db.SafetyHandoffs.Where(h => h.Id == second.Id).ExecuteUpdateAsync(u => u.SetProperty(h => h.Attempts, SafetyHandoffPolicy.MaximumAttempts - 1).SetProperty(h => h.NextAttemptAt, now.AddMinutes(-1)));
        await Job(true).RunAsync(now, default);
        row = await Fresh(second.Id);
        check("after the last attempt it fails visibly", row is { State: SafetyHandoffState.Failed, NextAttemptAt: null }
            && await db.AuditEntries.AnyAsync(a => a.Action == "safety-report.handoff-failed" && a.TargetId == item.Id.ToString()));

        await db.Reports.Where(r => r.Id == second.ReportId).ExecuteDeleteAsync();
        check("a hand-off goes with its report", !await db.SafetyHandoffs.AnyAsync(h => h.Id == second.Id));
        await db.Set<TicketingCaseLink>().Where(l => l.CaseId == item.Id).ExecuteDeleteAsync();
        await db.Reports.Where(r => r.CaseId == item.Id).ExecuteDeleteAsync();
        await db.ModerationCases.Where(c => c.Id == item.Id).ExecuteDeleteAsync();
        await db.Accounts.Where(a => a.Id == subject || a.Id == reporter).ExecuteDeleteAsync();
    }
}
