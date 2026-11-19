# First sign-in onboarding (profile steps)

Issue: https://github.com/repasscloud/wanderconnect/issues/389 (web only; mobile follows in M7). Decisions: D382 (owner direction CONFIRMED in #389; every Q181 sub-question built on its DEFAULT, [#390](https://github.com/repasscloud/wanderconnect/issues/390) stays OPEN). Extends D342 ([ISSUE-253-254-ONBOARDING.md](ISSUE-253-254-ONBOARDING.md)).

## What was wrong

A new member who finished account setup landed on Explore with "Add your home city to discover people in your community." followed by a plain link, "Add your home city", that repeated it. Nothing guided them through a profile.

## What changed

1. **Explore notices** (`ExplorePanel.razor`): home city, date of birth, Dating details and mode-off each show one sentence and a natural-width primary button (`.notice-action`): Set your home city, Add date of birth, Set up Dating, Turn on <mode>.
2. **API** (D382): `setupStep` continues after `dateOfBirth` with `profile`, `about`, `photos`, `modes`, `identity`, `datingShowMe` until `member_profiles.onboarding_completed_at` is set (`ProfileOnboarding`, `OnboardingEndpoints`: `GET /api/me/onboarding`, `POST /api/me/onboarding/steps/{step}`, `POST /api/me/onboarding/complete`). Gender adds Trans man / Trans woman, counted under Man / Woman in "show me" (Q181.1 A). Pronouns become option data plus Self-describe (`varchar(50)`) with a separate `show_pronouns` switch; existing rows copy "Show my gender" and free text maps to an option or Self-describe. Orientation "Straight (heterosexual)". `PUT /api/me/picture-text` (≤ 100 characters, contact details refused) with its own text-harm source, content-removal field, profile-version snapshot and data export. Migration `FirstSignInOnboarding`.
3. **Web** (`ProfileSetupSteps.razor`, hosted by `OnboardingFlow` on `/welcome`): Your name and home city → About me → Add some photos / Use words instead → Choose your connection modes → About you → In Dating, show me → `/explore?mode=<first enabled mode>`. `RestrictionGate` keeps the member on `/welcome` through these steps (plus `/media` for their own photo links); sign out, `/account/*`, support, notices, safety and the public policies stay reachable.
4. **Photo step**: up to 10 uploads here (library still 60), the owner's rules as a list, uploaded photos shown as they're checked; or the member's own words, previewed as the same quote tile Explore draws without a photo. Explore and profiles show the words only while no approved main photo shows.

## Evidence (19 November 2026)

- `./scripts/check.sh` with `SWOOVR_TEST_DATABASE` on a disposable PostgreSQL 17.11 container (the platform's own pair couldn't start, see gaps): exit 0, **801 persistence cases** including the new migration. Matching checks: 87 cases (Trans man/woman under Man/Woman, not separate "show me" values; pronoun list, Self-describe ≤ 50, free-text mapping). Phone/onboarding checks cover the step order, Dating's gender rule, the optional steps and picture-text contact detection.
- Migration on real rows (scratch database, `dotnet ef database update` to `UnansweredMessageLimit`, five fictional identities, then up to `FirstSignInOnboarding`): " he / him " → `he-him`; "she/her" with Show my gender off → `she-her`, `show_pronouns` off; "xe/xem" → `self` + words kept; blank → none. Down restores "He/Him", "She/Her", "xe/xem" and removes the pronoun options.
- Rendering: the real `ProfileSetupSteps` and `ExplorePanel` components rendered with .NET `HtmlRenderer` against a stubbed API (fictional data), wrapped with the real `themes.css`, `app.css` and `member-settings.css`, screenshotted in headless Chrome at 1280 px (light and dark) and in a 390 px iframe: [evidence/2026-11-19-issue-389-onboarding](evidence/2026-11-19-issue-389-onboarding/).
- New owned browser suite `profile-onboarding` (`tests/accounts/profile-onboarding.browser.cjs`, registered in `run-owned.cjs`): gate, pre-filled name, home-city wording, about consent, resume in a new session, text picture with contact refusal and preview, modes, Dating gender rule, Self-describe pronouns, show-me choices, redirect, completion flag. `market` and `modes` suites assert the new notice button.

## Gaps

- **Browser suites not run.** The local platform runs every container in mailpit's network namespace, and port 8025 was held by an SSH tunnel on this machine (not this session's), so the platform couldn't start. `profile-onboarding`, `onboarding`, `market`, `modes`, `matching` and `accessibility` need a run: `SWOOVR_OWNED_SUITES=profile-onboarding,onboarding,market,modes,matching,accessibility` through `tests/auth/run-owned.cjs` (see VALIDATION.md).
- **Not compared pixel-for-pixel with the issue's screenshots.** The images in #389 are private GitHub attachments; this session couldn't open them (no browser connection). The bio field and the text picture follow the issue's written description; please compare the evidence screenshots with your two reference images.
- The stub render proves layout, not the requests; the request paths are covered by the BFF allow-list check (`scripts/bff_routes.py`) and the unrun browser suite.
- The photo upload path on the onboarding step reuses `UploadPhotoAsync` but wasn't exercised end to end here (the suite takes the words branch).
- MAUI has no onboarding screens yet (web first); the API deliberately doesn't refuse other routes for these steps.
