#!/usr/bin/env bash

set -euo pipefail

# Require exactly one argument in X.Y.Z format.
if [[ $# -ne 1 || ! "$1" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
    echo "Usage: $(basename "$0") X.Y.Z"
    echo "Example: $(basename "$0") 1.0.6"
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

# Update repository.
git checkout main
git pull --ff-only

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
