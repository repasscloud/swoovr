<#ftl output_format="plainText">
<#if user?? && user.firstName?has_content>Hi ${user.firstName},

</#if>We got a request to reset the password for ${user.email}. Choose a new password here:

${link}

This link works for ${linkExpirationFormatter(linkExpiration)} and only once.

If you didn't ask for this, ignore this email. Your password stays the same. If these keep arriving, contact Swoovr Support.

${msg("swoovrMemberFooter")}
Swoovr is run by RePass Cloud Pty Ltd. Help: ${properties.swoovrSupportUrl}
