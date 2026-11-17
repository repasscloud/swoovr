<#import "template.ftl" as layout>
<#-- 01-verify-email (issue 355, approved). -->
<@layout.emailLayout title="Confirm your email" preheader="One tap to finish creating your ${layout.product(realmName)} account." footer=msg("swoovrMemberFooter")>
<#if user?? && user.firstName?has_content><@layout.p>Hi ${user.firstName},</@layout.p></#if>
<@layout.p>Tap the button to confirm <strong>${user.email}</strong> and finish setting up your ${layout.product(realmName)} account.</@layout.p>
<@layout.button label="Confirm email" url=link/>
<@layout.note>This link works for ${linkExpirationFormatter(linkExpiration)}. If it has expired, just sign in again and we'll send you a new one.</@layout.note>
<@layout.p>Didn't sign up for Swoovr? You can ignore this email. Nobody can use the account until this address is confirmed.</@layout.p>
</@layout.emailLayout>
