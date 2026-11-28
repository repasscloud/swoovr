# Overnight development report, 28–29 November 2026

Updated as work goes. It's the resume point after an interruption. The plan approved by the owner is summarised in [Approved plan](#approved-plan).

## Initial state (28 November 2026, evening)

- `main` = `e2757dc` (v1.3.14). UAT runs v1.3.14 (Deploy UAT succeeded 11:43). Every workflow that is switched on is green.
- Open PRs: **#627** only (Privacy Notice 0.22, D453). It is mergeable, Codex found no issues, and no threads are open.
- Uncommitted: the D454 Photo Verified decision notification on `feature/530-photo-verified-notice`, built on #627.
- 87 open issues. The groups are in the plan. The deferred backlog (#35–#64, #611–#626 compatibility and others) is not touched.
- Owned suites that are stale on main: `review-workflows` (line 79) and `notifications` (line 430).
- Next IDs: **D455**, **Q196**.

## Decision register

The owner approved the plan without answering F1–F7, so every recommended option is used as a **DEFAULT**, and each PR says so. Any of them can still be changed.

| Ref | Question | Applied (DEFAULT) |
|---|---|---|
| F1 | Effective date of Privacy Notice 0.22 (#627) | 28 November 2026 |
| F2 | Photo Verified decision channels (D454) | Notification centre only |
| F3 | #544: how the place is chosen | Current location (one-time) or a pasted maps link or coordinates. Optional label, screened. No map tiles. Card links to Apple Maps, Google Maps and OpenStreetMap |
| F4 | #543/#431: sharing a whole album | Covers the photos in the album at that moment. The sender can stop it. The gate is re-checked on every view |
| F5 | #569 captions | Per photo, 150 characters, moderated like About me |
| F6 | #373 ban-evasion signals | Review only on a verified legal name + date of birth HMAC match with a different email and mobile |
| F7 | Housekeeping closures | #257, #372, #488 (and #509 once checked); #313/#319 checked and closed as the owner asked |

## Approved plan

Waves, in order. A blocked item is recorded and skipped.
1. **Baseline:** this report; D454 PR; fix the stale owned suites; housekeeping (F7).
2. **#544** sharing a location in chat (D455).
3. **#543** chat attachment menu (library, upload, camera), then album photos and albums in chat (#431 §4–5, D456).
4. **#448** attribute model, slice 1 (catalogue, storage, per-mode publication, server-side filtering), then slice 2 (editor and public rendering), **#452**, **#570/#571**, then narrowing **#500**.
5. Independent: **#376** ClamAV freshness. **#569** per-photo report and captions. **#373** if F6 holds.

Every PR that appends to DECISIONS.md is in one linear stack with base `main`, starting with #627.

## Progress log

| Time | Item | Result |
|---|---|---|
| 28 Nov, night | Plan approved | Report started |
| | Housekeeping (F7) | Closed #319, #509, #372, #488 and #257 with evidence comments. #313: the repository still names `hello@repasscloud.com`; I can't read the UAT host, so I asked on the issue |
| | PR A (D454) | Committed and pushed. **#628** opened, Codex review requested. check.sh passed. The owned suite `photo-verified` passed with the new notification checks |
| | Stale suites | **#630**: `notifications` (held the Connections page's own centre read; PASS 147) and `review-workflows` (missing staff OTP; PASS). Test-only |
| | #544 (D455) | **#631**. check.sh passed; `chat-location` PASS (36). Screenshot checked. Codex requested |
| | #543a (D456) | **#632**. check.sh passed (on the stack); `chat-library` PASS (20). Codex requested |
| | #448 slice 1 (D457) | **#633**. Catalogue, `profile_attribute_values` (migration `ProfileAttributes`), API, per-mode details, snapshot, export, deletion. check.sh passed; `profile-details` PASS (21) |
| | #448 slice 2 (D458) | **#634**. More about you on /me/profile; More about me on profiles. `profile-details` PASS (31); editor screenshot checked |
| | Codex on #631–#633 | #631: location is a reply in the client (fixed 0239d01, suite covers it live). Text-harm history now counts labelled locations (fix + runtime check, check.sh running). #632/#633 repeated the first finding: replied and resolved |
| | #452 (D459) | "You both like…" when both members publish interests in the mode. check.sh and suite running |
| | #571 (D460) | Their trip to your area on the profile, by the Who's visiting rule. General availability is already a detail (D457). Suite pending |

## Pull requests (merge order)

| Order | PR | Issues | Summary | Tests | Codex | Ready |
|---|---|---|---|---|---|---|
| 1 | #627 | #609, #530, #544 | Privacy Notice 0.22 | check.sh, photo-verified suite | no findings | yes |
| 2 | #628 | #530 | Photo Verified decision notification (D454) | check.sh, photo-verified suite | no findings | yes |
| 3 | #631 | #544 | Share a location in chat (D455) | check.sh, chat-location (36) | requested | yes |
| 4 | #632 | #543 (part) | Chat + menu: library, upload, camera (D456) | check.sh, chat-library (20) | requested | yes |
| 5 | #633 | #448 | Profile details slice 1 (D457, migration `ProfileAttributes`) | check.sh, profile-details (21) | finding fixed upstream | yes |
| 6 | #634 | #448 | Profile details slice 2: web (D458) | profile-details (31) | requested | yes |
| 7 | #635 | #452 | Shared interests (D459) | check.sh, profile-details (36) | no findings | yes |
| 8 | #636 | #571 (part) | Travel on the profile (D460) | check.sh, profile-details (40) | no findings | yes |
| any | #630 | — | Fix stale owned suites (test-only) | notifications (147), review-workflows | — | yes |
| any | #629 | — | This report (docs) | — | — | yes |

## Decisions made independently

- **D455 storage:** a shared location is stored as the message's own text (`geo:lat,lon` plus a label line), so every text rule (unsend, 30-day evidence, purge, holds, report, removal, export, deletion) covers it. No migration.
- **D455:** 0,0 is refused, and points are kept to 5 decimal places.
- **D457:** nationality, causes and accessibility count as sensitive (unavailable). Free-text details wait for a slice with text screening. Option lists are drafted.
- **D459:** publishing your own interests in a mode is opting in to "You both like…" there.
- **D460:** only a trip to the viewer's own area shows on a profile, by the Who's visiting rule.
- **D456:** a library photo sent to a chat is re-run through the upload pipeline as a fresh chat copy, rather than reusing the library item. Retention, removal and the explicit gates then never couple the two.

## Blockers and owner actions

- After #627 merges and is deployed, an operator stages and publishes the 0.22 policy-update notice. I'm not doing this.

## Remaining work

| Item | State | Why / next step |
|---|---|---|
| #543 slice 2: albums in chat (#431 §4–5) | Not built | The chat copy of an explicit album photo must keep its explicit rating whatever the screener says. Otherwise the per-view adult gate (D451, 182.4) could be skipped. Needs a reviewed change in `MediaPipeline` plus the F4 snapshot share record. Design noted on #543 |
| #376 ClamAV freshness | Design posted on #376, not built | Needs a read-only `clamav-db` mount in the API and a fail-closed upload gate on UAT. Proposed observe-only first. Waiting for your OK |
| #569 captions (F5) | Not built | Needs a new text-screening source (`PhotoCaption`) plus removal by staff in candidate review and content removal. The viewer, report and dedupe parts are already built (see #569) |
| #450 prompts, #451 badges/completeness | Not started | #450 needs the same text-screening source work as captions. #451: members already see "n of m answered" privately (D458); the badge audit is open |
| #373 ban evasion (F6) | Not started | Security-sensitive review queue on D191 HMAC matches; better as a reviewed slice |
| #448 sensitive categories | Unavailable by design | Your privacy review, consent and notice wording come first (§5). Ids are reserved |
| #448 free-text details | Not built | Occupation, music/films/TV, ideal first date, deal breakers and boundaries need text screening |
| #500 | Suggested closing | Superseded by D443 and D458 (comment on #500) |
| #570 | Covered by #633/#634 | Close after they merge |
| #313 | Waiting for you | Repository still names `hello@repasscloud.com`; confirm the UAT host's sender |
| #440 | Waiting for you | Fresh UAT date of birth session |

### Worktrees left for you to remove (merged branches)

Removal was blocked by a safety check in this session, so these remain: `../swoovr-ma` … `../swoovr-mh`, `../swoovr-n1` … `../swoovr-n6` (all merged). This session's: `../swoovr-p2`, `-l1`, `-l2`, `-a1`, `-a2`, `-a3`, `-a4`, `-s1`, `-r0` (remove once their PRs merge). `git worktree remove <path>` for each.

## Morning handover

### Executive summary
- **Start:** one open PR (#627), uncommitted D454 work, two stale owned suites, 87 open issues.
- **End:** 10 PRs open for review: #627 (yours, untouched), #628, #629 (this report), #630, #631, #632, #633, #634, #635, #636. 7 issues closed with evidence (#257, #319, #372, #488, #509, plus #544/#452 once their PRs merge). Issue comments on #313, #376, #448, #500, #543, #544, #569, #570.
- **Codex:** every finding fixed, replied to and resolved: #631 (a location counts as a reply in the client; labelled locations count in text-harm history); #632 (the database removal guard didn't accept locations, fixed on #631 with migration `LocationMessageRemoval`); #632/#633 repeated the first finding. Clean at their last review: #628, #630, #631, #633, #634, #635, #636. Re-review requested on #631/#632 after the last fix.
- **Tests:** check.sh passed on every branch (856 PostgreSQL persistence cases on the final stack). Owned suites: photo-verified, notifications (147), review-workflows, chat-location (36), chat-library (20), profile-details (40). Screenshots checked for the chat card and the profile editor.

### Merge order (one linear stack, all base `main`, each appends to DECISIONS.md)
1. #627 Privacy Notice 0.22 (D453)
2. #628 Photo Verified decision notification (D454)
3. #631 Share a location in chat (D455), closes #544
4. #632 Chat + menu: library, upload, camera (D456), part of #543
5. #633 Profile details slice 1 (D457), **migration `ProfileAttributes`**
6. #634 Profile details slice 2: web (D458)
7. #635 Shared interests (D459), closes #452
8. #636 Travel on the profile (D460), part of #571

Independent, any time: #630 (stale suites, test-only), #629 (this report).

### Release considerations
- **Migrations, in order:** `LocationMessageRemoval` (#631: re-creates `content_removal_guard()` to accept Location; Down restores the previous guard), then `ProfileAttributes` (#633: a new table). #632 needs none.
- **Flags:** none new. `Verification:PhotoVerified:Enabled` is already on in UAT (#627).
- **Privacy Notice:** after #627 is deployed, an operator stages and publishes the "0.22, 28 November 2026" policy-update notice. Location sharing (#631) and Photo Verified depend on 0.22 being in force.
- **Member communications:** the 0.22 notice only. Nothing else in this batch emails or pushes anyone.
- **UAT checks after deploy:** share a location both ways (current location and a pasted link); send a library photo; Photo Verified approve/reject leaves one notification; add details on /me/profile, publish to one mode, check from another account in that mode and another; "You both like…"; a trip to your area on a visitor's profile.
- **Rollback:** all code-only except the two migrations (both have a Down). Rolling back #631 leaves any shared locations as text messages starting `geo:` (shown as plain text by older clients).

### Your decisions (applied as DEFAULTs; change any)
F1 0.22 effective 28 November 2026 · F2 Photo Verified decisions in the notification centre only · F3 location chosen from the current location or a pasted link, no map provider · F4 album share snapshot (not built yet) · F5 captions per photo, 150 characters (not built yet) · F6 ban-evasion review on HMAC match only (not built yet) · F7 housekeeping closures done. Also D457 option lists, nationality/causes/accessibility treated as sensitive, D459 opt-in by publishing, D460 only trips to your area.
