<#ftl output_format="plainText">
<#assign product=realmName?lower_case?contains("staff")?then("Swoovr Admin", realmName?lower_case?contains("venue")?then("Swoovr Venues", "Swoovr"))>
<#if user?? && user.firstName?has_content>Hi ${user.firstName},

</#if>Confirm ${user.email} to finish setting up your ${product} account:

${link}

This link works for ${linkExpirationFormatter(linkExpiration)}. If it has expired, just sign in again and we'll send you a new one.

Didn't sign up for Swoovr? You can ignore this email. Nobody can use the account until this address is confirmed.

${msg("swoovrMemberFooter")}
Swoovr is run by RePass Cloud Pty Ltd. Help: ${properties.swoovrSupportUrl}
