using System.Text.RegularExpressions;

/// <summary>
/// Issue 467 (owner, 24 November 2026): another member's photos on the profile gallery, the photo viewer, chat and the
/// Explore cards can't be right-clicked, dragged, selected or long-pressed to save. Source-level: every photo image in
/// those components carries the guard. The owned person-profile browser suite checks the behaviour on the rendered page.
/// It only stops casual saving; screenshots still work, and the short-lived signed links (D203) are the real protection.
/// </summary>
static partial class PhotoGuardChecks
{
    public static void Run()
    {
        var root = Root();
        var count = 0;
        foreach (var (file, expected) in new[]
        {
            ("src/Swoovr.Web.Client/Profiles/PersonPanel.razor", 2),        // lead and other gallery photos
            ("src/Swoovr.Web.Client/Profiles/PersonPhotoViewer.razor", 1),  // the viewer
            ("src/Swoovr.Web.Client/Messaging/ConversationPanel.razor", 4), // chat photos, adult chat photos and the sender's own in its dialog (issue 546), and the header thumbnail (issue 542)
        })
        {
            var images = ImagePattern().Matches(File.ReadAllText(Path.Combine(root, file))).Select(m => m.Value).ToList();
            if (images.Count != expected) throw new Exception($"Photo guard: expected {expected} photo images in {file}, found {images.Count}.");
            foreach (var image in images)
                if (!image.Contains("draggable=\"false\"") || !image.Contains("@oncontextmenu:preventDefault=\"true\"") || !image.Contains("@ondragstart:preventDefault=\"true\"") || !image.Contains("data-photo-guard"))
                    throw new Exception($"Photo guard missing in {file}: {image[..Math.Min(120, image.Length)]}");
            count += images.Count;
        }
        // Explore cards build the image in code.
        var tile = File.ReadAllText(Path.Combine(root, "src/Swoovr.Web.Client/Profiles/ExplorePanel.razor"));
        var builder = tile[tile.IndexOf("TilePhoto(PhotoView", StringComparison.Ordinal)..];
        builder = builder[..builder.IndexOf("};", StringComparison.Ordinal)];
        if (!builder.Contains("\"draggable\", \"false\"") || !builder.Contains("\"data-photo-guard\"") || !builder.Contains("AddEventPreventDefaultAttribute(7, \"oncontextmenu\", true)")
            || !builder.Contains("AddEventPreventDefaultAttribute(8, \"ondragstart\", true)"))
            throw new Exception("Photo guard missing on the Explore card photo.");
        count++;
        var css = File.ReadAllText(Path.Combine(root, "src/Swoovr.Web/wwwroot/app.css"));
        var rule = Regex.Match(css, @"\[data-photo-guard\]\s*\{([^}]*)\}");
        if (!rule.Success || !rule.Groups[1].Value.Contains("user-select: none") || !rule.Groups[1].Value.Contains("-webkit-touch-callout: none"))
            throw new Exception("Photo guard CSS (user-select, -webkit-touch-callout) is missing.");
        Console.WriteLine($"PASS photo save deterrent (issue 467): {count} photo images guarded (profile gallery, viewer, chat, Explore card) and the CSS rule; source-level, the person-profile browser suite checks behaviour.");
    }

    [GeneratedRegex("<img\\b[^>]*>", RegexOptions.Singleline)] private static partial Regex ImagePattern();

    static string Root()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Swoovr.sln"))) return dir.FullName;
        throw new Exception("Photo guard checks need the repository (Swoovr.sln not found above the checks).");
    }
}
