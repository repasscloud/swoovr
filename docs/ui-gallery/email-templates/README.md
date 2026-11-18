# Swoovr email templates

**Approved by the owner on issue #355 (17 November 2026) and built as D370:** Keycloak theme `containers/keycloak/themes/swoovr/email`, API `EmailHtml`. These previews stay the reference design.

One self-contained HTML file per email Swoovr sends. Open [`index.html`](index.html) in a browser. Each page shows a review panel (trigger, sender, subject, preview text, link lifetime, variants), then the email as the recipient sees it.

- Look: the Standard black-and-white scheme (`docs/design/themes.json`), the official logo (`docs/brand/swoovr-logo.svg`, D340) and the Swoovr font stack. Personal schemes are never used in email.
- Sample people, codes, case IDs and addresses are fictional.
- **Keycloak emails (01–04):** new Swoovr wording, replacing Keycloak's stock English text. Today the realms have no email theme, so members get Keycloak's default "Verify email" email.
- **API emails (05–13):** current wording from the code, in the new layout. Wording changes are marked "Proposed" in the review panel.

Rebuild after editing: `python3 docs/ui-gallery/email-templates/build.py`.

Not covered: helpdesk (Zammad) emails, which use Zammad's own templates, and SMS.
