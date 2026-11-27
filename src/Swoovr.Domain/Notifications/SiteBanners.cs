namespace Swoovr.Domain;

/// <summary>Issue 588 (D445): the banner's tone. Each tone is a fixed colour pair checked for contrast in every scheme.</summary>
public enum SiteBannerTone { Info, Warning, Critical }

/// <summary>
/// Issue 588 (D445): a site-wide announcement staff publish. It can't be dismissed. It shows in the app (signed in and signed
/// out), on the website's front page, or both. An optional start and end time limit when it shows, and switching it off
/// always wins. Only one banner is switched on per placement at a time.
/// </summary>
public sealed class SiteBanner
{
    public Guid Id { get; set; }
    public string Text { get; set; } = "";
    public SiteBannerTone Tone { get; set; }
    public bool InApp { get; set; }
    public bool OnWebsite { get; set; }
    public DateTimeOffset? StartsAt { get; set; }
    public DateTimeOffset? EndsAt { get; set; }
    public bool Enabled { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid UpdatedBy { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public static class SiteBannerPolicy
{
    public const int TextMax = 200;
    public const string App = "app", Website = "website";

    public static string? Text(string? value) => value?.Trim() is { Length: > 0 and <= TextMax } text && !text.Any(char.IsControl) ? text : null;

    /// <summary>Null when the request is valid; otherwise what's wrong, for the admin form.</summary>
    public static string? Problem(string? text, bool inApp, bool onWebsite, DateTimeOffset? startsAt, DateTimeOffset? endsAt) =>
        Text(text) is null ? $"Write the announcement in up to {TextMax} characters, on one line."
        : !inApp && !onWebsite ? "Choose where it shows: the app, the website, or both."
        : startsAt is { } start && endsAt is { } end && end <= start ? "The end must be after the start."
        : null;

    /// <summary>Showing now: switched on and inside its optional window. Switching off always wins.</summary>
    public static bool Live(SiteBanner banner, DateTimeOffset now) =>
        banner.Enabled && (banner.StartsAt is not { } start || start <= now) && (banner.EndsAt is not { } end || end > now);

    public static bool Shares(SiteBanner a, SiteBanner b) => a.InApp && b.InApp || a.OnWebsite && b.OnWebsite;

    public static bool In(SiteBanner banner, string placement) => placement == App ? banner.InApp : placement == Website && banner.OnWebsite;

    /// <summary>For the admin list: live, scheduled (on, before its start), ended (on, after its end) or off.</summary>
    public static string State(SiteBanner banner, DateTimeOffset now) =>
        !banner.Enabled ? "off" : Live(banner, now) ? "live" : banner.StartsAt is { } start && start > now ? "scheduled" : "ended";
}
