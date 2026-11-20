using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Swoovr.Api.Accounts;
using Swoovr.Contracts;
using Swoovr.Domain;
using Swoovr.Infrastructure.Persistence;

namespace Swoovr.Api.Media;

/// <summary>
/// Whether the two image services answer: the photo screening service (`/ready`) and the shared malware scanner (clamd
/// PING). Cached briefly so the staff Overview and monitoring can't flood either service.
/// </summary>
public sealed class ImageProcessingHealth(IServiceScopeFactory scopes, TimeProvider clock)
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(15);
    private readonly SemaphoreSlim gate = new(1, 1);
    private (DateTimeOffset At, bool? Screening, bool Malware)? cached;

    public async Task<(bool? Screening, bool Malware)> CheckAsync(CancellationToken cancellationToken)
    {
        if (cached is { } hit && clock.GetUtcNow() - hit.At < CacheFor) return (hit.Screening, hit.Malware);
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (cached is { } again && clock.GetUtcNow() - again.At < CacheFor) return (again.Screening, again.Malware);
            await using var scope = scopes.CreateAsyncScope();
            var screening = scope.ServiceProvider.GetService<IMediaScreening>() is { } screener ? await screener.ReadyAsync(cancellationToken) : false;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            var malware = scope.ServiceProvider.GetService<IAttachmentMalwareScanner>() is { } scanner && await scanner.HealthyAsync(timeout.Token);
            cached = (clock.GetUtcNow(), screening, malware);
            return (screening, malware);
        }
        finally { gate.Release(); }
    }
}

/// <summary>
/// Image processing status for Support and Admin on the staff Overview, for monitoring (`/health/screening`) and for the
/// operator report (`media pending-report`). Counts and machine reason codes only: never photo ids, account ids or images.
/// </summary>
public static class ImageProcessingStatusEndpoints
{
    public static async Task<ImageProcessingStatus> ReadAsync(SwoovrDbContext db, ImageProcessingHealth health, bool admin, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var (screening, malware) = await health.CheckAsync(cancellationToken);
        // Only photos the worker acts on count towards the backlog and health. Photos paused with their account (locked,
        // suspended, being deleted) are reported separately and never make processing look degraded.
        var pending = db.MediaItems.AsNoTracking().ScreenablePending(db);
        var paused = await db.MediaItems.AsNoTracking().Pending().CountAsync(cancellationToken) - await pending.CountAsync(cancellationToken);
        var stallBefore = now - MediaPolicy.StallAge;
        var rows = await pending.GroupBy(i => i.LastScreenError ?? "waiting")
            .Select(g => new { Code = g.Key, Count = g.Count(), Stalled = g.Count(i => i.ScreenAttempts >= MediaPolicy.StallAttempts || i.CreatedAt <= stallBefore) })
            .ToListAsync(cancellationToken);
        var total = rows.Sum(r => r.Count);
        var stalled = rows.Sum(r => r.Stalled);
        var overHour = await pending.CountAsync(i => i.CreatedAt < now - MediaPolicy.SlowAge, cancellationToken);
        var oldest = total == 0 ? null : await pending.MinAsync(i => (DateTimeOffset?)i.CreatedAt, cancellationToken);
        var since = now.AddHours(-24);
        var screened = db.MediaItems.AsNoTracking().Where(i => i.ScreenedAt >= since);
        var passed = await screened.CountAsync(i => i.Status == MediaStatus.Passed && i.ReviewedAt == null, cancellationToken);
        var review = await screened.CountAsync(i => i.Status == MediaStatus.Uncertain || i.ReviewedAt != null, cancellationToken);
        int? blocked = admin ? await screened.CountAsync(i => i.Status == MediaStatus.Blocked, cancellationToken) : null;
        var state = screening == false || !malware ? "unavailable" : stalled > 0 || overHour > 0 ? "degraded" : "healthy";
        return new ImageProcessingStatus(state, screening, malware, total, overHour, stalled, oldest,
            rows.OrderByDescending(r => r.Count).ThenBy(r => r.Code, StringComparer.Ordinal).Select(r => new ImageProcessingReason(r.Code, r.Count, r.Stalled)).ToList(),
            passed, review, blocked, since, now, Math.Max(0, paused));
    }

    public static void Map(WebApplication app)
    {
        app.MapGet("/api/admin/media/processing", async (ClaimsPrincipal user, AccountResolver accounts, ImageProcessingHealth health, TimeProvider clock, HttpContext context, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var (current, denied) = await AdminEndpoints.StaffAsync(user, accounts, AccountRole.Support, token);
            if (denied is not null) return denied;
            return Results.Ok(await ReadAsync(accounts.Db!, health, current!.Allows(AccountRole.Admin), clock.GetUtcNow(), token));
        }).RequireAuthorization();

        // For monitoring and alerts, like /health/attachments: 200 when both services answer and nothing is stalled, else 503.
        // Readiness doesn't depend on it (the API keeps serving; photos simply wait).
        app.MapGet("/health/screening", async (HttpContext context, ImageProcessingHealth health, IServiceProvider services, TimeProvider clock) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            if (services.GetService<SwoovrDbContext>() is not { } db)
            {
                var (screening, malware) = await health.CheckAsync(timeout.Token);
                var up = screening != false && malware;
                return Results.Json(new { status = up ? "healthy" : "unavailable", screening, malware }, statusCode: up ? 200 : 503);
            }
            try
            {
                var status = await ReadAsync(db, health, admin: false, clock.GetUtcNow(), timeout.Token);
                return Results.Json(new { status = status.State, screening = status.ScreeningReady, malware = status.MalwareScannerReady, pending = status.Pending, stalled = status.Stalled, paused = status.Paused,
                    pendingOverHour = status.PendingOverHour, reasons = status.Reasons.Select(r => new { r.Code, r.Count, r.Stalled }) },
                    statusCode: status.State == "healthy" ? 200 : 503);
            }
            catch (Exception e) when (e is System.Data.Common.DbException or InvalidOperationException or OperationCanceledException)
            { return Results.Json(new { status = "unavailable" }, statusCode: 503); }
        });
    }
}
