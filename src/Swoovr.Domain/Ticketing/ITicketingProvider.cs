namespace Swoovr.Domain;

// Conversations, group/assignment/state and article bodies are projections fetched from the provider, never EF entities.
public sealed record SupportCustomer(long Id, string Login, string? Email, bool Active);
public sealed record SupportTicket(long Id, string Number, string Title, long CustomerId, long GroupId, long OwnerId,
    long StateId, long PriorityId, DateTimeOffset UpdatedAt, string? OperationReference = null)
{
    public IReadOnlyDictionary<string,string> Classification { get; init; } = new Dictionary<string,string>();
}
public sealed record SupportAttachment(long Id, string Filename, string ContentType, long Size);
public sealed record SupportArticle(long Id, long TicketId, string Body, string ContentType, bool Internal, string Sender,
    DateTimeOffset CreatedAt, IReadOnlyList<SupportAttachment> Attachments)
{
    public string? OperationReference { get; init; }
    public long? AuthorId { get; init; }
    /// <summary>The helpdesk channel ("note", "email", "web", ...). Email senders are unverified addresses.</summary>
    public string? Type { get; init; }
    /// <summary>
    /// What a member or guest may see in Swoovr: no internal notes, and no customer messages that arrived by email, because
    /// an email sender address can be forged and must not speak as the member inside the app (DEFAULT, conservative).
    /// </summary>
    public bool CustomerVisible => !Internal && !(Sender == "Customer" && Type == "email");
    /// <summary>For an emailed message: its Reply-To, else its From (as the helpdesk stored it).</summary>
    public string? ReplyAddress { get; init; }
}
/// <summary>D368: send a staff reply as an email from the group's support address.</summary>
public sealed record SupportEmail(string To, string Subject);
/// <summary>
/// D368 (owner, 18 November 2026): a ticket that started as an email is answered by email, also from Swoovr Admin. A ticket a
/// member or guest raised in Swoovr is answered in Swoovr (recorded in the helpdesk), whatever later arrives by email.
/// </summary>
public static class TicketingEmail
{
    public static bool StartedByEmail(IReadOnlyList<SupportArticle> articles) =>
        articles.OrderBy(a => a.Id).FirstOrDefault() is { Sender: "Customer", Type: "email" };
    /// <summary>Where staff replies go: the latest emailed customer message's reply address; null if not an email ticket.</summary>
    public static string? Recipient(IReadOnlyList<SupportArticle> articles) =>
        !StartedByEmail(articles) ? null : articles.Where(a => a is { Sender: "Customer", Type: "email" } && Valid(a.ReplyAddress))
            .OrderByDescending(a => a.Id).Select(a => a.ReplyAddress!.Trim()).FirstOrDefault();
    private static bool Valid(string? address) => address is { Length: > 2 and <= 998 } a && a.Contains('@') && !a.Any(char.IsControl);
}
public sealed record SupportTicketDraft(string Title, string Body, long CustomerId, long GroupId, string OperationReference,string Source="member",
    IReadOnlyList<SupportUpload>? Attachments=null);
public sealed record SupportTicketChange(long? GroupId = null, long? OwnerId = null, long? StateId = null, long? PriorityId = null);

/// <summary>Implemented with a credential whose provider permissions match the resolved actor; never a universal agent token.</summary>
public interface ITicketingProvider
{
    Task<SupportCustomer?> FindCustomerAsync(string canonicalLogin, CancellationToken cancellationToken);
    Task<SupportCustomer> CreateCustomerAsync(string canonicalLogin, string? verifiedEmail, CancellationToken cancellationToken);
    Task<SupportCustomer> UpdateCustomerAsync(long id, string? verifiedEmail, bool active, CancellationToken cancellationToken);
    Task<IReadOnlyList<SupportTicket>> SearchAsync(string query, int page, int pageSize, CancellationToken cancellationToken);
    Task<SupportTicket?> GetTicketAsync(long id, CancellationToken cancellationToken);
    Task<SupportTicket> CreateTicketAsync(SupportTicketDraft draft, CancellationToken cancellationToken);
    Task<SupportTicket> ChangeTicketAsync(long id, SupportTicketChange change, CancellationToken cancellationToken);
    Task<IReadOnlyList<SupportArticle>> GetArticlesAsync(long ticketId, CancellationToken cancellationToken);
    Task<SupportArticle> ReplyAsync(long ticketId, string body, bool internalNote, bool customer, CancellationToken cancellationToken);
}

/// <summary>Writes may have reached Zammad despite a lost response. Callers must reconcile before attempting another write.</summary>
public sealed class TicketingProviderException(string code, bool uncertain = false) : Exception(code)
{
    public string Code { get; } = code;
    public bool Uncertain { get; } = uncertain;
}
