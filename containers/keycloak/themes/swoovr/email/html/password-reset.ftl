<#import "template.ftl" as layout>
<#-- 02-password-reset (issue 355, approved). -->
<@layout.emailLayout title="Reset your password" preheader="Use this link to choose a new password." footer=msg("swoovrMemberFooter")>
<#if user?? && user.firstName?has_content><@layout.p>Hi ${user.firstName},</@layout.p></#if>
<@layout.p>We got a request to reset the password for <strong>${user.email}</strong>.</@layout.p>
<@layout.button label="Choose a new password" url=link/>
<@layout.note>This link works for ${linkExpirationFormatter(linkExpiration)} and only once.</@layout.note>
<@layout.p>If you didn't ask for this, ignore this email. Your password stays the same. If these keep arriving, contact Swoovr Support.</@layout.p>
</@layout.emailLayout>
