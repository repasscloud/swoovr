namespace Swoovr.Contracts;

/// <summary>Issue 588 (D445): what the app and the website show. Tone is "info", "warning" or "critical".</summary>
public sealed record SiteBannerNotice(string Text, string Tone);

public sealed record SiteBannerRequest(string? Text, string? Tone, bool InApp, bool OnWebsite, DateTimeOffset? StartsAt, DateTimeOffset? EndsAt);

public sealed record SiteBannerView(Guid Id, string Text, string Tone, bool InApp, bool OnWebsite, DateTimeOffset? StartsAt, DateTimeOffset? EndsAt,
    bool Enabled, string State, DateTimeOffset UpdatedAt, string? UpdatedBy, bool CanEdit);
