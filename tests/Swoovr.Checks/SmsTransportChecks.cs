using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using Swoovr.Infrastructure.Messaging;
static class SmsTransportChecks
{
    public static async Task Run()
    {
        var count=0;var options=new TwilioOptions("ACfictional0000","fictional-private-token","+15005550006",new Uri("https://api.twilio.com/"));
        var sid="SM"+new string('a',32);string Ack(string state)=>"{\"sid\":\""+sid+"\",\"status\":\""+state+"\"}";
        async Task Check(HttpStatusCode status,string body,bool accepted)
        {
            var handler=new Handler(()=>new HttpResponseMessage(status){Content=new StringContent(body,Encoding.UTF8,"application/json")});var logger=new PrivateLogger();using var http=new HttpClient(handler);
            var actual=await new TwilioMessageSender(http,options,logger).SendCodeAsync("+15005550009","123456",default);
            if(actual!=accepted||handler.Calls!=1)throw new Exception("SMS acceptance/single-attempt contract failed: "+(int)status);
            if(logger.Messages.Any(m=>m.Contains("123456")||m.Contains("15005550009")||m.Contains("fictional-private-token")||m.Contains(sid)))throw new Exception("Private SMS detail reached logs.");count++;
        }
        // Unexpected 2xx statuses must not be accepted as a created message.
        foreach(var status in new[]{HttpStatusCode.OK,HttpStatusCode.Accepted,HttpStatusCode.NoContent,HttpStatusCode.Redirect,HttpStatusCode.BadRequest,HttpStatusCode.Unauthorized,HttpStatusCode.Forbidden,HttpStatusCode.TooManyRequests,HttpStatusCode.InternalServerError,HttpStatusCode.ServiceUnavailable})await Check(status,Ack("queued"),false);
        foreach(var state in new[]{"accepted","queued","sending","sent","delivered"})await Check(HttpStatusCode.Created,Ack(state),true);
        foreach(var body in new[]{"","{broken","[]","{}","{\"sid\":\"invalid\",\"status\":\"queued\"}","{\"sid\":\""+sid+"\"}",Ack("failed"),Ack("undelivered"),Ack("canceled"),new string('x',8193)})await Check(HttpStatusCode.Created,body,false);
        {var handler=new Handler(()=>throw new HttpRequestException("fictional-private-token 123456 +15005550009"));var logger=new PrivateLogger();using var http=new HttpClient(handler);if(await new TwilioMessageSender(http,options,logger).SendCodeAsync("+15005550009","123456",default)||handler.Calls!=1||logger.Messages.Any(m=>m.Contains("123456")||m.Contains("fictional-private-token")))throw new Exception("SMS unknown failure privacy/retry contract.");count++;}
        // Issue 315: a restricted API key authenticates instead of the Auth Token; the Account SID stays in the URL.
        foreach(var (keySid,keySecret,token,user,password) in new (string?,string?,string?,string,string)[]{("SK"+new string('b',32),"fictional-key-secret",null,"SK"+new string('b',32),"fictional-key-secret"),("SK"+new string('b',32),"fictional-key-secret","fictional-private-token","SK"+new string('b',32),"fictional-key-secret"),(null,null,"fictional-private-token","ACfictional0000","fictional-private-token")})
        {
            var keyed=new TwilioOptions("ACfictional0000",token,"+15005550006",new Uri("https://api.twilio.com/"),keySid,keySecret);
            var seen=new SeenHandler(()=>new HttpResponseMessage(HttpStatusCode.Created){Content=new StringContent(Ack("queued"),Encoding.UTF8,"application/json")});using var http=new HttpClient(seen);
            if(!keyed.Configured||!await new TwilioMessageSender(http,keyed,new PrivateLogger()).SendCodeAsync("+15005550009","123456",default))throw new Exception("Twilio credential form refused: "+user[..2]);
            var basic=Encoding.ASCII.GetString(Convert.FromBase64String(seen.Authorization!.Parameter!));
            if(seen.Authorization.Scheme!="Basic"||basic!=user+":"+password||seen.Path!="/2010-04-01/Accounts/ACfictional0000/Messages.json")throw new Exception("Twilio basic auth or account URL wrong for "+user[..2]);count++;
        }
        foreach(var (keySid,keySecret) in new (string?,string?)[]{("SK"+new string('b',32),null),(null,"fictional-key-secret"),("AC"+new string('b',32),"fictional-key-secret"),("SK123","fictional-key-secret")})
        {
            var half=new TwilioOptions("ACfictional0000","fictional-private-token","+15005550006",new Uri("https://api.twilio.com/"),keySid,keySecret);
            var seen=new SeenHandler(()=>throw new Exception("no request expected"));using var http=new HttpClient(seen);
            if(half.Configured||await new TwilioMessageSender(http,half,new PrivateLogger()).SendCodeAsync("+15005550009","123456",default)||seen.Path is not null)throw new Exception("A half or invalid API key must not fall back to the Auth Token.");count++;
        }
        Console.WriteLine($"PASS Twilio acceptance transport: {count} fake HTTP/private-log checks; acceptance is not recipient delivery; no external request.");
    }
    private sealed class SeenHandler(Func<HttpResponseMessage> reply):HttpMessageHandler
    {public System.Net.Http.Headers.AuthenticationHeaderValue? Authorization;public string? Path;protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token){Authorization=request.Headers.Authorization;Path=request.RequestUri!.AbsolutePath;return Task.FromResult(reply());}}
    private sealed class Handler(Func<HttpResponseMessage> reply):HttpMessageHandler
    {public int Calls;protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token){Calls++;return Task.FromResult(reply());}}
    private sealed class PrivateLogger:ILogger<TwilioMessageSender>
    {
        public List<string> Messages{get;}=[];public IDisposable? BeginScope<TState>(TState state)where TState:notnull=>null;public bool IsEnabled(LogLevel level)=>true;
        public void Log<TState>(LogLevel level,EventId id,TState state,Exception? error,Func<TState,Exception?,string> formatter)=>Messages.Add(formatter(state,error));
    }
}
