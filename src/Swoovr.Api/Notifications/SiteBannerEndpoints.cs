using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Swoovr.Api.Accounts;
using Swoovr.Contracts;
using Swoovr.Domain;
using Swoovr.Infrastructure.Persistence;
using static Swoovr.Api.Accounts.AccountEndpoints;

namespace Swoovr.Api.Notifications;

/// <summary>
/// Issue 588 (D445): site-wide banners. Admins publish, schedule and switch them off; Support can see them. Every change is
/// audited with ids only. The app (signed in and out) and the website's front page read the one live banner for their
/// placement from one public, briefly cached endpoint that returns only its text and tone.
/// </summary>
public static class SiteBannerEndpoints
{
    public static void Map(WebApplication app)
    {
        // The app reads ?placement=app; the website (swoovr-marketing) reads ?placement=website, cross-origin. One stable path:
        // D211: Swoovr API paths carry no numerical version, so issue 588's suggested numbered path isn't used.
        app.MapGet("/api/site-banner", (string? placement, AccountResolver accounts, TimeProvider clock, HttpContext context, CancellationToken token) =>
        {
            var website = placement == SiteBannerPolicy.Website;
            if (website) context.Response.Headers.AccessControlAllowOrigin = "*";
            return PublicAsync(website ? SiteBannerPolicy.Website : SiteBannerPolicy.App, accounts, clock, context, token);
        }).AllowAnonymous();

        var admin = app.MapGroup("/api/admin/site-banners").RequireAuthorization();
        admin.MapGet("", async (ClaimsPrincipal user, AccountResolver accounts, TimeProvider clock, CancellationToken token) =>
        {
            var (current, denied) = await AdminEndpoints.StaffAsync(user, accounts, AccountRole.Support, token);
            if (denied is not null) return denied;
            var rows = await accounts.Db!.SiteBanners.AsNoTracking().OrderByDescending(b => b.Enabled).ThenByDescending(b => b.UpdatedAt).Take(100).ToListAsync(token);
            return Results.Ok(await ViewsAsync(accounts.Db, rows, current!.Allows(AccountRole.Admin), clock.GetUtcNow(), token));
        });
        admin.MapPost("", async (SiteBannerRequest request, ClaimsPrincipal user, AccountResolver accounts, TimeProvider clock, CancellationToken token) =>
        {
            var (current, denied) = await AdminEndpoints.StaffAsync(user, accounts, AccountRole.Admin, token);
            if (denied is not null) return denied;
            if (Invalid(request) is { } bad) return bad;
            var (db, now, actor) = (accounts.Db!, clock.GetUtcNow(), current!.Account!.Id);
            var banner = new SiteBanner { Id = Guid.NewGuid(), CreatedBy = actor, CreatedAt = now };
            Apply(banner, request, actor, now);
            db.SiteBanners.Add(banner);
            Audit.Record(db, now, actor, "account", "site-banner.created", "site-banner", banner.Id.ToString());
            await db.SaveChangesAsync(token);
            return Results.Ok((await ViewsAsync(db, [banner], true, now, token)).Single());
        });
        admin.MapPut("/{id:guid}", async (Guid id, SiteBannerRequest request, ClaimsPrincipal user, AccountResolver accounts, TimeProvider clock, CancellationToken token) =>
        {
            var (current, denied) = await AdminEndpoints.StaffAsync(user, accounts, AccountRole.Admin, token);
            if (denied is not null) return denied;
            if (Invalid(request) is { } bad) return bad;
            var (db, now, actor) = (accounts.Db!, clock.GetUtcNow(), current!.Account!.Id);
            await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, token);
            var banner = await db.SiteBanners.SingleOrDefaultAsync(b => b.Id == id, token);
            if (banner is null) return Results.NotFound();
            Apply(banner, request, actor, now);
            if (banner.Enabled && await TakenAsync(db, banner, token)) return Taken();
            Audit.Record(db, now, actor, "account", "site-banner.updated", "site-banner", banner.Id.ToString());
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            return Results.Ok((await ViewsAsync(db, [banner], true, now, token)).Single());
        });
        admin.MapPost("/{id:guid}/{action:regex(^(enable|disable)$)}", async (Guid id, string action, ClaimsPrincipal user, AccountResolver accounts, TimeProvider clock,
            CancellationToken token) =>
        {
            var (current, denied) = await AdminEndpoints.StaffAsync(user, accounts, AccountRole.Admin, token);
            if (denied is not null) return denied;
            var (db, now, actor) = (accounts.Db!, clock.GetUtcNow(), current!.Account!.Id);
            // Serializable, so two admins can't switch on two banners for one placement at once.
            await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, token);
            var banner = await db.SiteBanners.SingleOrDefaultAsync(b => b.Id == id, token);
            if (banner is null) return Results.NotFound();
            var on = action == "enable";
            if (on && await TakenAsync(db, banner, token)) return Taken();
            if (banner.Enabled != on)
            {
                (banner.Enabled, banner.UpdatedBy, banner.UpdatedAt) = (on, actor, now);
                Audit.Record(db, now, actor, "account", on ? "site-banner.enabled" : "site-banner.disabled", "site-banner", banner.Id.ToString());
                await db.SaveChangesAsync(token);
            }
            await transaction.CommitAsync(token);
            return Results.Ok((await ViewsAsync(db, [banner], true, now, token)).Single());
        });
    }

    private static async Task<IResult> PublicAsync(string placement, AccountResolver accounts, TimeProvider clock, HttpContext context, CancellationToken token)
    {
        // A minute in caches at most, so switching a banner off reaches everyone quickly.
        context.Response.Headers.CacheControl = "public, max-age=60";
        if (accounts.Db is not { } db) return Results.NoContent();
        var now = clock.GetUtcNow();
        var rows = await db.SiteBanners.AsNoTracking().Where(b => b.Enabled && (placement == SiteBannerPolicy.App ? b.InApp : b.OnWebsite))
            .OrderByDescending(b => b.UpdatedAt).Take(5).ToListAsync(token);
        return rows.FirstOrDefault(b => SiteBannerPolicy.Live(b, now)) is { } live ? Results.Ok(new SiteBannerNotice(live.Text, Wire(live.Tone))) : Results.NoContent();
    }

    private static IResult? Invalid(SiteBannerRequest request) =>
        Tone(request.Tone) is null ? Problem(StatusCodes.Status400BadRequest, ProblemCodes.InvalidRequest, "Choose a tone: info, warning or critical.")
        : SiteBannerPolicy.Problem(request.Text, request.InApp, request.OnWebsite, request.StartsAt, request.EndsAt) is { } problem
            ? Problem(StatusCodes.Status400BadRequest, ProblemCodes.InvalidRequest, problem) : null;

    private static void Apply(SiteBanner banner, SiteBannerRequest request, Guid actor, DateTimeOffset now)
    {
        banner.Text = SiteBannerPolicy.Text(request.Text)!;
        banner.Tone = Tone(request.Tone)!.Value;
        (banner.InApp, banner.OnWebsite) = (request.InApp, request.OnWebsite);
        (banner.StartsAt, banner.EndsAt) = (request.StartsAt?.ToUniversalTime(), request.EndsAt?.ToUniversalTime());
        (banner.UpdatedBy, banner.UpdatedAt) = (actor, now);
    }

    private static SiteBannerTone? Tone(string? value) => value switch
    {
        "info" => SiteBannerTone.Info, "warning" => SiteBannerTone.Warning, "critical" => SiteBannerTone.Critical, _ => null,
    };

    private static string Wire(SiteBannerTone tone) => tone.ToString().ToLowerInvariant();

    private static async Task<bool> TakenAsync(SwoovrDbContext db, SiteBanner banner, CancellationToken token) =>
        (await db.SiteBanners.AsNoTracking().Where(b => b.Id != banner.Id && b.Enabled).ToListAsync(token)).Any(b => SiteBannerPolicy.Shares(b, banner));

    private static IResult Taken() => Problem(StatusCodes.Status409Conflict, ProblemCodes.SiteBannerPlacementTaken,
        "Another banner is switched on for that place. Switch it off first.");

    private static async Task<List<SiteBannerView>> ViewsAsync(SwoovrDbContext db, IReadOnlyList<SiteBanner> rows, bool admin, DateTimeOffset now, CancellationToken token)
    {
        var ids = rows.Select(b => b.UpdatedBy).Distinct().ToList();
        var staff = await db.Accounts.AsNoTracking().Where(a => ids.Contains(a.Id)).ToDictionaryAsync(a => a.Id,
            a => string.Join(" ", new[] { a.RegisteredFirstName, a.RegisteredLastName }.Where(n => !string.IsNullOrWhiteSpace(n))) is { Length: > 0 } name ? name : a.Email ?? "Staff", token);
        return rows.Select(b => new SiteBannerView(b.Id, b.Text, Wire(b.Tone), b.InApp, b.OnWebsite, b.StartsAt, b.EndsAt, b.Enabled, SiteBannerPolicy.State(b, now),
            b.UpdatedAt, staff.GetValueOrDefault(b.UpdatedBy), admin)).ToList();
    }
}
