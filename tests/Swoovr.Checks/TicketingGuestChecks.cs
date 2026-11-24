extern alias AdminHost;
using System.Net;
using Microsoft.Extensions.Configuration;
using Swoovr.Api.Ticketing;
using Swoovr.Client;
using Swoovr.Domain;

/// <summary>Guest code/grant policy and the optional bot check, with a fake HTTP verifier (no Cloudflare request).</summary>
internal static class TicketingGuestChecks
{
    private sealed class Fake(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { Calls++; return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) }); }
    }
    private sealed class Clients(HttpMessageHandler handler) : IHttpClientFactory { public HttpClient CreateClient(string name) => new(handler, false); }

    public static async Task Run()
    {
        var n = 0;
        void Check(bool ok, string name) { if (!ok) throw new Exception(name); n++; }
        var key = new byte[32]; var now = DateTimeOffset.UnixEpoch.AddYears(56);
        TicketingGuestChallenge Make(string code) { var c = new TicketingGuestChallenge { Id = Guid.NewGuid(), CreatedAt = now, ExpiresAt = now + TicketingGuestPolicy.CodeLifetime }; c.CodeHash = TicketingGuestPolicy.CodeHash(key, c.Id, code); return c; }
        var code = TicketingGuestPolicy.Code();
        Check(TicketingGuestPolicy.CodeShape(code) && !TicketingGuestPolicy.CodeShape("1234567") && !TicketingGuestPolicy.CodeShape("1234567a"), "eight digit codes only");
        var ok = Make(code);
        Check(TicketingGuestPolicy.Check(ok, key, code, now) && !TicketingGuestPolicy.Check(ok, key, code, now), "a code works once");
        var tried = Make(code);
        for (var i = 0; i < TicketingGuestPolicy.MaximumFailures; i++) TicketingGuestPolicy.Check(tried, key, "00000000" == code ? "11111111" : "00000000", now);
        Check(!TicketingGuestPolicy.Check(tried, key, code, now), "five wrong codes end the challenge");
        var old = Make(code); old.SupersededAt = now;
        Check(!TicketingGuestPolicy.Check(old, key, code, now), "a superseded code fails");
        Check(!TicketingGuestPolicy.Check(Make(code), key, code, now + TicketingGuestPolicy.CodeLifetime), "an expired code fails");
        var other = new byte[32]; other[0] = 1;
        Check(!TicketingGuestPolicy.EmailLookup(key, "a@example.test").SequenceEqual(TicketingGuestPolicy.EmailLookup(other, "a@example.test")), "address lookups are keyed");
        Check(TicketingGuestPolicy.Login(Guid.Empty) != TicketingPolicy.MemberLogin(Guid.Empty), "guest and member helpdesk logins never coincide");
        Check(TicketingGuestPolicy.Categories.Count == 8 && TicketingGuestPolicy.Categories.Values.All(TicketingPolicy.Groups.Contains)
            && !TicketingGuestPolicy.Categories.Values.Contains("Legal & Law Enforcement") && !TicketingGuestPolicy.Categories.Values.Contains("Trust & Safety — Restricted"),
            "public categories route only to known, non-restricted queues");
        Check(TicketingGuestPolicy.TokenShape(TicketingGuestPolicy.Token()) && !TicketingGuestPolicy.TokenShape("short"), "grant shape");

        IConfiguration Config(string? secret) => new ConfigurationBuilder().AddInMemoryCollection(secret is null ? [] : [new("Ticketing:Guest:Turnstile:Secret", secret)]).Build();
        var unused = new Fake(HttpStatusCode.OK, "{}");
        Check(await new GuestBotCheck(Config(null), new Clients(unused)).PassesAsync(null, null, default) && unused.Calls == 0, "no bot secret: no check made and none claimed");
        var yes = new Fake(HttpStatusCode.OK, "{\"success\":true}");
        Check(await new GuestBotCheck(Config("fictional-secret"), new Clients(yes)).PassesAsync("token", "203.0.113.1", default) && yes.Calls == 1, "configured: verifier success passes");
        var no = new Fake(HttpStatusCode.OK, "{\"success\":false}");
        Check(!await new GuestBotCheck(Config("fictional-secret"), new Clients(no)).PassesAsync("token", null, default), "configured: verifier refusal fails");
        Check(!await new GuestBotCheck(Config("fictional-secret"), new Clients(yes)).PassesAsync(null, null, default), "configured: missing token fails");
        var down = new Fake(HttpStatusCode.BadGateway, "");
        Check(!await new GuestBotCheck(Config("fictional-secret"), new Clients(down)).PassesAsync("token", null, default), "configured: unreachable verifier fails closed");
        // Issue 453: every ticket-route code has its own wording; none falls back to the generic 403 text.
        const string generic = "This request isn't available.";
        foreach (var problemCode in new[] { "mfa-elevation-required", "stale-session", "ticket-unavailable", "ticket-staff-actor-invalid", "ticket-actor-mismatch", "ticket-customer-unavailable", "ticket-customer-inactive", "ticket-customer-role-invalid" })
            Check(SupportTicketText.Problem(403, problemCode, staff: true) != generic && SupportTicketText.Problem(403, problemCode, staff: true) != "Something went wrong. Try again.", "explicit staff wording for " + problemCode);
        Check(SupportTicketText.Problem(403, "mfa-elevation-required", staff: true).Contains("MFA", StringComparison.Ordinal) && AdminHost::Swoovr.Admin.AdminText.NeedsSignIn(403, "mfa-elevation-required"), "expired MFA sign-in names MFA and offers Sign in again");
        Check(SupportTicketText.Problem(404, "ticket-unavailable") == generic && !SupportTicketText.Problem(403, "ticket-staff-actor-invalid").Contains("role", StringComparison.Ordinal), "members keep their wording and never see staff detail");
        // Issue 454: the date of birth template never contains a date; Create ticket says why when it can't be used.
        Check(!SupportTicketText.DobTicketTitle.Any(char.IsDigit) && !SupportTicketText.DobTicketBody.Any(char.IsDigit) && Swoovr.Contracts.TicketTopics.Known("dob") && !Swoovr.Contracts.TicketTopics.Known("other") && !Swoovr.Contracts.TicketTopics.Known(null), "date of birth ticket template has no dates; only known topics are kept");
        Check(SupportTicketText.CreateUnavailable(200, ["General Support"]) is null && SupportTicketText.CreateUnavailable(200, ["Security"]) is { } queue && queue.Contains("General Support", StringComparison.Ordinal)
            && SupportTicketText.CreateUnavailable(403, null) is not null && SupportTicketText.CreateUnavailable(503, null)!.Contains("helpdesk", StringComparison.Ordinal), "Create ticket shows a reason when the role or helpdesk can't create");
        Check(Swoovr.Api.Accounts.AccountInspectionEndpoints.HistoryActions.Contains("helpdesk.ticket.created"), "a staff-opened request appears in the member's account history");
        // Codex review on PR 461: a replayed create matches the ticket's existing entry (with or without a topic) and never another ticket's.
        Check(Swoovr.Api.Ticketing.TicketingRuntime.CreatedAuditDetail(12, "dob") == "ticket:12;topic:dob" && Swoovr.Api.Ticketing.TicketingRuntime.CreatedAuditDetail(12, "other") == Swoovr.Api.Ticketing.TicketingRuntime.CreatedAuditKey(12)
            && !Swoovr.Api.Ticketing.TicketingRuntime.CreatedAuditDetail(123, null).StartsWith(Swoovr.Api.Ticketing.TicketingRuntime.CreatedAuditKey(12) + ";", StringComparison.Ordinal), "the creation audit key identifies one ticket so a retry records it once");
        Check(SupportTicketText.ValidReference("SW-ABC234") && !SupportTicketText.ValidReference("SW-ABC23I") && SupportTicketText.NewLink("<x>") == "/me/support/new", "support links carry only valid report references");
        Console.WriteLine($"PASS: ticket guest code/grant policy, public categories and optional bot check ({n} checks; fake verifier, no Cloudflare request).");
    }
}
