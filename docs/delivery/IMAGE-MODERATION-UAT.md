# Photo screening and moderation on the single UAT server

Decision: [D384](DECISIONS.md#d384--photo-screening-on-the-single-uat-server-behind-the-api-20-november-2026). This replaces the two Vultr processor VMs, their Caddy gateway and per-VM ClamAV, and the three Cloudflare image Workers ([VULTR-IMAGE-PIPELINE.md](VULTR-IMAGE-PIPELINE.md), now historical). Production keeps its own deployment design (OPEN). This page is the UAT runbook.

## Why photos stayed "being checked"

**Evidence from code and configuration, 20 November 2026. Not yet confirmed on the host: run step 1 below.**

Every client (web, admin, mobile) uploads through `POST /api/me/photos`. The API stores the photo privately in R2 (`quarantine/`), and its background worker asks the screening service for a verdict:

1. It calls `GET {Media__Screening__BaseUrl}/ready`.
2. It then calls `POST {Media__Screening__BaseUrl}/v1/scan`.

On UAT the only screening address was the Caddy gateway in front of the two processor VMs. That gateway forwarded only `/v1/process` (the Cloudflare Worker contract), `/health` and `/ready`. Every other path got `404`. So:

- `/ready` answered 200 and `/v1/scan` answered 404.
- Each attempt left the photo **Pending** (`last_screen_error = http-404`).
- The photo was retried at 1, 5 and 15 minutes, then hourly, forever.
- Nothing moved it to approved, review or refused, and nothing alerted anyone.

The Cloudflare Worker pipeline never helped: no client used it, its R2 notification and secrets were never set up, and by design it could not approve a photo.

Other defects that would also have left photos waiting, all fixed in this change:

- **No screening key required.** The API accepted a missing key. A rotated processor key gives 401 on every scan.
- **Sweep starvation.** The worker took the 20 earliest due photos. Pending photos of inactive accounts never moved their due time, so 20 of them blocked every other retry.
- **No visibility.** There was no stall report, no health endpoint and no staff view of why photos wait.
- **No malware scan on photos.** Only support attachments were scanned.

## How it works now

```text
Member app / web / admin
        │  POST /api/me/photos  (multipart, ≤ 10 MB; web through its BFF)
        ▼
Swoovr API ──① ClamAV INSTREAM on the ORIGINAL bytes, before anything decodes them
        │      shared clamav:3310 (ticketing network). Infected or unscannable → refused, nothing stored.
        │      Scanner unavailable → refused "try again in a few minutes" (never stored unscanned).
        │──② validate, strip metadata, re-encode to JPEG (PhotoProcessor)
        │──③ private R2  quarantine/{id}  +  media_items row: Pending
        ▼
MediaScreeningWorker (inside the API; durable: database due times, survives restarts)
        │──④ POST http://image-moderation:8080/v1/scan  (internal screening network, Bearer key)
        │      NudeNet · Freepik NSFW · Project Arachnid Shield (only a PDQ hash leaves the server)
        ▼
MediaScreeningPolicy (unchanged thresholds)
        ├─ Passed     → R2 photos/{id}; shown through short-lived signed links
        ├─ Uncertain  → Admin photo review queue (/media); stays private
        ├─ Blocked    → sealed to evidence/{id}; Serious Safety Incident + legal hold; no staff view
        └─ Pending    → retried (1, 5, 15 min, then hourly) with the reason recorded
                         after 12 attempts or 24 h: "stalled" → audited + error log + Overview, still private
```

Containers on the UAT server, all in `deploy/vultr-uat/compose.yaml`:

| Service | Networks | Notes |
| --- | --- | --- |
| `api` | edge, data, ticketing, mail, **screening** | Scans with ClamAV, calls image-moderation, decides |
| `image-moderation` | **screening** (internal), egress | Built from `services/image-moderation` (`swoovr-screening` image). No published port, no public hostname. 4 GB / 2 CPU. Runs `ENABLE_PROCESSING=false` |
| `clamav` | ticketing (internal), egress | **The one ClamAV.** Scans support attachments and photo uploads. freshclam every 2 hours (D379). Limits as `compose.local.yaml` |

Nothing image-related runs in Cloudflare any more except R2 storage (the existing `Media__R2__Bucket`).

### Sizing

The measurements below come from the old processor VMs (4 vCPU / 8 GB):

- **Processor:** about 0.9 GB resident after warm-up, and about 4.5–5 seconds per photo with one inference thread.
- **ClamAV:** about 1.5 GB per loaded signature copy; reloads pause scanning rather than doubling memory.

On the 8 vCPU / 32 GB UAT VM, the processor's 4 GB / 2 CPU limit and ClamAV's 4 GB limit leave room for Zammad, Elasticsearch, Keycloak and PostgreSQL. Measure with `docker stats` during the first validation (step 4) and record the result here.

### Configuration

| Value | Where | Notes |
| --- | --- | --- |
| `Media__Screening__BaseUrl=http://image-moderation:8080` | `compose.yaml` (api, api-command) | Overrides any old value still in `env/api.env` |
| `Media__Screening__ApiKey` | `env/screening.env` (generated on the host) | Same generated value as `API_KEY` in `env/image-moderation.env`. Hosted startup refuses a key under 32 characters |
| `ARACHNID_API_USERNAME`, `ARACHNID_API_PASSWORD` | `env/image-moderation.env`, from GitHub secrets `UAT_ARACHNID_USERNAME` / `UAT_ARACHNID_PASSWORD` | Required. The deploy stops at the env check until both are filled |
| `Media__Screening__RequireKnownContentCheck=true` | `compose.yaml` | Unchanged: a photo is never approved without its known-content check |

## Owner steps for the first deployment

1. **Confirm the cause (read only).** In the UAT maintenance workflow, or over SSH on the host, run:
   - `docker compose -p swoovr-uat exec api printenv Media__Screening__BaseUrl`. Before this release it shows the old gateway address.
   - `docker compose -p swoovr-uat exec swoovr-postgres sh -c 'psql -U "$POSTGRES_USER" -d swoovr -c "SELECT status, last_screen_error, count(*) FROM media_items WHERE status = '"'"'Pending'"'"' GROUP BY 1, 2;"'`. Expect mostly `http-404`. Counts only; no photo or member data is printed.
2. **Rotate the leaked credentials.** The processor key and the Shield username and password were kept in plaintext notes in the old repositories' ignored `.local/` folders. They were never committed. Ask Project Arachnid for a new Shield password if you can. The processor key is no longer used (the host generates a new one).
3. **GitHub `uat` environment.**
   - Add the secrets `UAT_ARACHNID_USERNAME` and `UAT_ARACHNID_PASSWORD`.
   - Delete `UAT_SCREENING_API_KEY`, `UAT_SCREENING_BASE_URL`, `UAT_MEDIA_PIPELINE_SERVICE_TOKEN` and `UAT_MEDIA_PIPELINE_UPLOAD_SIGNING_KEY`.
4. **Release.** Merge, then run `./scripts/update-release.sh X.Y.Z` (D383). The first run builds the new `ghcr.io/repasscloud/swoovr-screening` package.
   - That package is created **private**, so the deploy job stops at *Images are public*.
   - Make it public: repasscloud → Packages → `swoovr-screening` → Package settings → Change visibility → Public.
   - Then re-run the failed job.
   - The first start of `image-moderation` loads its models; the deploy waits up to 10 minutes for `/ready`.
5. **Validate.** All of these should hold:
   - The admin **Overview** shows *Image processing: Working normally*.
   - A clean photo is approved within a minute.
   - An explicit test photo appears in the photo review queue.
   - The EICAR test file renamed `.jpg` is refused with "That file can't be used".
   - `docker stats` shows the memory headroom; record it here.
6. **Recover the photos that were stuck.** On the host, run `./bin/swoovr job api-command media rescreen --pending`, then `./bin/swoovr job api-command media pending-report`.
   - Nothing else is needed: once screening answers, every pending photo is screened on its next retry. The rescreen just avoids waiting for the hourly retry.
   - Photos of inactive accounts stay pending by design and are reported.
7. **Retire the old infrastructure** after 7 days of healthy UAT (see the checklist below).

## Seeing and fixing problems

**Admin Overview (Support and Admin): the *Image processing* panel.** It shows:
- whether the screening service and the malware scanner answer;
- how many photos are waiting, waiting over an hour, and stalled;
- the oldest waiting photo;
- the last 24 hours' automatic approvals and review referrals (and known-content matches for Admin);
- *Why photos are waiting*, in plain words, by reason (for example "Screening service address is wrong (not found)").

It shows counts only: never photos, photo ids or account ids. Reload the page to check again; health is cached for 15 seconds.

**Monitoring:** `GET https://api-uat.swoovr.app/health/screening` returns:
- **200** `healthy`;
- **503** `degraded` (photos stalled or waiting over an hour);
- **503** `unavailable` (screening service or ClamAV not answering).

It returns the same counts and reasons as the panel. Like `/health/attachments`, it does not affect readiness or deploy gates.

**Operator commands** (on the host: `./bin/swoovr job api-command <command>`):

| Command | Effect |
| --- | --- |
| `media pending-report` | Prints the status, counts and reasons; exit 1 unless healthy |
| `media rescreen --pending` | Makes every pending photo due now; the API screens them within a minute |
| `media rescreen --pending --reason http-404` | Only those whose last attempt failed with that reason |

**Common reasons and fixes:**

| Reason | Meaning | Fix |
| --- | --- | --- |
| `unreachable`, `not-ready` | image-moderation not running, or still loading models | Check its health: `docker compose -p swoovr-uat ps image-moderation` and `logs --tail 100 image-moderation` |
| `http-401` | Key mismatch | `env/screening.env` and `env/image-moderation.env` must hold the same value (`envtool.py init` keeps them equal). Recreate `api` and `image-moderation` |
| `known-content-unchecked` | Project Arachnid Shield didn't answer, or its credentials are wrong | Check the `ARACHNID_*` values and outbound HTTPS. Photos wait safely; never switch the check off |
| `http-503` | Processor busy | Normal under bursts; retried automatically |
| Upload refused "can't be checked right now" | ClamAV not answering | `docker compose -p swoovr-uat ps clamav`. The first start loads signatures (a few minutes); support attachments wait in quarantine meanwhile |

**Safety rules that don't change:**
- A failure or time-out never approves a photo.
- A stalled photo is never shown to a human reviewer unless its known-content check completed.
- Rejected and blocked photos stay inaccessible under the existing retention and evidence rules.

## Rollback

- `./bin/swoovr rollback --to <previous tag>`, as for any release.
- A rollback never publishes a pending photo.
- The previous release's screening address (the old gateway) answered 404 for scans. Photos therefore simply wait again until this release is redeployed.
- The `DropImageProcessingStaging` migration is the only one-way step. It held Worker staging records only, never member photos.

## Retiring the old infrastructure (owner, after 7 days of healthy UAT)

All of these are outside this repository and irreversible. Nothing here deletes them.

1. **Vultr:** take the Caddy gateway (`swoovr-img-uat-syd.swoovr.app`) out of DNS, then destroy the gateway VM and processor VM1 and VM2. Their ClamAV sidecars go with them.
2. **Cloudflare Workers:** delete `swoovr-images-upload-uat`, `swoovr-images-processing-uat` and `swoovr-images-delivery-uat`. Remove the custom domains `images-upload-uat.swoovr.app` and `images-uat.swoovr.app`.
3. **Cloudflare Queues and R2:**
   - Delete the queues `swoovr-images-jobs-uat` and `swoovr-images-dlq-uat`.
   - Remove any R2 event notification on `swoovr-images-quarantine-uat`.
   - Empty and delete `swoovr-images-quarantine-uat` and `swoovr-images-processed-uat`. They hold staging copies only, and the quarantine bucket already expires objects after 30 days.
   - **Keep the photo bucket `Media__R2__Bucket`.**
4. **Secrets:** revoke the Worker tokens (`API_SERVICE_TOKEN`, `UPLOAD_SIGNING_KEY`, `PROCESSOR_API_KEY`) and the old processor `API_KEY`.
5. **Repositories:** archive (don't delete) `repasscloud/image-moderation-api` and `repasscloud/swoovr-cf-workers`, with a README pointer to `services/image-moderation` in this repository. Delete their local clones only when you choose to.

## Evidence and gaps (20 November 2026)

**Run and passed:**
- `./scripts/check.sh` (persistence against a throwaway database on the local PostgreSQL). It includes:
  - 825 persistence cases, among them the new checks: malware refusal before storage; fail-closed scanner unavailable and unsupported paths; status counts and reasons; stalled detection; Support/Admin visibility of known-content counts; rescreen; the `DropImageProcessingStaging` migration.
  - hosting-policy checks for the private screening address and the required key;
  - the deploy configuration and CLI scenario tests;
  - the processor's own suite: ruff clean, 162 passed, 2 provider integrations skipped. This used a fresh `services/image-moderation/.venv` on macOS arm64 with the pinned versions (torch without the linux `+cpu` suffix).

- **Local end-to-end** on the managed Docker platform rebuilt from this branch (`./scripts/local-platform.sh up --reuse`):
  - the screener ran from the in-repo build (`swoovr-image-moderation:local`, healthy);
  - `DropImageProcessingStaging` applied;
  - `/health/screening` and `/health/attachments` answered 200 `healthy`.
- `node tests/local-platform/image-processing.browser.cjs` passed:
  - a fictional member's EICAR upload was refused (422 `photo-refused`) by the shared ClamAV;
  - a synthetic photo went from checking to **approved** through real NudeNet/Freepik screening (local `RequireKnownContentCheck=false`, so Shield wasn't called);
  - the admin Overview panel showed *Working normally*, both services *Answering* and 1 approved photo. Screenshots were checked at desktop and 390 px widths.
  - The photo was then deleted.

**Pre-existing, not caused by this change:** `tests/local-platform/browser.cjs` fails its `/account` text assertion on a fresh local stack, because the first-sign-in steps (D382) are shown there instead.

**Not run:**
- The compiled Azure template assertions (`tests/uat/infrastructure.py`; no Bicep CLI here).
- A Docker build of the processor image.
- The owned browser suites for photos.
- Anything on the UAT host or GitHub Actions.

**Still OPEN:**
- Scanner freshness alerting (#376, D379's 24-hour alert and 96-hour fail-closed).
- The NudeNet licence question (Q174).
- Production's deployment design.
