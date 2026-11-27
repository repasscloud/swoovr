using System.Globalization;
using System.Text;
using Swoovr.Contracts;

namespace Swoovr.Client;

/// <summary>Issue 545 (D430): wording for private meet-up cards, and the Add to calendar file.</summary>
public static class MeetupText
{
    public static readonly (int Minutes, string Label)[] Durations = [(30, "30 minutes"), (60, "1 hour"), (90, "1½ hours"), (120, "2 hours"), (180, "3 hours"), (240, "4 hours")];

    public const string Private = "Only the two of you can see this. Swoovr doesn't book the venue.";

    public static string State(MeetupView m) => m.State switch
    {
        "pending" => m.Mine ? "Waiting for a reply" : "Invitation for you",
        "accepted" => "Accepted",
        "declined" => "Declined",
        "withdrawn" => "Withdrawn",
        "superseded" => "Changed: see the newer plan",
        "cancelled" => "Called off",
        "expired" => "Expired",
        "past" => "Past",
        _ => m.State,
    };

    /// <summary>"Fri 4 Dec · 7:00 pm", in the viewer's own time zone.</summary>
    public static string When(MeetupView m) => m.StartsAt.ToLocalTime().ToString("ddd d MMM · h:mm tt", CultureInfo.InvariantCulture).Replace("AM", "am").Replace("PM", "pm");

    public static string? Duration(int? minutes) => minutes is { } value ? Durations.FirstOrDefault(d => d.Minutes == value).Label : null;

    /// <summary>
    /// An iCalendar file for an accepted plan (D430 After acceptance A). Made on the device from the card; nothing is sent
    /// anywhere. Times in UTC; two hours when no duration was given. No names beyond the first name shown in the chat.
    /// </summary>
    public static string Calendar(MeetupView m, string? withName, DateTimeOffset now)
    {
        static string Utc(DateTimeOffset t) => t.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        static string Escape(string value) => value.Replace("\\", "\\\\").Replace(";", "\\;").Replace(",", "\\,").Replace("\r", "").Replace("\n", "\\n");
        var title = withName is { Length: > 0 } ? $"{m.CategoryLabel} with {withName}" : m.CategoryLabel;
        var text = new StringBuilder()
            .Append("BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Swoovr//Meet-up//EN\r\nCALSCALE:GREGORIAN\r\nBEGIN:VEVENT\r\n")
            .Append("UID:").Append(m.Id.ToString("N")).Append("@swoovr.app\r\n")
            .Append("DTSTAMP:").Append(Utc(now)).Append("\r\n")
            .Append("DTSTART:").Append(Utc(m.StartsAt)).Append("\r\n")
            .Append("DTEND:").Append(Utc(m.StartsAt.AddMinutes(m.DurationMinutes ?? 120))).Append("\r\n")
            .Append("SUMMARY:").Append(Escape(title)).Append("\r\n")
            .Append("LOCATION:").Append(Escape(m.Venue)).Append("\r\n")
            .Append("DESCRIPTION:").Append(Escape("Planned on Swoovr. Swoovr doesn't book the venue.")).Append("\r\n")
            .Append("END:VEVENT\r\nEND:VCALENDAR\r\n");
        return text.ToString();
    }
}
