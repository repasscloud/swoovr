using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Swoovr.Domain;

namespace Swoovr.Infrastructure.Verification;

/// <summary>`Didit:IdImages:Enabled` (off by default) and `Didit:IdImages:Hosts`, the exact image hosts; empty refuses every image.</summary>
public sealed record DiditImageOptions(bool Enabled, IReadOnlyCollection<string> Hosts)
{
    public static IReadOnlyCollection<string> Parse(string? hosts) => (hosts ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

/// <summary>
/// Issue 488 (D413): the document's front and back from a decision (docs.didit.me/reference/data-models#id-verification,
/// read 25 November 2026: short-lived signed links, host not documented). Its HTTP client follows no redirects and writes
/// no logs, because the links themselves open the image. Nothing is stored; only a refused host's name is logged.
/// </summary>
public sealed class DiditIdImages(HttpClient http, DiditOptions options, DiditImageOptions images, ILogger<DiditIdImages> log) : IIdImageSource
{
    public const int MaxBytes = 5 * 1024 * 1024;
    private const int MaxDocuments = 3;
    /// <summary>Everything, inside the admin app's 10-second limit.</summary>
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(8);

    public bool Enabled => images.Enabled && options.Configured;

    public async Task<ProviderImages> ReadAsync(string sessionId, Guid accountId, CancellationToken cancellationToken)
    {
        if (!Enabled) return new(ProviderImageResult.Unavailable);
        if (!Guid.TryParse(sessionId, out var id)) return new(ProviderImageResult.Gone);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(Limit);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(options.BaseUrl, $"v3/session/{id}/decision/"));
            request.Headers.Add("x-api-key", options.ApiKey);
            using var response = await http.SendAsync(request, limit.Token);
            if (response.StatusCode == HttpStatusCode.NotFound) return new(ProviderImageResult.Gone);
            if (response.StatusCode != HttpStatusCode.OK) return new(ProviderImageResult.Unavailable);
            using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(limit.Token), cancellationToken: limit.Token);
            // Another session's or another member's document is never fetched, whatever Didit answers.
            if (DiditClient.Read(body.RootElement) is not { } read || !Guid.TryParse(read.SessionId, out var got) || got != id || read.VendorData != accountId.ToString())
                return new(ProviderImageResult.Unavailable);
            var links = Links(body.RootElement);
            if (links.Count == 0) return new(ProviderImageResult.None);
            if (links.Any(l => !Allowed(l.Link))) return new(ProviderImageResult.Refused);
            var sides = await Task.WhenAll(links.Select(l => FetchAsync(l.Side, l.Link, limit.Token)));
            return sides.FirstOrDefault(s => s.Result != ProviderImageResult.Available) ?? new(ProviderImageResult.Available, sides.SelectMany(s => s.Images).ToList());
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or JsonException or IOException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new(ProviderImageResult.Unavailable); // the exception may name the link, so it isn't logged
        }
    }

    private static List<(string Side, string Link)> Links(JsonElement root)
    {
        var decision = root.TryGetProperty("decision", out var d) ? d : root;
        if (decision.ValueKind != JsonValueKind.Object || !decision.TryGetProperty("id_verifications", out var checks) || checks.ValueKind != JsonValueKind.Array) return [];
        return checks.EnumerateArray().Where(c => c.ValueKind == JsonValueKind.Object).Take(MaxDocuments)
            .SelectMany(c => new[] { ("Front", c, "front_image"), ("Back", c, "back_image") })
            .Select(x => (x.Item1, x.c.TryGetProperty(x.Item3, out var link) && link.ValueKind == JsonValueKind.String ? link.GetString() : null))
            .Where(x => !string.IsNullOrEmpty(x.Item2)).Select(x => (x.Item1, x.Item2!)).ToList();
    }

    private bool Allowed(string link)
    {
        var url = Uri.TryCreate(link, UriKind.Absolute, out var parsed) ? parsed : null;
        // Plain HTTP only to this machine, for the owned tests' stand-in; hosted APIs refuse a loopback host (HostingPolicy).
        if (url is not null && url.UserInfo.Length == 0 && (url.Scheme == Uri.UriSchemeHttps || url.Scheme == Uri.UriSchemeHttp && url.IsLoopback)
            && images.Hosts.Contains(url.Authority, StringComparer.OrdinalIgnoreCase)) return true;
        // The host only (letters, digits, dots, dashes and port): how the owner learns which name to approve.
        log.LogWarning("Didit image host not allowed: {Host}.", url is not null && Uri.CheckHostName(url.Host) == UriHostNameType.Dns ? url.Authority : "unknown");
        return false;
    }

    private async Task<ProviderImages> FetchAsync(string side, string link, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, link);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode != HttpStatusCode.OK) return new(ProviderImageResult.Unavailable);
        if (response.Content.Headers.ContentLength > MaxBytes) return new(ProviderImageResult.Refused);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        for (int read; (read = await stream.ReadAsync(chunk, cancellationToken)) > 0;)
        {
            if (buffer.Length + read > MaxBytes) return new(ProviderImageResult.Refused);
            buffer.Write(chunk, 0, read);
        }
        var data = buffer.ToArray();
        return IdImagePolicy.ContentType(data) is { } type ? new(ProviderImageResult.Available, [new(side, type, data)]) : new(ProviderImageResult.Refused);
    }
}
