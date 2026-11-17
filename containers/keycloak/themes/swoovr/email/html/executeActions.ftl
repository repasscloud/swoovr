<#import "template.ftl" as layout>
<#-- 03-staff-account-setup (issue 355, approved): lists only the steps still needed. -->
<#assign steps=[]>
<#list requiredActions as action><#assign steps = steps + [msg("swoovrAction" + action)]></#list>
<@layout.emailLayout title="Set up your ${layout.product(realmName)} account" preheader="Confirm your email, choose a password and add an authenticator." footer=msg("swoovrStaffSetupFooter")>
<@layout.p>An administrator created a Swoovr staff account for <strong>${user.email}</strong>. To finish setting it up you'll:</@layout.p>
<@layout.steps items=steps/>
<@layout.button label="Set up account" url=link/>
<@layout.note>This link works for ${linkExpirationFormatter(linkExpiration)}. Keep your password and authenticator to yourself; Swoovr staff will never ask for them.</@layout.note>
<@layout.p>Not expecting this? Ignore it and tell your Swoovr administrator.</@layout.p>
</@layout.emailLayout>
