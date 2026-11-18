<#ftl output_format="plainText">
An administrator created a Swoovr staff account for ${user.email}. To finish setting it up you'll:

<#list requiredActions as action>  ${action?counter}. ${msg("swoovrAction" + action)}
</#list>

Set up your account here:

${link}

This link works for ${linkExpirationFormatter(linkExpiration)}. Keep your password and authenticator to yourself; Swoovr staff will never ask for them.

Not expecting this? Ignore it and tell your Swoovr administrator.

${msg("swoovrStaffSetupFooter")}
Swoovr is run by RePass Cloud Pty Ltd. Help: ${properties.swoovrSupportUrl}
