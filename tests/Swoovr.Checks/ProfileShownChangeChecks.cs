using Swoovr.Domain;

/// <summary>Issue 568 (D436, PR 582 review): "Profile updated" moves only when the profile as shown changes.</summary>
static class ProfileShownChangeChecks
{
    public static void Run()
    {
        static void Check(string label, bool ok) { if (!ok) throw new Exception("Profile shown change: " + label); }
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        static string P(string id) => $$"""{"id":"{{id}}","sha256":"h{{id}}","isMain":false}""";
        static string M(string id) => $$"""{"id":"{{id}}","sha256":"h{{id}}","isMain":true}""";
        static string Mode(string main, string photos, string about = "null", bool share = false) =>
            $$"""{"enabled":true,"visible":true,"shownName":"Fictional","modeAbout":{{about}},"shareCoreAbout":{{(share ? "true" : "false")}},"shownAbout":{{about}},"intentions":[],"mainPhoto":{{main}},"photos":[{{photos}}]}""";
        ProfileVersion V(string mode, int day, string json) => new() { ModeId = mode, CapturedAt = t0.AddDays(day), ContentJson = json };
        string[] friends = ["friends"], both = ["dating", "friends"];

        // The main photo leads and isn't repeated, so reordering [A, B] to [B, A] behind main A shows the same gallery.
        var reorder = new[] { V("friends", 0, Mode(M("a"), P("a") + "," + P("b"))), V("friends", 5, Mode(M("a"), P("b") + "," + P("a"))) };
        Check("reorder behind the main photo", ProfileShownChange.Last(reorder, friends, "friends", false) == t0);
        // A photo already shown by an earlier mode isn't shown again, so moving it in a later mode changes nothing.
        var duplicate = new[] { V("dating", 0, Mode("null", P("a"))), V("friends", 0, Mode("null", P("a") + "," + P("b"))), V("friends", 5, Mode("null", P("b") + "," + P("a"))) };
        Check("a repeat in a later mode", ProfileShownChange.Last(duplicate, both, "dating", false) == t0);
        Check("but it shows on its own view", ProfileShownChange.Last(duplicate, friends, "friends", false) == t0.AddDays(5));
        // Another mode's main photo isn't shown; the viewed mode's is.
        var main = new[] { V("dating", 0, Mode("null", "")), V("friends", 0, Mode(M("a"), P("a") + "," + P("b"))), V("friends", 5, Mode(M("b"), P("a") + "," + P("b"))) };
        Check("another mode's main photo", ProfileShownChange.Last(main, both, "dating", false) == t0);
        Check("the viewed mode's main photo", ProfileShownChange.Last(main, friends, "friends", false) == t0.AddDays(5));
        // A setting nobody sees (Share my About behind the mode's own words).
        var consent = new[] { V("friends", 0, Mode("null", "", "\"Words\"")), V("friends", 5, Mode("null", "", "\"Words\"", share: true)) };
        Check("a hidden consent", ProfileShownChange.Last(consent, friends, "friends", false) == t0);
        // The core text picture counts only while it is what's shown; an unshown mode never counts.
        var core = new[] { V("friends", 0, Mode("null", "")), V("core", 3, """{"displayName":"Fictional","about":"x","pictureText":"Hi"}"""), V("dating", 4, Mode("null", P("z"))) };
        Check("text picture shown", ProfileShownChange.Last(core, friends, "friends", true) == t0.AddDays(3));
        Check("text picture hidden, unshown mode", ProfileShownChange.Last(core, friends, "friends", false) == t0);
        Console.WriteLine("PASS profile shown change: gallery as shown (lead main photo, each photo once), hidden settings and modes ignored.");
    }
}
