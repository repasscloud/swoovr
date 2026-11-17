<#ftl output_format="plainText">
<#if user?? && user.firstName?has_content>Hi ${user.firstName},

</#if>You asked to change the email on your Swoovr account to ${newEmail}. Confirm it here:

${link}

Until you confirm, you keep signing in with your current email. If you have a founder code, we'll send a new one to this address.

Didn't ask for this? Ignore this email and nothing changes.

${msg("swoovrMemberFooter")}
Swoovr is run by RePass Cloud Pty Ltd. Help: ${properties.swoovrSupportUrl}
