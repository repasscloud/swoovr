extern alias AdminHost;
using AccountLabels = AdminHost::Swoovr.Admin.AccountLabels;
using Swoovr.Domain;

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
        // #347: avatars, readable audit/session labels and one timestamp format.
        Check(AccountLabels.StaffInitials("Danijel-James", "Wynyard", null) == "DW", "initials from names");
        Check(AccountLabels.StaffInitials(null, null, "fictional.root@example.test") == "FR", "initials from email local part");
        Check(AccountLabels.StaffInitials(null, null, null) == "?", "no initials placeholder");
        Check(AccountLabels.StaffAction("staff.roles-synced") == "Roles updated from Microsoft Entra", "audit label");
        Check(AccountLabels.StaffAction("staff.something-new") == "Something new", "unknown audit action stays readable");
        Check(AccountLabels.StaffDevice("web") == "Admin portal" && AccountLabels.StaffDevice("other") == "Other Swoovr service", "session device labels");
        Check(AccountLabels.When(DateTimeOffset.Parse("2026-11-17T12:46:11+11:00")) == "17 Nov 2026, 01:46 UTC", "timestamps in UTC");
        Check(AccountLabels.When(null, "Never") == "Never", "missing timestamp");
        // #347: staff names follow the staff realm's verified token; a blank, long or absent claim never clears a stored name.
        var staff = new Account { Realm = AccountRealm.Staff };
        Check(StaffNames.Adopt(staff, " Fictional ", "Person") && staff.RegisteredFirstName == "Fictional" && staff.RegisteredLastName == "Person", "names adopted and trimmed");
        Check(!StaffNames.Adopt(staff, "Fictional", "Person"), "unchanged names don't save");
        Check(!StaffNames.Adopt(staff, null, " ") && staff.RegisteredFirstName == "Fictional" && staff.RegisteredLastName == "Person", "absent claims keep names");
        Check(!StaffNames.Adopt(staff, new string('x', 101), "Bad\u0007") && staff.RegisteredFirstName == "Fictional", "over-long or control characters refused");
        Check(StaffNames.Adopt(staff, "Renamed", null) && staff.RegisteredFirstName == "Renamed" && staff.RegisteredLastName == "Person", "one name changes alone");
        var member = new Account { Realm = AccountRealm.Members };
        Check(!StaffNames.Adopt(member, "Member", "Name") && member.RegisteredFirstName is null, "member names never come from this path");
        Console.WriteLine($"PASS: staff display names and labels ({count} checks).");
    }
}
