#!/usr/bin/env bash
# Send ONE plain test email from the UAT server through the same provider, sender and key that Keycloak uses
# (Postmark by default, D368; or the Twilio Email API; env/mail.env). Run it yourself, on your Mac. It proves the
# server can hand mail to the provider; delivery is then up to the provider and the mailbox (check inbox AND junk).
#
#   scripts/uat/mail-test.sh <server IPv4> <recipient address>
#
# SSH uses the dedicated deploy key ($SWOOVR_UAT_SSH_KEY, default ~/.ssh/swoovr-uat-deploy). Nothing is changed on the
# server. Output: the configured service names, the provider's HTTP status and whether it returned a message ID; never
# a key or secret. See docs/delivery/UAT-OPERATIONS-SOP.md "Email".
set -euo pipefail
host="${1:?usage: mail-test.sh <server IPv4> <recipient address>}"
to="${2:?usage: mail-test.sh <server IPv4> <recipient address>}"
[[ "$to" =~ ^[^@[:space:]\']+@[^@[:space:]\']+\.[^@[:space:]\']+$ ]] || { echo "Not an email address: $to" >&2; exit 2; }
key="${SWOOVR_UAT_SSH_KEY:-$HOME/.ssh/swoovr-uat-deploy}"
ssh -i "$key" -o IdentitiesOnly=yes -o BatchMode=yes -o ConnectTimeout=15 "root@$host" \
  "cd /opt/swoovr/swoovr-uat && python3 -I - env/mail.env '$to'" <<'PY'
import base64, json, sys, urllib.error, urllib.request
values = {}
for line in open(sys.argv[1], encoding='utf-8'):
    key, _, value = line.rstrip('\n').partition('=')
    values[key] = value.strip('"\'')
services = {name: values.get('SWOOVR_' + name) for name in ('KEYCLOAK_MAIL_SERVICE', 'API_MAIL_SERVICE')}
print('Keycloak mail service:', services['KEYCLOAK_MAIL_SERVICE'], '| API mail service:', services['API_MAIL_SERVICE'],
      '| from:', values.get('SWOOVR_MAIL_FROM'))
service = services['KEYCLOAK_MAIL_SERVICE']
if service not in ('postmark', 'twilio'):
    sys.exit('Keycloak mail service is ' + str(service) + '; this test covers postmark and twilio only.')
sender, name = values['SWOOVR_MAIL_FROM'], values.get('SWOOVR_MAIL_FROM_NAME') or 'Swoovr'
text = 'Test of the Swoovr sender (' + service + '). No action needed.'
html = '<html><body><p>' + text + '</p></body></html>'
if service == 'postmark':
    # D368: the same token, stream and tracking settings as Keycloak and the API.
    url = 'https://api.postmarkapp.com/email'
    headers = {'X-Postmark-Server-Token': values['SWOOVR_MAIL_POSTMARK_SERVER_TOKEN']}
    message = {'From': name + ' <' + sender + '>', 'To': sys.argv[2], 'Subject': 'Swoovr mail delivery test',
               'TextBody': text, 'HtmlBody': html, 'MessageStream': values.get('SWOOVR_MAIL_POSTMARK_STREAM') or 'outbound',
               'TrackOpens': False, 'TrackLinks': 'None'}
else:
    url = 'https://comms.twilio.com/v1/Emails'
    headers = {'Authorization': 'Basic ' + base64.b64encode((values['SWOOVR_MAIL_TWILIO_KEY_SID'] + ':' + values['SWOOVR_MAIL_TWILIO_KEY_SECRET']).encode()).decode()}
    message = {'from': {'address': sender, 'name': name}, 'to': [{'address': sys.argv[2]}],
               'content': {'subject': 'Swoovr mail delivery test', 'text': text, 'html': html}}
request = urllib.request.Request(url, method='POST', data=json.dumps(message).encode(),
                                 headers={**headers, 'Content-Type': 'application/json', 'Accept': 'application/json'})
try:
    with urllib.request.urlopen(request, timeout=20) as response:
        code, body = response.status, json.loads(response.read() or b'{}')
except urllib.error.HTTPError as error:
    code, body = error.code, {}
    try:
        body = json.loads(error.read() or b'{}')
    except ValueError:
        pass
accepted = (code == 200 and body.get('ErrorCode') == 0 and body.get('MessageID')) if service == 'postmark' \
    else (code == 202 and body.get('operationId'))
if accepted:
    print('ACCEPTED by ' + service + ' (HTTP ' + str(code) + ', message ID returned). Now check the inbox and junk folder.')
else:
    detail = body.get('Message') if service == 'postmark' else (body.get('errors') or body.get('message'))
    print('REFUSED by ' + service + ': HTTP', code, 'ErrorCode', body.get('ErrorCode'), json.dumps(detail)[:600])
    sys.exit(1)
PY
