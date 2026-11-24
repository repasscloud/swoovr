using System.Net;
using System.Text;
using Swoovr.Domain;
using Swoovr.Infrastructure.Ticketing;

internal static class ZammadTransportChecks
{
    public static async Task Run()
    {
        var handler = new Handler();
        using var http = new HttpClient(handler);
        var provider = new ZammadTicketingProvider(http, new Uri("http://localhost:8095/"), "private-token");
        handler.Body = "[]";
        await provider.SearchAsync("customer_id:42", 2, 25, default);
        if (handler.Path != "/api/v1/tickets/search?query=customer_id%3A42&page=2&per_page=25&sort_by=updated_at&order_by=desc") throw new Exception("bounded search and paging");
        handler.Status = HttpStatusCode.Forbidden;
        try { await provider.GetTicketAsync(42, default); throw new Exception("expected denial"); }
        catch (TicketingProviderException error) when (error.Code == "provider-forbidden" && !error.Uncertain) { }
        handler.Status = HttpStatusCode.ServiceUnavailable;
        try { await provider.ReplyAsync(42, "hello", false, false, default); throw new Exception("expected uncertainty"); }
        catch (TicketingProviderException error) when (error.Uncertain) { }
        if (handler.Calls != 3) throw new Exception("writes never automatically retried");
        handler.Status = HttpStatusCode.OK;
        handler.Body = "[{\"id\":1,\"ticket_id\":42,\"body\":\"secret\",\"content_type\":\"text/plain\",\"sender\":\"Agent\",\"created_at\":\"2026-11-04T00:00:00Z\",\"attachments\":[]}]";
        try { await provider.GetArticlesAsync(42, default); throw new Exception("missing visibility must fail closed"); }
        catch (TicketingProviderException error) when (error.Code == "provider-schema") { }
        try { await provider.ReplyAsync(42, "hello", false, false, default); throw new Exception("successful malformed write must be uncertain"); }
        catch (TicketingProviderException error) when (error.Uncertain) { }
        using var cancel = new CancellationTokenSource();
        handler.CancelAfterApply = cancel;
        try { await provider.ReplyAsync(42, "cancel after send", false, false, cancel.Token); throw new Exception("expected cancelled uncertainty"); }
        catch (TicketingProviderException error) when (error.Uncertain) { }
        handler.CancelAfterApply=null;
        handler.Body="{\"id\":42,\"login\":\"swoovr-customer\",\"active\":true,\"roles\":[\"Customer\"],\"group_ids\":{}}";
        using var memberHttp=new HttpClient(handler);
        var member=new ZammadTicketingProvider(memberHttp,new Uri("http://localhost:8095/"),"private-token",42);
        var actor=await member.ActorAsync(default);
        if(actor.Id!=42||handler.From!="42"||actor.Roles.Single()!="Customer")throw new Exception("server-bound actor and role facts");
        handler.Body="[]";
        await member.SearchAsync("customer_id:42",1,20,"title",false,default);
        if(!handler.Path!.EndsWith("sort_by=title&order_by=asc"))throw new Exception("explicit safe sorting");
        handler.Body="{\"id\":9,\"ticket_id\":42,\"body\":\"reply\",\"content_type\":\"text/plain\",\"internal\":false,\"sender\":\"Customer\",\"created_at\":\"2026-11-04T00:00:00Z\",\"created_by_id\":42,\"preferences\":{\"swoovr_operation\":\"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa\"},\"attachments\":[]}";
        var marked=await member.ReplyWithOperationAsync(42,"reply",false,true,[],"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",default);
        if(marked.OperationReference!="aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"||marked.AuthorId!=42)throw new Exception("article recovery marker and actor roundtrip");
        handler.Body=handler.Body.Replace("\"attachments\":[]", "\"attachments\":[{\"id\":7,\"filename\":\"ordinary.txt\",\"size\":\"8\",\"preferences\":{\"Mime-Type\":\"text/plain\"}}]");
        var attached=await member.ReplyWithOperationAsync(42,"reply",false,true,[],"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",default);
        if(attached.Attachments.Single().Size!=8||attached.Attachments.Single().ContentType!="text/plain")throw new Exception("actual Zammad attachment schema");
        handler.Body=handler.Body.Replace("\"preferences\":{\"swoovr_operation\":\"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa\"}","\"preferences\":{},\"message_id\":\"swoovr-operation-aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa@swoovr.invalid\"");
        var customerMarker=await member.ReplyWithOperationAsync(42,"reply",false,true,[],"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",default);
        if(customerMarker.OperationReference!="aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")throw new Exception("customer reply operation marker survives provider preference sanitization");
        handler.Body="[{\"id\":1,\"name\":\"close\",\"active\":true,\"perform\":{\"ticket.state_id\":{\"value\":4}}},{\"id\":2,\"name\":\"advanced\",\"active\":true,\"perform\":{\"ticket.tags\":{\"operator\":\"add\",\"value\":\"spam\"}}}]";
        var macros=await provider.MacrosAsync(default);
        if(!macros[0].Supported||macros[0].Change.StateId!=4||macros[1].Supported)throw new Exception("actual macro descriptors and unsupported actions fail closed");
        // D369: a staff reply on an emailed ticket is an email article to the sender, marked in preferences, not the Message-ID.
        handler.Body="{\"id\":10,\"ticket_id\":42,\"body\":\"reply\",\"content_type\":\"text/plain\",\"internal\":false,\"sender\":\"Agent\",\"type\":\"email\",\"from\":\"Swoovr Support <support@example.test>\",\"created_at\":\"2026-11-18T00:00:00Z\",\"created_by_id\":5,\"preferences\":{\"swoovr_operation\":\"bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb\"},\"attachments\":[]}";
        var emailed=await provider.ReplyWithOperationAsync(42,"reply",false,false,[],"bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",default,new SupportEmail("Fictional Sender <sender@example.test>","Billing question"));
        using(var sent=System.Text.Json.JsonDocument.Parse(handler.Sent!))
        {
            var r=sent.RootElement;
            if(r.GetProperty("type").GetString()!="email"||r.GetProperty("to").GetString()!="Fictional Sender <sender@example.test>"||r.GetProperty("subject").GetString()!="Billing question"
                ||r.GetProperty("sender").GetString()!="Agent"||r.GetProperty("internal").GetBoolean()||r.TryGetProperty("message_id",out _))throw new Exception("email reply payload");
        }
        if(emailed.OperationReference!="bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"||emailed.Type!="email")throw new Exception("email reply recovery marker");
        foreach(var (note,customer) in new[]{(true,false),(false,true)})
            try{await provider.ReplyWithOperationAsync(42,"reply",note,customer,[],"bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",default,new SupportEmail("sender@example.test","x"));throw new Exception("email only for public staff replies");}
            catch(ArgumentException){}
        await provider.ReplyWithOperationAsync(42,"reply",false,false,[],"bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",default);
        if(!handler.Sent!.Contains("\"type\":\"note\""))throw new Exception("no email instruction keeps an in-app note");
        // D369: which tickets are answered by email, and to which address.
        SupportArticle A(long id,string sender,string type,string? reply=null)=>new(id,42,"b","text/plain",false,sender,DateTimeOffset.UnixEpoch,[]){Type=type,ReplyAddress=reply};
        if(TicketingEmail.Recipient([A(1,"Customer","email","first@example.test"),A(2,"Agent","note"),A(3,"Customer","email","later@example.test")])!="later@example.test")throw new Exception("latest emailed address");
        if(TicketingEmail.Recipient([A(2,"Customer","email","x@example.test"),A(1,"Customer","note")])is not null)throw new Exception("app ticket stays in-app even after an email arrives");
        if(TicketingEmail.Recipient([A(1,"Customer","email","first@example.test"),A(3,"Customer","email","bad\u0007@example.test")])is not null
            ||TicketingEmail.Recipient([A(1,"Customer","email","first@example.test"),A(3,"Customer","email",null)])is not null)throw new Exception("an unusable latest address never falls back to an older sender");
        if(TicketingEmail.Recipient([A(1,"Customer","email","bad\r\nBcc: x@example.test")])is not null||!TicketingEmail.StartedByEmail([A(1,"Customer","email",null)]))throw new Exception("unsafe or missing address refused");
        // Issue 454: a request staff open for a member starts with a public message from support (Agent), so the member's
        // Support page shows it as from Swoovr and the event worker notifies them; a member's own request stays Customer.
        foreach (var (source, sender) in new[] { ("staff", "Agent"), ("member", "Customer") })
        {
            handler.Body = "{\"id\":77,\"number\":\"77\",\"title\":\"t\",\"customer_id\":42,\"group_id\":1,\"owner_id\":1,\"state_id\":1,\"priority_id\":2,\"updated_at\":\"2026-11-24T00:00:00Z\"}";
            await provider.CreateTicketAsync(new SupportTicketDraft("t", "b", 42, 1, "cccccccc-cccc-cccc-cccc-cccccccccccc", source), default);
            using var created = System.Text.Json.JsonDocument.Parse(handler.Sent!);
            var article = created.RootElement.GetProperty("article");
            if (article.GetProperty("sender").GetString() != sender || article.GetProperty("internal").GetBoolean()) throw new Exception("first article sender for " + source);
        }
        Console.WriteLine("PASS: Zammad transport paging, denial, uncertain writes without retry and required article visibility.");
    }

    private sealed class Handler : HttpMessageHandler
    {
        public string Body = "";
        public string? Path;
        public string? From;
        public string? Sent;
        public HttpStatusCode Status = HttpStatusCode.OK;
        public int Calls;
        public CancellationTokenSource? CancelAfterApply;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            From=request.Headers.TryGetValues("From",out var values)?values.Single():null;
            if (CancelAfterApply is { } cancellation) { cancellation.Cancel(); throw new OperationCanceledException(cancellationToken); }
            Path = request.RequestUri!.PathAndQuery;
            Sent = request.Content?.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();
            if (request.Headers.Authorization?.Scheme != "Token") throw new Exception("provider auth missing");
            return Task.FromResult(new HttpResponseMessage(Status) {Content = new StringContent(Body, Encoding.UTF8, "application/json")});
        }
    }
}
