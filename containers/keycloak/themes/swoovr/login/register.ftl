<#-- Swoovr: keycloak.v2 26.8.1 register.ftl with the prototype's button and "Already have an account? Sign in". -->
<#import "template.ftl" as layout>
<#import "field.ftl" as field>
<#import "user-profile-commons.ftl" as userProfileCommons>
<#import "register-commons.ftl" as registerCommons>
<#import "password-validation.ftl" as validator>
<#-- #385: a refused creation (invite-only or closed) replaces the form with one focused card. The refusal comes only from
     the server's admission check after a submit, so nothing here decides who may register. -->
<#assign swInviteOnly = (message?? && message.summary?contains(msg("swoovrRegistrationInviteOnly")))>
<#assign swClosed = (!swInviteOnly && message?? && message.summary?contains(msg("swoovrRegistrationClosed")))>
<@layout.registrationLayout displayMessage=(messagesPerField.exists('global') && !swInviteOnly && !swClosed) displayRequiredFields=!(swInviteOnly || swClosed); section>
<!-- template: register.ftl -->

    <#if section = "header">
        <#if swInviteOnly>
            ${msg("swoovrInviteOnlyTitle")}
        <#elseif swClosed>
            ${msg("swoovrRegistrationClosed")}
        <#elseif messageHeader??>
            ${msg(messageHeader)}
        <#else>
            ${msg("registerTitle")}
        </#if>
    <#elseif section = "form">
        <#if swInviteOnly>
            <section class="sw-gate" aria-label="${msg("swoovrInviteOnlyTitle")}" data-invite-only>
                <p>${msg("swoovrInviteOnlyLede")}</p>
                <p>${msg("swoovrInviteOnlyEarlyAccess")}</p>
                <a class="${properties.kcButtonClass!} ${properties.kcButtonPrimaryClass!} ${properties.kcButtonBlockClass!} ${properties.kcButtonLargeClass!} sw-gate-action" href="https://swoovr.com/beta" rel="noopener" data-beta-waitlist>${msg("swoovrJoinWaitlist")}</a>
                <div class="sw-hint sw-gate-legal">
                    <p>${msg("swoovrAdultsOnly")}</p>
                    <p>${msg("swoovrAgreementBefore")} <a href="${properties.swoovrTermsUrl!'/terms'}" target="_blank" rel="noopener" id="sw-terms-link">${msg("termsLink")}</a> ${msg("swoovrAgreementMiddle")} <a href="${properties.swoovrPrivacyUrl!'/privacy'}" target="_blank" rel="noopener" id="sw-privacy-link">${msg("privacyNoticeLink")}</a> ${msg("swoovrAgreementAnd")} <a href="${properties.swoovrCommunityUrl!'/community-guidelines'}" target="_blank" rel="noopener" id="sw-community-link">${msg("communityLink")}</a>.</p>
                </div>
                <p class="sw-switch">${msg("swoovrHaveAccount")} <a href="${url.loginUrl}">${msg("doLogIn")}</a></p>
            </section>
        <#elseif swClosed>
            <section class="sw-gate" aria-label="${msg("swoovrRegistrationClosed")}" data-registration-closed>
                <p>${msg("swoovrRegistrationClosedText")}</p>
                <a class="${properties.kcButtonClass!} ${properties.kcButtonPrimaryClass!} ${properties.kcButtonBlockClass!} ${properties.kcButtonLargeClass!} sw-gate-action" href="${url.loginUrl}">${msg("doLogIn")}</a>
            </section>
        <#else>
        <form id="kc-register-form" class="${properties.kcFormClass!}" action="${url.registrationAction}" method="post" novalidate="novalidate">
            <@userProfileCommons.userProfileFormFields; callback, attribute>
                <#if callback = "afterField">
                    <#-- #386: Keycloak's own duplicate-address refusal (emailExistsMessage) offers the two ways back in. -->
                    <#if attribute.name == 'email' && messagesPerField.existsError('email') && messagesPerField.get('email')?contains(msg("emailExistsMessage"))>
                        <p class="sw-hint sw-exists" data-email-exists><a href="${url.loginUrl}">${msg("doLogIn")}</a> · <a href="${url.loginResetCredentialsUrl}">${msg("doForgotPassword")}</a></p>
                    </#if>
                <#-- render password fields just under the username or email (if used as username) -->
                    <#if passwordRequired?? && (attribute.name == 'username' || (attribute.name == 'email' && realm.registrationEmailAsUsername))>
                        <@field.password name="password" required=true label=msg("password") autocomplete="new-password" />
                        <@field.password name="password-confirm" required=true label=msg("passwordConfirm") autocomplete="new-password" />
                    </#if>
                </#if>
            </@userProfileCommons.userProfileFormFields>

            <#-- issue 179: the same Swoovr checkbox as "I'm 18 or over". -->
            <label class="sw-check sw-check-mononym" for="swoovr-mononym">
                <input id="swoovr-mononym" type="checkbox" name="swoovr_mononym" value="true" <#if ((register.formData['swoovr_mononym'][0])!'false') == 'true'>checked</#if> />
                <span class="sw-box" aria-hidden="true"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="3.2" stroke-linecap="round" stroke-linejoin="round"><path d="M20 6 9 17l-5-5"/></svg></span>
                <span>${msg("swoovrMononym")}</span>
            </label>
            <@registerCommons.termsAcceptance/>

            <#if recaptchaRequired?? && (recaptchaVisible!false)>
                <div class="form-group">
                    <div class="${properties.kcInputWrapperClass!}">
                        <div class="g-recaptcha" data-size="compact" data-sitekey="${recaptchaSiteKey}" data-action="${recaptchaAction}"></div>
                    </div>
                </div>
            </#if>

            <#if recaptchaRequired?? && !(recaptchaVisible!false)>
                <script>
                    function onSubmitRecaptcha(token) {
                        document.getElementById("kc-register-form").requestSubmit();
                    }
                </script>
                <div id="kc-form-buttons" class="${properties.kcFormButtonsClass!}">
                    <button class="${properties.kcButtonClass!} ${properties.kcButtonPrimaryClass!} ${properties.kcButtonBlockClass!} ${properties.kcButtonLargeClass!} g-recaptcha"
                            data-sitekey="${recaptchaSiteKey}" data-callback="onSubmitRecaptcha" data-action="${recaptchaAction}" type="submit" id="kc-submit">
                        ${msg("doRegister")}
                    </button>
                </div>
            <#else>
                <div id="kc-form-buttons" class="${properties.kcFormButtonsClass!}">
                    <button class="${properties.kcButtonClass!} ${properties.kcButtonPrimaryClass!} ${properties.kcButtonBlockClass!} ${properties.kcButtonLargeClass!}" type="submit" id="kc-register">${msg("swoovrCreateAccount")}</button>
                </div>
            </#if>

            <p class="sw-switch">${msg("swoovrHaveAccount")} <a href="${url.loginUrl}">${msg("doLogIn")}</a></p>

        </form>

        <#-- Email-first registration (D44) has no password field here; the upstream validator would throw. -->
        <#if passwordRequired??>
            <@validator.templates/>
            <@validator.script field="password"/>
        </#if>
        <script>
          // issue 179: "Create account" stays disabled until "I'm 18 or over" is ticked and every required field is valid.
          // The realm's terms step and the server's field validation remain authoritative (D40, D300).
          (function () {
            var form = document.getElementById("kc-register-form"), box = document.getElementById("termsAccepted"), button = document.getElementById("kc-register");
            if (!form || !box || !button) return;
            var fields = function () { return Array.prototype.filter.call(form.querySelectorAll("input[name]"), function (input) { return input.type !== "checkbox" && input.type !== "hidden" && !input.disabled; }); };
            var valid = function (input) { var value = input.value.trim(); if ((input.required || input.name === "firstName" || input.name === "lastName" || input.name === "email") && !value) return false; if (input.type === "email" || input.name === "email") return /^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(value); return input.checkValidity(); };
            var sync = function () { button.disabled = !(box.checked && fields().every(valid)); };
            form.addEventListener("input", sync); form.addEventListener("change", sync); sync();
          })();
        </script>
        </#if>
    </#if>
</@layout.registrationLayout>
