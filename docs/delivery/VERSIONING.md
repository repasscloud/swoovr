# Swoovr versioning

CONFIRMED Q129 / D320 (owner, 4 November 2026, [#189](https://github.com/repasscloud/wanderconnect/issues/189)); release
process CONFIRMED D383 (owner, 20 November 2026).

## One product version: `Major.Minor.Build`

| Part | Meaning | How it changes |
| --- | --- | --- |
| Major (X) | Significant platform changes, major redesigns | You choose it: `./scripts/update-release.sh X.Y.Z` |
| Minor (Y) | New functionality, substantial enhancements | You choose it: `./scripts/update-release.sh X.Y.Z` |
| Build (Z) | Fixes, security, performance, UI, routine releases | You choose it: `./scripts/update-release.sh X.Y.Z` |

The version you give is used exactly (D383): nothing is reset or renumbered, so `1.1.5` after `1.0.13` and `2.3.7` are
allowed. It only has to be newer than the committed version and every release tag (numeric: `1.10.0` > `1.9.99`).

The only definition is `Directory.Build.props` (`SwoovrVersionMajor`, `SwoovrVersionMinor`, `SwoovrVersionBuild`), which
holds the full version of the latest release commit. Every .NET project compiles with it. `Swoovr.Contracts.AppVersion` reads it back from the compiled assembly:

- `ProductVersion`: `1.4.28`
- `DisplayVersion`: `Version 1.4.28`. Public footers and app screens; never the commit.
- `DiagnosticVersion`: `1.4.28+<commit>`. Internal diagnostics only.

Where it shows:

- **Footers:** every web page (public, signed-in, invite-only), every admin page, and Keycloak sign-in and registration.
- **Mobile:** the app's welcome screen and Me area.
- **API:** `GET /version` (unauthenticated) returns `{ "version", "major", "minor", "build" }`.

None of these need a network call to know their own version.

Separate things that are **not** the product version:

- the native store build number (`SwoovrNativeBuild` → MAUI `ApplicationVersion`, monotonically increasing for Apple/Google);
- the API contract date (`Swoovr-Version` header);
- deployment ids;
- database migrations.

## Releasing

One command, from a clean `main`: `./scripts/update-release.sh X.Y.Z`. In order, it:

1. **Synchronises.** It refuses another branch, a dirty tree, unpushed commits or a concurrent run, fetches `origin/main`
   and tags, and fast-forwards (never resets or force-pushes). If that brought a new release script, it restarts with it.
2. **Validates** X.Y.Z: well formed, newer than the committed version and newer than every local or remote `vX.Y.Z` tag.
3. **Updates** `Directory.Build.props` and the Keycloak theme's `swoovrVersion` (`release_version.py set`). Nothing
   else holds a version: the projects, API `/version`, footers and mobile display read the props.
   `SwoovrNativeBuild` (store build) and dependency versions are not touched.
4. **Checks.** Every version file holds X.Y.Z (`verify`), `msbuild-args` and `check_version.py` accept it, the release
   tests pass and `./scripts/check.sh` passes. Any failure restores the files and stops with nothing committed.
5. **Commits** `updating to vX.Y.Z` and pushes it to `origin/main` (fast-forward only), then confirms the remote has it.
6. **Tags** that exact commit with an annotated `vX.Y.Z` (signed when `user.signingkey` is set). It verifies the tagged
   commit holds X.Y.Z, sets `DEPLOYMENT_MODE=upgrade`, and pushes the tag.
7. **Watches** the Deploy UAT run for that tag and commit, and exits non-zero unless it succeeds.

The workflow checks again: the tag must point at a commit on `main` whose version files hold exactly X.Y.Z.

**Recovery.** Every failure names its stage and says whether the commit or tag was published. Run the same command
again to resume:

- A version commit whose push failed is pushed again; no second commit is made.
- A pushed version commit without its tag gets tagged, even if more commits landed after it.

A published tag is never moved, deleted or reused. If a tagged release failed to deploy, re-run its workflow run
(`gh run rerun <id> --failed`) or release a higher version. For example, `v1.1.0` was tagged on a 1.0 commit on
19 November 2026 and can never deploy, so the next release is `1.1.1` or later.

Building blocks (`python3 scripts/release_version.py ...`): `show [REV]`, `next` (a suggestion), `check-new X.Y.Z`,
`set X.Y.Z`, `verify X.Y.Z [REV]`, `for-head`, and `msbuild-args [X.Y.Z]` (refuses anything but the committed version).

```sh
dotnet publish ... $(python3 scripts/release_version.py msbuild-args) -p:SwoovrNativeBuild=<store build>
docker build --build-arg SWOOVR_VERSION=$(python3 scripts/release_version.py for-head) -f containers/uat/Keycloak.Dockerfile .
```

- **Untagged builds:** development builds report the committed version (the last release's).
- **Rebuilding a release:** rebuilding or redeploying a tagged commit (`for-head`) keeps its version.
- **Drift:** `scripts/check_version.py` fails if the version files disagree or anything hard-codes a version.

GitHub Actions stays disabled (AGENTS.md). [.github/workflow-templates/release.yml](../../.github/workflow-templates/release.yml) holds the fully commented release steps for when the owner enables minutes.
