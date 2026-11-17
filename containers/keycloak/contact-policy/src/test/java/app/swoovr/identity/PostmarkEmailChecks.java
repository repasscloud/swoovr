package app.swoovr.identity;

import com.sun.net.httpserver.HttpServer;
import com.fasterxml.jackson.databind.ObjectMapper;
import org.keycloak.email.EmailException;
import java.net.*;
import java.net.http.HttpClient;
import java.nio.charset.StandardCharsets;
import java.util.*;
import java.util.concurrent.atomic.AtomicInteger;
import java.util.concurrent.atomic.AtomicReference;

/** Real Java Postmark adapter against a loopback HTTP fake (#357). No provider/mailbox/delivery claim. */
public final class PostmarkEmailChecks {
    private static int checks;
    private static void check(boolean ok,String message){if(!ok)throw new AssertionError(message);checks++;}
    private static final String OK="{\"To\":\"fictional.adult@example.test\",\"SubmittedAt\":\"2026-11-18T00:00:00Z\",\"MessageID\":\"0a129aee-e1cd-480d-b08d-4f48548ff48d\",\"ErrorCode\":0,\"Message\":\"OK\"}";
    public static void main(String[] args)throws Exception{
        var server=HttpServer.create(new InetSocketAddress("127.0.0.1",0),0);
        var handlerFailure=new AtomicReference<Throwable>();var calls=new AtomicInteger();var status=new AtomicInteger(200);
        var body=new String[]{OK};var hasText=new boolean[]{true};var hasHtml=new boolean[]{true};
        var mapper=new ObjectMapper();
        server.createContext("/email",exchange->{
            calls.incrementAndGet();
            try{
                check(exchange.getRequestMethod().equals("POST"),"POST contract");
                check("fictional-server-token".equals(exchange.getRequestHeaders().getFirst("X-Postmark-Server-Token")),"server token header");
                check(exchange.getRequestHeaders().getFirst("Authorization")==null,"no other credential header");
                var value=mapper.readTree(exchange.getRequestBody());
                check(value.path("From").asText().equals("Swoovr <no-reply@example.test>"),"fixed sender with display name");
                check(value.path("To").asText().equals("fictional.adult@example.test"),"one recipient");
                check(value.path("Subject").asText().equals("Verify your email"),"subject");
                check(value.has("TextBody")==hasText[0]&&value.has("HtmlBody")==hasHtml[0],"bodies only when supplied");
                check(value.path("MessageStream").asText().equals("outbound"),"transactional stream");
                check(value.path("TrackOpens").isBoolean()&&!value.path("TrackOpens").asBoolean(),"open tracking off");
                check(value.path("TrackLinks").asText().equals("None"),"links never rewritten");
                var bytes=body[0].getBytes(StandardCharsets.UTF_8);exchange.sendResponseHeaders(status.get(),bytes.length==0?-1:bytes.length);exchange.getResponseBody().write(bytes);
            }catch(Throwable error){handlerFailure.set(error);}finally{exchange.close();}
        });server.start();
        try(var client=HttpClient.newBuilder().followRedirects(HttpClient.Redirect.NEVER).build()){
            var endpoint=URI.create("http://127.0.0.1:"+server.getAddress().getPort()+"/email");
            var sender=new PostmarkEmailSender("fictional-server-token","outbound","no-reply@example.test","Swoovr",client,endpoint);
            sender.send(Map.of(),"fictional.adult@example.test","Verify your email","fictional-link","<a>fictional-link</a>");check(calls.get()==1,"single accepted send");
            hasHtml[0]=false;sender.send(Map.of(),"fictional.adult@example.test","Verify your email","fictional-link",null);checks++;
            hasHtml[0]=true;hasText[0]=false;sender.send(Map.of(),"fictional.adult@example.test","Verify your email",null,"<a>fictional-link</a>");checks++;
            hasText[0]=true;
            for(int code:new int[]{201,202,302,400,401,403,404,409,422,429,500,503}){status.set(code);int before=calls.get();refused(sender);check(calls.get()==before+1,"no automatic replay");}
            status.set(200);
            for(String reply:new String[]{"","not-json","{}","[]","{\"ErrorCode\":0}","{\"ErrorCode\":0,\"MessageID\":\"\"}","{\"ErrorCode\":0,\"MessageID\":\"bad id\"}",
                    "{\"ErrorCode\":\"0\",\"MessageID\":\"a1\"}","{\"ErrorCode\":406,\"MessageID\":\"a1\",\"Message\":\"Inactive recipient fictional.adult@example.test\"}",
                    "{\"ErrorCode\":300,\"MessageID\":\"a1\"}","x".repeat(8193)}){body[0]=reply;refused(sender);}
            body[0]=OK;
            int before=calls.get();
            try{sender.send(Map.of(),"invalid\r\naddress","subject","text","html");throw new AssertionError("invalid recipient accepted");}catch(EmailException expected){check(calls.get()==before,"invalid input refused before HTTP");}
            for(var bad:new String[][]{{"","outbound"},{"tok en","outbound"},{"token",""},{"token","Outbound"},{"token","out bound"},{"token","-x"}}){
                try{new PostmarkEmailSender(bad[0],bad[1],"no-reply@example.test","Swoovr",client,endpoint);throw new AssertionError("unsafe token or stream accepted");}catch(IllegalArgumentException expected){checks++;}
            }
            try{new PostmarkEmailSender("token","outbound","Swoovr <no-reply@example.test>","Swoovr",client,endpoint);throw new AssertionError("display name in sender accepted");}catch(IllegalArgumentException expected){checks++;}
        }finally{server.stop(0);}
        if(handlerFailure.get()!=null)throw new AssertionError("Loopback request contract assertion failed.",handlerFailure.get());
        System.out.println("PASS Keycloak Postmark email: "+checks+" loopback HTTP assertions; no real provider/mailbox/delivery evidence.");
    }
    private static void refused(PostmarkEmailSender sender)throws Exception{
        try{sender.send(Map.of(),"fictional.adult@example.test","Verify your email","fictional-link","<a>fictional-link</a>");throw new AssertionError("unconfirmed provider response accepted");}
        catch(EmailException expected){check(!expected.getMessage().contains("fictional")&&!expected.getMessage().contains("example.test"),"private failure text");}
    }
}
