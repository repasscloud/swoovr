namespace Swoovr.Client;

/// <summary>
/// Photo wording and links shared by the web and the app (D203). The API returns a signed path `api/media/{token}`:
/// the browser loads it through the web host's `/media/{token}`, the app straight from the API.
/// </summary>
public static class PhotoText
{
    public static string Web(string link) => "/media/" + link[(link.LastIndexOf('/') + 1)..];
    public static string App(Uri apiBase, string link) => new Uri(apiBase, link).ToString();

    /// <summary>Issue 475: a date without the time in the viewer's local zone, "24 Nov 2026".</summary>
    public static string Day(DateTimeOffset at) => at.ToLocalTime().ToString("d MMM yyyy", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>One photo in a profile gallery: the mode it was shown from (null for the main photo).</summary>
    public sealed record GalleryPhoto(Swoovr.Contracts.PhotoView Photo, string? ModeId, string? ModeLabel);

    /// <summary>
    /// Issue 445: the profile gallery. The main photo first, then each shown mode's photos in their order, each distinct
    /// photo exactly once. The main photo is also one of its mode's placements and a photo can be placed in more than
    /// one mode, so the API lists them more than once; nothing stored is removed, only the repeat isn't shown.
    /// </summary>
    public static IReadOnlyList<GalleryPhoto> Gallery(Swoovr.Contracts.PhotoView? main, IEnumerable<(string Id, string Label, IReadOnlyList<Swoovr.Contracts.PhotoView>? Photos)> modes)
    {
        var seen = new HashSet<Guid>();
        var gallery = new List<GalleryPhoto>();
        if (main is not null && seen.Add(main.Id)) gallery.Add(new(main, null, null));
        foreach (var mode in modes)
            foreach (var photo in mode.Photos ?? [])
                if (seen.Add(photo.Id)) gallery.Add(new(photo, mode.Id, mode.Label));
        return gallery;
    }

    /// <summary>What a member's own photo state means. Every refusal reads the same, whatever the reason.</summary>
    public static string State(string state) => state switch
    {
        "checking" => "Checking — not visible to anyone yet",
        "in-review" => "Being reviewed — not visible to anyone yet",
        "approved" => "Approved",
        "not-approved" => "Not approved — this photo can't be used on Swoovr",
        _ => "Removed",
    };

    /// <summary>#41: a chat photo's state as its sender sees it. The other person sees it only once it's approved.</summary>
    public static string ChatState(string? state) => state switch
    {
        "checking" => "Checking — they'll see it once it's approved",
        "in-review" => "Being reviewed — they'll see it once it's approved",
        "not-approved" => "Not approved — they won't see it",
        "removed" => "Removed",
        _ => "",
    };

    /// <summary>Issue 542: the blanket "No nudity or sexual content" line is gone (explicit media has its own rules, #431).</summary>
    public const string ChatRules = "Photos are checked before the other person sees them.";

    /// <summary>#41, Q85: the fixed closing messages offered when ending a conversation.</summary>
    public static readonly IReadOnlyList<(string Id, string Label)> Closings =
    [
        ("thanks", "“Thanks for chatting. I don't think we're the right match, but I wish you all the best.”"),
        ("not-a-match", "“I've enjoyed our conversation, but I've decided not to continue. Take care.”"),
        ("take-care", "“I'm moving in a different direction, but thank you for your time. Wishing you well.”"),
    ];

    public const string ClosedTitle = "Conversation closed";
    public const string ClosedBody = "This conversation has ended. Your messages are still available to view, but you can no longer reply.";
    public const string ClosedSafety = "Report and block options remain available.";

    public const string EndHelp = "Ending disconnects you in every mode and closes this chat for both of you. You both keep the history. They can't ask to connect again for 30 days. It doesn't block or report them.";

    public const string Rules = "Photos are checked before anyone can see them. No nudity or sexual content, no one under 18, and only photos you have the right to share. Location details in photos are removed.";

    /// <summary>#231: what may go in a private album. Adult content needs ID Verified status (owner, 6 November 2026).</summary>
    public static string AlbumRules(bool idVerified) => idVerified
        ? "Photos are checked before anyone can see them. Adult content is allowed in private albums only. Never anyone under 18, nothing without consent, and only photos you have the right to share. Each one uses a library spot."
        : "Photos are checked before anyone can see them. No nudity or sexual content until you verify your ID. Each one uses a library spot.";

    public const string AlbumsIntro = "Only people you share an album with can see it, in a chat in a mode you've allowed. Up to 10 photos per album, from your library.";
    public const string AlbumsNeedId = "Adult photos need ID verification. Your albums can hold non-explicit photos now.";
    public const string ExplicitIntro = "Choose, for each profile, whether people can send you explicit photos in chat. It's off until you turn it on.";
    public const string ExplicitNeedsId = "Only ID Verified members can send or receive explicit media. Verify your ID to turn this on.";

    /// <summary>#231: a member's own photo, as the library shows it.</summary>
    public static string Short(string state, bool albumOnly) => state switch
    {
        "checking" => "Being checked",
        "in-review" => "Being reviewed",
        "not-approved" => "Not approved",
        "removed" => "Removed",
        _ => albumOnly ? "Approved for private albums only" : "Approved",
    };

    public static string Refusal(string? code) => code switch
    {
        "album-limit" => "You can have up to 4 albums.",
        "album-full" => "An album holds up to 10 photos.",
        "unknown-album" => "That album isn't available any more.",
        "invalid-album" => "Give the album a name of up to 40 characters, and choose modes from the list.",
        "album-only-photo" => "Adult photos in an album can only be deleted.",
        "id-verification-required" => "Only ID Verified members can do this.",
        "photo-too-big" => "Photos can be up to 10 MB.",
        "photo-unsupported" => "Use a JPEG, PNG or WebP photo (not animated).",
        "photo-invalid" => "That photo couldn't be used. Photos need to be at least 200 pixels on each side.",
        "photo-limit" => "You've reached the photo limit. Delete one first.",
        "photo-refused" => "That file can't be used. Choose a different photo.",
        "photo-check-unavailable" => "Photos can't be checked right now. Try again in a few minutes.",
        "upload-limit" => "You've uploaded a lot of photos in the last hour. Try again later.",
        "attachment-limit" => "You can send up to 20 photos a day.",
        "photos-off" => "This person isn't accepting photos in chat.",
        "photo-not-approved" => "Only approved photos can be shown.",
        "invalid-placement" => "That's more photos than this mode can show. Remove one first.",
        "media-unavailable" => "Photos aren't available right now.",
        _ => "Something went wrong. Try again.",
    };
}
