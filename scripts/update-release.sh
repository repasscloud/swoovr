#!/usr/bin/env bash

set -euo pipefail

# Require exactly one argument in X.Y.Z format.
if [[ $# -ne 1 || ! "$1" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
    echo "Usage: $(basename "$0") X.Y.Z"
    echo "Example: $(basename "$0") 1.0.6"
    exit 1
fi

VERSION="v$1"

# Update repository.
cd ~/Developer/wanderconnect-brief
git checkout main
git pull --ff-only

# Configure deployment mode.
gh variable set DEPLOYMENT_MODE --body upgrade

# Create and push signed release tag.
git tag -s -m "$VERSION" "$VERSION" origin/main
git push origin "$VERSION"

# Watch the deployment workflow.
gh run watch "$(gh run list \
    --workflow deploy-uat.yml \
    --limit 1 \
    --json databaseId \
    -q '.[0].databaseId')"