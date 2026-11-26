namespace Swoovr.Domain;

/// <summary>Where a sensitive report's helpdesk item stands. Sending means the create may have reached Zammad: it is reconciled, never resent blind.</summary>
public enum SafetyHandoffState { Pending, Sending, Created, Failed }

/// <summary>
/// Issue 188 (Q128, D411): one sensitive (serious, D190) report waiting for, or holding, its item in the helpdesk's
/// "Trust &amp; Safety" group. Written in the report's own transaction, so a helpdesk outage never loses or delays the report.
/// Ids, the state and a failure code only: no names, report text or evidence. The id is also the Zammad operation id.
/// </summary>
public sealed class SafetyHandoff
{
    public Guid Id { get; set; }
    public Guid ReportId { get; set; }
    public Guid CaseId { get; set; }
    public string CategoryId { get; set; } = "";
    public SafetyHandoffState State { get; set; }
    public long? TicketId { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset? NextAttemptAt { get; set; }
    public string? FailureCode { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Issue 188 (D420): the helpdesk item for a sensitive report. It carries the case, never the people or what was said.</summary>
public static class SafetyHandoffPolicy
{
    public const string Group = "Trust & Safety";
    /// <summary>The integration identity, which is also the item's customer, so no member is ever its customer and no member can see it.</summary>
    public const string Login = "swoovr-safety-reports";
    public const string Title = "Sensitive safety report";
    public const int MaximumAttempts = 24;

    /// <summary>Retries start at a minute and double up to an hour; 24 attempts cover about a day.</summary>
    public static TimeSpan Backoff(int attempts) => TimeSpan.FromMinutes(Math.Min(60, Math.Pow(2, Math.Max(0, attempts - 1))));

    public static string Note(string categoryLabel, Guid caseId, string? adminBaseUrl)
    {
        var link = (adminBaseUrl is { Length: > 0 } admin ? admin.TrimEnd('/') : "Swoovr Admin") + "/cases/" + caseId.ToString("D");
        return "A member sent a sensitive safety report through Swoovr.\n\n"
            + "Category: " + categoryLabel + "\n"
            + "Case: " + link + "\n\n"
            + "Work the case in Swoovr Admin, where the report, evidence and holds are. Don't copy names, messages or evidence into this item.";
    }
}
