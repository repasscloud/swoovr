using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Mail;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Swoovr.Infrastructure.Email;

/// <summary>Postmark settings (#357, D368). Resolved runtime secrets only; ToString never exposes them.</summary>
public sealed partial class PostmarkEmailOptions
{
    internal string ServerToken { get; }
    internal MailAddress From { get; }
    public string MessageStream { get; }
    public TimeSpan Timeout { get; }

    public PostmarkEmailOptions(string serverToken, string from, string messageStream = "outbound", TimeSpan? timeout = null)
    {
        if (string.IsNullOrWhiteSpace(serverToken) || serverToken.Length > 256 || serverToken.Any(char.IsWhiteSpace) || serverToken.Any(char.IsControl))
            throw new ArgumentException("Postmark server token must be configured without whitespace or control characters.");
        if (!MailerSendOptions.ValidAddress(from) || !MailAddress.TryCreate(from, out var address))
            throw new ArgumentException("Postmark sender must be a configured mailbox.");
        if (!Stream().IsMatch(messageStream ?? ""))
            throw new ArgumentException("Postmark message stream must be a stream ID (lower-case letters, digits and '-').");
        Timeout = timeout ?? TimeSpan.FromSeconds(15);
        if (Timeout <= TimeSpan.Zero || Timeout > TimeSpan.FromSeconds(60))
            throw new ArgumentException("Postmark timeout must be positive and at most 60 seconds.");
        ServerToken = serverToken;
        From = address;
        MessageStream = messageStream!;
    }

    public override string ToString() => "PostmarkEmailOptions(redacted)";

    /// <summary>No environment/file access; the caller has already selected Email:Provider=postmark.</summary>
    public static PostmarkEmailOptions ReadConfiguration(Func<string, string?> read)
    {
        var seconds = 15;
        if (read("Email:Postmark:TimeoutSeconds") is { } raw && (!int.TryParse(raw, out seconds) || seconds is < 1 or > 60))
            throw new InvalidOperationException("Email:Postmark:TimeoutSeconds must be 1–60.");
        var stream = read("Email:Postmark:MessageStream");
        return new PostmarkEmailOptions(read("Email:Postmark:ServerToken") ?? "", read("Email:From") ?? "",
            string.IsNullOrWhiteSpace(stream) ? "outbound" : stream, TimeSpan.FromSeconds(seconds));
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,99}$")]
    private static partial Regex Stream();
}

/// <summary>
/// Postmark Email API: POST https://api.postmarkapp.com/email with the X-Postmark-Server-Token header. HTTP 200 with
/// ErrorCode 0 and a MessageID is provider acceptance, never delivery. Open and link tracking are off: these are account,
/// sign-in and safety emails, and their links must not be rewritten. Single attempt; use a dedicated client with
/// redirects/retries/loggers disabled. Swoovr's messages are plain text, so the HTML part is the escaped text.
/// </summary>
public sealed class PostmarkEmailSender(HttpClient http, PostmarkEmailOptions options) : IEmailSender
{
    public const string Kind = "postmark";
    internal static readonly Uri Endpoint = new("https://api.postmarkapp.com/email");
    private const int ResponseLimit = 8192;

    public async Task<bool> SendAsync(EmailMessage message, CancellationToken cancellationToken) =>
        (await SendOutcomeAsync(message, cancellationToken)).Status == "accepted";

    public async Task<EmailOutcome> SendOutcomeAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        if (!MailerSendOptions.ValidAddress(message.To) || message.Subject is null || message.Text is null
            || message.Subject.Length > 998 || message.Subject.Any(char.IsControl) || message.Text.Length > 65536 || message.Html?.Length > 131072)
            return new("rejected", "invalid-message");
        if (cancellationToken.IsCancellationRequested) return new("retry", "cancelled-before-send");
        var recipient = new MailAddress(message.To);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(options.Timeout);
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
        request.Headers.Add("X-Postmark-Server-Token", options.ServerToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Content = JsonContent.Create(new Dictionary<string, object>
        {
            ["From"] = new MailAddress(options.From.Address, string.IsNullOrWhiteSpace(options.From.DisplayName) ? "Swoovr" : options.From.DisplayName).ToString(),
            ["To"] = recipient.Address,
            ["Subject"] = message.Subject,
            ["TextBody"] = message.Text,
            ["HtmlBody"] = message.Html ?? TwilioEmailSender.Html(message.Text),
            ["MessageStream"] = options.MessageStream,
            ["TrackOpens"] = false,
            ["TrackLinks"] = "None"
        });
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (response.StatusCode == HttpStatusCode.OK)
                return await ReplyAsync(response.Content, deadline.Token) is (0, true) ? new("accepted", "provider-accepted") : new("unknown", "missing-acknowledgement");
            if (response.StatusCode == HttpStatusCode.UnprocessableEntity)
                return (await ReplyAsync(response.Content, deadline.Token)).Code switch
                {
                    // Postmark API error codes: 300 invalid email, 406 inactive (bounced/complained/suppressed) recipient.
                    300 => new("rejected", "invalid-recipient"),
                    406 => new("rejected", "inactive-recipient"),
                    _ => new("rejected", "provider-refused")
                };
            return response.StatusCode switch
            {
                HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound
                    or HttpStatusCode.MethodNotAllowed or HttpStatusCode.RequestEntityTooLarge => new("rejected", "provider-refused"),
                // EmailOutcome cannot carry Retry-After, so no blind outbox resend on a shorter schedule.
                HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable => new("unknown", "rate-limited-review"),
                _ => new("unknown", "unconfirmed")
            };
        }
        catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException or JsonException)
        {
            // Exception text, provider body, destination, content and the token are never logged or returned.
            return new("unknown", "unconfirmed");
        }
    }

    /// <summary>Bounded read of Postmark's reply: (ErrorCode or -1, whether a well-formed MessageID is present).</summary>
    private static async Task<(int Code, bool HasId)> ReplyAsync(HttpContent content, CancellationToken token)
    {
        if (content.Headers.ContentLength > ResponseLimit) return (-1, false);
        await using var stream = await content.ReadAsStreamAsync(token);
        var bytes = new byte[ResponseLimit + 1];
        var count = 0;
        while (count < bytes.Length)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(count, bytes.Length - count), token);
            if (read == 0) break;
            count += read;
        }
        if (count == 0 || count > ResponseLimit) return (-1, false);
        JsonDocument document;
        try { document = JsonDocument.Parse(bytes.AsMemory(0, count)); }
        catch (JsonException) { return (-1, false); } // A malformed reply carries no code: a 422 stays rejected, a 200 unknown.
        using var owned = document;
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return (-1, false);
        var code = root.TryGetProperty("ErrorCode", out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : -1;
        var hasId = root.TryGetProperty("MessageID", out var id) && id.ValueKind == JsonValueKind.String
            && id.GetString() is { Length: > 0 and <= 128 } text && text.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');
        return (code, hasId);
    }
}
