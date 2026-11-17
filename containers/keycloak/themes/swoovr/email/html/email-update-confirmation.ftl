<#import "template.ftl" as layout>
<#-- 04-confirm-new-email (issue 355, approved). Sent to the NEW address. -->
<@layout.emailLayout title="Confirm your new email" preheader="Confirm the change to finish updating your sign-in email." footer=msg("swoovrMemberFooter")>
<#if user?? && user.firstName?has_content><@layout.p>Hi ${user.firstName},</@layout.p></#if>
<@layout.p>You asked to change the email on your Swoovr account to <strong>${newEmail}</strong>. Tap the button to confirm it.</@layout.p>
<@layout.button label="Confirm new email" url=link/>
<@layout.note>Until you confirm, you keep signing in with your current email. If you have a founder code, we'll send a new one to this address.</@layout.note>
<@layout.p>Didn't ask for this? Ignore this email and nothing changes.</@layout.p>
</@layout.emailLayout>
