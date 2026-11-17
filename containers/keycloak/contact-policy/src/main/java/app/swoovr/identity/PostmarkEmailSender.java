package app.swoovr.identity;

import com.fasterxml.jackson.databind.ObjectMapper;
import jakarta.mail.internet.InternetAddress;
import org.keycloak.email.EmailException;
import org.keycloak.email.EmailSenderProvider;
import java.net.URI;
import java.net.http.*;
import java.time.Duration;
import java.util.*;
import java.util.concurrent.*;

/**
 * Postmark Email API (#357, D368): POST https://api.postmarkapp.com/email with the X-Postmark-Server-Token header.
 * Exactly HTTP 200 with ErrorCode 0 and one bounded MessageID is provider acceptance, never mailbox delivery.
 * Open and link tracking are off (account, sign-in and safety email; links must not be rewritten). Single attempt,
 * no private logging.
 */
public final class PostmarkEmailSender implements EmailSenderProvider {
    static final URI ENDPOINT=URI.create("https://api.postmarkapp.com/email"); // External provider contract.
    private final String token,from,name,stream;
    private final HttpClient client;
    private final URI endpoint;
    private static final ObjectMapper JSON=new ObjectMapper();
    PostmarkEmailSender(String token,String stream,String from,String name,HttpClient client,URI endpoint){
        if(!credential(token)||stream==null||!stream.matches("[a-z0-9][a-z0-9-]{0,99}")||!TwilioEmailSender.address(from)
                ||name==null||name.isBlank()||name.length()>100||controls(name))throw new IllegalArgumentException("Incomplete email provider configuration.");
        this.token=token;this.stream=stream;this.from=from;this.name=name;this.client=client;this.endpoint=endpoint;
    }
    private static boolean credential(String value){return value!=null&&!value.isBlank()&&value.length()<=256&&!controls(value)&&value.chars().noneMatch(Character::isWhitespace);}
    private static boolean controls(String value){return value.codePoints().anyMatch(Character::isISOControl);}
    @Override public void validate(Map<String,String> ignored)throws EmailException{
        // Global SPI configuration. No SMTP substitution or test email side effect.
        if(!TwilioEmailSender.address(from))throw new EmailException("Email provider configuration unavailable.");
    }
    @Override public void send(Map<String,String> ignored,String recipient,String subject,String text,String html)throws EmailException{
        if(!TwilioEmailSender.address(recipient)||subject==null||subject.length()>998||controls(subject)||(text==null&&html==null)||(text!=null&&text.length()>65536)||(html!=null&&html.length()>131072))throw new EmailException("Email message is invalid.");
        CompletableFuture<HttpResponse<byte[]>> pending=null;
        try{
            var message=new LinkedHashMap<String,Object>();
            message.put("From",new InternetAddress(from,name,"UTF-8").toString());message.put("To",recipient);message.put("Subject",subject);
            if(html!=null)message.put("HtmlBody",html);if(text!=null)message.put("TextBody",text);
            message.put("MessageStream",stream);message.put("TrackOpens",false);message.put("TrackLinks","None");
            var request=HttpRequest.newBuilder(endpoint).timeout(Duration.ofSeconds(15)).header("X-Postmark-Server-Token",token).header("Accept","application/json").header("Content-Type","application/json").POST(HttpRequest.BodyPublishers.ofByteArray(JSON.writeValueAsBytes(message))).build();
            pending=client.sendAsync(request,info->new MailerSendEmailSender.LimitedBody());
            var response=pending.get(15,TimeUnit.SECONDS);
            if(response.statusCode()!=200||!accepted(response.body()))throw new EmailException("Email provider acknowledgement unavailable.");
        }catch(InterruptedException interrupted){Thread.currentThread().interrupt();throw new EmailException("Email provider acknowledgement unavailable.");}
        catch(EmailException expected){throw expected;}
        catch(Exception ignoredFailure){throw new EmailException("Email provider acknowledgement unavailable.");}
        finally{if(pending!=null&&!pending.isDone())pending.cancel(true);}
    }
    private static boolean accepted(byte[] body){
        try{var value=JSON.readTree(body);if(value==null)return false;var code=value.get("ErrorCode");var id=value.get("MessageID");
            return code!=null&&code.isIntegralNumber()&&code.asLong()==0&&id!=null&&id.isTextual()&&id.asText().matches("[A-Za-z0-9-]{1,128}");}
        catch(Exception ignored){return false;}
    }
    @Override public void close(){} // Factory owns and closes the shared client.
}
