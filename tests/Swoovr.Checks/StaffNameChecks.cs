extern alias AdminHost;
using AccountLabels = AdminHost::Swoovr.Admin.AccountLabels;

/// <summary>Staff list/detail names fall back to the email so SSO and break-glass accounts stay openable.</summary>
static class StaffNameChecks
{
    public static void Run()
    {
        var count = 0;
        void Check(bool condition, string name) { if (!condition) throw new Exception("staff name: " + name); count++; }
        Check(AccountLabels.StaffName("Danijel-James", "Wynyard", "a@example.test") == "Danijel-James Wynyard", "first and last name");
        Check(AccountLabels.StaffName(" Sam ", null, "a@example.test") == "Sam", "first name only, trimmed");
        Check(AccountLabels.StaffName(null, null, "fictional.root@example.test") == "fictional.root@example.test", "no name falls back to email");
        Check(AccountLabels.StaffName("  ", "", " fictional.staff@example.test ") == "fictional.staff@example.test", "blank names fall back to trimmed email");
        Check(AccountLabels.StaffName(null, null, null) == "Unnamed staff account", "nothing at all still has a label");
        Console.WriteLine($"PASS: staff display names ({count} checks).");
    }
}
