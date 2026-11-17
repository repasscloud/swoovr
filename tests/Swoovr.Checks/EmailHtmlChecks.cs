using System.Net;
using System.Text;
using Swoovr.Api;
using Swoovr.Api.Accounts;
using Swoovr.Api.Moderation;
using Swoovr.Api.Notifications;
using Swoovr.Api.Safety;
using Swoovr.Domain;
using Swoovr.Infrastructure.Email;

/// <summary>Issue 355: the approved branded email layout for the API's emails. Fictional values only; no request.
/// With SWOOVR_EMAIL_RENDER_DIR set, every email is also written there as HTML for a visual check.</summary>
static class EmailHtmlChecks
{
    public static async Task Run()
    {
        var count = 0; void Check(string name, bool ok) { if (!ok) throw new Exception(name); count++; }
        var branding = EmailBranding.Read(key => key switch
        {
            "Email:Branding:LogoUrl" => "https://uat.example.test/email/swoovr-logo.png",
            "Email:Branding:MemberUrl" => "https://uat.example.test/",
            "Email:Branding:AdminUrl" => "https://admin-uat.example.test/",
            _ => null
        });
        Check("Branding reads the three addresses", branding.LogoUrl?.AbsoluteUri == "https://uat.example.test/email/swoovr-logo.png"
            && branding.Member("/account")?.AbsoluteUri == "https://uat.example.test/account" && branding.Admin("/security")?.AbsoluteUri == "https://admin-uat.example.test/security");
        foreach (var unsafeUrl in new[] { "http://uat.example.test/", "javascript:alert(1)", "https://user:pw@uat.example.test/", "https://uat.example.test/?a=1", "https://uat.example.test/#x", "not a url", "" })
            Check("Unsafe branding address ignored: " + unsafeUrl, EmailBranding.Read(_ => unsafeUrl) == EmailBranding.None);
        Check("Loopback HTTP allowed for local development", EmailBranding.Read(key => key == "Email:Branding:MemberUrl" ? "http://localhost:5081/" : null).Member("/")?.AbsoluteUri == "http://localhost:5081/");

        // Every piece of text is encoded: no markup from data reaches the email.
        var hostile = "<script>alert(1)</script> & \"quotes\"";
        var page = EmailHtml.Page(branding, hostile, hostile, EmailHtml.P(hostile) + EmailHtml.Note(hostile) + EmailHtml.Facts(("Case", hostile)) + EmailHtml.Code(hostile), hostile);
        Check("Text is HTML-encoded everywhere", !page.Contains("<script>", StringComparison.Ordinal) && page.Contains("&lt;script&gt;", StringComparison.Ordinal));
        Check("Only **bold** becomes markup", EmailHtml.P("a **b** <i>c</i>").Contains("a <strong>b</strong> &lt;i&gt;c&lt;/i&gt;", StringComparison.Ordinal));
        Check("A button without a link is left out", EmailHtml.Button("Open", null) == "" && EmailHtml.LinkLine("Withdraw:", null) == "");
        Check("Logo image when configured", page.Contains("src=\"https://uat.example.test/email/swoovr-logo.png\"", StringComparison.Ordinal));
        var plain = EmailHtml.Page(EmailBranding.None, "p", "t", "", "f");
        Check("Text wordmark when no logo is configured", !plain.Contains("<img", StringComparison.Ordinal) && plain.Contains(">swoovr</span>", StringComparison.Ordinal));
        Check("Footer: company link and Help on its own line", page.Contains("<a href=\"https://repasscloud.com\"", StringComparison.Ordinal) && page.Contains("</a>.<br>Help:", StringComparison.Ordinal));

        var emails = new Dictionary<string, string>
        {
            ["05-beta-invitation"] = BetaInvitationDelivery.InvitationHtml(branding, new Uri("https://uat.example.test/")),
            ["06-founder-code"] = Founders.CodeHtml(branding, "SWV-7Q4K-2M9X"),
            ["07-something-new"] = Notifier.NotificationHtml(branding, NotificationKind.Request),
            ["08-account-notice"] = ModerationEndpoints.NoticeHtml(branding),
            ["09-security-changed"] = MfaEndpoints.SecurityChangedHtml(branding, "Multi-factor authentication was disabled on your Swoovr account. If this wasn't you, contact Swoovr support straight away."),
            ["10-trusted-contact-invite"] = MeetingSafetyDelivery.InviteHtml(branding, "Sam", new Uri("https://uat.example.test/safety/contact/confirm-token"), new Uri("https://uat.example.test/safety/contact/manage-token")),
            ["11-meeting-safety-update"] = MeetingSafetyDelivery.UpdateHtml(branding, "Sam", "missed", "A check-in is missed/unconfirmed. This is not proof of an emergency.", new Uri("https://uat.example.test/safety/meeting/link-token"), new Uri("https://uat.example.test/safety/contact/link-token")),
            ["12-staff-urgent-report"] = ModerationEndpoints.EscalationHtml(branding, true, Guid.Parse("3f2a9c1e-5b7d-4e8a-9c2f-1a2b3c4d5e6f"), "threats-of-violence", "TrustSafety"),
            ["13-staff-security-alert"] = Swoovr.Api.Security.SecurityAlerts.SecurityAlertHtml(branding, Guid.Parse("9b1c7d2e-4f6a-4b8c-8d9e-0f1a2b3c4d5e"), "High", "privileged-access-failures", null),
        };
        Check("Notification email never names a person", !Notifier.NotificationHtml(branding, NotificationKind.Request).Contains("Sam", StringComparison.Ordinal));
        Check("Account notice links to Me", emails["08-account-notice"].Contains("https://uat.example.test/account", StringComparison.Ordinal));
        Check("MFA disabled shows the warning box once", emails["09-security-changed"].Contains("<strong>If this wasn&#39;t you</strong>", StringComparison.Ordinal)
            && !emails["09-security-changed"].Contains("contact Swoovr support straight away.", StringComparison.Ordinal));
        Check("MFA requirement change has no warning box", !MfaEndpoints.SecurityChangedHtml(branding, "The multi-factor authentication requirement on your Swoovr account was changed.").Contains("If this wasn", StringComparison.Ordinal));
        Check("Staff escalation deep-links the case", emails["12-staff-urgent-report"].Contains("https://admin-uat.example.test/cases/3f2a9c1e-5b7d-4e8a-9c2f-1a2b3c4d5e6f", StringComparison.Ordinal));
        Check("Security alert links Security incidents", emails["13-staff-security-alert"].Contains("https://admin-uat.example.test/security", StringComparison.Ordinal));
        // PR 368 review: each update kind gets its own heading, with the member's name in front.
        foreach (var (kind, title) in new[] { ("missed", "Sam: a check-in is missed"), ("help", "Sam: asked for help"), ("closed", "Sam: monitoring has ended"), ("shared", "Sam: shared a meeting safety plan") })
            Check("Meeting safety heading for " + kind, MeetingSafetyDelivery.UpdateHtml(branding, "Sam", kind, "status", new Uri("https://uat.example.test/m"), new Uri("https://uat.example.test/w")).Contains(">" + title + "</h1>", StringComparison.Ordinal));
        Check("Meeting safety update names 000", emails["11-meeting-safety-update"].Contains("(000 in Australia)", StringComparison.Ordinal));
        Check("Staff escalation without admin address has no button", !ModerationEndpoints.EscalationHtml(EmailBranding.None, false, Guid.Empty, "c", "q").Contains("Open Swoovr Admin", StringComparison.Ordinal));
        foreach (var (name, html) in emails)
            Check("Every email is a full branded page: " + name, html.StartsWith("<!doctype html>", StringComparison.Ordinal) && html.Contains("swoovr.com/support", StringComparison.Ordinal) && html.Length < 131072);

        // Senders send the branded HTML when there is one, and the escaped text otherwise.
        var message = new EmailMessage("fictional@example.test", "Fictional subject", "Plain text", "<p>Branded</p>");
        string? sentHtml = null;
        using (var client = new HttpClient(new Fake(async (request, token) =>
        {
            using var json = System.Text.Json.JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            sentHtml = json.RootElement.GetProperty("HtmlBody").GetString();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"ErrorCode\":0,\"MessageID\":\"a1\"}", Encoding.UTF8, "application/json") };
        })))
        {
            await new PostmarkEmailSender(client, new PostmarkEmailOptions("token", "sender@example.test")).SendOutcomeAsync(message, default);
            Check("Postmark sends the branded HTML", sentHtml == "<p>Branded</p>");
            await new PostmarkEmailSender(client, new PostmarkEmailOptions("token", "sender@example.test")).SendOutcomeAsync(message with { Html = null }, default);
            Check("Postmark falls back to the escaped text", sentHtml == "<!doctype html><html><body><p>Plain text</p></body></html>");
        }
        using (var http = new HttpClient(new Fake((_, _) => throw new Exception("must not be called"))))
            Check("Oversized HTML refused before HTTP", (await new PostmarkEmailSender(http, new PostmarkEmailOptions("token", "sender@example.test")).SendOutcomeAsync(message with { Html = new string('a', 131073) }, default)).Status == "rejected");

        if (Environment.GetEnvironmentVariable("SWOOVR_EMAIL_RENDER_DIR") is { Length: > 0 } dir)
        {
            Directory.CreateDirectory(dir);
            foreach (var (name, html) in emails) File.WriteAllText(Path.Combine(dir, name + ".html"), html);
        }
        Console.WriteLine($"PASS branded email HTML (issue 355): {count} checks; no outbound request.");
    }

    private sealed class Fake(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
