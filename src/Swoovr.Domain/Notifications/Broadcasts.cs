namespace Swoovr.Domain;

/// <summary>
/// Issue 588 (D446): a notification Swoovr sends to every member's notification centre (for example planned maintenance).
/// It goes to every active account (restricted ones too), never to deactivated or pending-deletion ones, and never to one
/// member (a ticket covers that). Once sent it can't be edited, only withdrawn, which removes it from every centre.
/// </summary>
public sealed class Broadcast
{
    public Guid Id { get; set; }
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public int Recipients { get; set; }
    public Guid SentBy { get; set; }
    public DateTimeOffset SentAt { get; set; }
    public Guid? WithdrawnBy { get; set; }
    public DateTimeOffset? WithdrawnAt { get; set; }
}

public static class BroadcastPolicy
{
    public const int TitleMax = 80, BodyMax = 2000;
    public static string? Title(string? value) => value?.Trim() is { Length: > 0 and <= TitleMax } title && !title.Any(char.IsControl) ? title : null;
    public static string? Body(string? value) =>
        value?.Replace("\r\n", "\n").Trim() is { Length: > 0 and <= BodyMax } body && !body.Any(c => char.IsControl(c) && c != '\n') ? body : null;
}
