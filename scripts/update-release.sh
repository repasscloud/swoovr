#!/usr/bin/env bash

set -euo pipefail

# Require exactly one argument in X.Y.Z format.
if [[ $# -ne 1 || ! "$1" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
    echo "Usage: $(basename "$0") X.Y.Z"
    echo "Example: $(basename "$0") 1.0.6   (or 1.1.0: a new Major.Minor is bumped, merged and then tagged)"
    exit 1
fi

VERSION="v$1"

cd "$(dirname "$0")/.."

# Refuse anything that is not strictly newer than the highest release tag on
# origin. Runs before any side effect (checkout, variable, tag, push).
if ! REMOTE_TAGS="$(git ls-remote --tags --refs origin 'v*')"; then
    echo "error: could not list release tags on origin" >&2
    exit 1
fi
LATEST="$(printf '%s\n' "$REMOTE_TAGS" \
    | sed 's#.*refs/tags/##' \
    | grep -E '^v[0-9]+\.[0-9]+\.[0-9]+$' \
    | sort -V \
    | tail -n 1 || true)"

if [[ -n "$LATEST" ]]; then
    HIGHEST="$(printf '%s\n%s\n' "$LATEST" "$VERSION" | sort -V | tail -n 1)"
    if [[ "$VERSION" == "$LATEST" || "$HIGHEST" != "$VERSION" ]]; then
        echo "error: $VERSION is not newer than the latest release $LATEST" >&2
        exit 1
    fi
    echo "Latest release: $LATEST -> releasing $VERSION"
fi

# A release on a newer Major.Minor (for example 1.1.0 after 1.0.x) must first move Directory.Build.props and the
# Keycloak theme to that version (D320, docs/delivery/VERSIONING.md); the deploy workflow refuses a tag that does not
# match them. Decide that now, still before any side effect.
IFS=. read -r WANT_MAJOR WANT_MINOR WANT_BUILD <<< "$1"
git fetch --quiet origin main
PROPS="$(git show origin/main:Directory.Build.props)"
HAVE_MAJOR="$(sed -nE 's#.*<SwoovrVersionMajor>([0-9]+)</SwoovrVersionMajor>.*#\1#p' <<< "$PROPS")"
HAVE_MINOR="$(sed -nE 's#.*<SwoovrVersionMinor>([0-9]+)</SwoovrVersionMinor>.*#\1#p' <<< "$PROPS")"
BUMP=false
if (( 10#$WANT_MAJOR != 10#$HAVE_MAJOR || 10#$WANT_MINOR != 10#$HAVE_MINOR )); then
    if (( 10#$WANT_MAJOR < 10#$HAVE_MAJOR || (10#$WANT_MAJOR == 10#$HAVE_MAJOR && 10#$WANT_MINOR < 10#$HAVE_MINOR) )); then
        echo "error: $VERSION is older than main's version $HAVE_MAJOR.$HAVE_MINOR.x" >&2
        exit 1
    fi
    if (( 10#$WANT_BUILD != 0 )); then
        echo "error: the first release of $WANT_MAJOR.$WANT_MINOR must be $WANT_MAJOR.$WANT_MINOR.0" >&2
        exit 1
    fi
    if ! git diff --quiet || ! git diff --cached --quiet; then
        echo "error: commit or discard your local changes first (the version bump is committed for you)" >&2
        exit 1
    fi
    BUMP=true
    echo "Version bump: $HAVE_MAJOR.$HAVE_MINOR.x -> $WANT_MAJOR.$WANT_MINOR.0"
fi

# Update repository.
git checkout main
git pull --ff-only

# New Major.Minor: bump, check, and merge it to main through a PR, then tag that merge.
if [[ "$BUMP" == true ]]; then
    BRANCH="release/$VERSION"
    git checkout -b "$BRANCH"
    python3 scripts/release_version.py set "$WANT_MAJOR.$WANT_MINOR"
    python3 scripts/check_version.py
    python3 -m unittest tests/release/test_release_version.py
    git add Directory.Build.props containers/keycloak/themes/swoovr/login/theme.properties
    git commit -m "Bump Swoovr version to $1"
    git push -u origin "$BRANCH"
    gh pr create --base main --head "$BRANCH" --title "Bump Swoovr version to $1" \
        --body "Version bump for release $VERSION, made by scripts/update-release.sh (D320, docs/delivery/VERSIONING.md)."
    gh pr merge "$BRANCH" --merge --delete-branch
    git checkout main
    git pull --ff-only
fi

# Configure deployment mode.
gh variable set DEPLOYMENT_MODE --body upgrade

# Create and push signed release tag.
git tag -s -m "$VERSION" "$VERSION" origin/main
git push origin "$VERSION"

# Wait for the deployment run triggered by this tag (not the previous one),
# then watch it.
RUN_ID=""
for _ in $(seq 1 30); do
    RUN_ID="$(gh run list \
        --workflow deploy-uat.yml \
        --branch "$VERSION" \
        --event push \
        --limit 1 \
        --json databaseId \
        -q '.[0].databaseId // empty')"
    [[ -n "$RUN_ID" ]] && break
    sleep 2
done

if [[ -z "$RUN_ID" ]]; then
    echo "error: no Deploy UAT run found for $VERSION after 60s" >&2
    exit 1
fi

gh run watch "$RUN_ID" --exit-status
