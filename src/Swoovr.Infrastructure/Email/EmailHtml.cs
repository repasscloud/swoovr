using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Swoovr.Infrastructure.Email;

/// <summary>
/// Where branded email points (issue 355): the logo PNG and the member/admin sites. Read from Email:Branding:LogoUrl,
/// Email:Branding:MemberUrl and Email:Branding:AdminUrl. A missing or unsafe value is left out (no logo image, no
/// button), never guessed. HTTPS only, except loopback HTTP for local development.
/// </summary>
public sealed record EmailBranding(Uri? LogoUrl, Uri? MemberUrl, Uri? AdminUrl)
{
    public static readonly EmailBranding None = new(null, null, null);

    public static EmailBranding Read(Func<string, string?> read) =>
        new(Safe(read("Email:Branding:LogoUrl"), file: true), Safe(read("Email:Branding:MemberUrl")), Safe(read("Email:Branding:AdminUrl")));

    internal static Uri? Safe(string? value, bool file = false) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme == "https" || uri.Scheme == "http" && uri.IsLoopback)
            && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0 && (file || uri.AbsolutePath == "/") ? uri : null;

    /// <summary>A page on the member site, or null when the member site is not configured.</summary>
    public Uri? Member(string path) => MemberUrl is null ? null : new Uri(MemberUrl, path);
    /// <summary>A page in the admin app, or null when the admin app is not configured.</summary>
    public Uri? Admin(string path) => AdminUrl is null ? null : new Uri(AdminUrl, path);
}

/// <summary>
/// The approved Swoovr email layout (issue 355, docs/ui-gallery/email-templates/build.py): table layout, inline styles,
/// Standard light colours and the Swoovr font stack. Every piece of text is HTML-encoded; `**bold**` is the only markup.
/// The plain-text body of each email is unchanged and is always sent alongside this HTML.
/// </summary>
public static partial class EmailHtml
{
    const string Font = "'Helvetica Neue', Helvetica, Arial, sans-serif";
    const string Text = "#0A0A0A", Muted = "#6B6B6B", Soft = "#F4F4F4", Border = "#E5E5E5", Surface = "#FFFFFF", OnPrimary = "#FAFAFA";
    const string PStyle = "margin:0 0 16px;font-family:" + Font + ";font-size:16px;line-height:24px;color:" + Text + ";";
    const string SmallStyle = "margin:0 0 12px;font-family:" + Font + ";font-size:14px;line-height:21px;color:" + Muted + ";";

    public const string MemberFooter = "You got this email because it is about your Swoovr account. Swoovr never asks for your password by email.";
    public const string StaffFooter = "Sent to Swoovr staff escalation addresses only. Contains no member names, messages or images.";

    public static string Page(EmailBranding branding, string preheader, string title, string body, string footer)
    {
        var logo = branding.LogoUrl is null
            ? $"<span style=\"font-family:{Font};font-size:26px;font-weight:700;color:{Text};\">swoovr</span>"
            : $"<img src=\"{Attr(branding.LogoUrl.AbsoluteUri)}\" width=\"129\" height=\"40\" alt=\"swoovr\" style=\"display:block;border:0;\">";
        return $"""
            <!doctype html>
            <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1"><title>{Encode(title)}</title></head>
            <body style="margin:0;padding:0;background:{Soft};">
            <table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0" style="background:{Soft};">
            <tr><td align="center" style="padding:32px 16px;">
            <div style="display:none;max-height:0;overflow:hidden;opacity:0;">{Encode(preheader)}</div>
            <table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0" style="max-width:560px;background:{Surface};border:1px solid {Border};border-radius:20px;">
            <tr><td style="padding:32px 32px 8px;">{logo}</td></tr>
            <tr><td style="padding:16px 32px 8px;">
            <h1 style="margin:0 0 20px;font-family:{Font};font-size:26px;line-height:32px;font-weight:700;color:{Text};">{Encode(title)}</h1>
            {body}
            </td></tr>
            <tr><td style="padding:20px 32px 28px;border-top:1px solid {Border};">
            <p style="{SmallStyle}">{Encode(footer)}</p>
            <p style="{SmallStyle}">Swoovr is run by <a href="https://repasscloud.com" style="color:{Muted};">RePass Cloud Pty Ltd</a>.<br>Help: <a href="https://swoovr.com/support" style="color:{Muted};">swoovr.com/support</a></p>
            </td></tr>
            </table>
            </td></tr></table>
            </body></html>
            """;
    }

    public static string P(string text) => $"<p style=\"{PStyle}\">{Inline(text)}</p>";
    public static string Small(string text) => $"<p style=\"{SmallStyle}\">{Inline(text)}</p>";

    /// <summary>A button and its copy-the-link fallback; nothing when the link is unknown.</summary>
    public static string Button(string label, Uri? url)
    {
        if (url is null) return "";
        var href = Attr(url.AbsoluteUri);
        return $"<table role=\"presentation\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\" style=\"margin:8px 0 24px;\"><tr>"
            + $"<td style=\"border-radius:14px;background:{Text};\"><a href=\"{href}\" style=\"display:inline-block;padding:14px 28px;font-family:{Font};font-size:16px;font-weight:700;line-height:20px;color:{OnPrimary};text-decoration:none;border-radius:14px;\">{Encode(label)}</a></td>"
            + "</tr></table>"
            + $"<p style=\"{SmallStyle}\">Button not working? Copy this link into your browser:<br><a href=\"{href}\" style=\"color:{Text};word-break:break-all;\">{Encode(url.AbsoluteUri)}</a></p>";
    }

    /// <summary>A small link line (for example withdraw/unsubscribe); nothing when the link is unknown.</summary>
    public static string LinkLine(string label, Uri? url) => url is null ? ""
        : $"<p style=\"{SmallStyle}\">{Encode(label)} <a href=\"{Attr(url.AbsoluteUri)}\" style=\"color:{Muted};word-break:break-all;\">{Encode(url.AbsoluteUri)}</a></p>";

    public static string Note(string text, bool strong = false) =>
        "<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\" style=\"margin:0 0 20px;\"><tr>"
        + $"<td style=\"background:{Soft};border:1px solid {(strong ? Text : Border)};border-radius:14px;padding:14px 16px;font-family:{Font};font-size:14px;line-height:21px;color:{Text};\">{Inline(text)}</td>"
        + "</tr></table>";

    public static string Code(string value) =>
        "<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\" style=\"margin:0 0 20px;\"><tr>"
        + $"<td align=\"center\" style=\"background:{Soft};border-radius:14px;padding:20px;font-family:{Font};font-size:28px;font-weight:700;letter-spacing:6px;color:{Text};\">{Encode(value)}</td>"
        + "</tr></table>";

    public static string Facts(params (string Name, string Value)[] rows)
    {
        var cells = new StringBuilder();
        foreach (var (name, value) in rows)
            cells.Append($"<tr><td style=\"padding:6px 12px 6px 0;font-family:{Font};font-size:14px;color:{Muted};white-space:nowrap;vertical-align:top;\">{Encode(name)}</td>")
                 .Append($"<td style=\"padding:6px 0;font-family:{Font};font-size:14px;color:{Text};word-break:break-all;\">{Encode(value)}</td></tr>");
        return $"<table role=\"presentation\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\" style=\"margin:0 0 20px;border-top:1px solid {Border};border-bottom:1px solid {Border};width:100%;\">{cells}</table>";
    }

    static string Encode(string text) => WebUtility.HtmlEncode(text);
    static string Attr(string text) => WebUtility.HtmlEncode(text);
    /// <summary>Encode, then turn **bold** into strong; line breaks become br.</summary>
    static string Inline(string text) => Bold().Replace(Encode(text), "<strong>$1</strong>").Replace("\n", "<br>");

    [GeneratedRegex(@"\*\*(.+?)\*\*")]
    private static partial Regex Bold();
}
