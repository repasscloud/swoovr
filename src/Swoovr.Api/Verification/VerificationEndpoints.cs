using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Swoovr.Api.Accounts;
using Swoovr.Contracts;
using Swoovr.Domain;
using Swoovr.Infrastructure.Persistence;
using Swoovr.Infrastructure.Verification;
using static Swoovr.Api.Accounts.AccountEndpoints;

namespace Swoovr.Api.Verification;

/// <summary>
/// Optional ID verification through Didit's hosted flow (D126–D129, D147, D152). The API creates the session with the
/// internal account id as vendor_data and hands the member Didit's link, which they open in their external browser.
/// Results arrive by the signed webhook and, when the member comes back, by re-reading the decision from Didit, so
/// verification also works without a webhook. Swoovr keeps only the outcome and operational metadata (D128). Every
/// change is audited with ids and machine values only.
/// </summary>
public static class VerificationEndpoints
{
    /// <summary>Webhook bodies above this are refused unread (DEFAULT; Didit decisions are a few kilobytes).</summary>
    public const int MaxWebhookBytes = 1024 * 1024;

    /// <summary>
    /// The configured return address for a client kind, or null when the web one isn't configured (issue 438). The app
    /// keeps its fixed custom-scheme default because it can only ever open the installed app.
    /// </summary>
    public static string? WebCallback(string client, IConfiguration configuration) => client switch
    {
        "web" => string.IsNullOrWhiteSpace(configuration["Didit:Callbacks:Web"]) ? null : configuration["Didit:Callbacks:Web"],
        "app" => string.IsNullOrWhiteSpace(configuration["Didit:Callbacks:App"]) ? "com.swoovr.app:/verification" : configuration["Didit:Callbacks:App"],
        _ => null,
    };

    public static void Map(WebApplication app)
    {
        var me = app.MapGroup("/api/me/verification").RequireAuthorization();
        me.MapGet("", async (ClaimsPrincipal user, AccountResolver accounts, IIdVerificationProvider provider, IConfiguration configuration,
            VerifiedIdentityStore identities, TimeProvider clock, CancellationToken cancellationToken) =>
        {
            var (current, denied) = await MemberAsync(user, accounts, cancellationToken, ageGateExempt: true);
            if (denied is not null) return denied;
            var (db, profile, now) = (accounts.Db!, current!.Profile!, clock.GetUtcNow());
            // D152: coming back from Didit re-reads the session, so the result shows without waiting for a webhook. The
            // query string Didit appends to the return page is never trusted; only Didit's own answer is.
            var open = await OpenAttemptAsync(db, profile.AccountId, cancellationToken);
            if (open is not null && provider.Configured && await provider.GetDecisionAsync(open.SessionId, cancellationToken) is { } decision
                && decision.SessionId == open.SessionId && decision.VendorData == profile.AccountId.ToString())
                await ApplyAsync(db, open, decision, now, "return", cancellationToken, identities: identities);
            return Results.Ok(await ViewAsync(db, profile, provider, configuration, cancellationToken));
        });
        me.MapPost("", async (StartVerification request, ClaimsPrincipal user, AccountResolver accounts, IIdVerificationProvider provider,
            IConfiguration configuration, TimeProvider clock, CancellationToken cancellationToken) =>
        {
            var (current, denied) = await MemberAsync(user, accounts, cancellationToken, ageGateExempt: true);
            if (denied is not null) return denied;
            var (db, profile, now) = (accounts.Db!, current!.Profile!, clock.GetUtcNow());
            if (!provider.Configured)
                return Problem(StatusCodes.Status503ServiceUnavailable, ProblemCodes.VerificationNotConfigured, "ID verification isn't available right now.");
            // The return address comes from configuration by client kind, never from the request (no open redirect).
            // Issue 438: the web return page has no built-in default. A missing value used to send UAT members back to
            // localhost; hosted APIs now refuse to start without it (HostingPolicy) and this never guesses one.
            if (request.Client is not ("web" or "app")) return Problem(StatusCodes.Status400BadRequest, ProblemCodes.InvalidRequest, "Unknown client.");
            var callback = WebCallback(request.Client, configuration);
            if (callback is null)
                return Problem(StatusCodes.Status503ServiceUnavailable, ProblemCodes.VerificationNotConfigured, "ID verification isn't available right now.");
            var attempts = await db.IdVerificationAttempts.Where(a => a.AccountId == profile.AccountId).ToListAsync(cancellationToken);
            switch (IdVerificationPolicy.CanStart(profile, attempts))
            {
                case IdStartRefusal.InProgress: return Results.Ok(await ViewAsync(db, profile, provider, configuration, cancellationToken));
                case IdStartRefusal.AlreadyVerified: return Problem(StatusCodes.Status409Conflict, ProblemCodes.AlreadyVerified, "Your ID is already verified.");
                case IdStartRefusal.AttemptUsed:
                    return Problem(StatusCodes.Status409Conflict, ProblemCodes.VerificationAttemptUsed, "You've used your ID check. Contact Swoovr support if you'd like another.");
                case not IdStartRefusal.None: return Problem(StatusCodes.Status403Forbidden, ProblemCodes.NotAvailable, "ID verification isn't available for this account.");
            }
            var session = await provider.CreateSessionAsync(profile.AccountId, callback, cancellationToken);
            if (session is null || IdVerificationPolicy.Map(session.Status) is not { } state || session.Url is null)
            {
                // A technical failure before any real attempt keeps the member's attempt (D129).
                Audit.Record(db, now, profile.AccountId, "account", "verification.provider-failed", "account", profile.AccountId.ToString());
                await db.SaveChangesAsync(cancellationToken);
                return Problem(StatusCodes.Status502BadGateway, ProblemCodes.VerificationProviderFailed, "We couldn't start the ID check. Try again shortly.");
            }
            var attempt = new IdVerificationAttempt
            {
                Id = Guid.NewGuid(), AccountId = profile.AccountId, RegisteredNamesRevision=current!.Account!.RegisteredNamesRevision, SessionId = session.SessionId, WorkflowId = session.WorkflowId ?? provider.WorkflowId,
                State = state, ProviderStatus = Short(session.Status), Started = state is IdAttemptState.InProgress or IdAttemptState.InReview, SelfService = profile.CanRequestIdVerification,
                Url = session.Url, CreatedAt = now, UpdatedAt = now,
            };
            db.IdVerificationAttempts.Add(attempt);
            profile.CanRequestIdVerification = false;
            if (profile.IdStatus != VerificationStatus.Verified) profile.IdStatus = VerificationStatus.Pending;
            Audit.Record(db, now, profile.AccountId, "account", "verification.started", "id-verification", attempt.Id.ToString(), request.Client);
            try { await db.SaveChangesAsync(cancellationToken); }
            catch (DbUpdateException)
            {
                // A concurrent start won (one open attempt per member); show that one.
                db.ChangeTracker.Clear();
                profile = await db.MemberProfiles.SingleAsync(p => p.AccountId == profile.AccountId, cancellationToken);
            }
            return Results.Ok(await ViewAsync(db, profile, provider, configuration, cancellationToken));
        });

        // D166: the member's one correction. Their date is saved, Didit's decision is read again (no new scan), and a
        // match marks it verified; otherwise an admin reviews it. Didit unreachable: nothing changes and they can retry.
        me.MapPost("/date-of-birth", async (DobCorrectionRequest request, ClaimsPrincipal user, AccountResolver accounts, IIdVerificationProvider provider,
            TimeProvider clock, CancellationToken cancellationToken) =>
        {
            var (current, denied) = await MemberAsync(user, accounts, cancellationToken, ageGateExempt: true);
            if (denied is not null) return denied;
            var (db, profile, now) = (accounts.Db!, current!.Profile!, clock.GetUtcNow());
            await using var lifecycle=await AccountLifecycleLease.AcquireAsync(db,profile.AccountId,cancellationToken);
            await db.Entry(profile).ReloadAsync(cancellationToken);
            await db.Entry(current.Account!).ReloadAsync(cancellationToken);
            if(current.Account!.Status!=AccountStatus.Active)return Problem(403,ProblemCodes.AccountLocked,"This account is locked.");
            (current,denied)=await MemberAsync(user,accounts,cancellationToken,ageGateExempt:true);
            if(denied is not null)return denied;
            await using var preservation=await Swoovr.Api.Retention.PreservationLease.SharedAsync(db,cancellationToken);
            await using var transaction=await db.Database.BeginTransactionAsync(cancellationToken);
            if (profile.DobCheck != DobCheck.CorrectionNeeded)
                return Problem(StatusCodes.Status409Conflict, ProblemCodes.DobCorrectionNotOpen, "There's no date of birth to correct.");
            if (request.DateOfBirth is not { } corrected
                || AgePolicy.Evaluate(profile.AgeStatus, corrected, null, DateOnly.FromDateTime(now.UtcDateTime)).Error != AgeChangeError.None)
                return Problem(StatusCodes.Status400BadRequest, ProblemCodes.InvalidAge, "Enter the date of birth shown on your ID.");
            var attempt = await db.IdVerificationAttempts.Where(a => a.AccountId == profile.AccountId && a.State == IdAttemptState.Approved)
                .OrderByDescending(a => a.CompletedAt).FirstOrDefaultAsync(cancellationToken);
            var decision = attempt is null || !provider.Configured ? null : await provider.GetDecisionAsync(attempt.SessionId, cancellationToken);
            if (attempt is null || decision is null || decision.SessionId != attempt.SessionId || decision.VendorData != profile.AccountId.ToString())
                return Problem(StatusCodes.Status503ServiceUnavailable, ProblemCodes.VerificationProviderFailed, "We couldn't check your ID just now. Try again shortly.");
            var keys=accounts.Services.GetRequiredService<IdentityKeys>();
            if(profile.DateOfBirth!=corrected&&profile.DateOfBirth is not null&&keys.CurrentVersion is null)
                return Problem(503,ProblemCodes.NotConfigured,"Date correction is temporarily unavailable.");
            DobChangeEndpoints.PreservePrevious(db,keys,profile,corrected,null,now);
            profile.DateOfBirth = corrected;
            profile.DeclaredAge = null;
            var age=AgePolicy.Evaluate(profile.AgeStatus,corrected,null,DateOnly.FromDateTime(now.UtcDateTime));
            if(profile.AgeStatus!=age.Status)(profile.AgeStatus,profile.AgeStatusAt)=(age.Status,now);
            if(age.LocksAccount){current!.Account!.Status=AccountStatus.Locked;current.Account.StatusReason="underage";}
            var result = DobCorrectionPolicy.AfterCorrection(corrected, decision.DateOfBirth);
            (profile.DateOfBirthVerified, profile.DateOfBirthVerifiedAt) = (result == DobCorrectionResult.Verified, result == DobCorrectionResult.Verified ? now : null);
            (profile.DobCheck, profile.DobCheckAt) = (result == DobCorrectionResult.Verified ? DobCheck.None : DobCheck.ManualReview, now);
            Audit.Record(db, now, profile.AccountId, "account", result == DobCorrectionResult.Verified ? "dob.verified" : "dob.review-opened", "account",
                profile.AccountId.ToString(), "correction");
            if (result == DobCorrectionResult.ManualReview) await OpenReviewAsync(db, profile.AccountId, attempt.Id, DobCorrectionPolicy.CorrectionReason(decision), now, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            if(age.LocksAccount)return Problem(403,ProblemCodes.AccountLocked,"This account is locked.");
            return Results.Ok(AccountEndpoints.View(profile, DateOnly.FromDateTime(now.UtcDateTime), await Profiles.ProfileEndpoints.BadgeOrderAsync(db, cancellationToken)));
        });

        // Issue 533 (D429): the name on the member's own approved ID, while their one-time name correction is open. Read from
        // Didit's decision just now; never stored or logged.
        me.MapGet("/name", async (ClaimsPrincipal user, AccountResolver accounts, IIdVerificationProvider provider, CancellationToken cancellationToken) =>
        {
            var (current, denied) = await MemberAsync(user, accounts, cancellationToken, ageGateExempt: true);
            if (denied is not null) return denied;
            var (db, profile) = (accounts.Db!, current!.Profile!);
            if (profile.NameCheck != NameCheck.CorrectionNeeded)
                return Problem(StatusCodes.Status409Conflict, ProblemCodes.NameCorrectionNotOpen, "There's no name to correct.");
            var id = await IdNameAsync(db, profile.AccountId, provider, cancellationToken);
            // An ID name that isn't a valid registered name (a comma in "Smith, Jr.") is shown but can't be taken; asking
            // Swoovr to check it stays available (PR 581 review).
            return id.First is not { } first
                ? Problem(StatusCodes.Status503ServiceUnavailable, ProblemCodes.VerificationProviderFailed, "We couldn't check your ID just now. Try again shortly.")
                : Results.Ok(new IdNameView(first, id.Last, string.IsNullOrWhiteSpace(id.Last), CanUse: id.Names is not null));
        });

        // Issue 533 (Q192 A, D429): the member's one name correction. "use-id" takes the name exactly as it is on the approved
        // ID (read from Didit again, never typed), once and audited: the registered names, the identity record and ban
        // matching (D191) follow it; the public display name never changes. "review" leaves the name and opens an admin's
        // review. ID Verified stays either way.
        me.MapPost("/name", async (NameCorrectionRequest request, ClaimsPrincipal user, AccountResolver accounts, IIdVerificationProvider provider,
            VerifiedIdentityStore identities, TimeProvider clock, CancellationToken cancellationToken) =>
        {
            var (current, denied) = await MemberAsync(user, accounts, cancellationToken, ageGateExempt: true);
            if (denied is not null) return denied;
            if (request.Choice is not ("use-id" or "review")) return Problem(StatusCodes.Status400BadRequest, ProblemCodes.InvalidRequest, "Choose the name on your ID, or ask Swoovr to check it.");
            var (db, profile, account, now) = (accounts.Db!, current!.Profile!, current.Account!, clock.GetUtcNow());
            await using var lifecycle = await AccountLifecycleLease.AcquireAsync(db, profile.AccountId, cancellationToken);
            await using var preservation = await Swoovr.Api.Retention.PreservationLease.SharedAsync(db, cancellationToken);
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            await db.Entry(profile).ReloadAsync(cancellationToken);
            await db.Entry(account).ReloadAsync(cancellationToken);
            if (account.Status != AccountStatus.Active) return Problem(403, ProblemCodes.AccountLocked, "This account is locked.");
            if (profile.NameCheck != NameCheck.CorrectionNeeded)
                return Problem(StatusCodes.Status409Conflict, ProblemCodes.NameCorrectionNotOpen, "There's no name to correct.");
            var (attempt, first, last, names) = await IdNameAsync(db, profile.AccountId, provider, cancellationToken);
            if (request.Choice == "review")
            {
                // Always available (PR 581 review): it needs neither Didit nor an ID name that passes registered-name rules.
                // Without the ID's name the reason is the general one.
                var middleOnly = first is not null && RegisteredNamePolicy.Compare(new(account.RegisteredFirstName!, account.RegisteredLastName, account.RegisteredMononym), first, last)
                    == NameComparison.MiddleOnly;
                (profile.NameCheck, profile.NameCheckAt) = (NameCheck.ManualReview, now);
                var open = await db.NameChangeReviews.SingleOrDefaultAsync(r => r.AccountId == account.Id && r.ResolvedAt == null, cancellationToken);
                var reason = middleOnly ? "id-name-middle" : "id-name-differs";
                if (open is null) db.NameChangeReviews.Add(new NameChangeReview { Id = Guid.NewGuid(), AccountId = account.Id, Revision = account.RegisteredNamesRevision, Reason = reason, CreatedAt = now });
                else (open.Revision, open.Reason) = (account.RegisteredNamesRevision, reason);
                Audit.Record(db, now, account.Id, "account", "name.review-opened", "account", account.Id.ToString(), reason);
            }
            else
            {
                if (attempt is null || first is null)
                    return Problem(StatusCodes.Status503ServiceUnavailable, ProblemCodes.VerificationProviderFailed, "We couldn't check your ID just now. Try again shortly.");
                if (names is null)
                    return Problem(StatusCodes.Status409Conflict, ProblemCodes.IdNameNotUsable, "The name on your ID can't be used as it is. Ask Swoovr to check it.");
                (account.RegisteredFirstName, account.RegisteredLastName, account.RegisteredMononym) = (names.FirstName, names.LastName, names.Mononym);
                account.RegisteredNamesConfirmedAt = now;
                account.RegisteredNamesRevision++;
                // The approved check is where this name came from, so it stays current for the new revision.
                attempt.RegisteredNamesRevision = account.RegisteredNamesRevision;
                account.RegisteredNamesMatchId = true;
                (profile.NameCheck, profile.NameCheckAt) = (NameCheck.None, null);
                Audit.Record(db, now, account.Id, "account", "name.corrected-to-id", "account", account.Id.ToString(), "revision:" + account.RegisteredNamesRevision);
                if (await provider.GetDecisionAsync(attempt.SessionId, cancellationToken) is { } decision && decision.SessionId == attempt.SessionId)
                    await identities.RecordAsync(account.Id, attempt, decision, now, cancellationToken);
            }
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return Results.Ok(AccountEndpoints.View(profile, DateOnly.FromDateTime(now.UtcDateTime), await Profiles.ProfileEndpoints.BadgeOrderAsync(db, cancellationToken)));
        });

        // D152: Didit calls the API directly (never through the BFF). Signature first, then the signed timestamp, then
        // a durable dedupe on event_id in the same transaction as the change.
        app.MapPost("/api/webhooks/didit", async (HttpRequest http, SwoovrDbContext db, DiditOptions options, TimeProvider clock,
            VerifiedIdentityStore identities, IIdVerificationProvider provider, ILoggerFactory logs, CancellationToken cancellationToken) =>
        {
            var log = logs.CreateLogger("Swoovr.Api.Verification.DiditWebhook");
            if (http.ContentLength > MaxWebhookBytes) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            using var buffer = new MemoryStream();
            await http.Body.CopyToAsync(buffer, cancellationToken);
            if (buffer.Length > MaxWebhookBytes) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            var now = clock.GetUtcNow();
            var check = DiditSignature.Check(buffer.ToArray(), http.Headers["X-Signature-V2"].FirstOrDefault(), http.Headers["X-Signature"].FirstOrDefault(),
                options.WebhookSecret ?? "", now, out var body);
            if (check != DiditWebhookCheck.Valid)
            {
                // Logged without the body: it may hold someone's decision data.
                log.LogWarning("Didit webhook refused: {Check}.", check);
                Audit.Record(db, now, null, "system", "verification.webhook-refused", null, null, check.ToString());
                await db.SaveChangesAsync(cancellationToken);
                return check == DiditWebhookCheck.BadSignature ? Results.Unauthorized() : Results.BadRequest();
            }
            using (body)
            {
                var root = body!.RootElement;
                var type = root.TryGetProperty("webhook_type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString()! : "";
                if (!root.TryGetProperty("event_id", out var e) || e.ValueKind != JsonValueKind.String || !Guid.TryParse(e.GetString(), out var eventId))
                    return Results.BadRequest();
                if (await db.DiditWebhookEvents.AnyAsync(w => w.EventId == eventId, cancellationToken)) return Results.Ok(new { duplicate = true });
                var session = DiditClient.Read(root);
                var outcome = "ignored";
                IdVerificationAttempt? attempt = null;
                if (type is "status.updated" or "data.updated" && session is not null)
                {
                    attempt = await db.IdVerificationAttempts.SingleOrDefaultAsync(a => a.SessionId == session.SessionId, cancellationToken);
                    outcome = attempt is null ? "unknown-session"
                        : session.VendorData != attempt.AccountId.ToString() ? "vendor-mismatch"
                        : session.WorkflowId is { } workflow && workflow != attempt.WorkflowId ? "workflow-mismatch"
                        : "applied";
                    // Issue 440 (D402): read before the account lease and the save, so a slow provider holds neither.
                    if (outcome == "applied" && provider.Configured && !IdVerificationPolicy.Settled(attempt!.State) && IdVerificationPolicy.NeedsDecision(session))
                        session = IdVerificationPolicy.Complete(session, await provider.GetDecisionAsync(session.SessionId, cancellationToken));
                }
                db.DiditWebhookEvents.Add(new DiditWebhookEvent { EventId = eventId, ReceivedAt = now, WebhookType = Short(type, 48), SessionId = session?.SessionId is { Length: <= 64 } s ? s : null, Outcome = outcome });
                // Hold the account lease through the final save so a registered-name edit can't commit between the
                // revision check in ApplyAsync and this save (the lease is reentrant inside ApplyAsync).
                await using var lease = outcome == "applied" ? await AccountLifecycleLease.AcquireAsync(db, attempt!.AccountId, cancellationToken) : null;
                if (outcome == "applied") await ApplyAsync(db, attempt!, session!, now, "webhook", cancellationToken, save: false, identities: identities);
                else if (outcome != "ignored")
                    Audit.Record(db, now, null, "system", "verification.webhook-" + outcome, "id-verification", attempt?.Id.ToString());
                try { await db.SaveChangesAsync(cancellationToken); }
                catch (DbUpdateException)
                {
                    // The same event raced in on another request; it was applied there.
                    return Results.Ok(new { duplicate = true });
                }
                return Results.Ok(new { outcome });
            }
        }).AllowAnonymous();

        // D166: the manual date-of-birth review queue. Admins read the document's date in Didit's console and decide.
        var reviews = app.MapGroup("/api/admin/dob-reviews").RequireAuthorization();
        reviews.MapGet("", async (bool? open, ClaimsPrincipal user, AccountResolver accounts, IIdImageSource images, CancellationToken cancellationToken) =>
        {
            var (current, denied) = await AdminEndpoints.StaffAsync(user, accounts, AccountRole.Admin, cancellationToken);
            if (denied is not null) return denied;
            var db = accounts.Db!;
            // D300: Admin may see confirmed registered names; the Didit session id lets staff compare in Didit (no dates are returned).
            var query = from r in db.DobReviews join p in db.MemberProfiles on r.AccountId equals p.AccountId
                              join a in db.Accounts on r.AccountId equals a.Id
                              join t in db.IdVerificationAttempts on r.AttemptId equals t.Id into attempts from t in attempts.DefaultIfEmpty()
                              join s in db.Accounts on r.ResolvedBy equals (Guid?)s.Id into resolvers from s in resolvers.DefaultIfEmpty()
                              where open == false || r.ResolvedAt == null
                              select new { r, p.DisplayName, p.DobCheckAt, First = a.RegisteredNamesConfirmedAt != null ? a.RegisteredFirstName : null,
                                  Last = a.RegisteredNamesConfirmedAt != null ? a.RegisteredLastName : null, Session = t == null ? null : t.SessionId,
                                  ResolverFirst = s == null ? null : s.RegisteredFirstName, ResolverLast = s == null ? null : s.RegisteredLastName, ResolverEmail = s == null ? null : s.Email };
            // The open queue is oldest first; the full list (open=false) returns the newest 200 for history.
            var rows = await (open == false ? query.OrderByDescending(x => x.r.CreatedAt) : query.OrderBy(x => x.r.CreatedAt)).Take(200).ToListAsync(cancellationToken);
            var escalations = await EscalationEndpoints.ForReviewsAsync(db, rows.Select(x => x.r.Id).ToList(), current!, cancellationToken);
            return Results.Ok(rows.Select(x => ReviewView(x.r, x.DisplayName) with
            {
                FirstName = x.First, LastName = x.Last, DiditSessionId = string.IsNullOrEmpty(x.Session) ? null : x.Session,
                ResolvedByName = x.r.ResolvedBy is null ? null : StaffName(x.ResolverFirst, x.ResolverLast, x.ResolverEmail),
                ChangedAt = x.DobCheckAt ?? x.r.CreatedAt,
                IdImage = images.Enabled && x.r.ResolvedAt is null && !string.IsNullOrEmpty(x.Session),
                Escalation = escalations.GetValueOrDefault(x.r.Id),
            }).ToList());
        });
        reviews.MapPost("/{reviewId:guid}/resolve", async (Guid reviewId, DobReviewDecision request, ClaimsPrincipal user, AccountResolver accounts, IIdImageSource images, TimeProvider clock,
            CancellationToken cancellationToken) =>
        {
            var (current, denied) = await AdminEndpoints.StaffAsync(user, accounts, AccountRole.Admin, cancellationToken);
            if (denied is not null) return denied;
            if (!Enum.TryParse<DobReviewOutcome>(request.Outcome, true, out var outcome) || int.TryParse(request.Outcome, out _)
                || string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Length > ModerationPolicy.ReasonMax)
                return Problem(StatusCodes.Status400BadRequest, ProblemCodes.InvalidRequest, "Choose verified or not verified, with a reason.");
            var (db, now) = (accounts.Db!, clock.GetUtcNow());
            // Issue 488: the member's update and the decision never overlap, so a look always covers the date being approved.
            var subject = await db.DobReviews.Where(r => r.Id == reviewId).Select(r => (Guid?)r.AccountId).SingleOrDefaultAsync(cancellationToken);
            if (subject is null) return Results.NotFound();
            await using var lifecycle = await AccountLifecycleLease.AcquireAsync(db, subject.Value, cancellationToken);
            // PR 505 review: two admins at once make one decision, and an escalation closed meanwhile keeps its own resolution.
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var review = await db.DobReviews.FromSqlInterpolated($"SELECT * FROM dob_reviews WHERE id={reviewId} FOR UPDATE").SingleOrDefaultAsync(cancellationToken);
            if (review is null) return Results.NotFound();
            if (review.ResolvedAt is not null) return Problem(StatusCodes.Status409Conflict, ProblemCodes.InvalidRequest, "This review is already resolved.");
            // PR 491 review: a conflict of interest (D134) closes this route too, not only the escalation's own.
            if (await StaffAccessEndpoints.ExcludedAsync(db, current!.Account!.Id, [review.AccountId], cancellationToken)) return StaffAccessEndpoints.Conflict();
            // Issue 455 (D404): an escalated review is decided by an admin who didn't escalate it, which also closes the escalation.
            var escalation = await db.AccountEscalations.AsNoTracking().SingleOrDefaultAsync(e => e.DobReviewId == reviewId && e.ResolvedAt == null, cancellationToken);
            if (escalation is not null && !EscalationPolicy.MayResolve(escalation, current!.Account!.Id, true))
                return Problem(StatusCodes.Status403Forbidden, ProblemCodes.ModerationNotPermitted, "Another admin has to decide a review you escalated.");
            var profile = await db.MemberProfiles.SingleAsync(p => p.AccountId == review.AccountId, cancellationToken);
            // Issue 488 (D413, Q186 A): with ID images on, verifying approves the date of birth and the ID after the admin's own look.
            // PR 516 review: the admin decided on a date the member has since changed.
            if (request.ChangedAt is { } seen && seen != (profile.DobCheckAt ?? review.CreatedAt))
                return Problem(StatusCodes.Status409Conflict, ProblemCodes.RecordChanged, "The member changed their date of birth. Look at the review again.");
            var approvesId = images.Enabled && outcome == DobReviewOutcome.Verified;
            if (approvesId)
            {
                var viewed = await db.DobReviewIdViews.Where(v => v.ReviewId == reviewId && v.ActorAccountId == current.Account.Id).MaxAsync(v => (DateTimeOffset?)v.At, cancellationToken);
                switch (DobCorrectionPolicy.ApproveId(profile.IdStatus, viewed, profile.DobCheckAt ?? review.CreatedAt))
                {
                    case IdApprovalRefusal.NotViewed: return Problem(StatusCodes.Status409Conflict, ProblemCodes.IdNotViewed, "Open the ID document first, after the member's latest update.");
                    case IdApprovalRefusal.NotVerified: return Problem(StatusCodes.Status409Conflict, ProblemCodes.IdBadgeRemoved, "This member isn't ID Verified any more.");
                }
            }
            if (escalation is not null && await db.AccountEscalations.Where(e => e.Id == escalation.Id && e.ResolvedAt == null).ExecuteUpdateAsync(u => u
                    .SetProperty(e => e.ResolvedAt, now).SetProperty(e => e.ResolvedBy, current!.Account!.Id).SetProperty(e => e.ResolutionNote, "Date of birth review decided."), cancellationToken) == 1)
                Audit.Record(db, now, current.Account.Id, "account", "account.escalation-resolved", "account", review.AccountId.ToString(), escalation.Id.ToString());
            (review.ResolvedAt, review.ResolvedBy, review.Outcome, review.ResolutionNote) = (now, current!.Account!.Id, outcome, request.Reason.Trim());
            (profile.DateOfBirthVerified, profile.DateOfBirthVerifiedAt) = (outcome == DobReviewOutcome.Verified, outcome == DobReviewOutcome.Verified ? now : null);
            (profile.DobCheck, profile.DobCheckAt) = (DobCheck.None, now);
            if (approvesId)
            {
                review.IdApprovedAt = now;
                Audit.Record(db, now, current.Account.Id, "account", "id.review-approved", "account", review.AccountId.ToString(), reviewId.ToString());
            }
            // Issue 488 (D413): a decision withdraws an unlock the member hasn't used.
            await DobChangeEndpoints.RevokeUnusedAsync(db, review.AccountId, current.Account.Id, now, cancellationToken);
            Audit.Record(db, now, current.Account.Id, "account", "dob.review-resolved", "account", review.AccountId.ToString(), Wire(outcome));
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return Results.Ok(ReviewView(review, profile.DisplayName));
        });

        // Q125 A (D317): registered name changes by ID-verified members. ID Verified stays until an admin decides.
        var names = app.MapGroup("/api/admin/name-reviews").RequireAuthorization();
        names.MapGet("", async (bool? open, ClaimsPrincipal user, AccountResolver accounts, CancellationToken cancellationToken) =>
        {
            var (_, denied) = await AdminEndpoints.StaffAsync(user, accounts, AccountRole.Admin, cancellationToken);
            if (denied is not null) return denied;
            var db = accounts.Db!;
            var query = from r in db.NameChangeReviews join p in db.MemberProfiles on r.AccountId equals p.AccountId
                        join a in db.Accounts on r.AccountId equals a.Id
                        join s in db.Accounts on r.ResolvedBy equals (Guid?)s.Id into resolvers from s in resolvers.DefaultIfEmpty()
                        where open == false || r.ResolvedAt == null
                        select new { r, p.DisplayName, a.RegisteredFirstName, a.RegisteredLastName, a.RegisteredMononym,
                            ResolverFirst = s == null ? null : s.RegisteredFirstName, ResolverLast = s == null ? null : s.RegisteredLastName, ResolverEmail = s == null ? null : s.Email };
            var rows = await (open == false ? query.OrderByDescending(x => x.r.CreatedAt) : query.OrderBy(x => x.r.CreatedAt)).Take(200).ToListAsync(cancellationToken);
            return Results.Ok(rows.Select(x => new NameReviewView(x.r.Id, x.r.AccountId, x.DisplayName, x.RegisteredFirstName, x.RegisteredLastName, x.RegisteredMononym,
                x.r.Revision, x.r.CreatedAt, x.r.ResolvedAt, x.r.Outcome is { } o ? NameWire(o) : null, x.r.ResolutionNote,
                x.r.ResolvedBy is null ? null : StaffName(x.ResolverFirst, x.ResolverLast, x.ResolverEmail), x.r.Reason)).ToList());
        });
        names.MapPost("/{reviewId:guid}/resolve", async (Guid reviewId, NameReviewDecision request, ClaimsPrincipal user, AccountResolver accounts, TimeProvider clock,
            CancellationToken cancellationToken) =>
        {
            var (current, denied) = await AdminEndpoints.StaffAsync(user, accounts, AccountRole.Admin, cancellationToken);
            if (denied is not null) return denied;
            var outcome = request.Outcome switch { "matches" => NameReviewOutcome.Matches, "mismatch" => NameReviewOutcome.Mismatch, _ => (NameReviewOutcome?)null };
            if (outcome is null || string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Length > ModerationPolicy.ReasonMax)
                return Problem(StatusCodes.Status400BadRequest, ProblemCodes.InvalidRequest, "Choose matches or mismatch, with a reason.");
            var (db, now) = (accounts.Db!, clock.GetUtcNow());
            if (!current!.RecentlyAuthenticated(now)) return Problem(StatusCodes.Status403Forbidden, ProblemCodes.StaleSession, "Sign in again to do this.");
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var review = await db.NameChangeReviews.FindAsync([reviewId], cancellationToken);
            if (review is null) return Results.NotFound();
            if (review.ResolvedAt is not null) return Problem(StatusCodes.Status409Conflict, ProblemCodes.InvalidRequest, "This review is already resolved.");
            var account = await db.Accounts.SingleAsync(a => a.Id == review.AccountId, cancellationToken);
            if (account.RegisteredNamesRevision != review.Revision) return Problem(StatusCodes.Status409Conflict, ProblemCodes.InvalidRequest, "The member changed their name again. Reload and review the latest name.");
            var profile = await db.MemberProfiles.SingleAsync(p => p.AccountId == review.AccountId, cancellationToken);
            (review.ResolvedAt, review.ResolvedBy, review.Outcome, review.ResolutionNote) = (now, current.Account!.Id, outcome, request.Reason.Trim());
            if (outcome == NameReviewOutcome.Mismatch && profile.IdStatus == VerificationStatus.Verified)
            {
                // The earlier rule, now a staff decision: the badge goes and the member may verify again.
                (profile.IdStatus, profile.IdCheckedAt, profile.DateOfBirthVerified, profile.DateOfBirthVerifiedAt, profile.CanRequestIdVerification) = (VerificationStatus.Rejected, now, false, null, true);
                Audit.Record(db, now, current.Account.Id, "account", "verification.revoked-registered-name-change", "account", review.AccountId.ToString());
            }
            // Issue 533 (D429): the member's name check closes with the review; "matches" records the name as matching the ID.
            if (profile.NameCheck == NameCheck.ManualReview)
            {
                (profile.NameCheck, profile.NameCheckAt) = (NameCheck.None, null);
                account.RegisteredNamesMatchId = outcome == NameReviewOutcome.Matches;
            }
            Audit.Record(db, now, current.Account.Id, "account", "verification.name-review-resolved", "account", review.AccountId.ToString(), NameWire(outcome.Value));
            await db.SaveChangesAsync(cancellationToken); await transaction.CommitAsync(cancellationToken);
            return Results.Ok(new NameReviewView(review.Id, review.AccountId, profile.DisplayName, account.RegisteredFirstName, account.RegisteredLastName, account.RegisteredMononym,
                review.Revision, review.CreatedAt, review.ResolvedAt, NameWire(outcome.Value), review.ResolutionNote, Reason: review.Reason));
        });

        var admin = app.MapGroup("/api/admin/accounts/{id:guid}").RequireAuthorization();
        admin.MapGet("/verification", async (Guid id, ClaimsPrincipal user, AccountResolver accounts, CancellationToken cancellationToken) =>
        {
            var (_, denied) = await AdminEndpoints.StaffAsync(user, accounts, AccountRole.Admin, cancellationToken);
            if (denied is not null) return denied;
            var db = accounts.Db!;
            var profile = await db.MemberProfiles.FindAsync([id], cancellationToken);
            return profile is null ? Results.NotFound() : Results.Ok(await AdminViewAsync(db, profile, cancellationToken));
        });
        // D129: admins decide case by case. "allow-again" gives one more self-service attempt; "revoke" removes ID
        // Verified. Both need a recent sign-in and are audited.
        admin.MapPost("/verification/{action:regex(^(allow-again|revoke)$)}", async (Guid id, string action, ClaimsPrincipal user, AccountResolver accounts,
            TimeProvider clock, CancellationToken cancellationToken) =>
        {
            var (current, denied) = await AdminEndpoints.StaffAsync(user, accounts, AccountRole.Admin, cancellationToken);
            if (denied is not null) return denied;
            var now = clock.GetUtcNow();
            if (!current!.RecentlyAuthenticated(now)) return Problem(StatusCodes.Status403Forbidden, ProblemCodes.StaleSession, "Sign in again to do this.");
            var db = accounts.Db!;
            var profile = await db.MemberProfiles.FindAsync([id], cancellationToken);
            if (profile is null) return Results.NotFound();
            if (action == "allow-again")
            {
                if (profile.IdStatus == VerificationStatus.Verified || await OpenAttemptAsync(db, id, cancellationToken) is not null)
                    return Problem(StatusCodes.Status409Conflict, ProblemCodes.InvalidRequest, "This member is verified or has a check open.");
                profile.CanRequestIdVerification = true;
            }
            else
            {
                if (profile.IdStatus != VerificationStatus.Verified) return Problem(StatusCodes.Status409Conflict, ProblemCodes.InvalidRequest, "This member isn't ID verified.");
                (profile.IdStatus, profile.IdCheckedAt) = (VerificationStatus.Rejected, now);
            }
            Audit.Record(db, now, current.Account!.Id, "account", "verification." + action, "account", id.ToString());
            await db.SaveChangesAsync(cancellationToken);
            return Results.Ok(await AdminViewAsync(db, profile, cancellationToken));
        });
        // D127: an admin flags an account for age assurance, or clears the flag. Clearing it is only for a flag set in
        // error; an authorised adult resolution is the adult override.
        admin.MapPut("/age-assurance", async (Guid id, AgeAssuranceRequest request, ClaimsPrincipal user, AccountResolver accounts, TimeProvider clock,
            CancellationToken cancellationToken) =>
        {
            var (current, denied) = await AdminEndpoints.StaffAsync(user, accounts, AccountRole.Admin, cancellationToken);
            if (denied is not null) return denied;
            var now = clock.GetUtcNow();
            if (!current!.RecentlyAuthenticated(now)) return Problem(StatusCodes.Status403Forbidden, ProblemCodes.StaleSession, "Sign in again to do this.");
            var db = accounts.Db!;
            var profile = await db.MemberProfiles.FindAsync([id], cancellationToken);
            if (profile is null) return Results.NotFound();
            if (profile.AgeStatus is AgeStatus.Underage) return Problem(StatusCodes.Status409Conflict, ProblemCodes.InvalidRequest, "This account is locked as under 18.");
            if (request.NeedsVerification && profile.AgeStatus != AgeStatus.NeedsVerification)
                (profile.AgeStatus, profile.AgeStatusAt) = (AgeStatus.NeedsVerification, now);
            else if (!request.NeedsVerification && profile.AgeStatus == AgeStatus.NeedsVerification)
                (profile.AgeStatus, profile.AgeStatusAt) = (profile.DateOfBirth is not null || profile.DeclaredAge is not null ? AgeStatus.SelfDeclaredAdult : AgeStatus.Attested, now);
            Audit.Record(db, now, current.Account!.Id, "account", request.NeedsVerification ? "age.needs-verification" : "age.needs-verification-cleared", "account", id.ToString());
            await db.SaveChangesAsync(cancellationToken);
            return Results.Ok(await AdminViewAsync(db, profile, cancellationToken));
        });
    }

    /// <summary>D166: an admin-queue review for a date of birth that didn't match the ID; at most one open per member.</summary>
    private static async Task OpenReviewAsync(SwoovrDbContext db, Guid account, Guid attempt, string reason, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (await db.DobReviews.AnyAsync(r => r.AccountId == account && r.ResolvedAt == null, cancellationToken)) return;
        db.DobReviews.Add(new DobReview { Id = Guid.NewGuid(), AccountId = account, AttemptId = attempt, Reason = reason, CreatedAt = now });
    }

    private static DobReviewView ReviewView(DobReview r, string? name) =>
        new(r.Id, r.AccountId, name, r.Reason, r.CreatedAt, r.ResolvedAt, r.Outcome is { } o ? Wire(o) : null, ResolutionNote: r.ResolutionNote, IdApproved: r.IdApprovedAt is not null);

    private static string NameWire(NameReviewOutcome outcome) => outcome == NameReviewOutcome.Matches ? "matches" : "mismatch";

    private static string StaffName(string? first, string? last, string? email)
    {
        var name = string.Join(" ", new[] { first, last }.Where(n => !string.IsNullOrWhiteSpace(n)));
        return name.Length > 0 ? name : email ?? "Staff member";
    }

    private static string Short(string value, int max = 32) => value.Length <= max ? value : value[..max];

    private static Task<IdVerificationAttempt?> OpenAttemptAsync(SwoovrDbContext db, Guid accountId, CancellationToken cancellationToken) =>
        db.IdVerificationAttempts.Where(a => a.AccountId == accountId
            && (a.State == IdAttemptState.Created || a.State == IdAttemptState.InProgress || a.State == IdAttemptState.InReview))
            .OrderByDescending(a => a.CreatedAt).FirstOrDefaultAsync(cancellationToken);

    /// <summary>
    /// Applies a reported status to an attempt and the member's flags. States only move forward (IdVerificationPolicy.Next),
    /// so a stale re-read or an out-of-order webhook never undoes a result.
    /// </summary>
    public static async Task ApplyAsync(SwoovrDbContext db, IdVerificationAttempt attempt, ProviderSession session, DateTimeOffset now, string source,
        CancellationToken cancellationToken, bool save = true, VerifiedIdentityStore? identities = null)
    {
        // Reentrant: the webhook already holds this lease and keeps it until its own save (dedupe record included).
        await using var lifecycle=await AccountLifecycleLease.AcquireAsync(db,attempt.AccountId,cancellationToken);
        // A registered-name edit may have expired this attempt after the caller read it; decide on the committed state.
        await db.Entry(attempt).ReloadAsync(cancellationToken);
        var profile = await db.MemberProfiles.SingleAsync(p => p.AccountId == attempt.AccountId, cancellationToken);
        var account = await db.Accounts.SingleAsync(a => a.Id == attempt.AccountId, cancellationToken);
        await db.Entry(account).ReloadAsync(cancellationToken);await db.Entry(profile).ReloadAsync(cancellationToken);
        var reported = IdVerificationPolicy.Next(attempt.State, session.Status);
        if (reported is { } moved)
        {
            (attempt.State, attempt.ProviderStatus, attempt.UpdatedAt) = (moved, Short(session.Status), now);
            // Expiry or abandonment alone isn't a real attempt (D129); progress or a decision is.
            attempt.Started |= moved is IdAttemptState.InProgress or IdAttemptState.InReview or IdAttemptState.Approved or IdAttemptState.Declined;
            if (IdVerificationPolicy.Settled(moved)) (attempt.CompletedAt, attempt.Url) = (now, null);
            if (moved == IdAttemptState.Approved) attempt.Adult = session.Adult;
        }
        if(attempt.RegisteredNamesRevision!=account.RegisteredNamesRevision)
        {
            // Old declarations cannot restore a revoked badge. A confirmed underage result still protects safety, even
            // when the name edit already expired the attempt and the ordinary state transition is refused.
            if(IdVerificationPolicy.Map(session.Status)==IdAttemptState.Approved&&session.Adult==false&&profile.AgeStatus!=AgeStatus.Underage)
            {
                account.Status=AccountStatus.Locked;account.StatusReason="underage";
                profile.AgeStatus=AgeStatus.Underage;profile.AgeStatusAt=now;profile.IdStatus=VerificationStatus.Rejected;profile.IdCheckedAt=now;
                Audit.Record(db,now,null,"system","account.locked","account",account.Id.ToString(),"underage");
            }
            Audit.Record(db,now,null,"system","verification.stale-registered-names","id-verification",attempt.Id.ToString());
            if(save)await db.SaveChangesAsync(cancellationToken);
            return;
        }
        if (reported is not { } next) return;
        if (profile.IdStatus != VerificationStatus.Verified || next == IdAttemptState.Approved)
            (profile.IdStatus, profile.IdCheckedAt) = (IdVerificationPolicy.StatusFor(next), IdVerificationPolicy.Settled(next) ? now : profile.IdCheckedAt);
        if (IdVerificationPolicy.ReturnsAttempt(attempt)) profile.CanRequestIdVerification = true;
        Audit.Record(db, now, null, "system", "verification." + next.ToString().ToLowerInvariant(), "id-verification", attempt.Id.ToString(), source);
        if (next == IdAttemptState.Approved)
        {
            account.RegisteredNamesMatchId=null;
            // Issue 533 (D429): the registered name against the ID's. Without names in this delivery the identity store reads
            // Didit's decision and checks there (VerifiedIdentityStore.RecordAsync).
            if(session.Identity is{}declaredDocument)NameCheckAfterApproval(db,account,profile,declaredDocument.FirstName,declaredDocument.LastName,now);
            var age = IdVerificationPolicy.AgeAfterApproval(profile.AgeStatus, session.Adult);
            if (age != profile.AgeStatus)
            {
                (profile.AgeStatus, profile.AgeStatusAt) = (age, now);
                Audit.Record(db, now, null, "system", "age.verified", "account", account.Id.ToString(), age.ToString());
            }
            if (age == AgeStatus.Underage)
            {
                // D127: confirmed under-18 accounts are not permitted; the account locks for review.
                (account.Status, account.StatusReason) = (AccountStatus.Locked, "underage");
                (profile.IdStatus, profile.IdCheckedAt) = (VerificationStatus.Rejected, now);
                Audit.Record(db, now, null, "system", "account.locked", "account", account.Id.ToString(), "underage");
            }
            // D158, D166: compare the document's date of birth with the profile's; a match marks it verified. A different
            // date opens the member's one correction; no date at all goes to an admin's review. ID Verified stays either
            // way. The document's date is never stored; the audit says what happened, never either date.
            var check = DobCorrectionPolicy.AfterApproval(profile.DateOfBirth, session.DateOfBirth);
            (profile.DateOfBirthVerified, profile.DateOfBirthVerifiedAt) = (check == DobCheck.None, check == DobCheck.None ? now : null);
            (profile.DobCheck, profile.DobCheckAt) = (check, check == DobCheck.None ? null : now);
            Audit.Record(db, now, null, "system", check switch { DobCheck.None => "dob.verified", DobCheck.CorrectionNeeded => "dob.correction-needed", _ => "dob.review-opened" },
                "account", account.Id.ToString(), check == DobCheck.ManualReview ? DobCorrectionPolicy.ReviewReason(session) : null);
            if (check == DobCheck.ManualReview) await OpenReviewAsync(db, profile.AccountId, attempt.Id, DobCorrectionPolicy.ReviewReason(session), now, cancellationToken);
            // D191: the minimum verified identity record, sealed in the restricted store.
            if (identities is not null) await identities.RecordAsync(account.Id, attempt, session, now, cancellationToken);
            if (FounderEligibility.Stamp(profile, account.Status, now))
                Audit.Record(db, now, null, "system", "founder.eligible", "account", account.Id.ToString());
        }
        if (save) await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Issue 533 (Q192 A, D429): after an approved check, compare the registered name with the ID's (the ID's name is never
    /// stored). The same name marks it matched; middle names only, or any other difference, opens the member's one-time
    /// correction. No name from Didit changes nothing. An open admin review stays open. The audit names the kind of
    /// difference, never a name. Unsaved.
    /// </summary>
    public static void NameCheckAfterApproval(SwoovrDbContext db, Account account, MemberProfile profile, string? first, string? last, DateTimeOffset now)
    {
        if (!RegisteredNamePolicy.Complete(account)) return;
        var comparison = RegisteredNamePolicy.Compare(new(account.RegisteredFirstName!, account.RegisteredLastName, account.RegisteredMononym), first, last);
        if (comparison == NameComparison.Unknown) return;
        account.RegisteredNamesMatchId = comparison == NameComparison.Same;
        var check = comparison == NameComparison.Same ? NameCheck.None : NameCheck.CorrectionNeeded;
        if (profile.NameCheck == NameCheck.ManualReview && check != NameCheck.None) return;
        if (profile.NameCheck == check) return;
        (profile.NameCheck, profile.NameCheckAt) = (check, check == NameCheck.None ? null : now);
        Audit.Record(db, now, null, "system", check == NameCheck.None ? "name.verified" : "name.correction-needed", "account", account.Id.ToString(),
            comparison switch { NameComparison.MiddleOnly => "middle-names", NameComparison.Different => "different", _ => null });
    }

    /// <summary>
    /// Issue 533: the member's latest approved attempt and the names on its ID, read from Didit now. First/Last are the ID's
    /// names as given (trimmed; null First when Didit can't say); Names is them as valid registered names, or null when they
    /// aren't (PR 581 review: such a name can still go to a manual review).
    /// </summary>
    private static async Task<(IdVerificationAttempt? Attempt, string? First, string? Last, RegisteredNames? Names)> IdNameAsync(SwoovrDbContext db, Guid account,
        IIdVerificationProvider provider, CancellationToken cancellationToken)
    {
        var attempt = await db.IdVerificationAttempts.Where(a => a.AccountId == account && a.State == IdAttemptState.Approved)
            .OrderByDescending(a => a.CompletedAt).FirstOrDefaultAsync(cancellationToken);
        var decision = attempt is null || !provider.Configured ? null : await provider.GetDecisionAsync(attempt.SessionId, cancellationToken);
        if (attempt is null || decision is null || decision.SessionId != attempt.SessionId || decision.VendorData != account.ToString() || decision.Identity is not { } id
            || string.IsNullOrWhiteSpace(id.FirstName)) return (attempt, null, null, null);
        var last = string.IsNullOrWhiteSpace(id.LastName) ? null : id.LastName.Trim();
        return (attempt, id.FirstName.Trim(), last, RegisteredNamePolicy.Validate(id.FirstName, last, last is null));
    }

    private static async Task<IdVerificationView> ViewAsync(SwoovrDbContext db, MemberProfile profile, IIdVerificationProvider provider,
        IConfiguration configuration, CancellationToken cancellationToken)
    {
        var attempts = await db.IdVerificationAttempts.Where(a => a.AccountId == profile.AccountId).OrderByDescending(a => a.CreatedAt).ToListAsync(cancellationToken);
        var latest = attempts.FirstOrDefault();
        var refusal = IdVerificationPolicy.CanStart(profile, attempts);
        return new(Wire(profile.IdStatus), profile.IdCheckedAt, latest is null ? null : Wire(latest.State), latest?.Url,
            refusal == IdStartRefusal.None, refusal == IdStartRefusal.None ? null : Wire(refusal), provider.Configured,
            configuration.GetValue("Didit:TestMode", true), AgeAssurance.Blocks(profile.AgeStatus));
    }

    private static async Task<AdminVerificationView> AdminViewAsync(SwoovrDbContext db, MemberProfile profile, CancellationToken cancellationToken)
    {
        var attempts = await db.IdVerificationAttempts.Where(a => a.AccountId == profile.AccountId).OrderByDescending(a => a.CreatedAt).ToListAsync(cancellationToken);
        return new(Wire(profile.IdStatus), profile.CanRequestIdVerification, Wire(profile.AgeStatus),
            attempts.Select(a => new IdAttemptAdminView(a.Id, Wire(a.State), a.ProviderStatus, a.Started, a.SelfService, a.Adult, a.CreatedAt, a.UpdatedAt, a.CompletedAt)).ToList(),
            await db.DobReviews.AnyAsync(r => r.AccountId == profile.AccountId && r.ResolvedAt == null, cancellationToken));
    }
}
