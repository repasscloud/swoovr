# UAT refresh SOP: wipe and rebuild UAT, every step

This is the complete procedure for a **refresh** of the Swoovr UAT environment. It is written for someone who has never done one and knows nothing about how UAT is built. Follow it **top to bottom, in order**. Do not skip a step. Every command is given in full. Where a command needs a value only you have (for example the server's address), the step says exactly where to find it.

Related documents (you don't need them to follow this page; they hold the background):

- [UAT operations SOP](UAT-OPERATIONS-SOP.md): day-to-day owner procedures.
- [Server runbook](../../deploy/vultr-uat/README.md): what the server does, in technical detail.
- [GitHub deployment configuration](UAT-GITHUB-DEPLOYMENT.md): every GitHub secret and variable.
- Decisions: D362 (deployment), D363 (staff roles from Entra), D364 (break-glass root admin), D383 (one-command releases).

**Quick start: the refresh script.** After the one-time set-up (section 2) and telling people (section 3.1), one command does sections 3.2 to 8 and prepares section 9:

```bash
./scripts/refresh-uat.sh                     # releases the next minor version (1.1.2 -> 1.2.0), then refreshes to it
./scripts/refresh-uat.sh --existing v1.2.0   # refreshes a tag that is already released (section 4 Option A, or a retry)
```

Run it from the repository root on `main` with a clean tree. It connects to the server as `swoovr-uat-root` (a host alias in `~/.ssh/config`; set `SWOOVR_UAT_SSH` to use another). It asks you to type `refresh vX.Y.Z` before anything changes. It runs every check in section 3, releases with `./scripts/update-release.sh`, unlocks, re-runs the deployment as a refresh, and **relocks as soon as the run's deploy job reads "Deploy to UAT (refresh)"** (and on any failure or Control-C). It then checks the result (section 8) and the server, copies the temporary Keycloak password (section 9.1) to your clipboard without showing it, and prints your remaining steps (sections 9.2 to 14) with the tag filled in. If it fails after the release, it prints the `--existing` command to retry. The rest of this page is the same procedure by hand, and the detail for every manual step.

## Contents

- [UAT refresh SOP: wipe and rebuild UAT, every step](#uat-refresh-sop-wipe-and-rebuild-uat-every-step)
  - [Contents](#contents)
  - [0. What a refresh is, and what it deletes](#0-what-a-refresh-is-and-what-it-deletes)
  - [1. Things you need before you start](#1-things-you-need-before-you-start)
  - [2. One-time set-up on your Mac (skip any part you have done before)](#2-one-time-set-up-on-your-mac-skip-any-part-you-have-done-before)
    - [2.1 Install and sign in to the GitHub command line](#21-install-and-sign-in-to-the-github-command-line)
    - [2.2 Get the repository](#22-get-the-repository)
    - [2.3 Check you can reach the server](#23-check-you-can-reach-the-server)
    - [2.4 Prepare Firefox for the private Keycloak admin console](#24-prepare-firefox-for-the-private-keycloak-admin-console)
      - [2.4.1 Why you need a special Firefox profile and a certificate](#241-why-you-need-a-special-firefox-profile-and-a-certificate)
      - [2.4.2 Install Firefox](#242-install-firefox)
      - [2.4.3 Download Cloudflare's Origin CA root certificate](#243-download-cloudflares-origin-ca-root-certificate)
      - [2.4.4 Verify the certificate (three checks, all must pass)](#244-verify-the-certificate-three-checks-all-must-pass)
      - [2.4.5 Create the separate Firefox profile](#245-create-the-separate-firefox-profile)
      - [2.4.6 Send this profile's traffic through the tunnel (proxy settings)](#246-send-this-profiles-traffic-through-the-tunnel-proxy-settings)
      - [2.4.7 Import the root certificate into this profile only](#247-import-the-root-certificate-into-this-profile-only)
      - [2.4.8 Test the whole path once](#248-test-the-whole-path-once)
      - [2.4.9 Rules for this profile](#249-rules-for-this-profile)
  - [3. Pre-flight checks (every refresh)](#3-pre-flight-checks-every-refresh)
    - [3.1 Tell people](#31-tell-people)
    - [3.2 Check GitHub sign-in](#32-check-github-sign-in)
    - [3.3 Check no other deployment is running](#33-check-no-other-deployment-is-running)
    - [3.4 Check the current deployment mode](#34-check-the-current-deployment-mode)
    - [3.5 Check every required GitHub setting is present](#35-check-every-required-github-setting-is-present)
    - [3.6 Note the break-glass root admin's email address](#36-note-the-break-glass-root-admins-email-address)
    - [3.7 Check the server and the current deployment](#37-check-the-server-and-the-current-deployment)
  - [4. Choose the version to refresh to](#4-choose-the-version-to-refresh-to)
    - [Option A (usual): refresh the version that is running now](#option-a-usual-refresh-the-version-that-is-running-now)
    - [Option B: refresh to a new version](#option-b-refresh-to-a-new-version)
  - [5. UNLOCK: switch the deployment mode to refresh](#5-unlock-switch-the-deployment-mode-to-refresh)
  - [6. Run the refresh](#6-run-the-refresh)
  - [7. RELOCK: switch the deployment mode back to upgrade](#7-relock-switch-the-deployment-mode-back-to-upgrade)
  - [8. Read and check the result](#8-read-and-check-the-result)
  - [9. Replace the temporary Keycloak administrator](#9-replace-the-temporary-keycloak-administrator)
    - [9.1 Find the temporary password (on the server)](#91-find-the-temporary-password-on-the-server)
    - [9.2 UNLOCK: open the private tunnel to the Keycloak console](#92-unlock-open-the-private-tunnel-to-the-keycloak-console)
    - [9.3 Create your personal Keycloak administrator](#93-create-your-personal-keycloak-administrator)
    - [9.4 RELOCK: close the tunnel](#94-relock-close-the-tunnel)
  - [10. Set up the break-glass root admin](#10-set-up-the-break-glass-root-admin)
  - [11. Set up the helpdesk (Zammad) mailbox again](#11-set-up-the-helpdesk-zammad-mailbox-again)
  - [12. Staff sign in again](#12-staff-sign-in-again)
  - [13. Registration and beta invitations](#13-registration-and-beta-invitations)
  - [14. Final checks](#14-final-checks)
  - [15. If something goes wrong](#15-if-something-goes-wrong)
  - [16. Every lock and unlock in this procedure](#16-every-lock-and-unlock-in-this-procedure)
  - [17. One-page checklist](#17-one-page-checklist)

---

## 0. What a refresh is, and what it deletes

UAT is the test copy of Swoovr. It runs on **one server**, named `uat-01`, hosted at Vultr. Everything (the apps, the databases, sign-in, the helpdesk) runs on that server in Docker. Cloudflare sits in front of it.

You never type deployment commands on the server. Instead, GitHub does it: a GitHub **workflow** named **Deploy UAT** connects to the server and runs everything. A GitHub setting (a *repository variable*) named `DEPLOYMENT_MODE` tells that workflow which kind of deployment to do:

| `DEPLOYMENT_MODE` | What happens |
| --- | --- |
| `upgrade` (normal) | A backup is taken, then new versions of the apps are installed. **All data is kept.** |
| `refresh` | A backup is taken, then **everything is deleted and rebuilt from nothing**. |

**A refresh deletes:**

- every member account, profile, message, connection and report;
- every staff account in Keycloak (staff sign in again; see section 12);
- every role given to staff **inside Swoovr Admin** (roles that come from Microsoft Entra come back by themselves);
- the break-glass root admin's account (it is recreated and must be set up again; section 10);
- the Keycloak administrator account (a temporary one comes back; section 9);
- all helpdesk (Zammad) tickets, and Zammad's mailbox set-up (section 11);
- **every photo** in the UAT photo store (the Cloudflare R2 bucket; the bucket itself is kept);
- registration settings and beta enrolments (they go back to "beta registration only", with no enrolments; section 13).

**A refresh does not delete or change:**

- the server itself, its firewall, its certificates and its secrets (database passwords, encryption keys). One exception (D386, #407): if the founder coupon key isn't in the hex form the app needs, the refresh replaces it once;
- anything in GitHub (secrets, variables, code, tags);
- anything in Microsoft Entra (groups and roles stay);
- records held by outside providers (for example Didit identity-check sessions, Postmark email history).

Before deleting anything, the refresh **always** takes a backup and checks it. If the backup fails, nothing is deleted.

**Who is affected:** everyone using UAT. Every member and staff member is signed out and their data is gone. Tell them before you start (section 3, step 3.1).

---

## 1. Things you need before you start

You need all of these. If one is missing, stop and get it first.

| What | Why | Where it comes from |
| --- | --- | --- |
| A Mac with Terminal | All commands are typed into Terminal | Applications → Utilities → Terminal |
| The repository checked out at `~/Developer/swoovr` | The commands run from it | Section 2.2 |
| The GitHub command line `gh`, signed in to an account with **admin** rights on the repository https://github.com/repasscloud/swoovr | It changes GitHub settings and starts the workflow | Section 2.1 |
| The deploy SSH key `~/.ssh/swoovr-uat-deploy` | To reach the server | Created once when UAT was built ([GitHub deployment, prerequisite 6](UAT-GITHUB-DEPLOYMENT.md#prerequisites-one-time)) |
| The server's public IPv4 address | To reach the server | Vultr console: https://my.vultr.com/ → **Products** → **Compute** → the server `uat-01` → **IPv4** (Overview tab). Write it down; this page calls it `<server IPv4>` |
| Access to the mailbox of the break-glass root admin address | It receives a set-up email (section 10) | See section 3, step 3.6, for how to see which address it is |
| An authenticator app on your phone (Microsoft Authenticator, 1Password, Google Authenticator or similar) | Keycloak and the root admin need a 6-digit code | App Store |
| Firefox, with the `swoovr-uat-admin` profile and Cloudflare's Origin CA root certificate imported into it | To open the private Keycloak admin console (section 9) | Section 2.4 sets up all of it, step by step |
| The Microsoft 365 sign-in for the support mailbox that Zammad uses | To reconnect the helpdesk mailbox (section 11) | Your Microsoft 365 admin. **This address is not recorded in the repository**: write it down here when you know it |
| About 1–2 hours | The deployment runs for a while, then the manual set-up follows | — |

In every command below:

- Replace `<server IPv4>` with the server's address, for example `203.0.113.10`. Type it **without** the `<` and `>`.
- Replace `vX.Y.Z` with the version chosen in section 4, for example `v1.1.2`.
- Lines starting with `#` are comments. You don't have to type them.

---

## 2. One-time set-up on your Mac (skip any part you have done before)

### 2.1 Install and sign in to the GitHub command line

1. Install Homebrew if `brew --version` doesn't print a version. Follow https://brew.sh/.
2. Install `gh`:

   ```bash
   brew install gh
   ```

3. Sign in:

   ```bash
   gh auth login
   ```

   Answer: **GitHub.com** → **HTTPS** → **Yes** (authenticate Git) → **Login with a web browser**. Copy the code it shows, press Enter, paste the code in the browser, approve.

4. Check:

   ```bash
   gh auth status
   ```

   **You should see** `✓ Logged in to github.com account <your account>`.

### 2.2 Get the repository

If `~/Developer/swoovr` doesn't exist yet:

```bash
mkdir -p ~/Developer
cd ~/Developer
gh repo clone https://github.com/repasscloud/swoovr swoovr
```

### 2.3 Check you can reach the server

```bash
ssh -i ~/.ssh/swoovr-uat-deploy -o IdentitiesOnly=yes root@<server IPv4> 'hostname'
```

**You should see** `uat-01`. The first time, SSH asks `Are you sure you want to continue connecting (yes/no/[fingerprint])?`. Type `yes` only if the fingerprint matches the ED25519 fingerprint recorded when the server was built (`ssh/host-key-fingerprints.txt` in your 12 November 2026 server collection, under `~/swoovr-uat-backups/`). If it doesn't match, **stop** and ask for help.

**If you see** `Permission denied (publickey)`: the key file is missing or wrong. **Stop** and ask for help.

### 2.4 Prepare Firefox for the private Keycloak admin console

Do this once per Mac. It takes about 15 minutes. After it, section 9 needs only "start the tunnel, open Firefox".

#### 2.4.1 Why you need a special Firefox profile and a certificate

- The Keycloak admin console (`https://auth-uat.swoovr.app/admin/`) is deliberately **hidden from the internet**. Through Cloudflare it answers **404 Not Found**.
- You reach it through an **SSH tunnel** (section 9.2). Firefox sends its traffic into the tunnel, and the server opens the page as if it were the one browsing. For that, Firefox must be set to use the tunnel as a **SOCKS proxy**.
- Through the tunnel, Firefox talks to the server **directly**, not through Cloudflare. The server proves who it is with a **Cloudflare Origin certificate**, which only Cloudflare normally trusts. Browsers don't trust it, so Firefox would show **"Warning: Potential Security Risk Ahead"**.
- To fix that safely, you import the certificate authority that issued it, **Cloudflare's Origin CA root certificate**, into **one separate Firefox profile** that you use only for this.

**Never** import this certificate into your normal Firefox profile or into the macOS Keychain, and **never** click "Accept the Risk and Continue" on a certificate warning for this site.

What the server presents (checked 22 November 2026):

| | |
| --- | --- |
| Server certificate | `CloudFlare Origin Certificate`, for `uat`, `api-uat`, `admin-uat`, `auth-uat`, `helpdesk-uat`, `venues-uat` `.swoovr.app`, valid until **9 November 2028** |
| Issued by | `CloudFlare Origin SSL Certificate Authority` (the **RSA** Origin CA root) |
| Root certificate valid until | **15 August 2029** |
| Root SHA-256 fingerprint | `D3:C7:E8:5C:91:70:7F:C0:A1:2A:BC:5D:88:26:67:47:AA:4F:A8:E7:B1:62:F6:33:FF:B3:C9:D9:89:94:76:20` |

Cloudflare also publishes an **ECC** Origin CA root. It is **not** the one UAT uses; don't download it.

#### 2.4.2 Install Firefox

If Firefox isn't in your Applications folder, install it, either from https://www.mozilla.org/firefox/new/ (open the downloaded `.dmg` and drag Firefox to Applications), or with:

```bash
brew install --cask firefox
```

Check:

```bash
/Applications/Firefox.app/Contents/MacOS/firefox --version
```

**You should see** `Mozilla Firefox` and a version number. **Quit Firefox completely** (⌘Q) before going on.

#### 2.4.3 Download Cloudflare's Origin CA root certificate

Where it comes from: Cloudflare's documentation page **Origin CA certificates** (https://developers.cloudflare.com/ssl/origin-configuration/origin-ca/), section *Cloudflare Origin CA root certificate*, link **Cloudflare Origin RSA PEM**. The direct address of that file is used below.

```bash
mkdir -p ~/swoovr-uat-private && chmod 700 ~/swoovr-uat-private
curl -fsSL https://developers.cloudflare.com/ssl/static/origin_ca_rsa_root.pem -o ~/swoovr-uat-private/origin_ca_rsa_root.pem
ls -l ~/swoovr-uat-private/origin_ca_rsa_root.pem
```

**You should see** one file of about 1–2 KB. **If you see** `curl: (22)` or `Could not resolve host`: check your internet connection and try again.

#### 2.4.4 Verify the certificate (three checks, all must pass)

**Check 1: the fingerprint.** This proves the file is exactly Cloudflare's root, unmodified:

```bash
openssl x509 -in ~/swoovr-uat-private/origin_ca_rsa_root.pem -noout -fingerprint -sha256
```

**It must print exactly:**
`sha256 Fingerprint=D3:C7:E8:5C:91:70:7F:C0:A1:2A:BC:5D:88:26:67:47:AA:4F:A8:E7:B1:62:F6:33:FF:B3:C9:D9:89:94:76:20`

Compare every pair. (On some Macs it prints `SHA256 Fingerprint=` in capitals; that's fine.)

**Check 2: what it is and how long it is valid:**

```bash
openssl x509 -in ~/swoovr-uat-private/origin_ca_rsa_root.pem -noout -subject -enddate
```

**You should see:**

```none
subject=C=US, O=CloudFlare, Inc., OU=CloudFlare Origin SSL Certificate Authority, L=San Francisco, ST=California
notAfter=Aug 15 17:00:00 2029 GMT
```

**Check 3: the UAT server's certificate really was issued by this root.** This copies the server's **public** certificate (never its private key) to your Mac and checks it against the root:

```bash
ssh -i ~/.ssh/swoovr-uat-deploy -o IdentitiesOnly=yes root@<server IPv4> 'openssl x509 -in /etc/caddy/certs/uat-origin.pem' > ~/swoovr-uat-private/uat-origin.pem
openssl verify -CAfile ~/swoovr-uat-private/origin_ca_rsa_root.pem ~/swoovr-uat-private/uat-origin.pem
```

**You should see** a line ending in `uat-origin.pem: OK`.

**If any check differs: stop.** Delete the file (`rm ~/swoovr-uat-private/origin_ca_rsa_root.pem`), don't import it, and ask for help. If only check 3 fails with `unable to get local issuer certificate`, the server's certificate was replaced by one from a different authority (for example the ECC root): ask for help before importing anything.

#### 2.4.5 Create the separate Firefox profile

A Firefox *profile* is a separate set of settings, bookmarks and certificates. Your normal browsing isn't affected.

With Firefox **quit**, create the profile from Terminal:

```bash
/Applications/Firefox.app/Contents/MacOS/firefox -CreateProfile swoovr-uat-admin
```

**You should see** `Success: created profile 'swoovr-uat-admin' at '…'`. **If you see** that it already exists, it was created before: carry on.

Open it (this is also how you open it **every time** later, including in section 9.3):

```bash
/Applications/Firefox.app/Contents/MacOS/firefox -P swoovr-uat-admin --no-remote &
```

A new, empty Firefox window opens. To be sure it is the right profile, type `about:profiles` in its address bar and press Enter: the `swoovr-uat-admin` box says **This is the profile in use and it cannot be deleted.**

*(Alternative without Terminal: in any Firefox window type `about:profiles` → **Create a New Profile** → **Next** → name `swoovr-uat-admin` → **Finish** → **Launch profile in new browser**.)*

Do **all** of 2.4.6 to 2.4.8 **in this window**.

#### 2.4.6 Send this profile's traffic through the tunnel (proxy settings)

1. ☰ menu (top right) → **Settings** → **General** → scroll to the bottom → **Network Settings** → **Settings…**.
2. Choose **Manual proxy configuration** and fill in **exactly**:

   | Field | Value |
   | --- | --- |
   | HTTP Proxy | *(empty)* |
   | Port (HTTP) | *(empty or 0)* |
   | Also use this proxy for HTTPS | **unticked** |
   | HTTPS Proxy | *(empty)* |
   | SOCKS Host | `127.0.0.1` |
   | Port (SOCKS) | `1080` |
   | SOCKS v4 / SOCKS v5 | **SOCKS v5** |
   | No proxy for | *(empty)* |
   | Proxy DNS when using SOCKS v5 | **ticked** (important: without it Firefox looks up `auth-uat.swoovr.app` on your Mac and reaches the public Cloudflare address, which answers 404) |
   | Enable DNS over HTTPS (at the bottom of this dialog, if shown) | **unticked** |

3. **OK**.
4. Still in Settings → **Privacy & Security** → scroll to **DNS over HTTPS** → choose **Off**. (It would otherwise send name lookups around the tunnel.)

#### 2.4.7 Import the root certificate into this profile only

1. ☰ → **Settings** → **Privacy & Security** → scroll to **Security** → **Certificates** → **View Certificates…**.
2. **Authorities** tab → **Import…**.
3. In the file picker press **⌘⇧G**, type `~/swoovr-uat-private/origin_ca_rsa_root.pem`, press Enter, then **Open**.
4. In the dialog *"You have been asked to trust a new Certificate Authority (CA)"*:
   - tick **Trust this CA to identify websites**;
   - leave **Trust this CA to identify email users** **unticked**;
   - optionally click **View** and check the SHA-256 fingerprint matches 2.4.4 check 1;
   - **OK**.
5. **Check the import:** in the Authorities list, scroll to **CloudFlare, Inc.** and expand it. **You should see** **CloudFlare Origin SSL Certificate Authority**. Select it → **View…** → the **SHA-256** fingerprint must match `D3:C7:E8:5C:…:94:76:20` from 2.4.4. Close the tab, then **OK**.

**If you see** *"This certificate is already installed as a certificate authority"*: it's already there. Do step 5 to check it.

#### 2.4.8 Test the whole path once

1. In a **separate Terminal window**, start the tunnel (the same command as section 9.2):

   ```bash
   ssh -i ~/.ssh/swoovr-uat-deploy -o IdentitiesOnly=yes -D 1080 -N root@<server IPv4>
   ```

   It prints nothing and keeps running.
2. In another Terminal window, check the tunnel itself:

   ```bash
   curl -sk --socks5-hostname 127.0.0.1:1080 -o /dev/null -w '%{http_code}\n' https://auth-uat.swoovr.app/admin/
   ```

   **You should see** `302`.
3. In the `swoovr-uat-admin` Firefox window, open https://auth-uat.swoovr.app/admin/. **You should see** the Keycloak sign-in page (*Sign in to your account*), with **no** certificate warning.
4. Click the **padlock** left of the address → **Connection secure** → **More information** → **View Certificate**. **You should see** *Issuer Name*: **CloudFlare Origin SSL Certificate Authority**.
5. Don't sign in now. Close the Firefox window, then press **Control-C** in the tunnel window.
6. Negative test (proves this profile only works through the tunnel): open the profile again (2.4.5 command) **without** the tunnel and go to the same address. **You should see** *"The proxy server is refusing connections"*. That's correct. Close the window.

**If the test fails:**

| What you see | Cause | Fix |
| --- | --- | --- |
| *The proxy server is refusing connections* (while the tunnel runs) | The tunnel isn't running, or isn't on port 1080 | Check the tunnel window is still running the command from step 1, with `-D 1080` |
| *Warning: Potential Security Risk Ahead* / `SEC_ERROR_UNKNOWN_ISSUER` | The root isn't imported in **this** profile, or *Trust this CA to identify websites* wasn't ticked | Check `about:profiles` says `swoovr-uat-admin` is in use; redo 2.4.7. **Never** click "Accept the Risk" |
| A plain **404 Not Found** page | Firefox went round the tunnel, through Cloudflare | Redo 2.4.6: *Proxy DNS when using SOCKS v5* ticked, DNS over HTTPS **Off**, *No proxy for* empty |
| `curl` in step 2 prints `000` | The tunnel isn't running | Redo step 1; see 9.2 for the messages the tunnel can print |
| Tunnel window prints `channel N: open failed: connect failed: Name or service not known` | Firefox asked for an address the server can't look up (its own background requests) | Harmless; ignore it |
| `Permission denied (publickey)` in the tunnel window | The command isn't using the deploy key | Use the command exactly as in step 1 |

#### 2.4.9 Rules for this profile

- Use the `swoovr-uat-admin` profile **only** for the Keycloak admin console. Don't browse other sites in it: while the tunnel runs, all of its traffic goes out from the UAT server.
- Keep `~/swoovr-uat-private/` private (`chmod 700`, already set). It holds only public certificates, but keep the folder for this purpose only.
- **When this must be redone:** on a new Mac; if the profile is deleted; or when the server's certificate is replaced (the current one expires on **9 November 2028**; redo 2.4.4 check 3 then). If Cloudflare ever publishes a new root (the current one expires on **15 August 2029**), repeat 2.4.3 to 2.4.7 with the fingerprint Cloudflare publishes for it, and update this page.
- To remove everything: `about:profiles` → `swoovr-uat-admin` → **Remove** → **Delete Files**, then `rm -r ~/swoovr-uat-private`.

*(If Tailscale has been set up for UAT, you can use it instead of the SSH tunnel: [UAT-TAILSCALE-SETUP.md §5](UAT-TAILSCALE-SETUP.md#5-keycloak-admin-through-tailscale). You still need this profile and the certificate, with the proxy set to **No proxy**. The SSH tunnel always works, so this page uses it.)*

---

## 3. Pre-flight checks (every refresh)

Open Terminal and go to the repository. **Every** command from here on is typed in this Terminal window, unless a step says "on the server".

```bash
cd ~/Developer/swoovr
git checkout main
git pull
```

**You should see** `Already up to date.` or a list of updated files. If you see `error: Your local changes … would be overwritten`, you have unsaved changes: stop and ask for help.

### 3.1 Tell people

Tell everyone using UAT (testers and staff) that UAT will be wiped, when, and that their UAT accounts, messages and photos will be gone. Nothing technical; just make sure no one is in the middle of a test.

### 3.2 Check GitHub sign-in

```bash
gh auth status
```

**You should see** `✓ Logged in to github.com`.

### 3.3 Check no other deployment is running

```bash
gh run list --workflow deploy-uat.yml --limit 3
gh run list --workflow uat-maintenance.yml --limit 3
```

**You should see** no line whose status is `in_progress` or `queued` (shown as `*` or `-` in the first column). If one is running, wait for it to finish (`gh run watch <its ID>`) before going on.

### 3.4 Check the current deployment mode

```bash
gh variable get DEPLOYMENT_MODE
```

**You should see** `upgrade`. If you see `refresh`, someone left it switched on: that is fine for now (you're about to refresh anyway), but note it.

### 3.5 Check every required GitHub setting is present

```bash
scripts/uat/setup-github-uat.sh status
```

**You should see** three lists (secrets, variables, the repository variable) and **no** line starting with `MISSING`. Also check that these two names appear under *secrets* (photos stay "being checked" forever without them):

```text
UAT_ARACHNID_USERNAME
UAT_ARACHNID_PASSWORD
```

If anything says `MISSING`, or the Arachnid names aren't listed, **stop** and set them first ([UAT operations SOP §7](UAT-OPERATIONS-SOP.md#7-settings-where-each-value-lives) and [§9 "Setting the Project Arachnid secrets"](UAT-OPERATIONS-SOP.md#setting-the-project-arachnid-secrets-photo-known-content-checks)).

### 3.6 Note the break-glass root admin's email address

```bash
gh variable get UAT_INITIAL_ADMIN_EMAIL --env uat
```

**You should see** an email address. Write it down. **This address receives the set-up email in section 10.** Make sure you can open that mailbox now. This address must **not** be a Microsoft/Entra (RePass Cloud tenant) account (D364). To change it:

```bash
gh variable set UAT_INITIAL_ADMIN_EMAIL --env uat --body 'new-address@example.com'
```

### 3.7 Check the server and the current deployment

```bash
ssh -i ~/.ssh/swoovr-uat-deploy -o IdentitiesOnly=yes root@<server IPv4>
```

(If you keep an SSH alias for this, such as `ssh swoovr-uat-root`, that works too.) You first see a `Linux uat-01 …` banner and `Last login: …`.

Now you are **on the server** (the prompt changes to `root@uat-01:~#`). Type:

```bash
cd /opt/swoovr/swoovr-uat
./bin/swoovr history
./bin/swoovr status
df -h /opt/swoovr
exit
```

**You should see, for each command:**

1. **`history`**: one line per deployment, oldest first: the time it finished, the tag, the first 12 characters of the commit, the mode (`upgrade`, `refresh` or `rollback`), and the result. The **last line** is what is running now and must say `success`. Write its tag down; section 4 Option A refreshes that version.

   ```text
   <finished, UTC>       v1.1.2     <commit>      upgrade  success
   ```

   A failed deployment shows `failed` and the stage it stopped at (for example `migrate`). That is fine on an **earlier** line. On the **last** line, stop and ask for help.

   **If you see** `SyntaxError: unexpected character after line continuation character` instead: the server still runs a release from before this command was fixed (v1.1.2 and earlier). It is harmless and the next deployment replaces it. Read the history directly instead:

   ```bash
   tail -3 state/releases.jsonl
   ```

   The last line is what is running now. It must contain `"result": "success"`; its `"tag"` is the version.

2. **`status`** prints three parts:

   - **A container table** (`NAME  IMAGE  COMMAND …`). Every `swoovr-uat-…` container is `Up`, except two that are meant to be `Exited (0)`: `volume-permissions` and `zammad-init` (they run once and finish).
   - **`== containers`**: one line per service. Everything `running` except those same two `exited`. `healthy` or `-` are both fine (`-` means the service has no health check, for example `caddy`, `zammad-nginx`). Nothing may say `unhealthy` or `restarting`. A `restarts=` count of 1 on the Zammad services is normal after a deployment; if a count goes up when you run `./bin/swoovr status` again a minute later, stop and ask for help.
   - **`== application readiness through Caddy`**: nine `PASS` lines (API live, ready, version, attachment scanning, web, admin, helpdesk, and both realm discoveries), then `SWOOVR_RESULT smoke=passed`. The `api version` line must match the tag from `history` (`PASS api version 1.1.2` for `v1.1.2`). Any `FAIL` line: stop and ask for help.

3. **`df`**: the `Avail` column shows at least **15G** (the deployment refuses with less), for example:

   ```text
   Filesystem      Size  Used Avail Use% Mounted on
   /dev/vda2       601G   35G  541G   6% /
   ```

`exit` (you see `logout` and `Connection to … closed.`) takes you back to your Mac.

---

## 4. Choose the version to refresh to

**Important: you cannot start a refresh with `./scripts/update-release.sh`.** That script always sets `DEPLOYMENT_MODE` back to `upgrade` just before it pushes the new tag (D383), so a release made with it is always an upgrade. A refresh is started by **re-running an existing Deploy UAT run** while `DEPLOYMENT_MODE=refresh` (section 6). A re-run reads `DEPLOYMENT_MODE` again when it starts.

Pick **one**:

### Option A (usual): refresh the version that is running now

Nothing to do here. Go to section 5.

### Option B: refresh to a new version

Use this when there is new code on `main` that UAT should run after the refresh.

1. Find the next version number:

   ```bash
   git fetch --tags
   git tag --sort=-v:refname | head -3
   python3 scripts/release_version.py next
   ```

   The second command suggests the next version, for example `1.1.3`.

2. Release it as a normal **upgrade** first. The script does everything (version commit on `main`, tag, deployment) and watches it:

   ```bash
   ./scripts/update-release.sh 1.1.3
   ```

   Replace `1.1.3` with the suggested version. **You should see** one line per step ending in `PASS`, then `Status: SUCCESS` and `Release v1.1.3 completed successfully.` If it fails, read [VERSIONING.md "Releasing"](VERSIONING.md#releasing) and fix it before going on.

3. Go to section 5. The run you re-run in section 6 is the one this release just made.

---

## 5. UNLOCK: switch the deployment mode to refresh

This is the "unlock" that allows the wipe. While it is set, **any** Deploy UAT run that starts (including a re-run) deletes all UAT data.

```bash
gh variable set DEPLOYMENT_MODE --body refresh
gh variable get DEPLOYMENT_MODE
```

**You should see** `refresh`.

---

## 6. Run the refresh

1. Find the newest **successful** Deploy UAT run, and its tag:

   ```bash
   RUN=$(gh run list --workflow deploy-uat.yml --status success --limit 1 --json databaseId -q '.[0].databaseId')
   TAG=$(gh run list --workflow deploy-uat.yml --status success --limit 1 --json headBranch -q '.[0].headBranch')
   echo "run=$RUN tag=$TAG"
   ```

   **You should see** something like `run=19876543210 tag=v1.1.2`. The tag should be the version you chose in section 4. Write it down: this page calls it `vX.Y.Z` from now on.

2. Re-run **all** jobs of that run (not only failed ones; the first job is the one that reads the mode):

   ```bash
   gh run rerun "$RUN"
   ```

   **If `gh` refuses to re-run it** (GitHub only re-runs runs from the last 30 days): set the mode back (`gh variable set DEPLOYMENT_MODE --body upgrade`), do section 4 **Option B** to make a fresh run, then come back to section 5.

3. Watch it:

   ```bash
   gh run watch "$RUN" --exit-status
   ```

   It shows three stages: **Validate tag, branch and mode**, then **Image …** (six of them; they reuse the existing images, so they're quick), then **Deploy to UAT (refresh)**. The deploy stage takes the longest: Keycloak, the helpdesk and photo screening start from nothing.

4. **Check the run really is a refresh.** In the watch output, or on the website, the deploy job's name must read **Deploy to UAT (refresh)**, and the run shows the warning *"DEPLOYMENT_MODE=refresh: this run deletes and recreates all UAT application data (after a verified backup), and empties the R2 photo bucket."* If it reads **(upgrade)**, the mode was not switched in time: it's harmless (nothing was wiped); go back to section 5.

5. Wait until `gh run watch` ends. **You should see** every job with a ✓ and the command exits without an error.

You can also follow it in the browser: https://github.com/repasscloud/swoovr/actions/workflows/deploy-uat.yml → the newest run.

**If the run fails**, do **not** switch the mode back yet. Go to [section 15](#15-if-something-goes-wrong).

---

## 7. RELOCK: switch the deployment mode back to upgrade

**Do this as soon as the run in section 6 succeeded.** If you forget, the next re-run deletes everything again.

```bash
gh variable set DEPLOYMENT_MODE --body upgrade
gh variable get DEPLOYMENT_MODE
```

**You should see** `upgrade`.

---

## 8. Read and check the result

1. Open the run's summary in the browser:

   ```bash
   gh run view "$RUN" --web
   ```

   Scroll to **UAT deployment: vX.Y.Z** at the bottom of the run page.

2. **You should see** in the table:

   | Line | Expected value | Meaning |
   | --- | --- | --- |
   | Mode | `refresh` | It was a refresh |
   | backup | a folder name ending in `-pre-refresh-vX.Y.Z` | The backup taken before the wipe |
   | root-account | `created-invite-sent` | The break-glass account was recreated and its set-up email was sent |
   | initial-admin | `invite-not-completed` | Expected: you finish it in section 10 |
   | bootstrap-admin | `kept-no-personal-admin` | Expected: you fix it in section 9 |
   | email-lifespans | each realm `ok` or `corrected` | Email link lifetimes are set |
   | smoke | `passed` | Every internal check passed |
   | deployed | `vX.Y.Z` | Done |

   GitHub shows `***` wherever a secret would appear. Because the SSH user is `root`, the word "root" itself appears as `***` (for example `***-account=created-invite-sent`). That is only cosmetic.

   *The summary may also say "First administrator pending: sign in once … with the Entra account". That wording is out of date: the root admin is the **break-glass** account, not Entra. Follow section 10.*

   **If** `root-account` ends in `-email-failed`: the set-up email didn't go out. Carry on; section 10 step 2 tells you how to resend it.

3. Open each address in your normal browser. **Each should load:**
   - https://uat.swoovr.app (member app; the home page)
   - https://admin-uat.swoovr.app (Admin; it sends you to the sign-in page)
   - https://helpdesk-uat.swoovr.app (helpdesk; it shows **Swoovr staff sign-in**)
   - https://api-uat.swoovr.app/version (shows `"version":"X.Y.Z"`, the version you deployed, without the `v`)
   - https://api-uat.swoovr.app/health/ready (shows `"status":"ready"`)

---

## 9. Replace the temporary Keycloak administrator

**Why:** Keycloak (the sign-in system) started empty. It contains a temporary administrator named `swoovr-bootstrap`, with no second factor. You create your own personal Keycloak administrator with an authenticator, and the system then deletes the temporary one by itself. Without this step the deployments keep the weak temporary account.

### 9.1 Find the temporary password (on the server)

```bash
ssh -i ~/.ssh/swoovr-uat-deploy -o IdentitiesOnly=yes root@<server IPv4>
cd /opt/swoovr/swoovr-uat
less env/keycloak.env
```

Find the line `KC_BOOTSTRAP_ADMIN_PASSWORD=…`. Copy the value after `=` (select it and press ⌘C). Press `q` to close `less`, then:

```bash
exit
```

Never change this value in the file: Keycloak reads it only when its database is empty.

### 9.2 UNLOCK: open the private tunnel to the Keycloak console

In Terminal:

```bash
# without config
ssh -i ~/.ssh/swoovr-uat-deploy -o IdentitiesOnly=yes -D 1080 -N root@<server IPv4>

# with config
ssh swoovr-uat-root -D 1080 -N
```

The command **prints nothing and keeps running**: that is the tunnel. Leave this Terminal window open. Open a **new** Terminal window (⌘N) for anything else.

**Messages you may see in the tunnel window:**

- `channel 3: open failed: connect failed: Name or service not known` (the number varies): **harmless; ignore it and carry on.** Firefox sometimes asks, through the tunnel, for an address the server can't look up (its own background requests). The tunnel still works. Only if https://auth-uat.swoovr.app/admin/ itself won't load in step 9.3 is something wrong (see 9.3 step 2).
- `Permission denied (publickey)`: the `-i ~/.ssh/swoovr-uat-deploy -o IdentitiesOnly=yes` part is missing or the key file isn't there. Use the command exactly as written above. The deploy key is the only key the server accepts for `root`; there is no other one to fall back to.

**To check the tunnel from Terminal** (in a second window, while the tunnel runs):

```bash
curl -sk --socks5-hostname 127.0.0.1:1080 -o /dev/null -w '%{http_code}\n' https://auth-uat.swoovr.app/admin/
```

**You should see** `302` (the console redirecting to its sign-in page). Without the tunnel the same address gives `404` from the internet, by design.

### 9.3 Create your personal Keycloak administrator

1. Open Firefox with the `swoovr-uat-admin` profile (set up in section 2.4; if you haven't done 2.4, do it now):

   ```bash
   /Applications/Firefox.app/Contents/MacOS/firefox -P swoovr-uat-admin --no-remote &
   ```

2. Go to https://auth-uat.swoovr.app/admin/ in that window.
   **If** it says the connection failed: the tunnel (9.2) isn't running. **If** it shows a certificate warning: the certificate isn't imported in this profile; do section 2.4.7, and never click "Accept the Risk". **If** it shows a plain 404 page: the proxy settings are wrong; do section 2.4.6. More fixes: the table in 2.4.8.
3. Sign in: **Username** `swoovr-bootstrap`, **Password** the value from 9.1.
4. Top left, the realm drop-down must say **master** (Keycloak). If not, choose **master**.
5. **Users** (left menu) → **Add user** (or **Create new user**):
   - **Required user actions:** choose **Configure OTP**.
   - **Email verified:** On.
   - **Username:** your own username (for example your first name). Not `swoovr-bootstrap`.
   - **Email:** your email address.
   - **First name**, **Last name:** yours.
   - **Create**.
6. On the new user's page → **Credentials** tab → **Set password**:
   - a new long password (at least 16 characters; save it in your password manager);
   - **Temporary:** Off.
   - **Save** → **Save password**.
7. **Role mapping** tab → **Assign role** → change the filter to **Filter by realm roles** → tick **admin** → **Assign**.
8. Top right, click `swoovr-bootstrap` → **Sign out**.
9. Sign in again at https://auth-uat.swoovr.app/admin/ as **your new username** and password. Keycloak shows a QR code: scan it with your authenticator app, type the 6-digit code, give the device a name (for example "phone"), **Submit**. You are now in the console as yourself.
10. Sign out (top right → **Sign out**). Close the Firefox window.

### 9.4 RELOCK: close the tunnel

Go back to the Terminal window running the tunnel and press **Control-C**. The command ends and the tunnel is closed.

The temporary `swoovr-bootstrap` account is **deleted automatically** by the next deployment or by `admin-finalize` (section 10 step 5). The summary then shows `bootstrap-admin=deleted`.

---

## 10. Set up the break-glass root admin

**Why:** Swoovr Admin has exactly **one root admin**. It is a dedicated account (the address from section 3.6), deliberately **not** a Microsoft account, so it still works if Microsoft is down (D364). The refresh deleted it; the deployment created it again and emailed it a set-up link.

1. **Open the set-up email** in the mailbox of the address from section 3.6.
   - From: **Swoovr**, `support@swoovr.com`. Check **Junk** / **Other** too.
   - The link is valid for **12 hours**.

2. **If the email isn't there** (or the link expired), resend it. Use the version tag from section 6 (`vX.Y.Z`):

   ```bash
   gh workflow run uat-maintenance.yml --ref vX.Y.Z -f action=admin-invite
   sleep 5
   gh run watch $(gh run list --workflow uat-maintenance.yml --limit 1 --json databaseId -q '.[0].databaseId')
   ```

   The run's summary should show `root-account=invite-sent`. **The `--ref` must be the tag, never `main`.** From `main` it fails at once with *"The deployment was rejected or didn't satisfy other protection rules"*; nothing ran, so start it again with the tag. If it still doesn't arrive, follow [UAT operations SOP §6 "An email didn't arrive"](UAT-OPERATIONS-SOP.md#an-email-didnt-arrive-the-procedure), which includes:

   ```bash
   scripts/uat/mail-test.sh <server IPv4> <the address from 3.6>
   ```

3. **Complete the email's steps:**
   - click the link and verify the address;
   - choose a password of at least 12 characters (save it in your password manager);
   - scan the QR code with your authenticator app and enter the 6-digit code.
   **Keep a backup of this authenticator offline** (for example the recovery codes printed and stored safely): this account exists for emergencies.

4. **Sign in once to Admin:** open https://admin-uat.swoovr.app → use the **email and password form** (**not** "Sign in with Microsoft") → enter the 6-digit code. Admin then says something like **"Your role can't do this"**: that is **expected** at this point.

5. **Give the account the root role:**

   ```bash
   gh workflow run uat-maintenance.yml --ref vX.Y.Z -f action=admin-finalize
   sleep 5
   gh run watch $(gh run list --workflow uat-maintenance.yml --limit 1 --json databaseId -q '.[0].databaseId')
   ```

   Open the run's summary (`gh run view --web` on the run, or Actions → **UAT maintenance** → the newest run). **You should see** `initial-admin=granted`.

6. In Admin, choose **Sign in again** (or sign out and in again with the same email, password and code). The full Admin workspace appears.

**If the result is not `granted`:**

| Result | Meaning | Do |
| --- | --- | --- |
| `invite-not-completed` | The email's steps aren't finished | Finish step 3; resend with step 2 |
| `unverified-email` / `missing-password-or-authenticator` | Part of the set-up is missing | Same as above |
| `pending-first-sign-in` | Set-up done, but never signed in to Admin | Step 4, then step 5 again |
| `granted` / `already-granted` | Done | Nothing |
| `linked-to-identity-provider` | Someone linked this account to Microsoft; it must stay local | Stop; ask for help. Nothing was changed |
| `held-by-another-account` | Another account already holds root | Stop; ask for help. Nothing was changed |
| `pending-invite` | No account exists (address not set?) | Check section 3.6, then re-run step 2 |

---

## 11. Set up the helpdesk (Zammad) mailbox again

**Why:** the refresh deleted Zammad's data, including its connection to the Microsoft 365 support mailbox and its notification sender. Deployments never set this up themselves (owner, 12 November 2026), so you do it by hand after every refresh.

Staff normally sign in to Zammad only through **Swoovr staff sign-in** and can't change mail settings. To change them you sign in as Zammad's built-in administrator, `swoovr-provisioner`. Its password form is normally switched **off**; you switch it on for this step and off again at the end.

1. Connect to the server:

   ```bash
   ssh -i ~/.ssh/swoovr-uat-deploy -o IdentitiesOnly=yes root@<server IPv4>
   cd /opt/swoovr/swoovr-uat
   ```

2. **UNLOCK** Zammad's password sign-in:

   ```bash
   ./bin/swoovr zammad-admin-login on
   ```

   **You should see** `Zammad password sign-in is ON. …`

3. Find the administrator password: run `less env/zammad-provision.env`, copy the value after `SWOOVR_ZAMMAD_BOOTSTRAP_PASSWORD=`, then press `q`. Don't print it with `cat` or `echo`.

4. Open a **private** browser window (Safari: ⌘⇧N) at https://helpdesk-uat.swoovr.app. Sign in with the password form: **username** `swoovr-provisioner`, **password** from step 3.

5. Connect the support mailbox: **Settings** (gear, bottom left) → **Channels** → **Microsoft 365** (if shown; issue #319) **or** **Email** → **Add account** → sign in with the Microsoft 365 support mailbox account (section 1) and accept the permissions. When asked for the **destination group**, choose **General Support**.

6. Set the notification sender: **Settings** → **Channels** → **Email** → **Settings** tab → **Notification Sender**: `Swoovr Helpdesk <support mailbox address>` → **Submit**.

7. Sign out of Zammad (avatar, bottom left → **Sign out**). Close the private window.

8. **RELOCK** — back in the server Terminal:

   ```bash
   ./bin/swoovr zammad-admin-login off
   exit
   ```

   **You should see** `Zammad password sign-in is OFF (staff sign in with Swoovr staff sign-in only).`

   If you forget step 8, the next deployment switches it off anyway. Don't rely on that: do step 8.

9. **Check:** send an email from your own mailbox to the support mailbox address. Within a few minutes a new ticket should appear in the helpdesk (**General Support**) once staff can sign in (section 12).

---

## 12. Staff sign in again

The refresh deleted every staff account in Keycloak and in Swoovr. **Nothing in Microsoft Entra changed**, so staff who are in the right Entra groups can sign straight back in. Tell each staff member to do this once:

1. Open https://admin-uat.swoovr.app → **Sign in with Microsoft** → their RePass Cloud Microsoft account.
2. Keycloak asks them to **verify their email**: open the email (from `support@swoovr.com`) and click the link. The link is valid for 24 hours.
3. Keycloak shows a QR code: scan it with an authenticator app and enter the 6-digit code.
4. **Sign out, then sign in once more.** Only the second sign-in counts as multi-factor; some pages need it.
5. Helpdesk: https://helpdesk-uat.swoovr.app → **Swoovr staff sign-in**. This works about a minute after their first Admin sign-in, and only if they have a staff role. Without a role it shows **403**.

**Roles:**

- Roles that come from Entra (groups **Swoovr UAT Support**, **Swoovr UAT Trust & Safety**, **Swoovr UAT Admin**, **Swoovr UAT Legal**) come back by themselves at sign-in. Every staff member must also be in the group **Swoovr UAT** (that's what lets them sign in at all). Entra admin centre: https://entra.microsoft.com → **Groups** → **All groups**. Detail: [UAT operations SOP §5](UAT-OPERATIONS-SOP.md#5-staff-access-and-roles-microsoft-entra).
- Roles that were granted **inside Swoovr Admin** (Staff → a person → Internal roles), by invitation or on the command line are **gone**. Give them again: once the person has signed in (step 1), the root admin opens https://admin-uat.swoovr.app/staff → the person → **Internal roles**. Or use an invitation at https://admin-uat.swoovr.app/staff/new.

---

## 13. Registration and beta invitations

After a refresh, registration is back at its starting setting: **Beta registration only** (people need a beta enrolment to create an account; public sign-up is off). All beta enrolments are gone.

1. **Check the setting:** sign in to Admin as the root admin (section 10) → https://admin-uat.swoovr.app/registration. **You should see** *Registration: Beta registration only*.
2. **Only if you want a different setting:** tick or untick **Beta registration** and **Public registration**, write a **Reason**, tick **Confirm this change**, **Save availability**. Only the root admin can change this. (Members see "Swoovr is invite-only right now" when only beta is on, and "Registration is closed" when both are off.)
3. **Re-add the testers:** https://admin-uat.swoovr.app/beta → **Email to pre-authorise** → the tester's email → **Add enrolment**. Repeat for every tester. Each address gets an invitation email (from `support@swoovr.com`) with a link to https://uat.swoovr.app/ to create their account.
4. **Testers create new accounts** at https://uat.swoovr.app. Their old UAT accounts and photos are gone. Identity checks (Didit) start again from the beginning.
5. **SMS codes** (phone verification) don't go to real phones in UAT yet: they are caught on the server by Mailpit. To read one:

   ```bash
   ssh -i ~/.ssh/swoovr-uat-deploy -o IdentitiesOnly=yes -L 8025:127.0.0.1:8025 -N root@<server IPv4>
   ```

   Leave it running and open http://127.0.0.1:8025 in your browser. Press **Control-C** in that Terminal when finished, to close the tunnel.

---

## 14. Final checks

Do all of these. Write down anything that doesn't match.

1. **Server status, through GitHub:**

   ```bash
   gh workflow run uat-maintenance.yml --ref vX.Y.Z -f action=status
   sleep 5
   gh run watch $(gh run list --workflow uat-maintenance.yml --limit 1 --json databaseId -q '.[0].databaseId')
   ```

   **You should see** a successful run whose log shows `PASS` lines and `smoke=passed`.

2. **History:**

   ```bash
   gh workflow run uat-maintenance.yml --ref vX.Y.Z -f action=history
   sleep 5
   gh run watch $(gh run list --workflow uat-maintenance.yml --limit 1 --json databaseId -q '.[0].databaseId')
   ```

   **You should see** the last line: `vX.Y.Z … refresh  success`.
3. **Temporary Keycloak admin removed:** the `admin-finalize` summary in section 10 step 5 should have shown `bootstrap-admin=deleted`. If it showed `kept-no-personal-admin`, section 9 wasn't completed: do it, then run `admin-finalize` again (section 10 step 5).

4. **Root admin:** signed in to https://admin-uat.swoovr.app as the break-glass account, the full workspace is visible.

5. **Photos:** Admin → **Overview** → the **Image processing** panel says **Working normally**, and both services say **Answering**. Photo screening loads its models on first start, which can take up to 10 minutes. If it says otherwise, see [UAT operations SOP §9](UAT-OPERATIONS-SOP.md#9-photos-and-image-processing).

6. **Mail:** a test email to the support mailbox became a helpdesk ticket (section 11 step 9).

7. **Deployment mode is locked:**

   ```bash
   gh variable get DEPLOYMENT_MODE
   ```

   **It must print** `upgrade`. If it prints `refresh`, do section 7 now.

8. **Tunnels closed:** no Terminal window is still running an `ssh -D` or `ssh -L` command. Press Control-C in any that are.

9. **Zammad password login is off:** https://helpdesk-uat.swoovr.app shows only **Swoovr staff sign-in**, with no username/password form.

10. Tell everyone UAT is ready again.

---

## 15. If something goes wrong

**Leave `DEPLOYMENT_MODE` at `refresh` until the refresh has succeeded**, unless a step below says otherwise. A failed refresh is fixed by running it again in refresh mode.

**If you used `./scripts/refresh-uat.sh`**, the mode is already back at `upgrade`. `gh run rerun "$RUN" --failed` still re-runs as a refresh: the first job read the mode once and is not re-run. To re-run **every** job, use `./scripts/refresh-uat.sh --existing vX.Y.Z` (it unlocks again, re-runs and relocks).

| What you see | What it means | What to do |
| --- | --- | --- |
| The run failed at **Validate tag, branch and mode** with *Deployment mode* | `DEPLOYMENT_MODE` has a typo | `gh variable set DEPLOYMENT_MODE --body refresh`, then `gh run rerun "$RUN"` |
| Failed at **Images are public** | A GitHub package is private | For the package named in the error: https://github.com/orgs/repasscloud/packages → `swoovr-<name>` → **Package settings** → **Change visibility** → **Public**. Then `gh run rerun "$RUN" --failed` |
| Failed at **Server preflight** with *host key verification failed* | The server's identity doesn't match | **Stop.** Ask for help. Never turn off the check |
| Failed with `ssh: connection timed out` | Something blocks port 22 | Check the server is running in the Vultr console; ask for help |
| Failed with `another deployment is running` | Another run or maintenance job holds the lock | Wait for it, then `gh run rerun "$RUN" --failed` |
| Failed with `FAIL …: … is not filled` | A setting is missing | Set it (section 3.5), then `gh run rerun "$RUN" --failed` |
| Failed at **pre-refresh-backup** | The backup failed. **Nothing was deleted** | Usually disk space (needs 5 GB free in `/opt/swoovr/backups`). Ask for help; then `gh run rerun "$RUN" --failed` |
| Failed at **r2-empty**, **database**, **migrate**, **identity**, **keycloak-bootstrap**, **helpdesk** or **applications** | The old data is already gone; the rebuild stopped part-way. The backup is safe on the server in `/opt/swoovr/backups/*-pre-refresh-*` | Keep the mode at `refresh`. Look at the log (`gh run view "$RUN" --log-failed`). For `keycloak-bootstrap`, on the server: `cd /opt/swoovr/swoovr-uat && ./bin/swoovr logs keycloak`. After fixing the cause, `gh run rerun "$RUN"` (a refresh always starts again from a clean state) |
| Failed at **Public checks through Cloudflare** | The server is up, but something isn't reachable from the internet | Read which check failed in the log; check Cloudflare. Then `gh run rerun "$RUN" --failed` |
| A release you didn't mean to be a refresh wiped UAT | `DEPLOYMENT_MODE` was left at `refresh` | Do sections 7–14 now |

Where to look:

- The run's log: `gh run view "$RUN" --log-failed`, or the run page in the browser.
- On the server: `./bin/swoovr history` (the failed stage is shown), `./bin/swoovr status`, `./bin/swoovr logs <service>` (for example `api`, `keycloak`, `zammad-railsserver`, `image-moderation`).
- Full recovery table: [server runbook "Recovery"](../../deploy/vultr-uat/README.md#recovery).

---

## 16. Every lock and unlock in this procedure

Everything that this procedure switches on must be switched off again. Tick each one at the end.

| Unlock (switch on) | Section | Relock (switch off) | Section | If forgotten |
| --- | --- | --- | --- | --- |
| `gh variable set DEPLOYMENT_MODE --body refresh` | 5 | `gh variable set DEPLOYMENT_MODE --body upgrade` | 7 | The next re-run of a Deploy UAT run wipes UAT again. (`update-release.sh` sets `upgrade` itself, so a new release is safe.) |
| SSH SOCKS tunnel `ssh … -D 1080 -N root@<server IPv4>` | 9.2 | Control-C in that Terminal | 9.4 | The Keycloak admin console stays reachable from your Mac only; close it anyway |
| Temporary Keycloak admin `swoovr-bootstrap` (created by the refresh) | 9 | Personal admin with authenticator created (9.3), then `admin-finalize` or the next deployment deletes it | 10.5 | A weak admin account without a second factor stays in Keycloak |
| `./bin/swoovr zammad-admin-login on` | 11.2 | `./bin/swoovr zammad-admin-login off` | 11.8 | The helpdesk shows a password form until the next deployment |
| Mailpit tunnel `ssh … -L 8025:127.0.0.1:8025 -N …` | 13.5 | Control-C in that Terminal | 13.5 | Nothing serious; close it anyway |
| *(Tailscale only)* hosts entry `… auth-uat.swoovr.app # swoovr-uat-admin` | [Tailscale §5](UAT-TAILSCALE-SETUP.md#5-keycloak-admin-through-tailscale) | `sudo sed -i '' '/# swoovr-uat-admin$/d' /etc/hosts` | same | Every app on your Mac reaches `auth-uat` over Tailscale, bypassing Cloudflare |

The registration setting (section 13) isn't a lock to undo: it starts at "Beta registration only" and changes only if you change it.

---

## 17. One-page checklist

Print this and tick each line.

- [ ] 3.1 Told everyone UAT will be wiped
- [ ] 3.2 `gh auth status` signed in
- [ ] 3.3 No Deploy UAT or maintenance run in progress
- [ ] 3.4 `DEPLOYMENT_MODE` checked
- [ ] 3.5 `scripts/uat/setup-github-uat.sh status`: nothing `MISSING`, Arachnid secrets present
- [ ] 3.6 Root admin address noted, mailbox open: ______________________
- [ ] 3.7 Server `history` / `status` OK, at least 15 GB free
- [ ] 4 Version chosen (A: current / B: new release done): vX.Y.Z = ________
- [ ] 5 **UNLOCK** `DEPLOYMENT_MODE=refresh`
- [ ] 6 `gh run rerun` on the newest successful run; job reads **Deploy to UAT (refresh)**; run succeeded
- [ ] 7 **RELOCK** `DEPLOYMENT_MODE=upgrade`
- [ ] 8 Summary: Mode `refresh`, backup `…-pre-refresh-…`, smoke `passed`; five addresses load
- [ ] 9.1 Temporary Keycloak password copied
- [ ] 9.2 **UNLOCK** SOCKS tunnel open
- [ ] 9.3 Personal Keycloak admin created with password, `admin` role and authenticator
- [ ] 9.4 **RELOCK** tunnel closed
- [ ] 10.3 Root admin email completed (password + authenticator; backup kept offline)
- [ ] 10.4 Root admin signed in to Admin once
- [ ] 10.5 `admin-finalize` → `initial-admin=granted`
- [ ] 11.2 **UNLOCK** `zammad-admin-login on`
- [ ] 11.5 Microsoft 365 mailbox connected (destination group General Support)
- [ ] 11.6 Notification sender set
- [ ] 11.8 **RELOCK** `zammad-admin-login off`
- [ ] 12 Staff told to sign in again; Admin-granted roles given again
- [ ] 13 Registration checked; beta testers re-added
- [ ] 14.1–14.9 Final checks done, `bootstrap-admin=deleted`, `DEPLOYMENT_MODE=upgrade`
- [ ] 14.10 Told everyone UAT is ready

---

**Known gaps in this page** (fill in when known):

- The Microsoft 365 support mailbox address Zammad uses isn't recorded in the repository (sections 1 and 11).
- The Deploy UAT summary's "First administrator pending … with the Entra account" text is out of date (D364 made the root admin a non-Entra account); the steps here are correct.
- The refresh has not been timed; the deploy stage takes longest because Keycloak, Zammad and photo screening start from empty.
