using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Swoovr.Api.Media;
using Swoovr.Domain;
using Swoovr.Infrastructure.Media;
using Swoovr.Infrastructure.Persistence;

/// <summary>
/// Single-server image processing (IMAGE-MODERATION-UAT.md): stall rules, screening readiness, the malware scan before
/// decoding, operator rescreen and the staff status counts. Fictional accounts and synthetic images only.
/// </summary>
static class ImageProcessingChecks
{
    public static void Run()
    {
        var count = 0; void Expect(bool ok, string name) { if (!ok) throw new Exception(name); count++; }
        var now = DateTimeOffset.UtcNow;
        Expect(!MediaPolicy.Stalled(MediaPolicy.StallAttempts - 1, now.AddHours(-23), now), "not stalled before the attempt and age limits");
        Expect(MediaPolicy.Stalled(MediaPolicy.StallAttempts, now, now), "stalled at the attempt limit");
        Expect(MediaPolicy.Stalled(0, now - MediaPolicy.StallAge, now), "stalled at the age limit");
        // The retry schedule is unchanged: stalling reports a photo, it never stops retrying or publishes it.
        Expect(MediaPolicy.NextAttempt(MediaPolicy.StallAttempts, now) == now.AddHours(1), "stalled photos keep the hourly retry");

        static async Task<bool?> Ready(HttpStatusCode? status, Uri? baseUrl = null)
        {
            using var http = new HttpClient(new Answer(status));
            var screening = new ImageModerationScreening(http, new ScreeningOptions(baseUrl ?? new Uri("http://image-moderation:8080/"), new string('k', 48)), NullLogger<ImageModerationScreening>.Instance);
            return await screening.ReadyAsync(default);
        }
        Expect(Ready(HttpStatusCode.OK).GetAwaiter().GetResult() == true, "screening ready when /ready answers 200");
        Expect(Ready(HttpStatusCode.ServiceUnavailable).GetAwaiter().GetResult() == false, "screening not ready on 503");
        Expect(Ready(null).GetAwaiter().GetResult() == false, "screening not ready when unreachable");
        using (var none = new HttpClient(new Answer(HttpStatusCode.OK)))
            Expect(new ImageModerationScreening(none, new ScreeningOptions(null, null), NullLogger<ImageModerationScreening>.Instance).ReadyAsync(default).GetAwaiter().GetResult() == false, "unconfigured screening is never ready");
        Console.WriteLine($"PASS image processing: {count} stall and readiness checks.");
    }

    public static async Task RunPersistence(DbContextOptions<SwoovrDbContext> options, Action<string, bool> check)
    {
        var now = DateTimeOffset.UtcNow;
        await using var db = new SwoovrDbContext(options);
        Account Member(AccountStatus status) => new() { Id = Guid.NewGuid(), Realm = AccountRealm.Members, Issuer = "https://issuer.example.test", Subject = Guid.NewGuid().ToString(), Status = status, CreatedAt = now, LastSeenAt = now };
        var member = Member(AccountStatus.Active);
        db.Accounts.Add(member); await db.SaveChangesAsync();
        var folder = Path.Combine(Path.GetTempPath(), "swoovr-image-processing-" + Guid.NewGuid().ToString("N"));
        try
        {
            var scanner = new FakeScanner();
            var store = new LocalMediaStore(folder);
            using var services = new ServiceCollection().AddSingleton(db).AddSingleton<IMediaStore>(store).AddSingleton<TimeProvider>(TimeProvider.System)
                .AddSingleton<IAttachmentMalwareScanner>(scanner).AddSingleton<IMediaScreening>(new ReadyScreen()).BuildServiceProvider();
            var pipeline = new MediaPipeline(services, new PhotoProcessor(), new ReadyScreen(), new MediaSettings(true, null, true), new MediaQueue(), NullLogger<MediaPipeline>.Instance);
            var photo = MediaChecks.SyntheticJpeg(400, 300);
            async Task<int> Rows() => await db.MediaItems.CountAsync(i => i.AccountId == member.Id);
            int Objects() => Directory.Exists(folder) ? Directory.GetFiles(folder, "*", SearchOption.AllDirectories).Length : 0;

            scanner.Next = AttachmentScanState.Infected;
            var (infected, infectedError) = await pipeline.UploadAsync(member.Id, photo, null, now, default, deferScreening: true);
            check("malware detection refuses the upload before anything is stored", infected is null && infectedError == MediaUploadError.Malware && await Rows() == 0 && Objects() == 0);
            check("malware refusal is audited with ids only", await db.AuditEntries.AnyAsync(a => a.Action == "media.malware-refused" && a.TargetId == member.Id.ToString()));
            check("the scanner saw the original bytes, before decoding", scanner.LastLength == photo.Length);

            scanner.Next = AttachmentScanState.Failed;
            var (_, unavailable) = await pipeline.UploadAsync(member.Id, photo, null, now, default, deferScreening: true);
            check("an unavailable scanner fails closed: nothing stored, try again later", unavailable == MediaUploadError.ScanUnavailable && await Rows() == 0 && Objects() == 0);
            scanner.Next = AttachmentScanState.Unsupported;
            var (_, unsupported) = await pipeline.UploadAsync(member.Id, photo, null, now, default, deferScreening: true);
            check("a photo the scanner can't check is refused", unsupported == MediaUploadError.ScanUnsupported && await Rows() == 0);

            scanner.Next = AttachmentScanState.Clean;
            var (clean, cleanError) = await pipeline.UploadAsync(member.Id, photo, null, now, default, deferScreening: true);
            check("a clean photo is stored privately and waits for screening", cleanError == MediaUploadError.None && clean is { Status: MediaStatus.Pending } && clean.ObjectKey!.StartsWith("quarantine/") && Objects() == 1);

            // Status counts and reasons (no ids), stalled detection and operator rescreen.
            var stalled = new MediaItem { Id = Guid.NewGuid(), AccountId = member.Id, Status = MediaStatus.Pending, CreatedAt = now.AddDays(-2), ScreenAttempts = MediaPolicy.StallAttempts, LastScreenError = "http-404", NextScreenAt = now.AddMinutes(40), ObjectKey = "quarantine/" + Guid.NewGuid().ToString("N") };
            db.MediaItems.Add(stalled); await db.SaveChangesAsync();
            var health = new ImageProcessingHealth(services.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System);
            var status = await ImageProcessingStatusEndpoints.ReadAsync(db, health, admin: false, now, default);
            check("status counts waiting and stalled photos", status.Pending >= 2 && status.Stalled >= 1 && status.PendingOverHour >= 1 && status.State == "degraded");
            check("status explains why photos wait by reason code", status.Reasons.Any(r => r.Code == "http-404" && r.Stalled >= 1) && status.Reasons.Any(r => r.Code == "waiting"));
            check("status hides known-content counts from Support", status.Blocked is null && (await ImageProcessingStatusEndpoints.ReadAsync(db, health, admin: true, now, default)).Blocked is not null);
            check("status reports both services answering", status.ScreeningReady == true && status.MalwareScannerReady);
            scanner.Healthy = false;
            var down = await ImageProcessingStatusEndpoints.ReadAsync(db, new ImageProcessingHealth(services.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System), admin: false, now, default);
            check("a scanner that doesn't answer makes processing unavailable", down.State == "unavailable" && !down.MalwareScannerReady);

            // Codex review on #395: a locked account's pending photo is paused on purpose (the worker skips it), so it is
            // reported separately and never changes the backlog, stall counts or health.
            scanner.Healthy = true;
            ImageProcessingHealth Fresh() => new(services.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System);
            var before = await ImageProcessingStatusEndpoints.ReadAsync(db, Fresh(), admin: false, now, default);
            var locked = Member(AccountStatus.Locked); db.Accounts.Add(locked);
            db.MediaItems.Add(new MediaItem { Id = Guid.NewGuid(), AccountId = locked.Id, Status = MediaStatus.Pending, CreatedAt = now.AddDays(-3), ScreenAttempts = MediaPolicy.StallAttempts, LastScreenError = "paused-check", NextScreenAt = now.AddMinutes(-5), ObjectKey = "quarantine/" + Guid.NewGuid().ToString("N") });
            await db.SaveChangesAsync();
            var after = await ImageProcessingStatusEndpoints.ReadAsync(db, Fresh(), admin: false, now, default);
            check("a paused account's photo is reported separately, never as backlog or degraded health",
                after.Paused == before.Paused + 1 && after.Pending == before.Pending && after.Stalled == before.Stalled && after.PendingOverHour == before.PendingOverHour
                && after.State == before.State && !after.Reasons.Any(r => r.Code == "paused-check"));
            check("the worker's due query skips the paused photo", !await db.MediaItems.ScreenablePending(db).AnyAsync(i => i.AccountId == locked.Id));
            var rescreened = await pipeline.RescreenPendingAsync("http-404", now, default);
            db.ChangeTracker.Clear();
            check("rescreen makes matching pending photos due now, nothing else", rescreened == 1 && (await db.MediaItems.SingleAsync(i => i.Id == stalled.Id)) is { Status: MediaStatus.Pending, NextScreenAt: { } due } && Math.Abs((due - now).TotalMilliseconds) < 1);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    private sealed class Answer(HttpStatusCode? status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            status is { } code ? Task.FromResult(new HttpResponseMessage(code)) : throw new HttpRequestException("unreachable");
    }

    private sealed class FakeScanner : IAttachmentMalwareScanner
    {
        public AttachmentScanState Next = AttachmentScanState.Clean;
        public bool Healthy = true;
        public long LastLength;
        public string Name => "clamav";
        public Task<bool> HealthyAsync(CancellationToken cancellationToken = default) => Task.FromResult(Healthy);
        public Task<MalwareScanResult> ScanAsync(Stream content, CancellationToken cancellationToken = default)
        {
            LastLength = content.Length;
            return Task.FromResult(new MalwareScanResult(Next, Name, "ClamAV 1.4.3", "27450", Detection: Next == AttachmentScanState.Infected ? "Eicar-Test-Signature" : null,
                Failure: Next is AttachmentScanState.Failed ? "scanner-unavailable" : Next is AttachmentScanState.Unsupported ? "scan-limit" : null));
        }
    }

    private sealed class ReadyScreen : IMediaScreening
    {
        public string Name => "simulated";
        public Task<bool?> ReadyAsync(CancellationToken cancellationToken) => Task.FromResult<bool?>(true);
        public Task<ScreeningResponse> ScreenAsync(byte[] image, CancellationToken cancellationToken) => throw new InvalidOperationException("Not screened in these checks.");
    }
}
