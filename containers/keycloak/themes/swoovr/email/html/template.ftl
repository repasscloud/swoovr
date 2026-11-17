<#-- Swoovr email layout (issue 355, approved). Mirrors docs/ui-gallery/email-templates/build.py: table layout, inline
     styles, Standard light colours, the Swoovr font stack (single quotes: it sits inside style="..."). -->
<#assign font="'Helvetica Neue', Helvetica, Arial, sans-serif">
<#assign pStyle="margin:0 0 16px;font-family:${font};font-size:16px;line-height:24px;color:#0A0A0A;">
<#assign smallStyle="margin:0 0 12px;font-family:${font};font-size:14px;line-height:21px;color:#6B6B6B;">

<#-- Defaults (PR 368 review): Keycloak's inherited templates (email-test, identity-provider-link, …) call
     <@layout.emailLayout> with no arguments; they get the same frame with no heading and the member footer. -->
<#macro emailLayout title="" preheader="" footer=msg("swoovrMemberFooter")>
<!doctype html>
<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1"><title>${title?has_content?then(title, "Swoovr")}</title></head>
<body style="margin:0;padding:0;background:#F4F4F4;">
<table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0" style="background:#F4F4F4;">
<tr><td align="center" style="padding:32px 16px;">
<#if preheader?has_content><div style="display:none;max-height:0;overflow:hidden;opacity:0;">${preheader}</div></#if>
<table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0" style="max-width:560px;background:#FFFFFF;border:1px solid #E5E5E5;border-radius:20px;">
<tr><td style="padding:32px 32px 8px;"><img src="${properties.swoovrLogoUrl}" width="129" height="40" alt="swoovr" style="display:block;border:0;"></td></tr>
<tr><td style="padding:16px 32px 8px;">
<#if title?has_content><h1 style="margin:0 0 20px;font-family:${font};font-size:26px;line-height:32px;font-weight:700;color:#0A0A0A;">${title}</h1></#if>
<#nested>
</td></tr>
<tr><td style="padding:20px 32px 28px;border-top:1px solid #E5E5E5;">
<p style="${smallStyle}">${footer}</p>
<p style="${smallStyle}">Swoovr is run by <a href="${properties.swoovrCompanyUrl}" style="color:#6B6B6B;">RePass Cloud Pty Ltd</a>.<br>Help: <a href="${properties.swoovrSupportUrl}" style="color:#6B6B6B;">swoovr.com/support</a></p>
</td></tr>
</table>
</td></tr></table>
</body></html>
</#macro>

<#macro p><p style="${pStyle}"><#nested></p></#macro>
<#macro small><p style="${smallStyle}"><#nested></p></#macro>

<#macro button label url>
<table role="presentation" cellpadding="0" cellspacing="0" border="0" style="margin:8px 0 24px;"><tr>
<td style="border-radius:14px;background:#0A0A0A;"><a href="${url}" style="display:inline-block;padding:14px 28px;font-family:${font};font-size:16px;font-weight:700;line-height:20px;color:#FAFAFA;text-decoration:none;border-radius:14px;">${label}</a></td>
</tr></table>
<p style="${smallStyle}">Button not working? Copy this link into your browser:<br><a href="${url}" style="color:#0A0A0A;word-break:break-all;">${url}</a></p>
</#macro>

<#macro note>
<table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0" style="margin:0 0 20px;"><tr>
<td style="background:#F4F4F4;border:1px solid #E5E5E5;border-radius:14px;padding:14px 16px;font-family:${font};font-size:14px;line-height:21px;color:#0A0A0A;"><#nested></td>
</tr></table>
</#macro>

<#macro steps items>
<#list items as item><p style="${pStyle}margin-bottom:8px;">&nbsp;&nbsp;${item?counter}.&nbsp;&nbsp;${item}</p></#list>
<div style="height:8px;"></div>
</#macro>

<#-- "Swoovr", "Swoovr Admin" or "Swoovr Venues" from the realm's display name (Swoovr / Swoovr staff / Swoovr venues). -->
<#function product realm>
<#if realm?lower_case?contains("staff")><#return "Swoovr Admin"></#if>
<#if realm?lower_case?contains("venue")><#return "Swoovr Venues"></#if>
<#return "Swoovr">
</#function>
