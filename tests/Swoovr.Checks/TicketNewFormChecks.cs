extern alias AdminHost;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Swoovr.Client;
using Swoovr.Contracts;

/// <summary>
/// Issue 468: the real Admin /tickets/new page, rendered by the ASP.NET Core static SSR pipeline on loopback, with the admin
/// API replaced by a stub. Each entry point GETs the page, then POSTs the "create" form exactly as a browser would. A POST to
/// a form that isn't rendered is rejected by Blazor with 400 before the handler runs, which is what staff saw on UAT.
/// Proves the page's form dispatch and handler only: no Keycloak sign-in, admin layout, API or helpdesk is involved.
/// </summary>
static partial class TicketNewFormChecks
{
    public static async Task Run()
    {
        var count = 0;
        var member = Guid.Parse("6f1c2d3e-4a5b-4c6d-8e9f-0a1b2c3d4e5f");

        // 1. Account page: /tickets/new?account={id}
        {
            await using var host = await Host.StartAsync(member);
            var form = await host.CreateFormAsync("/tickets/new?account=" + member);
            var response = await host.PostAsync("/tickets/new?account=" + member, form.With("Title", "About your account").With("Body", "Hello from support."));
            Expect(response.StatusCode == HttpStatusCode.Redirect && response.Headers.Location?.OriginalString.EndsWith("/tickets/4242", StringComparison.Ordinal) == true,
                $"account prefill: create should redirect to /tickets/4242, got {(int)response.StatusCode} {response.Headers.Location} {await Text(response)}");
            var sent = host.Api.Created.Single();
            Expect(sent.AccountId == member && sent.Topic is null && sent.Ticket.Title == "About your account" && sent.Ticket.Body == "Hello from support."
                && sent.Ticket.OperationId == Guid.Parse(form["OpKey"]), "account prefill: the posted request was not sent as entered");
            count++;
        }

        // 2. Date of birth review: /tickets/new?account={id}&topic=dob, template sent as edited, topic kept.
        {
            await using var host = await Host.StartAsync(member);
            var form = await host.CreateFormAsync("/tickets/new?account=" + member + "&topic=dob");
            Expect(form["Topic"] == TicketTopics.DateOfBirth && form["Title"] == SupportTicketText.DobTicketTitle, "dob prefill: the date of birth template is missing from the form");
            var edited = SupportTicketText.DobTicketBody + "\n\nEdited by staff.";
            var response = await host.PostAsync("/tickets/new?account=" + member + "&topic=dob", form.With("Body", edited));
            Expect(response.StatusCode == HttpStatusCode.Redirect && response.Headers.Location?.OriginalString.EndsWith("/tickets/4242", StringComparison.Ordinal) == true,
                $"dob prefill: create should redirect to /tickets/4242, got {(int)response.StatusCode}");
            var sent = host.Api.Created.Single();
            Expect(sent.Topic == TicketTopics.DateOfBirth && sent.Ticket.Title == SupportTicketText.DobTicketTitle && sent.Ticket.Body == edited.Trim(), "dob prefill: the topic or edited template was not sent");
            count++;
        }

        // 3. Manual: Find the member, then create (no query string).
        {
            await using var host = await Host.StartAsync(member);
            var find = await host.FormAsync("/tickets/new", "find");
            var found = await host.PostAsync("/tickets/new", find.With("Query", member.ToString()));
            Expect(found.StatusCode == HttpStatusCode.OK, $"find: expected 200, got {(int)found.StatusCode}");
            var form = Form.Parse(await found.Content.ReadAsStringAsync(), "create");
            var response = await host.PostAsync("/tickets/new", form.With("Title", "A question").With("Body", "Found by account ID."));
            Expect(response.StatusCode == HttpStatusCode.Redirect && response.Headers.Location?.OriginalString.EndsWith("/tickets/4242", StringComparison.Ordinal) == true,
                $"find then create: create should redirect to /tickets/4242, got {(int)response.StatusCode}");
            Expect(host.Api.Created.Single().AccountId == member, "find then create: wrong member");
            count++;
        }

        // 4. An uncertain failure shows the in-page notice with the form still filled in, and "Send again" reuses the operation.
        {
            await using var host = await Host.StartAsync(member, createStatus: HttpStatusCode.ServiceUnavailable);
            var form = await host.CreateFormAsync("/tickets/new?account=" + member + "&topic=dob");
            var response = await host.PostAsync("/tickets/new?account=" + member + "&topic=dob", form.With("Title", "Kept title").With("Body", "Kept body"));
            Expect(response.StatusCode == HttpStatusCode.OK, $"failure: expected the page (200) with a notice, got {(int)response.StatusCode}");
            var html = await response.Content.ReadAsStringAsync();
            Expect(html.Contains("role=\"alert\"", StringComparison.Ordinal), "failure: no in-page notice");
            var again = Form.Parse(html, "create");
            Expect(again["Title"] == "Kept title" && again["Body"] == "Kept body" && again["Topic"] == TicketTopics.DateOfBirth && again["OpKey"] == form["OpKey"]
                && html.Contains("Send again", StringComparison.Ordinal), "failure: the form was not kept for an idempotent Send again");
            var retry = await host.PostAsync("/tickets/new?account=" + member + "&topic=dob", again);
            Expect(retry.StatusCode == HttpStatusCode.OK && host.Api.Created.Count == 2 && host.Api.Created.All(c => c.Ticket.OperationId == Guid.Parse(form["OpKey"])),
                "failure: Send again did not repeat the same operation");
            count++;
        }

        // 5. Expired MFA keeps the form and offers Sign in again, not a browser error page.
        {
            await using var host = await Host.StartAsync(member, createStatus: HttpStatusCode.Forbidden, createCode: "mfa-elevation-required");
            var form = await host.CreateFormAsync("/tickets/new?account=" + member);
            var response = await host.PostAsync("/tickets/new?account=" + member, form.With("Title", "T").With("Body", "B"));
            var html = await response.Content.ReadAsStringAsync();
            Expect(response.StatusCode == HttpStatusCode.OK && html.Contains("data-ticket-reauth", StringComparison.Ordinal) && Form.Parse(html, "create")["Title"] == "T",
                $"reauth: expected the notice with Sign in again and the form kept, got {(int)response.StatusCode}");
            count++;
        }

        Console.WriteLine($"PASS admin new support request (issue 468): {count} loopback SSR form checks (account, dob, find-then-create, uncertain retry, reauth); stub API, no Keycloak/helpdesk claim.");
    }

    static async Task<string> Text(HttpResponseMessage response) { var text = await response.Content.ReadAsStringAsync(); return text.Length > 300 ? text[..300] : text; }
    static void Expect(bool ok, string message) { if (!ok) throw new Exception("Admin new support request: " + message); }

    sealed class StubApi(Guid member, HttpStatusCode createStatus, string? createCode) : HttpMessageHandler
    {
        public List<TicketOnBehalf> Created { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Post && path == "/api/admin/tickets/customers")
            {
                var lookup = (await request.Content!.ReadFromJsonAsync<TicketCustomerLookup>(token))!;
                var known = lookup.AccountId == member || lookup.Query == member.ToString();
                return known ? Json(HttpStatusCode.OK, new TicketCustomerView(member, "mapped")) : Problem(HttpStatusCode.NotFound, "ticket-customer-not-found");
            }
            if (request.Method == HttpMethod.Post && path == "/api/admin/tickets")
            {
                Created.Add((await request.Content!.ReadFromJsonAsync<TicketOnBehalf>(token))!);
                return createStatus == HttpStatusCode.Created
                    ? Json(HttpStatusCode.Created, new TicketSummary(4242, "SR-4242", "t", 1, 2, DateTimeOffset.UnixEpoch))
                    : createCode is null ? new HttpResponseMessage(createStatus) : Problem(createStatus, createCode);
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
        static HttpResponseMessage Json(HttpStatusCode status, object value) => new(status) { Content = JsonContent.Create(value) };
        static HttpResponseMessage Problem(HttpStatusCode status, string code) => new(status)
        { Content = new StringContent(JsonSerializer.Serialize(new { code }), System.Text.Encoding.UTF8, "application/problem+json") };
    }

    /// <summary>Routes the Admin assembly's pages without the signed-in admin layout (which needs a staff session).</summary>
    sealed class PageRoot : ComponentBase
    {
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.AddMarkupContent(0, "<!DOCTYPE html><html><body>");
            builder.OpenComponent<Microsoft.AspNetCore.Components.Routing.Router>(1);
            builder.AddAttribute(2, "AppAssembly", typeof(AdminHost::Swoovr.Admin.AdminText).Assembly);
            builder.AddAttribute(3, "Found", (RenderFragment<RouteData>)(route => inner =>
            {
                inner.OpenComponent<RouteView>(0);
                inner.AddAttribute(1, "RouteData", route);
                inner.CloseComponent();
            }));
            builder.CloseComponent();
            builder.AddMarkupContent(4, "</body></html>");
        }
    }

    sealed class Host : IAsyncDisposable
    {
        public required WebApplication App { get; init; }
        public required HttpClient Http { get; init; }
        public required StubApi Api { get; init; }

        public static async Task<Host> StartAsync(Guid member, HttpStatusCode createStatus = HttpStatusCode.Created, string? createCode = null)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            var api = new StubApi(member, createStatus, createCode);
            builder.Services.AddRazorComponents();
            builder.Services.AddHttpContextAccessor();
            builder.Services.AddSingleton(new AdminClient(new HttpClient(api) { BaseAddress = new Uri("http://api.invalid/") }));
            var app = builder.Build();
            app.UseAntiforgery();
            app.MapRazorComponents<PageRoot>().AddAdditionalAssemblies(typeof(AdminHost::Swoovr.Admin.AdminText).Assembly);
            await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
            var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, CookieContainer = new CookieContainer() }) { BaseAddress = new Uri(address) };
            return new Host { App = app, Http = http, Api = api };
        }

        public async Task<Form> FormAsync(string path, string name)
        {
            var response = await Http.GetAsync(path);
            Expect(response.StatusCode == HttpStatusCode.OK, $"GET {path}: expected 200, got {(int)response.StatusCode}");
            return Form.Parse(await response.Content.ReadAsStringAsync(), name);
        }
        public Task<Form> CreateFormAsync(string path) => FormAsync(path, "create");
        public Task<HttpResponseMessage> PostAsync(string path, Form form) => Http.PostAsync(path, new FormUrlEncodedContent(form.Fields));
        public async ValueTask DisposeAsync() { Http.Dispose(); await App.StopAsync(); await App.DisposeAsync(); }
    }

    /// <summary>The named form's fields as a browser would post them (hidden inputs, inputs, textareas, Blazor's _handler).</summary>
    sealed partial class Form
    {
        public Dictionary<string, string> Fields { get; } = [];
        public string this[string name] => Fields.TryGetValue(name, out var value) ? value : "";
        public Form With(string name, string value) { Fields[name] = value; return this; }

        public static Form Parse(string html, string name)
        {
            var match = FormPattern().Match(html);
            while (match.Success && !match.Value.Contains("name=\"_handler\" value=\"" + name + "\"", StringComparison.Ordinal)) match = match.NextMatch();
            Expect(match.Success, $"the \"{name}\" form is not on the page");
            var form = new Form();
            foreach (Match input in InputPattern().Matches(match.Value))
            {
                var tag = input.Value;
                var field = Attribute(tag, "name");
                if (field is not null) form.Fields[field] = WebUtility.HtmlDecode(Attribute(tag, "value") ?? "");
            }
            foreach (Match area in TextAreaPattern().Matches(match.Value)) form.Fields[area.Groups[1].Value] = WebUtility.HtmlDecode(area.Groups[2].Value);
            return form;
        }
        static string? Attribute(string tag, string name) { var m = Regex.Match(tag, "\\s" + name + "=\"([^\"]*)\""); return m.Success ? m.Groups[1].Value : null; }
        [GeneratedRegex("<form\\b.*?</form>", RegexOptions.Singleline)] private static partial Regex FormPattern();
        [GeneratedRegex("<input\\b[^>]*>", RegexOptions.Singleline)] private static partial Regex InputPattern();
        [GeneratedRegex("<textarea\\b[^>]*\\sname=\"([^\"]+)\"[^>]*>(.*?)</textarea>", RegexOptions.Singleline)] private static partial Regex TextAreaPattern();
    }
}
