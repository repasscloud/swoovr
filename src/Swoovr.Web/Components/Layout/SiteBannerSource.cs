using System.Net;
using System.Net.Http.Json;
using Swoovr.Contracts;

namespace Swoovr.Web.Components.Layout;

/// <summary>
/// Issue 588 (D445): the app's live site banner, read server-side from the API's public endpoint and kept for 30 seconds,
/// so every page (signed in or out) renders it without a client call. A slow or failing API never breaks a page: there's
/// simply no banner until the next read.
/// </summary>
public sealed class SiteBannerSource(IHttpClientFactory factory, ILogger<SiteBannerSource> logger)
{
    public const string Client = "site-banner";
    private static readonly TimeSpan Keep = TimeSpan.FromSeconds(30);
    private readonly SemaphoreSlim gate = new(1, 1);
    private (DateTimeOffset At, SiteBannerNotice? Banner)? cached;

    public async Task<SiteBannerNotice?> GetAsync(CancellationToken cancellationToken = default)
    {
        if (cached is { } hit && DateTimeOffset.UtcNow - hit.At < Keep) return hit.Banner;
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (cached is { } again && DateTimeOffset.UtcNow - again.At < Keep) return again.Banner;
            SiteBannerNotice? banner = null;
            try
            {
                using var response = await factory.CreateClient(Client).GetAsync("api/site-banner?placement=app", cancellationToken);
                if (response.StatusCode == HttpStatusCode.OK) banner = await response.Content.ReadFromJsonAsync<SiteBannerNotice>(cancellationToken);
                else if (response.StatusCode != HttpStatusCode.NoContent) logger.LogWarning("Site banner read returned {Status}.", (int)response.StatusCode);
            }
            catch (Exception error) when (error is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
            {
                logger.LogWarning("Site banner read failed: {Error}.", error.GetType().Name);
            }
            cached = (DateTimeOffset.UtcNow, banner is { Text.Length: > 0, Tone: "info" or "warning" or "critical" } ? banner : null);
            return cached.Value.Banner;
        }
        finally { gate.Release(); }
    }
}
