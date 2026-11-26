using Swoovr.Domain;

/// <summary>
/// Owner answer to Q58 (issue #33, D166): an approved ID check whose date of birth differs opens one correction; the
/// corrected date is compared with Didit's decision again; a match verifies it, anything else goes to an admin's review.
/// </summary>
static class DobCorrectionChecks
{
    public static void Run()
    {
        var checks = 0;
        void Expect(bool condition, string what) { if (!condition) throw new Exception("Date-of-birth correction check failed: " + what); checks++; }
        DateOnly D(string s) => DateOnly.Parse(s);

        Expect(DobCorrectionPolicy.AfterApproval(D("1990-01-01"), D("1990-01-01")) == DobCheck.None, "the same date: verified, nothing to do");
        Expect(DobCorrectionPolicy.AfterApproval(D("1990-01-01"), D("1991-01-01")) == DobCheck.CorrectionNeeded, "a different year: one correction");
        Expect(DobCorrectionPolicy.AfterApproval(D("1990-01-01"), D("1990-01-02")) == DobCheck.CorrectionNeeded, "a day apart is still different");
        Expect(DobCorrectionPolicy.AfterApproval(null, D("1990-01-01")) == DobCheck.CorrectionNeeded, "no date on the profile: correct it");
        Expect(DobCorrectionPolicy.AfterApproval(D("1990-01-01"), null) == DobCheck.ManualReview, "Didit gave no date: straight to review");
        Expect(DobCorrectionPolicy.AfterCorrection(D("1991-01-01"), D("1991-01-01")) == DobCorrectionResult.Verified, "a corrected date that matches is verified");
        Expect(DobCorrectionPolicy.AfterCorrection(D("1991-01-02"), D("1991-01-01")) == DobCorrectionResult.ManualReview, "still different: an admin reviews it");
        Expect(DobCorrectionPolicy.AfterCorrection(D("1991-01-01"), null) == DobCorrectionResult.ManualReview, "Didit no longer gives a date: review");
        Expect(DobCorrectionPolicy.OrdinaryChangeAllowed(DobCheck.None), "the ordinary form works with nothing open");
        Expect(!DobCorrectionPolicy.OrdinaryChangeAllowed(DobCheck.CorrectionNeeded) && !DobCorrectionPolicy.OrdinaryChangeAllowed(DobCheck.ManualReview),
            "the prompt is the only way while a correction or review is open");
        // Issue 498 (D409): the new member window.
        var clock = new DateTimeOffset(2026, 11, 25, 0, 0, 0, TimeSpan.Zero);
        // Issue 495 (D418): mode rules for a card.
        Expect(ModePrivacy.OnCard("dating", 34, "Woman", "she/her") == (34, "Woman", "she/her") && ModePrivacy.OnCard("friends", 34, "Woman", null) == (34, "Woman", null)
            && ModePrivacy.OnCard("activities", 34, "Woman", "she/her") == (null, null, null) && ModePrivacy.OnCard("business", 34, "Woman", "she/her") == (null, null, null), "identity on cards per mode");
        Expect(NewMemberPolicy.From(14, clock) == clock.AddDays(-14) && NewMemberPolicy.From(0, clock) is null && NewMemberPolicy.From(-1, clock) is null && NewMemberPolicy.From(int.MaxValue, clock) == clock.AddDays(-90), "new member window: configurable, off at 0, at most 90 days");
        // Issue 455 (D404): escalations hold no dates, and only another admin resolves one.
        foreach (var date in new[] { "born 3/2/1991", "1991-02-03", "3 February 1991", "Feb 3rd, 1991", "ID says 03.02.91" }) Expect(EscalationPolicy.Text(date) is null, "refused: " + date);
        Expect(EscalationPolicy.Text("  Passport looks altered, please check  ") == "Passport looks altered, please check" && EscalationPolicy.Text(" ") is null && EscalationPolicy.Text(new string('a', 1001)) is null && EscalationPolicy.Text("See SW-123456, 2 of 4") is not null, "a plain reason");
        var (raiser, otherAdmin) = (Guid.NewGuid(), Guid.NewGuid());
        var raised = new AccountEscalation { CreatedBy = raiser };
        Expect(EscalationPolicy.MayResolve(raised, otherAdmin, true) && !EscalationPolicy.MayResolve(raised, raiser, true) && !EscalationPolicy.MayResolve(raised, otherAdmin, false), "another admin only");
        Expect(!EscalationPolicy.MayResolve(new AccountEscalation { CreatedBy = raiser, ResolvedAt = DateTimeOffset.UtcNow }, otherAdmin, true), "once");
        Expect(!typeof(AccountEscalation).GetProperties().Any(p => p.PropertyType == typeof(DateOnly) || p.PropertyType == typeof(DateOnly?)), "an escalation stores no date of birth");
        Expect(Enum.GetValues<DobCheck>().Length == 3 && Enum.GetValues<DobReviewOutcome>().Length == 2, "three states, two review outcomes");
        // Issue 488 (D413, Q186 A): approving needs the same admin's look since the member's last change, and a badge to keep.
        var changed = new DateTimeOffset(2026, 11, 25, 1, 0, 0, TimeSpan.Zero);
        Expect(DobCorrectionPolicy.ApproveId(VerificationStatus.Verified, changed.AddMinutes(1), changed) == IdApprovalRefusal.None, "looked since the change");
        Expect(DobCorrectionPolicy.ApproveId(VerificationStatus.Verified, null, changed) == IdApprovalRefusal.NotViewed
            && DobCorrectionPolicy.ApproveId(VerificationStatus.Verified, changed, changed) == IdApprovalRefusal.NotViewed
            && DobCorrectionPolicy.ApproveId(VerificationStatus.Verified, changed.AddMinutes(-1), changed) == IdApprovalRefusal.NotViewed, "a look before the change doesn't count");
        foreach (var status in Enum.GetValues<VerificationStatus>().Where(s => s != VerificationStatus.Verified))
            Expect(DobCorrectionPolicy.ApproveId(status, changed.AddMinutes(1), changed) == IdApprovalRefusal.NotVerified, "never restores a badge: " + status);
        // Nothing about the document's date is held: the review has no date fields.
        Expect(!typeof(DobReview).GetProperties().Any(p => p.PropertyType == typeof(DateOnly) || p.PropertyType == typeof(DateOnly?)), "a review stores no date of birth");
        var now = DateTimeOffset.Parse("2026-11-02T10:00:00Z");
        var member = Guid.NewGuid();
        var grant = new DobChangeGrant { AccountId = member, CreatedAt = now, ExpiresAt = now.AddHours(24) };
        Expect(!DobChangePolicy.CanSave(null, false, null, member, now), "initial entry requires explicit confirmation");
        Expect(DobChangePolicy.CanSave(null, true, null, member, now), "confirmed first entry allowed");
        Expect(!DobChangePolicy.CanSave(D("1990-01-01"), true, null, member, now), "existing unverified date locked without migration grace");
        Expect(DobChangePolicy.CanSave(D("1990-01-01"), true, grant, member, now), "live member-bound grant allows confirmed correction");
        Expect(!DobChangePolicy.CanSave(D("1990-01-01"), false, grant, member, now), "grant never skips confirmation");
        Expect(!DobChangePolicy.Live(grant, Guid.NewGuid(), now), "wrong member refused");
        Expect(!DobChangePolicy.Live(grant, member, now.AddHours(24)), "expiry boundary refused");
        grant.ConsumedAt = now;
        Expect(!DobChangePolicy.Live(grant, member, now), "consumed grant cannot replay");
        grant.ConsumedAt = null; grant.RevokedAt = now;
        Expect(!DobChangePolicy.Live(grant, member, now), "revoked grant refused");
        Expect(DobChangePolicy.MayGrant(AccountRole.Admin) && !DobChangePolicy.MayGrant(AccountRole.Support) && !DobChangePolicy.MayGrant(AccountRole.Legal), "only Admin capability grants corrections");
        var key = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        var history = DobChangePolicy.Seal(member, D("1990-01-01"), 1, key);
        var keys = new Dictionary<byte, byte[]> { [1] = key };
        Expect(DobChangePolicy.Open(member, keys, history) == D("1990-01-01"), "prior date round-trips encrypted");
        var refused = false;
        try { DobChangePolicy.Open(Guid.NewGuid(), keys, history); } catch (System.Security.Cryptography.CryptographicException) { refused = true; }
        Expect(refused, "encrypted history cannot be moved to another member");


        Console.WriteLine($"PASS: date-of-birth correction ({checks}).");
    }
}
