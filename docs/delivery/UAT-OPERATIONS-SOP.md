# UAT operations SOP

Plain-language procedures for running the Swoovr UAT environment day to day. Run every terminal command from your checkout (`cd ~/Developer/wanderconnect-brief`): `gh` takes the repository from it. Written for the owner, who doesn't work on the back end. Every procedure says **what to run**, **what you should see** and **what to do if you see something else**.

Technical detail lives in the [server runbook](../../deploy/vultr-uat/README.md) and [GitHub deployment](UAT-GITHUB-DEPLOYMENT.md). Decisions: D362 (deployment), D363 (staff roles from Entra), D364 (break-glass root admin).

## Contents

1. [How the system fits together](#1-how-the-system-fits-together)
2. [Running a maintenance action (and the tag rule)](#2-running-a-maintenance-action-and-the-tag-rule)
3. [Deploying a new version](#3-deploying-a-new-version)
4. [The break-glass root admin](#4-the-break-glass-root-admin)
5. [Staff access and roles (Microsoft Entra)](#5-staff-access-and-roles-microsoft-entra)
6. [Email](#6-email)
7. [Settings: where each value lives](#7-settings-where-each-value-lives)
8. [Looking at the server directly](#8-looking-at-the-server-directly)
9. [Photos and image processing](#9-photos-and-image-processing)
10. [Quick reference](#10-quick-reference)

## 1. How the system fits together

```
your Mac ──git tag vX.Y.Z──▶ GitHub ──"Deploy UAT" workflow──▶ builds 6 images (GHCR)
                                     │                          └─▶ SSH as root ──▶ server uat-01 (Vultr)
                                     └─"UAT maintenance" workflow (you start it by hand) ──▶ SSH ──▶ uat-01
```

- **Server `uat-01`** (Vultr, Debian) runs everything in Docker: the API, the member web app (`uat.swoovr.app`), Admin (`admin-uat.swoovr.app`), Keycloak sign-in (`auth-uat.swoovr.app`), the Zammad helpdesk (`helpdesk-uat.swoovr.app`), PostgreSQL, photo screening (`image-moderation`, private) and one virus scanner (`clamav`, private). Cloudflare sits in front.
- **The `swoovr` command on the server** (`/opt/swoovr/swoovr-uat/bin/swoovr`) does every server-side step. GitHub only ever runs that command over SSH.
- **Two GitHub workflows:**
  - **Deploy UAT** runs by itself when you push a version tag (section 3).
  - **UAT maintenance** runs only when you start it, for small jobs that don't deploy anything (section 2).
- **Keycloak** handles every sign-in. Staff sign in with Microsoft (Entra) or, for the root admin only, with email + password + code (sections 4–5).
- **Settings** live in two places: GitHub (secrets and variables) and env files on the server (section 7).

## 2. Running a maintenance action (and the tag rule)

The **UAT maintenance** workflow runs one small action on the server, then stops. It never deploys, migrates, wipes or rolls back anything.

| Action | What it does | When to use it |
| --- | --- | --- |
| `status` | Lists the containers and their health, checked through the web server | "Is everything up?" |
| `history` | Lists the versions deployed, newest last | "What's running, and since when?" |
| `admin-invite` | Emails the break-glass root admin its set-up link again | The set-up email didn't arrive or its link expired (section 4) |
| `admin-finalize` | Gives the root admin role to the break-glass account if it's ready | After the break-glass account has completed set-up and signed in once (section 4) |

### The tag rule (important)

The GitHub environment `uat` holds the SSH key to the server. It only lets runs **from a version tag** (`v*`) use it, so no branch or pull request can reach the server. A maintenance run must therefore **start from a tag**, normally the version currently deployed.

If you start it from `main` instead, it fails straight away with **"The deployment was rejected or didn't satisfy other protection rules."** Nothing ran; start it again from a tag.

**Which tag?** The latest deployed version: see the newest run of **Deploy UAT** in Actions, or `git tag --sort=-v:refname | head -1`. In the commands below it's `v1.0.2`; replace it with the current one.

### From the terminal

```bash
gh workflow run uat-maintenance.yml --ref v1.0.2 -f action=status
gh workflow run uat-maintenance.yml --ref v1.0.2 -f action=history
gh workflow run uat-maintenance.yml --ref v1.0.2 -f action=admin-invite
gh workflow run uat-maintenance.yml --ref v1.0.2 -f action=admin-finalize
```

Then follow it and read the result:

```bash
gh run watch $(gh run list --workflow uat-maintenance.yml --limit 1 --json databaseId -q '.[0].databaseId')
```

### From the GitHub website

1. **Actions** → **UAT maintenance** (left column) → **Run workflow** (right).
2. **Use workflow from:** open the drop-down, switch to the **Tags** tab, pick the deployed version (e.g. `v1.0.2`). Not `main`.
3. **action:** pick the action.
4. **Run workflow**, then open the run. The **summary** at the bottom of the run page lists the results (`SWOOVR_RESULT` lines).

### Reading the result

The summary shows lines such as `root-account=invite-sent` or `initial-admin=granted`. In logs, GitHub shows `***` wherever a secret's value would appear. Because the SSH user secret is `root`, the word "root" itself appears as `***` (for example `***-account=invite-sent`). That is cosmetic.

## 3. Deploying a new version

Run `./scripts/update-release.sh X.Y.Z` from a clean `main` (D383). It commits `updating to vX.Y.Z` (the version in `Directory.Build.props` and the Keycloak theme) to `main`, tags that commit, pushes the tag, which starts the deploy, and watches the run. It fails non-zero if the run fails. Re-running after an interruption resumes; a published tag is never moved. See [VERSIONING.md](VERSIONING.md#releasing).

**Upgrade or refresh.** The repository variable `DEPLOYMENT_MODE` decides, when the run starts:

| Mode | What happens | Use it when |
| --- | --- | --- |
| `upgrade` (normal) | Backup, then new versions of the apps; **all data kept** | Every ordinary release |
| `refresh` | Backup, then **everything is deleted and rebuilt**: databases, Keycloak users, Zammad data and its mail setup, every photo in the R2 bucket | Sign-in/realm changes that need a rebuild, or a deliberate clean start |

```bash
gh variable get DEPLOYMENT_MODE          # check first
gh variable set DEPLOYMENT_MODE --body upgrade   # or refresh

cd ~/Developer/wanderconnect-brief && git checkout main && git pull
git tag v1.0.3 origin/main && git push origin v1.0.3
gh run watch $(gh run list --workflow deploy-uat.yml --limit 1 --json databaseId -q '.[0].databaseId')
```

**After a refresh:**
- Set `DEPLOYMENT_MODE` back to `upgrade` once you're happy, or the next tag wipes everything again.
- Repeat section 4 (the break-glass account is recreated and gets a new set-up email).
- Repeat the Zammad mailbox setup ([runbook "Zammad mail"](../../deploy/vultr-uat/README.md#zammad-mail-microsoft-365)).
- Staff sign in again with Microsoft and set up their authenticator again.

**If a deployment fails**, the run names the stage. Data is never deleted before a verified backup. See the [runbook "Recovery"](../../deploy/vultr-uat/README.md#recovery); the usual fix is to correct the cause and push a new tag (tags can't be reused or moved).

## 4. The break-glass root admin

There is exactly **one root admin**, and it is deliberately **not** a Microsoft/Entra account (D364). It's the address in the GitHub variable `UAT_INITIAL_ADMIN_EMAIL`, with its own password and authenticator app, so it still works if Entra, the RePass Cloud tenant or Microsoft 365 is down. Use it for emergencies and for root-only tasks. Day-to-day work uses an Entra account with the Admin role (section 5).

### Setting it up (after every refresh, or the first time)

1. **Deploy** (section 3). The run summary shows `root-account=created-invite-sent` and `initial-admin=invite-not-completed`. Every deployment creates the account if it doesn't exist and Keycloak emails it a set-up link, **valid 12 hours**. It never creates a second account.
2. **Open the email** (from Swoovr, `support@swoovr.com`; check junk too). Verify the address, choose a password (at least 12 characters), and scan the QR code with an authenticator app (Microsoft Authenticator, 1Password, etc.). **Keep a backup of the authenticator offline**: this account exists for emergencies.
   - **Email missing or link expired?** Run **`admin-invite`** (section 2). It only resends the email; no redeploy is needed. The summary should show `root-account=invite-sent`. If it still doesn't arrive, see section 6.
3. **Sign in once** at https://admin-uat.swoovr.app using the **email + password form** (not "Sign in with Microsoft"), then the 6-digit code. "Your role can't do this" is expected now.
4. **Run `admin-finalize`** (section 2). The summary should show `initial-admin=granted`. In Admin, choose **Sign in again**; the full workspace appears.

### What the `initial-admin` result means

| Result | Meaning | Do |
| --- | --- | --- |
| `invite-not-completed` | The email's steps aren't finished | Complete the email (step 2); `admin-invite` to resend |
| `unverified-email` / `missing-password-or-authenticator` | Part of the set-up is missing | Same as above |
| `pending-first-sign-in` | Set-up done, but never signed in to Admin | Step 3, then `admin-finalize` |
| `granted` / `already-granted` | Done | Nothing |
| `linked-to-identity-provider` | Someone linked this account to Microsoft; it must stay local | Ask for help; nothing was changed |
| `held-by-another-account` | Another account already has root | Ask for help; nothing was changed |
| `pending-invite` | No account exists yet (address not set?) | Check `UAT_INITIAL_ADMIN_EMAIL`, then deploy |

**Changing the address:** `gh variable set UAT_INITIAL_ADMIN_EMAIL --env uat --body '<address>'`. The next deployment creates the new account and emails it, but root is never moved from an existing holder automatically: `admin-finalize` reports `held-by-another-account` until no root exists, which in practice means after a **refresh**.

## 5. Staff access and roles (Microsoft Entra)

Staff sign in with **Sign in with Microsoft**. Two things in Entra decide what happens (D363):

| In Entra | Effect |
| --- | --- |
| Member of the group **Swoovr UAT** (assigned to the app with Default Access) | May sign in at all |
| Member of **Swoovr UAT Support / Trust & Safety / Admin / Legal** (each assigned to the app with its app role) | Gets that role in Swoovr |

- **Adding someone:** add them to **Swoovr UAT**, then to the role group they need. Entra lets anyone in *any* assigned group sign in, so always add to Swoovr UAT first as a habit.
- **Changes apply at their next Microsoft sign-in.** Removing someone from a role group removes that role then.
- **Roles granted in Swoovr Admin** (Staff → a person → Internal roles) are separate: Entra never changes or removes them. Roles that came from Entra show as "(managed in Entra)" and can't be edited in Admin.
- **First sign-in for a staff member:** Microsoft, then verify their email by link, then set up an authenticator app (Keycloak shows a QR code). After that first set-up, **sign out and in once more** so the session counts as multi-factor.
- **Helpdesk** (`helpdesk-uat.swoovr.app` → **Swoovr staff sign-in**): only works for staff who already have a role; Swoovr creates their helpdesk access from their roles within a minute. Without a role you get 403.

## 6. Email

### Who sends what

| Sender | Service | From | Used for |
| --- | --- | --- | --- |
| Keycloak | Postmark (D368, #357) | `support@swoovr.com` | Set-up links, email verification, sign-in emails |
| Swoovr API | Postmark (D368, #357) | `support@swoovr.com` | Member and staff notifications |
| Zammad | Microsoft 365 mailbox (set up by you in Zammad) | the support mailbox | Helpdesk tickets |
| SMS | Mailpit (a catch-all on the server, nothing leaves) | — | Until Twilio SMS is verified |

The service names, sender and Postmark stream are in `env/mail.env` on the server (`SWOOVR_KEYCLOAK_MAIL_SERVICE`, `SWOOVR_API_MAIL_SERVICE`, `SWOOVR_MAIL_FROM`, `SWOOVR_MAIL_POSTMARK_STREAM`). They come from the GitHub `uat` variables `UAT_KEYCLOAK_MAIL_SERVICE`, `UAT_API_MAIL_SERVICE` and `UAT_POSTMARK_MESSAGE_STREAM`, and the token from the secret `UAT_POSTMARK_SERVER_TOKEN`. Twilio and MailerSend remain available by setting the service variables. Open and link tracking are off.

### "An email didn't arrive": the procedure

1. **Look it up in Postmark:** Servers → your server → **Activity**, search for the address. Postmark shows each message's status (processed, delivered, bounced, spam complaint) and the receiving server's reply. That answers most cases without anything else.
2. **Check junk / "Other"** in the receiving mailbox.
3. **Test the sending path** from your Mac. This sends one plain email through exactly the same provider, sender, token and stream that Keycloak uses:
   ```bash
   scripts/uat/mail-test.sh <server IPv4> <your address>
   ```
   - `ACCEPTED by postmark …` → the server and Postmark are fine. Go to step 4.
   - `REFUSED by postmark: HTTP 401` or `ErrorCode 10` → the token is wrong or revoked: update `UAT_POSTMARK_SERVER_TOKEN` (section 7) and deploy.
   - `ErrorCode 400`/`401` (sender signature) → `support@swoovr.com` / swoovr.com is not verified in Postmark.
   - `ErrorCode 406` → Postmark has deactivated that recipient after a bounce or spam complaint: reactivate it in Postmark (Activity → the address → Reactivate).
   - `ErrorCode 412` → the Postmark account is still pending approval and can send only to its own domain.
4. **If the test arrives but the real email didn't**, resend the real one:
   - root admin set-up email → `admin-invite` (section 2);
   - a member's or staff member's verification email → they sign in again; Keycloak offers "resend".
5. **If the resent email still doesn't arrive but the test does**, the receiving provider is filtering that message's content (for example its sign-in link). Postmark's Activity shows the receiving server's reply.

**Note:** "accepted" (HTTP 200, `ErrorCode` 0) means Postmark took the message, not that it reached the inbox.

**History (17 November 2026, #353):** with Twilio Email, iCloud deferred every message ("IPs were throttled by recipient server") because Twilio sends from shared SendGrid IPs; a verification email arrived about 30 minutes late. That is why UAT moved to Postmark (D368).

**swoovr.com DNS** (Cloudflare): mail is hosted in Microsoft 365 (MX). Postmark signs as swoovr.com with its own DKIM record and Return-Path CNAME (verified in Postmark). The older SendGrid DKIM records `s1`/`s2._domainkey` are only needed while Twilio is used. SPF lists only Microsoft (Postmark aligns through its Return-Path domain), DMARC is `p=none`. Use `dig @1.1.1.1 …` to check; some local resolvers return nothing.

## 7. Settings: where each value lives

| Kind | Where | How to change |
| --- | --- | --- |
| Deployment secrets and provider values (SSH, Entra, Didit, R2, Twilio…) | GitHub → Settings → Environments → **uat** | `scripts/uat/setup-github-uat.sh template` writes a private file listing every value with a description; fill it in, then `scripts/uat/setup-github-uat.sh values <file>`; check with `scripts/uat/setup-github-uat.sh status`; delete the file |
| A single variable | same | `gh variable set NAME --env uat --body '<value>'` |
| A single secret | same | `gh secret set NAME --env uat` (paste the value when asked) |
| Upgrade vs refresh | GitHub repository variable `DEPLOYMENT_MODE` | section 3 |
| Values generated on the server (database passwords, data keys, Zammad tokens) | `/opt/swoovr/swoovr-uat/env/*.env` on the server | Never in GitHub; they survive upgrades and refreshes |

A value left **empty** in GitHub keeps whatever the server already has. GitHub changes reach the server at the next deployment.

## 8. Looking at the server directly

Only when a procedure above says so. Everything here is read-only unless stated.

```bash
ssh -i ~/.ssh/swoovr-uat-deploy -o IdentitiesOnly=yes root@<server IPv4>
cd /opt/swoovr/swoovr-uat
./bin/swoovr status        # containers and health
./bin/swoovr history       # deployed versions
./bin/swoovr logs api      # recent logs of one service (api, web, admin, keycloak, zammad-railsserver, …)
exit
```

The full list of server commands is at the top of `deploy/vultr-uat/bin/swoovr` and in the [runbook "Day-2 commands"](../../deploy/vultr-uat/README.md#day-2-commands-on-the-host-exceptional).

## 9. Photos and image processing

Every photo is checked before anyone else can see it:
1. A virus scan (ClamAV), the moment it's uploaded.
2. Automatic checks for nudity, explicit content and known abuse material (`image-moderation`, on the same server, never on the internet).
3. Then one of three things happens: it is approved, sent to the photo review queue in Admin, or refused.

Full detail: [IMAGE-MODERATION-UAT.md](IMAGE-MODERATION-UAT.md).

### Is it working?

Open **Admin → Overview** (Support and Admin see it). The **Image processing** panel says one of:

- **Working normally.** Nothing to do.
- **Photos are waiting longer than usual.** The checks run, but some photos have waited over an hour. *Why photos are waiting* lists the reason in plain words.
- **Not processing photos.** The screening service or the virus scanner isn't answering. Photos stay private and wait; new uploads may be refused with "try again in a few minutes".

A waiting photo is **never** shown to other members, and is never approved because a check failed.

### When photos are waiting or not processing

1. Note the reason shown on the panel.
2. On the server (section 8):
   ```bash
   ./bin/swoovr status                              # is image-moderation / clamav running and healthy?
   ./bin/swoovr job api-command media pending-report  # the same counts and reasons as the panel
   ```
   **You should see** both services listed as healthy. If one isn't, run `./bin/swoovr logs image-moderation` (or `clamav`). Its first start takes up to 10 minutes while it loads.
3. If the reason mentions **Project Arachnid** (known-content check): check that the GitHub secrets `UAT_ARACHNID_USERNAME` and `UAT_ARACHNID_PASSWORD` are right, then deploy again. **Never** switch this check off.
4. Once the panel says *Working normally* again, check the waiting photos straight away instead of waiting for the hourly retry:
   ```bash
   ./bin/swoovr job api-command media rescreen --pending
   ```
   **You should see** "N pending photos are due now". The panel's numbers fall within a minute or two.

### Setting the Project Arachnid secrets (photo known-content checks)

Run these from your checkout on your Mac (they work in zsh and bash). Each line waits for you to type or paste the value. The password isn't shown on screen, and nothing goes into your shell history or onto the command line.

```bash
printf 'Arachnid username: '; read -r ARACHNID_USER; printf '%s' "$ARACHNID_USER" | gh secret set UAT_ARACHNID_USERNAME --env uat; unset ARACHNID_USER
printf 'Arachnid password: '; stty -echo; read -r ARACHNID_PASS; stty echo; echo; printf '%s' "$ARACHNID_PASS" | gh secret set UAT_ARACHNID_PASSWORD --env uat; unset ARACHNID_PASS
```

**You should see** `✓ Set Actions secret UAT_ARACHNID_USERNAME for …` and the same for the password. Check with:

```bash
gh secret list --env uat | grep ARACHNID
```

You should see both names with today's date. The values are never shown. The new values reach the server at the next deployment. To change a value later (for example after rotating the password), run the same line again.

Remove the four settings the old Vultr/Cloudflare image setup used (D384). Three are secrets and one is a variable:

```bash
for name in UAT_SCREENING_API_KEY UAT_MEDIA_PIPELINE_SERVICE_TOKEN UAT_MEDIA_PIPELINE_UPLOAD_SIGNING_KEY; do gh secret delete "$name" --env uat; done
gh variable delete UAT_SCREENING_BASE_URL --env uat
```

A "not found" message just means it was already gone. Do this once the D384 release is merged; deleting them earlier does no harm either.

### Clearing photos that are stuck "being checked"

Use this after the first D384 deployment (photos that were stuck before it), and any time the Overview panel showed *Not processing photos* and has now gone back to *Working normally*.

1. In Admin → Overview, check that *Image processing* says **Working normally**, and both services say **Answering**. If not, fix that first ("When photos are waiting or not processing", above). Re-checking against a broken service only records another failed attempt.
2. Connect to the server (section 8):
   ```bash
   ssh -i ~/.ssh/swoovr-uat-deploy -o IdentitiesOnly=yes root@<server IPv4>
   cd /opt/swoovr/swoovr-uat
   ```
3. See what is waiting, and why:
   ```bash
   ./bin/swoovr job api-command media pending-report
   ```
   **You should see** `Image processing: …`, the number of pending photos, and one line per reason. Before D384 the usual reason is `http-404`.
4. Re-check every waiting photo now, instead of at its next hourly retry:
   ```bash
   ./bin/swoovr job api-command media rescreen --pending
   ```
   **You should see** `Media rescreen: N pending photos are due now; …`. To re-check only photos with one reason, add it, for example `media rescreen --pending --reason http-404`.
5. Wait two or three minutes, then run step 3 again. **You should see** the pending number fall to 0, or close to it. In Admin, the photos are now approved, or waiting in the photo review queue (`/media`).
6. Type `exit` to leave the server.

**What this does and doesn't do:** it only moves each waiting photo's next check to "now". It never approves, rejects or shows a photo by itself; every photo still goes through the virus scan, the automatic checks and the normal rules.

**If the number doesn't fall:** run step 3 again and read the reason. Photos whose member account is locked, suspended or being deleted stay waiting on purpose. Any other reason: see "When photos are waiting or not processing", above.

### After the first deployment of D384 (once)

Full detail: "Owner steps for the first deployment" in [IMAGE-MODERATION-UAT.md](IMAGE-MODERATION-UAT.md). In order:
1. Set the Arachnid secrets and remove the four old settings (above).
2. Release. Make the new `swoovr-screening` package public when the deploy asks (section 3), then re-run the job.
3. Clear the photos that were stuck (above).
4. After 7 healthy days, retire the old Vultr image servers and Cloudflare image Workers, using that page's checklist (issue 396).

## 10. Quick reference

| I want to… | Do |
| --- | --- |
| Deploy a normal release | Check `DEPLOYMENT_MODE=upgrade`, push tag `vX.Y.Z` on `main` (section 3) |
| Wipe and rebuild UAT | `DEPLOYMENT_MODE=refresh`, push a tag, then sections 4 and 5, then back to `upgrade` |
| Resend the root admin's set-up email | Maintenance `admin-invite` **from the tag** (section 2) |
| Give the root admin role | Maintenance `admin-finalize` **from the tag** after the account signed in once |
| Give someone a staff role | Add them to Swoovr UAT + the role group in Entra (section 5) |
| Check an email problem | `scripts/uat/mail-test.sh <server IPv4> <address>` (section 6) |
| See what's running | Maintenance `status` / `history` |
| Change a setting | Section 7, then deploy |
| Check image processing | Admin → Overview → *Image processing*, or `./bin/swoovr job api-command media pending-report` (section 9) |
| Re-check waiting photos now | `./bin/swoovr job api-command media rescreen --pending` on the server (section 9, "Clearing photos that are stuck") |
| Set or change the Arachnid secrets | Section 9, "Setting the Project Arachnid secrets" |
