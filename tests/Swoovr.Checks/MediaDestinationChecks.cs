extern alias AdminHost;
using MediaText = AdminHost::Swoovr.Admin.MediaText;

/// <summary>Issues 536 and 537: the Destination row in Admin → Media review.</summary>
static class MediaDestinationChecks
{
    public static void Run()
    {
        var count = 0;
        void Check(bool condition, string name) { if (!condition) throw new Exception("media destination: " + name); count++; }
        Check(MediaText.Destination("library", "nudity-signal") == ("Photo library", "library"), "a flagged library upload is the amber pill");
        Check(MediaText.Destination("library", "manual-review") == ("Photo library", null), "other library signals stay plain text");
        Check(MediaText.Destination(null, "nudity-signal") == ("Photo library", "library"), "no destination is the library, as before");
        Check(MediaText.Destination("album", "nudity-signal") == ("Private album", "album"), "a private album is green, flagged or not");
        Check(MediaText.Destination("album", null) == ("Private album", "album"), "a private album without a signal is green");
        Check(MediaText.Reason("APPROVED_ALBUM_EXPLICIT") == "Adult content: private albums only", "the X reason's label");
        Console.WriteLine($"PASS: media review destination pills ({count} checks).");
    }
}
