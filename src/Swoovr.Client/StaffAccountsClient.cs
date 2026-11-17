using Swoovr.Contracts;
namespace Swoovr.Client;
public sealed partial class AdminClient
{
    public Task<ApiResult<StaffAccountPage>> StaffAccountsAsync(string? search = null, string? role = null, string? status = null, int page = 1, CancellationToken token = default, string? mfa = null, DateTimeOffset? signedInAfter = null, DateTimeOffset? signedInBefore = null) =>
        SendAsync<StaffAccountPage>(HttpMethod.Get, StaffAccountsPath(search, role, status, page, mfa, signedInAfter, signedInBefore), null, token);
    // Only filters that are set go on the URL: the API binds signedInAfter/signedInBefore as DateTimeOffset?, and an empty
    // "signedInAfter=" fails binding with 400, which turned every unfiltered /staff load into an error (issue 349).
    public static string StaffAccountsPath(string? search, string? role, string? status, int page, string? mfa, DateTimeOffset? signedInAfter, DateTimeOffset? signedInBefore)
    {
        var query = new List<string>();
        void Add(string name, string? value) { if (!string.IsNullOrWhiteSpace(value)) query.Add(name + "=" + Uri.EscapeDataString(value)); }
        Add("search", search); Add("role", role); Add("status", status); Add("mfa", mfa);
        Add("signedInAfter", signedInAfter?.ToString("O")); Add("signedInBefore", signedInBefore?.ToString("O"));
        query.Add("page=" + Math.Max(1, page));
        return "api/admin/staff/accounts?" + string.Join("&", query);
    }
    public Task<ApiResult<StaffAccountDetail>> StaffAccountAsync(Guid id, CancellationToken token = default) => SendAsync<StaffAccountDetail>(HttpMethod.Get, $"api/admin/staff/accounts/{id}", null, token);
    public Task<ApiResult<StaffAccountSummary>> CreateStaffAsync(StaffAccountCreate request, CancellationToken token = default) => SendAsync<StaffAccountSummary>(HttpMethod.Post, "api/admin/staff/accounts", request, token);
    public Task<ApiResult<StaffAccountSummary>> UpdateStaffAsync(Guid id, StaffAccountUpdate request, CancellationToken token = default) => SendAsync<StaffAccountSummary>(HttpMethod.Put, $"api/admin/staff/accounts/{id}", request, token);
    public Task<ApiResult<StaffAccountSummary>> StaffSecurityAsync(Guid id, StaffSecurityAction request, CancellationToken token = default) => SendAsync<StaffAccountSummary>(HttpMethod.Post, $"api/admin/staff/accounts/{id}/security", request, token);
}
