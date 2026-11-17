#!/usr/bin/env python3
"""Build the email template previews for owner review (issue: email templates, M2).

One self-contained HTML file per email Swoovr sends: a review panel (trigger, sender, subject, link lifetime, variants)
above the email itself. The email part is written the way it will be sent: table layout, inline styles, the Standard
light scheme from docs/design/themes.json and the Swoovr font stack. Sample people and addresses are fictional.

    python3 docs/ui-gallery/email-templates/build.py
"""
import html
import json
import re
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[2]
THEME = next(t for t in json.loads((ROOT / 'docs/design/themes.json').read_text()) if t['id'] == 'standard')['light']
FONT = "'Helvetica Neue', Helvetica, Arial, sans-serif"  # single quotes: it goes inside style="..." attributes
LOGO = re.sub(r'width="1160" height="360"', 'width="129" height="40"',
              (ROOT / 'docs/brand/swoovr-logo.svg').read_text().strip(), count=1)
FROM = 'Swoovr &lt;support@swoovr.com&gt;'
ISSUE = 'the email templates issue'

T = THEME
P = f'margin:0 0 16px;font-family:{FONT};font-size:16px;line-height:24px;color:{T["text"]};'
SMALL = f'margin:0 0 12px;font-family:{FONT};font-size:14px;line-height:21px;color:{T["muted"]};'


def p(text):
    return f'<p style="{P}">{text}</p>'


def small(text):
    return f'<p style="{SMALL}">{text}</p>'


def button(label, url):
    return (f'<table role="presentation" cellpadding="0" cellspacing="0" border="0" style="margin:8px 0 24px;"><tr>'
            f'<td style="border-radius:14px;background:{T["primary"]};">'
            f'<a href="{url}" style="display:inline-block;padding:14px 28px;font-family:{FONT};font-size:16px;'
            f'font-weight:700;line-height:20px;color:{T["onPrimary"]};text-decoration:none;border-radius:14px;">{label}</a>'
            f'</td></tr></table>'
            + small(f'Button not working? Copy this link into your browser:<br>'
                    f'<a href="{url}" style="color:{T["text"]};word-break:break-all;">{url}</a>'))


def note(text, strong=False):
    border = T['text'] if strong else T['border']
    return (f'<table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0" style="margin:0 0 20px;">'
            f'<tr><td style="background:{T["soft"]};border:1px solid {border};border-radius:14px;padding:14px 16px;'
            f'font-family:{FONT};font-size:14px;line-height:21px;color:{T["softText"]};">{text}</td></tr></table>')


def code(value):
    return (f'<table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0" style="margin:0 0 20px;">'
            f'<tr><td align="center" style="background:{T["soft"]};border-radius:14px;padding:20px;font-family:{FONT};'
            f'font-size:28px;font-weight:700;letter-spacing:6px;color:{T["text"]};">{value}</td></tr></table>')


def facts(rows):
    cells = ''.join(f'<tr><td style="padding:6px 12px 6px 0;font-family:{FONT};font-size:14px;color:{T["muted"]};'
                    f'white-space:nowrap;vertical-align:top;">{k}</td><td style="padding:6px 0;font-family:{FONT};'
                    f'font-size:14px;color:{T["text"]};word-break:break-all;">{v}</td></tr>' for k, v in rows)
    return (f'<table role="presentation" cellpadding="0" cellspacing="0" border="0" style="margin:0 0 20px;'
            f'border-top:1px solid {T["border"]};border-bottom:1px solid {T["border"]};width:100%;">{cells}</table>')


def steps(items):
    return ''.join(f'<p style="{P}margin-bottom:8px;">&nbsp;&nbsp;{i}.&nbsp;&nbsp;{text}</p>'
                   for i, text in enumerate(items, 1)) + '<div style="height:8px;"></div>'


def email(preheader, title, body, footer):
    return f'''<table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0" style="background:{T["soft"]};">
<tr><td align="center" style="padding:32px 16px;">
<div style="display:none;max-height:0;overflow:hidden;opacity:0;">{preheader}</div>
<table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0" style="max-width:560px;background:{T["surface"]};border:1px solid {T["border"]};border-radius:20px;">
<tr><td style="padding:32px 32px 8px;">{LOGO}</td></tr>
<tr><td style="padding:16px 32px 8px;">
<h1 style="margin:0 0 20px;font-family:{FONT};font-size:26px;line-height:32px;font-weight:700;color:{T["text"]};">{title}</h1>
{body}
</td></tr>
<tr><td style="padding:20px 32px 28px;border-top:1px solid {T["border"]};">
{small(footer)}
{small('Swoovr is run by RePass Cloud Pty Ltd. Help: <a href="https://swoovr.com/support" style="color:' + T["muted"] + ';">swoovr.com/support</a>')}
</td></tr>
</table>
</td></tr></table>'''


MEMBER_FOOTER = 'You got this email because it is about your Swoovr account. Swoovr never asks for your password by email.'
STAFF_FOOTER = 'Sent to Swoovr staff escalation addresses only. Contains no member names, messages or images.'

TEMPLATES = [
    dict(slug='01-verify-email', name='Confirm your email (sign-up)', sender='Keycloak — members realm (also staff/venues)',
         status='New wording (replaces Keycloak\'s stock English text)',
         trigger='A member creates an account, or signs in while their email is still unconfirmed.',
         subject='Confirm your email for Swoovr', preheader='One tap to finish creating your Swoovr account.',
         lifetime='24 hours (D367, PR for #353). After that, signing in sends a new link.',
         variants='Staff and venue realms: same email with "Swoovr Admin" / "Swoovr Venues" in the first paragraph.',
         title='Confirm your email',
         body=p('Hi Sam,') + p('Tap the button to confirm <strong>sam.example@example.test</strong> and finish setting up your Swoovr account.')
         + button('Confirm email', 'https://auth.swoovr.app/realms/swoovr-members/login-actions/action-token?key=…')
         + note('This link works for 24 hours. If it has expired, just sign in again and we\'ll send you a new one.')
         + p('Didn\'t sign up for Swoovr? You can ignore this email. Nobody can use the account until this address is confirmed.'),
         footer=MEMBER_FOOTER),
    dict(slug='02-password-reset', name='Reset your password', sender='Keycloak — members realm (staff break-glass account too)',
         status='New wording (replaces Keycloak\'s stock "update your credentials" text)',
         trigger='Someone chooses "Forgot password?" on the sign-in page and enters this address.',
         subject='Reset your Swoovr password', preheader='Use this link to choose a new password.',
         lifetime='5 minutes, single use (unchanged by D367).',
         variants='None.', title='Reset your password',
         body=p('Hi Sam,') + p('We got a request to reset the password for <strong>sam.example@example.test</strong>.')
         + button('Choose a new password', 'https://auth.swoovr.app/realms/swoovr-members/login-actions/action-token?key=…')
         + note('This link works for 5 minutes and only once.')
         + p('If you didn\'t ask for this, ignore this email. Your password stays the same. If these keep arriving, contact Swoovr Support.'),
         footer=MEMBER_FOOTER),
    dict(slug='03-staff-account-setup', name='Set up your staff account', sender='Keycloak — staff realm (execute-actions email)',
         status='New wording (replaces Keycloak\'s stock "Your administrator has just requested…" text)',
         trigger='The deployment creates the break-glass root account (D364), or an admin sends a staff set-up link.',
         subject='Set up your Swoovr Admin account', preheader='Confirm your email, choose a password and add an authenticator.',
         lifetime='12 hours (admin-sent links).',
         variants='The list shows only the steps still needed (for example only "Add an authenticator app" after an MFA reset).',
         title='Set up your Swoovr Admin account',
         body=p('An administrator created a Swoovr staff account for <strong>alex.staff@example.test</strong>. To finish setting it up you\'ll:')
         + steps(['Confirm this email address', 'Choose a password', 'Add an authenticator app'])
         + button('Set up account', 'https://auth.swoovr.app/realms/swoovr-staff/login-actions/action-token?key=…')
         + note('This link works for 12 hours. Keep your password and authenticator to yourself; Swoovr staff will never ask for them.')
         + p('Not expecting this? Ignore it and tell your Swoovr administrator.'),
         footer='You got this email because a Swoovr staff account was created for this address.'),
    dict(slug='04-confirm-new-email', name='Confirm your new email address', sender='Keycloak — members realm (update-email)',
         status='New wording (replaces Keycloak\'s stock text). OPEN: link lifetime, see below.',
         trigger='A member changes their email address in Me → Account.',
         subject='Confirm your new email for Swoovr', preheader='Confirm the change to finish updating your sign-in email.',
         lifetime='Today 5 minutes (realm default for user links). OPEN: lengthen to 24 hours like sign-up (D367)?',
         variants='Sent to the NEW address. The old address keeps working until this is confirmed.',
         title='Confirm your new email',
         body=p('Hi Sam,') + p('You asked to change the email on your Swoovr account to <strong>sam.new@example.test</strong>. Tap the button to confirm it.')
         + button('Confirm new email', 'https://auth.swoovr.app/realms/swoovr-members/login-actions/action-token?key=…')
         + note('Until you confirm, you keep signing in with your current email. If you have a founder code, we\'ll send a new one to this address.')
         + p('Didn\'t ask for this? Ignore this email and nothing changes.'),
         footer=MEMBER_FOOTER),
    dict(slug='05-beta-invitation', name='Private beta invitation', sender='Swoovr API (BetaInvitationDelivery)',
         status='Current wording, new layout',
         trigger='Staff add an address to the private beta (Admin → Beta).',
         subject='You\'re invited to the Swoovr private beta', preheader='Register or sign in with this email to join.',
         lifetime='No link expiry (link goes to the sign-in page).', variants='None.',
         title='You\'re invited to the Swoovr private beta',
         body=p('Staff have added this address to the Swoovr private beta. Register or sign in with this verified email to join.')
         + button('Open Swoovr', 'https://swoovr.app/account')
         + p('You must meet the account and onboarding requirements before connecting with members.')
         + p('If you did not expect this, you can ignore it or contact <a href="mailto:support@swoovr.com" style="color:' + T['text'] + ';">support@swoovr.com</a>.'),
         footer='You got this email because Swoovr staff added this address to the private beta.'),
    dict(slug='06-founder-code', name='Your founder code', sender='Swoovr API (Founders)',
         status='Current wording, new layout',
         trigger='A founding member\'s founder code is issued, or re-issued after an email change.',
         subject='Your Swoovr founder code', preheader='Keep this email: it restores your founding status.',
         lifetime='Code works until the email address changes.', variants='None.',
         title='Thank you for being a founding member',
         body=p('Thank you for being one of Swoovr\'s founding members. Your founder code is:') + code('SWV-7Q4K-2M9X')
         + p('<strong>Keep this email.</strong> If you ever delete your Swoovr account and come back with the same email address, enter this code on the coupon code screen to restore your founding status.')
         + small('To make recovery possible after account deletion, we keep the founder recovery-token record unless you choose "Also delete my founder recovery token" when deleting your account. You can later ask Swoovr Support to remove it at swoovr.com/support. Removing the record means founding status cannot be restored with this code.')
         + small('The code only works with this email address. If you change your email in Swoovr, we\'ll send you a new code and this one stops working.'),
         footer=MEMBER_FOOTER),
    dict(slug='07-something-new', name='Something new on Swoovr (notification)', sender='Swoovr API (Notifier)',
         status='Current wording, new layout',
         trigger='A request, accepted connection or travel reminder arrives and the member has email notifications on.',
         subject='Something new on Swoovr', preheader='Sign in to Swoovr to see it.',
         lifetime='No link expiry.',
         variants='"a new request" (shown) · "a new connection" · "a travel reminder". Never names the person or includes message text (privacy).',
         title='You have a new request',
         body=p('You have a new request on Swoovr. Sign in to Swoovr to see it.') + button('Open Swoovr', 'https://swoovr.app/')
         + small('You can change which notifications you get, or turn off emails, in Me → Notifications.'),
         footer='You got this email because email notifications are on for your Swoovr account.'),
    dict(slug='08-account-notice', name='A notice about your account', sender='Swoovr API (Moderation)',
         status='Current wording, new layout',
         trigger='Trust & Safety posts a notice to a member (warning, restriction, decision or appeal outcome).',
         subject='A notice about your Swoovr account', preheader='Sign in and open Me to read it.',
         lifetime='No link expiry.', variants='None; the notice itself is only shown inside Swoovr.',
         title='There\'s a notice about your account',
         body=p('There\'s a new notice about your Swoovr account. Sign in to Swoovr and open <strong>Me</strong> to read it.')
         + button('Open Swoovr', 'https://swoovr.app/account'),
         footer=MEMBER_FOOTER),
    dict(slug='09-security-changed', name='Your account security changed (MFA)', sender='Swoovr API (MfaEndpoints)',
         status='Current wording, new layout',
         trigger='Multi-factor authentication on an account is changed by the member or by staff.',
         subject='Your Swoovr account security changed', preheader='Multi-factor authentication was changed on your account.',
         lifetime='No link.',
         variants='"MFA was disabled…" (shown) · "The multi-factor authentication requirement on your Swoovr account was changed." · '
                  '"An administrator reset your Swoovr authenticator after an account recovery review. Existing sessions have ended. If MFA is required, you must enrol again before continuing."',
         title='Your account security changed',
         body=p('Multi-factor authentication was disabled on your Swoovr account.')
         + note('<strong>If this wasn\'t you</strong>, contact Swoovr Support straight away at swoovr.com/support.', strong=True),
         footer=MEMBER_FOOTER),
    dict(slug='10-trusted-contact-invite', name='Trusted contact invitation', sender='Swoovr API (MeetingSafetyDelivery)',
         status='Current wording, new layout',
         trigger='A member adds someone as a trusted contact for meeting safety (email channel).',
         subject='Swoovr meeting safety', preheader='Sam invited you to be a Swoovr trusted contact.',
         lifetime='Confirm link: 7 days.', variants='SMS channel sends the same text by SMS.',
         title='Sam invited you to be a trusted contact',
         body=p('Sam has invited you to be a Swoovr trusted contact. Only accept if you know this person and agree to receive voluntary meeting-safety updates.')
         + button('Confirm or decline', 'https://swoovr.app/safety/contact/…')
         + note('This is not marketing. Swoovr does not monitor emergencies or dispatch responders. Alerts are not guaranteed to arrive.')
         + small('Withdraw or unsubscribe at any time: <a href="https://swoovr.app/safety/contact/…" style="color:' + T['muted'] + ';">https://swoovr.app/safety/contact/…</a>'),
         footer='You got this email because a Swoovr member entered this address as a trusted contact.'),
    dict(slug='11-meeting-safety-update', name='Meeting safety update', sender='Swoovr API (MeetingSafetyDelivery)',
         status='Current wording, new layout. Proposed addition: "(000 in Australia)"',
         trigger='A member who named you as a trusted contact shares a plan, misses a check-in, asks for help or ends monitoring.',
         subject='Swoovr meeting safety', preheader='Sam: a check-in is missed/unconfirmed.',
         lifetime='Read-only status link: short-lived, needs a verification code.',
         variants='"A check-in is missed/unconfirmed…" (shown) · "The member requested help…" · "The member has shared a voluntary meeting safety plan." · '
                  '"The member marked home safe; monitoring has ended." · "Monitoring has ended without an assertion of safety."',
         title='Sam: a check-in is missed',
         body=p('<strong>Sam:</strong> A check-in is missed/unconfirmed. This is not proof of an emergency.')
         + small('Member-provided, not independently verified. Verify with a short-lived code before viewing meeting times or a shared place.')
         + button('View meeting status', 'https://swoovr.app/safety/meeting/…')
         + note('Swoovr does not monitor emergencies or dispatch responders. Alerts are not guaranteed to arrive. If needed, use local emergency services (000 in Australia). Never treat a missing alert as proof of safety.', strong=True)
         + small('Withdraw or unsubscribe: <a href="https://swoovr.app/safety/contact/…" style="color:' + T['muted'] + ';">https://swoovr.app/safety/contact/…</a>'),
         footer='You got this email because you agreed to be a Swoovr trusted contact.'),
    dict(slug='12-staff-urgent-report', name='Staff: urgent report / serious safety incident', sender='Swoovr API (Moderation, MediaPreservation)',
         status='Current wording, new layout',
         trigger='An urgent or Serious Safety Incident report is filed, or known-harmful media is preserved.',
         subject='Swoovr serious safety incident: acknowledgement needed', preheader='Open the Swoovr admin app and acknowledge the case.',
         lifetime='No link expiry; the case stays open until acknowledged.',
         variants='Urgent report ("Swoovr urgent report: acknowledgement needed", "An urgent report needs review.") · Serious Safety Incident (shown) · '
                  'Media preservation (Category media-known-content, Queue Admin, "No image or member details are included.").',
         title='Serious safety incident: acknowledgement needed',
         body=note('A Serious Safety Incident report needs review now. If anyone may be in immediate danger, act before routine work. Evidence is preserved under a provisional legal hold.', strong=True)
         + facts([('Case', '3f2a9c1e-5b7d-4e8a-9c2f-1a2b3c4d5e6f'), ('Category', 'threats-of-violence'), ('Queue', 'TrustSafety')])
         + button('Open Swoovr Admin', 'https://admin.swoovr.app/')
         + p('Acknowledge the case in the admin app. It stays open until someone does.'),
         footer=STAFF_FOOTER),
    dict(slug='13-staff-security-alert', name='Staff: security alert', sender='Swoovr API (SecurityOperations)',
         status='Current wording, new layout',
         trigger='A security incident is raised (for example repeated privileged-access failures or a possible data breach).',
         subject='Swoovr security alert (High): privileged-access-failures', preheader='A security alert needs review.',
         lifetime='No link expiry.', variants='Severity and kind change per incident; the summary line is optional.',
         title='A security alert needs review',
         body=facts([('Incident', '9b1c7d2e-4f6a-4b8c-8d9e-0f1a2b3c4d5e'), ('Severity', 'High'), ('Kind', 'privileged-access-failures')])
         + p('Open the Swoovr admin app (Security incidents).')
         + note('If this may be an eligible data breach, start the assessment now; it must finish within 30 days.', strong=True)
         + button('Open Security incidents', 'https://admin.swoovr.app/security'),
         footer=STAFF_FOOTER),
]


def panel(t):
    rows = [('Status', t['status']), ('Sent by', t['sender']), ('When', t['trigger']), ('From', FROM),
            ('Subject', html.escape(t['subject'])), ('Preview text', html.escape(t['preheader'])),
            ('Link lifetime', t['lifetime']), ('Variants', t['variants'])]
    cells = ''.join(f'<tr><th>{k}</th><td>{v}</td></tr>' for k, v in rows)
    return f'''<header class="review">
<p class="crumb"><a href="index.html">Swoovr email templates</a> · for owner review</p>
<h1>{t["name"]}</h1>
<table>{cells}</table>
<p class="ask">Approve or request changes on {ISSUE}, quoting <code>{t["slug"]}</code>.</p>
</header>'''


PAGE = '''<!doctype html>
<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
<title>{title}</title>
<style>
body{{margin:0;background:#FAFAFA;color:#0A0A0A;font-family:{font};}}
.review{{max-width:760px;margin:0 auto;padding:24px 16px 8px;}}
.review h1{{font-size:22px;margin:4px 0 12px;}}
.review table{{border-collapse:collapse;width:100%;font-size:14px;}}
.review th{{text-align:left;color:#6B6B6B;font-weight:400;padding:6px 12px 6px 0;vertical-align:top;white-space:nowrap;width:1%;}}
.review td{{padding:6px 0;border-bottom:1px solid #E5E5E5;}}
.crumb,.ask{{font-size:13px;color:#6B6B6B;}} .crumb a{{color:#0A0A0A;}}
.list{{max-width:760px;margin:0 auto;padding:24px 16px;}} .list li{{margin:0 0 10px;}} .list a{{color:#0A0A0A;font-weight:700;}}
.list span{{color:#6B6B6B;font-size:14px;}}
</style></head>
<body>
{body}
</body></html>
'''


def main():
    for t in TEMPLATES:
        body = panel(t) + '\n<!-- EMAIL START: the part below is what the recipient sees -->\n' + email(
            t['preheader'], t['title'], t['body'], t['footer']) + '\n<!-- EMAIL END -->'
        (HERE / (t['slug'] + '.html')).write_text(PAGE.format(title=html.escape('Email: ' + t['name']), font=FONT, body=body))
    items = ''.join(f'<li><a href="{t["slug"]}.html">{html.escape(t["name"])}</a><br><span>{html.escape(t["subject"])} · '
                    f'{t["sender"]} · {t["status"]}</span></li>' for t in TEMPLATES)
    (HERE / 'index.html').write_text(PAGE.format(title='Swoovr email templates', font=FONT, body=(
        f'<main class="list"><h1>Swoovr email templates</h1><p>Every email Swoovr sends, in the Standard black-and-white '
        f'scheme with the official logo. Sample people and addresses are fictional. Approve or request changes on {ISSUE}.</p>'
        f'<ol>{items}</ol></main>')))
    print(f'{len(TEMPLATES)} templates + index written to {HERE.relative_to(ROOT)}')


if __name__ == '__main__':
    main()
