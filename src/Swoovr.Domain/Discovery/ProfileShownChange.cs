using System.Text.Json;

namespace Swoovr.Domain;

/// <summary>
/// Issue 568 (D436): when what a viewer can see of a profile last changed, from its profile versions (#91). Versions also
/// hold settings nobody sees (the raw mode name and About, Share my About, on/visible, main-photo flags), so each moment
/// is reduced to the profile as shown and compared with the moment before (PR 582 review): every shown mode's name, About
/// and "Looking for" chips, the gallery exactly as PhotoText.Gallery builds it (the lead mode's main photo first, then
/// each shown mode's photos in order, each photo once), and the core text picture only while that is what's shown.
/// </summary>
public static class ProfileShownChange
{
    public static DateTimeOffset? Last(IEnumerable<ProfileVersion> versions, IReadOnlyList<string> shownModes, string lead, bool pictureTextShown)
    {
        var state = new Dictionary<string, Snapshot>();
        string? previous = null;
        DateTimeOffset? changed = null;
        foreach (var moment in versions.Where(v => shownModes.Contains(v.ModeId) || pictureTextShown && v.ModeId == ProfileVersions.Core)
                     .GroupBy(v => v.CapturedAt).OrderBy(g => g.Key))
        {
            foreach (var version in moment) if (Read(version.ContentJson) is { } snapshot) state[version.ModeId] = snapshot;
            var shown = Shown(state, shownModes, lead, pictureTextShown);
            if (shown != previous) changed = moment.Key;
            previous = shown;
        }
        return changed;
    }

    private sealed record Snapshot(string Text, string? Main, IReadOnlyList<string> Photos, string? PictureText);

    private static string Shown(Dictionary<string, Snapshot> state, IReadOnlyList<string> shownModes, string lead, bool pictureTextShown)
    {
        var parts = shownModes.Select(m => state.GetValueOrDefault(m)?.Text ?? "").ToList();
        var seen = new HashSet<string>();
        var gallery = new List<string>();
        if (state.GetValueOrDefault(lead)?.Main is { } main && seen.Add(main)) gallery.Add(main);
        foreach (var mode in shownModes)
            foreach (var photo in state.GetValueOrDefault(mode)?.Photos ?? [])
                if (seen.Add(photo)) gallery.Add(photo);
        parts.Add(string.Join(',', gallery));
        if (pictureTextShown) parts.Add(state.GetValueOrDefault(ProfileVersions.Core)?.PictureText ?? "");
        return string.Join('\u001f', parts);
    }

    private static Snapshot? Read(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var text = string.Join('\u001e', Raw(root, "shownName", "null"), Raw(root, "shownAbout", "null"), Raw(root, "intentions", "[]"));
            var main = root.TryGetProperty("mainPhoto", out var m) && m.ValueKind == JsonValueKind.Object ? Photo(m) : null;
            var photos = root.TryGetProperty("photos", out var p) && p.ValueKind == JsonValueKind.Array ? p.EnumerateArray().Select(Photo).ToList() : [];
            var picture = root.TryGetProperty("pictureText", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            return new(text, main, photos, picture);
        }
        catch (JsonException) { return null; }
    }

    private static string Raw(JsonElement root, string name, string missing) => root.TryGetProperty(name, out var value) ? value.GetRawText() : missing;

    /// <summary>A photo as seen: which image, never its main-photo flag.</summary>
    private static string Photo(JsonElement photo) => Raw(photo, "id", "null") + ":" + Raw(photo, "sha256", "null");
}
