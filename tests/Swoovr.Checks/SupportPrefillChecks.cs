using Swoovr.Client;

/// <summary>Issue 534: a new support request can start filled in from a link.</summary>
static class SupportPrefillChecks
{
    public static void Run()
    {
        var count = 0;
        void Check(bool condition, string name) { if (!condition) throw new Exception("support prefill: " + name); count++; }
        Check(SupportTicketText.Prefill(null, null, null) == ("", null), "a plain new request is blank");
        Check(SupportTicketText.Prefill(null, " Hello ", " There ") == ("Hello", "There"), "trimmed subject and body");
        Check(SupportTicketText.Prefill(null, "   ", "") == ("", null), "blank values are ignored");
        Check(SupportTicketText.Prefill(null, new string('a', 250), new string('b', 17000)) is { Title.Length: 200, Body.Length: 16000 }, "clipped to the form's limits");
        Check(SupportTicketText.Prefill("SW-ABC234", "Other", "Body") == ("About report SW-ABC234", "Body"), "a report reference keeps its own title");
        var href = AccountText.AnotherIdCheckHref;
        var query = System.Web.HttpUtility.ParseQueryString(new Uri("https://swoovr.invalid" + href).Query);
        Check(href.StartsWith("/me/support/new?", StringComparison.Ordinal), "the ID check link opens a new request");
        Check(query["subject"] == AccountText.AnotherIdCheckSubject && query["body"] == AccountText.AnotherIdCheckBody, "the link carries the owner's wording, encoded");
        Check(query.Count == 2, "nothing else is in the link");
        Console.WriteLine($"PASS: support request prefill ({count} checks).");
    }
}
