using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Swoovr.Api;
using System.Net;
using System.Text;
using System.Text.Json;
using Swoovr.Infrastructure.Email;

/// <summary>#357: Postmark Email API adapter against a fake HTTP handler, plus provider selection.
/// Fictional values only; no outbound request.</summary>
static class PostmarkEmailChecks
{
    const string Ok="{\"To\":\"fictional@example.test\",\"SubmittedAt\":\"2026-11-18T00:00:00Z\",\"MessageID\":\"0a129aee-e1cd-480d-b08d-4f48548ff48d\",\"ErrorCode\":0,\"Message\":\"OK\"}";

    public static async Task Run()
    {
        var count=0;void Check(string name,bool ok){if(!ok)throw new Exception(name);count++;}
        var message=new EmailMessage("fictional@example.test","Fictional subject","Line <one> & more\nsecond line\n\nNew paragraph");
        var options=new PostmarkEmailOptions("fictional-server-token-never-log","Swoovr <sender@example.test>");
        var calls=0;
        using(var client=new HttpClient(new Fake(async(request,token)=>
        {
            calls++;
            Check("Postmark canonical HTTPS endpoint",request.Method==HttpMethod.Post&&request.RequestUri!.AbsoluteUri=="https://api.postmarkapp.com/email");
            Check("Postmark server token header only",request.Headers.GetValues("X-Postmark-Server-Token").Single()=="fictional-server-token-never-log"&&request.Headers.Authorization is null);
            Check("Postmark JSON media type",request.Content!.Headers.ContentType?.MediaType=="application/json");
            using var payload=JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));var root=payload.RootElement;
            Check("Postmark sender with display name",root.GetProperty("From").GetString()=="\"Swoovr\" <sender@example.test>");
            Check("Postmark one recipient",root.GetProperty("To").GetString()==message.To);
            Check("Postmark subject and text",root.GetProperty("Subject").GetString()==message.Subject&&root.GetProperty("TextBody").GetString()==message.Text);
            Check("Postmark HTML is the escaped text",root.GetProperty("HtmlBody").GetString()=="<!doctype html><html><body><p>Line &lt;one&gt; &amp; more<br>second line</p><p>New paragraph</p></body></html>");
            Check("Postmark transactional stream",root.GetProperty("MessageStream").GetString()=="outbound");
            Check("Postmark open tracking off",root.GetProperty("TrackOpens").ValueKind==JsonValueKind.False);
            Check("Postmark links never rewritten",root.GetProperty("TrackLinks").GetString()=="None");
            return Response(200,Ok);
        })))
        {
            var outcome=await new PostmarkEmailSender(client,options).SendOutcomeAsync(message,default);
            Check("Postmark 200 with ErrorCode 0 and MessageID is acceptance only",outcome.Status=="accepted"&&outcome.Code=="provider-accepted");
            Check("Postmark sends exactly once",calls==1);
        }
        Check("Postmark options redact runtime material",options.ToString()=="PostmarkEmailOptions(redacted)"&&!options.ToString().Contains("token"));
        using(var client=new HttpClient(new Fake(async(request,token)=>
        {
            using var payload=JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            Check("Postmark sender without a name is sent as Swoovr",payload.RootElement.GetProperty("From").GetString()=="\"Swoovr\" <sender@example.test>");
            Check("Postmark configured stream is used",payload.RootElement.GetProperty("MessageStream").GetString()=="transactional-2");
            return Response(200,Ok);
        })))
            await new PostmarkEmailSender(client,new PostmarkEmailOptions("token","sender@example.test","transactional-2")).SendOutcomeAsync(message,default);
        foreach(var (status,expected) in new[]{(201,"unknown"),(202,"unknown"),(204,"unknown"),(302,"unknown"),(400,"rejected"),(401,"rejected"),(403,"rejected"),(404,"rejected"),(413,"rejected"),(422,"rejected"),(429,"unknown"),(500,"unknown"),(502,"unknown"),(503,"unknown")})
        {
            var attempts=0;
            using var http=new HttpClient(new Fake((_,_)=>{attempts++;return Task.FromResult(Response(status,"provider-private-error-"+message.To));}));
            var result=await new PostmarkEmailSender(http,options).SendOutcomeAsync(message,default);
            Check("Postmark explicit outcome for HTTP "+status,result.Status==expected&&attempts==1);
            Check("Postmark fixed codes never reflect the destination",!result.ToString().Contains(message.To,StringComparison.Ordinal));
        }
        foreach(var (body,code) in new[]{("{\"ErrorCode\":300,\"Message\":\"Invalid email\"}","invalid-recipient"),("{\"ErrorCode\":406,\"Message\":\"Inactive recipient fictional@example.test\"}","inactive-recipient"),("{\"ErrorCode\":10,\"Message\":\"Bad token\"}","provider-refused"),("{\"ErrorCode\":412}","provider-refused")})
        {
            using var http=new HttpClient(new Fake((_,_)=>Task.FromResult(Response(422,body))));
            var result=await new PostmarkEmailSender(http,options).SendOutcomeAsync(message,default);
            Check("Postmark 422 ErrorCode is a fixed rejection code",result.Status=="rejected"&&result.Code==code&&!result.ToString().Contains(message.To,StringComparison.Ordinal));
        }
        foreach(var body in new[]{"","not-json","[]","{}","{\"ErrorCode\":0}","{\"ErrorCode\":0,\"MessageID\":\"\"}","{\"ErrorCode\":0,\"MessageID\":\"bad id\"}","{\"ErrorCode\":\"0\",\"MessageID\":\"a1\"}","{\"ErrorCode\":406,\"MessageID\":\"a1\"}","{\"ErrorCode\":0,\"MessageID\":42}"})
        {
            using var http=new HttpClient(new Fake((_,_)=>Task.FromResult(Response(200,body))));
            Check("Postmark 200 without ErrorCode 0 and a valid MessageID stays unknown",(await new PostmarkEmailSender(http,options).SendOutcomeAsync(message,default)).Status=="unknown");
        }
        using(var http=new HttpClient(new Fake((_,_)=>Task.FromResult(Response(200,"{\"ErrorCode\":0,\"MessageID\":\""+new string('a',9000)+"\"}")))))
            Check("Postmark oversized acknowledgement stays unknown",(await new PostmarkEmailSender(http,options).SendOutcomeAsync(message,default)).Status=="unknown");
        using(var http=new HttpClient(new Fake((_,_)=>throw new HttpRequestException("fictional private failure "+message.To))))
            Check("Postmark transport failure is unknown, never retried blindly",(await new PostmarkEmailSender(http,options).SendOutcomeAsync(message,default)).Status=="unknown");
        using(var http=new HttpClient(new Fake((_,_)=>throw new Exception("must not be called"))))
        {
            Check("Postmark invalid recipient refused before HTTP",(await new PostmarkEmailSender(http,options).SendOutcomeAsync(message with{To="not an address"},default)).Status=="rejected");
            Check("Postmark control characters in subject refused before HTTP",(await new PostmarkEmailSender(http,options).SendOutcomeAsync(message with{Subject="a\nb"},default)).Status=="rejected");
        }
        foreach(var (token,stream) in new[]{("","outbound"),("tok en","outbound"),("token",""),("token","Outbound"),("token","out bound"),("token","-x")})
        {var refused=false;try{_=new PostmarkEmailOptions(token,"sender@example.test",stream);}catch(ArgumentException){refused=true;}Check("Postmark unsafe token or stream refused",refused);}

        IConfiguration Config(Dictionary<string,string?> values)=>new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        IEmailSender Resolve(Dictionary<string,string?> values)
        {var services=new ServiceCollection();services.AddLogging();services.AddSwoovrApplicationEmail(Config(values),false,"Uat");return services.BuildServiceProvider().GetRequiredService<IEmailSender>();}
        bool Refused(Dictionary<string,string?> values)
        {try{Resolve(values);return false;}catch(Exception error)when(error is ArgumentException or InvalidOperationException){return true;}}
        var postmark=new Dictionary<string,string?>{["Email:Provider"]="postmark",["Email:Postmark:ServerToken"]="fictional",["Email:From"]="Swoovr <sender@example.test>"};
        Check("Hosted Postmark resolves the Postmark adapter",Resolve(postmark) is PostmarkEmailSender);
        Check("Postmark empty stream falls back to outbound",Resolve(new(postmark){["Email:Postmark:MessageStream"]=""}) is PostmarkEmailSender);
        Check("Postmark invalid stream refuses startup",Refused(new(postmark){["Email:Postmark:MessageStream"]="Bad Stream"}));
        postmark.Remove("Email:Postmark:ServerToken");Check("Postmark without its token refuses startup",Refused(postmark));
        Console.WriteLine($"PASS Postmark email: {count} fake HTTP/DI checks; no outbound request or delivery claim.");
    }

    private static HttpResponseMessage Response(int status,string body="")=>new((HttpStatusCode)status){Content=new StringContent(body,Encoding.UTF8,"application/json")};
    private sealed class Fake(Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> send):HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken)=>send(request,cancellationToken);
    }
}
