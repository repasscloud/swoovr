#!/usr/bin/env bash
# Swoovr release (D320, D383): ./scripts/update-release.sh X.Y.Z
#
#  1. Synchronise: on main, clean tree, fetch origin/main + tags, fast-forward (never reset or force).
#  2. Validate:    X.Y.Z well formed and newer than the committed version and every local/remote vX.Y.Z tag.
#  3. Update:      Directory.Build.props + Keycloak theme (scripts/release_version.py set).
#  4. Check:       every version file holds X.Y.Z, release tooling reads it, release tests, ./scripts/check.sh.
#  5. Commit:      "updating to vX.Y.Z", pushed to origin/main (fast-forward only) and verified on the remote.
#  6. Tag:         annotated vX.Y.Z on that exact commit, verified, pushed (an existing tag is never moved).
#  7. Deploy:      find the Deploy UAT run for that tag and commit, watch it, exit non-zero if it fails.
#
# Re-running after an interruption resumes instead of duplicating: a version commit that failed to push is pushed
# again, and a pushed version commit without its tag is tagged. Nothing published is ever rewritten.
#
# The whole script is one function, so bash has parsed all of it before step 1 fast-forwards this file. A stale copy
# can never run half old, half new (why v1.1.0 was tagged on a 1.0 commit, 19 November 2026); if the fast-forward
# changed the release tooling, the new version is started instead.

main() {
    set -Eeuo pipefail

    if [[ $# -ne 1 ]]; then
        echo "Usage: $(basename "$0") X.Y.Z    (for example 1.0.14, 1.1.0 or 2.0.0)" >&2
        exit 2
    fi
    REQUESTED="$1"
    VERSION="v$REQUESTED"
    MESSAGE="updating to $VERSION"
    WORKFLOW="deploy-uat.yml"
    VERSION_FILES=(Directory.Build.props containers/keycloak/themes/swoovr/login/theme.properties)
    POLL_SECONDS="${SWOOVR_RELEASE_POLL_SECONDS:-3}"
    POLL_ATTEMPTS="${SWOOVR_RELEASE_POLL_ATTEMPTS:-40}"

    ROOT="$(cd "$(dirname "$0")/.." && pwd)"
    cd "$ROOT"
    RV=(python3 scripts/release_version.py)

    STAGE="" ; REUSE_TAG=false ; LOCAL_COMMIT="" ; COMMIT_PUBLISHED="" ; TAG_PUBLISHED=false ; LOCK="" ; RESTORE_FILES=false
    trap on_error ERR
    trap cleanup EXIT

    echo "Release Version Update"
    echo "----------------------"
    echo "Repository: $(repository_name)"
    echo "Branch: main"
    echo

    # ---- 1. Synchronise with GitHub -------------------------------------------------------------------------------
    stage "Synchronising main"
    [[ "$(git symbolic-ref --short -q HEAD || true)" == main ]] || fail "check out main first (current: $(git symbolic-ref --short -q HEAD || echo 'detached HEAD'))"
    [[ -z "$(git status --porcelain)" ]] || fail "the working tree is not clean; commit, stash or remove your changes first"
    LOCK="$(git rev-parse --git-common-dir)/swoovr-release.lock"
    mkdir "$LOCK" 2> /dev/null || { LOCK=""; fail "another release is running in this repository (if it is not, remove $(git rev-parse --git-common-dir)/swoovr-release.lock)"; }
    git fetch --quiet origin "+refs/heads/main:refs/remotes/origin/main" || fail "could not fetch origin/main"
    git fetch --quiet --tags origin || fail "could not fetch tags (a local tag may differ from origin's; never move a published tag)"
    BEFORE="$(git rev-parse HEAD)"
    read -r AHEAD BEHIND < <(git rev-list --left-right --count main...origin/main)
    RESUME_PUSH=false
    if (( AHEAD == 0 )); then
        (( BEHIND == 0 )) || git merge --quiet --ff-only origin/main || fail "could not fast-forward main to origin/main"
    elif (( AHEAD == 1 && BEHIND == 0 )) && [[ "$(git log -1 --format=%s HEAD)" == "$MESSAGE" ]]; then
        RESUME_PUSH=true            # an earlier run committed the bump but its push failed: push that commit
    elif [[ "$(git log -1 --format=%s HEAD)" == "$MESSAGE" ]]; then
        fail "origin/main moved after the unpublished '$MESSAGE' commit; drop it with 'git reset --keep origin/main' (it was never published) and run again"
    else
        fail "local main has $AHEAD commit(s) that are not on origin/main; push them through the normal process or drop them first"
    fi
    [[ "$RESUME_PUSH" == true || "$(git rev-parse HEAD)" == "$(git rev-parse origin/main)" ]] || fail "main does not match origin/main"
    pass
    if [[ "$BEFORE" != "$(git rev-parse HEAD)" && -z "${SWOOVR_RELEASE_REEXEC:-}" ]] \
        && ! git diff --quiet "$BEFORE" HEAD -- scripts/update-release.sh scripts/release_version.py; then
        echo "Release tooling changed on main; restarting with the new version"
        rmdir "$LOCK"; LOCK=""; trap - EXIT ERR
        SWOOVR_RELEASE_REEXEC=1 exec "$ROOT/scripts/update-release.sh" "$REQUESTED"
    fi

    # ---- 2. Validate the requested version ------------------------------------------------------------------------
    CURRENT="$("${RV[@]}" show origin/main)"
    echo
    echo "Current version:   $CURRENT (latest release tag: $(latest_tag | grep . || echo none))"
    echo "Requested version: $REQUESTED"
    echo
    RELEASE_COMMIT=""
    if [[ "$RESUME_PUSH" == false ]]; then
        RELEASE_COMMIT="$(git log origin/main --format='%H%x09%s' | awk -F'\t' -v m="$MESSAGE" '$2 == m { print $1; exit }')"
    fi
    stage "Validating version"
    check_tag_absent
    "${RV[@]}" check-new "$REQUESTED" > /dev/null 2>&1 || [[ "$RESUME_PUSH" == true || -n "$RELEASE_COMMIT" ]] \
        || fail "$("${RV[@]}" check-new "$REQUESTED" 2>&1 >/dev/null || true)"
    if [[ "$RESUME_PUSH" == true ]]; then
        "${RV[@]}" verify "$REQUESTED" HEAD > /dev/null || fail "the unpublished '$MESSAGE' commit does not hold $REQUESTED"
    elif [[ -n "$RELEASE_COMMIT" ]]; then
        "${RV[@]}" verify "$REQUESTED" "$RELEASE_COMMIT" > /dev/null || fail "'$MESSAGE' ($RELEASE_COMMIT) on main does not hold $REQUESTED"
    fi
    pass

    stage "Checking existing tags"
    check_tag_absent
    REMOTE_TAGS=()
    while read -r _ ref; do REMOTE_TAGS+=("${ref#refs/tags/}"); done < <(git ls-remote --tags --refs origin 'v*')
    "${RV[@]}" check-new "$REQUESTED" ${REMOTE_TAGS[@]+"${REMOTE_TAGS[@]}"} > /dev/null 2>&1 || [[ "$RESUME_PUSH" == true || -n "$RELEASE_COMMIT" ]] \
        || fail "$("${RV[@]}" check-new "$REQUESTED" ${REMOTE_TAGS[@]+"${REMOTE_TAGS[@]}"} 2>&1 >/dev/null || true)"
    if [[ "$RESUME_PUSH" == true || -n "$RELEASE_COMMIT" ]]; then
        LATEST="$(latest_tag ${REMOTE_TAGS[@]+"${REMOTE_TAGS[@]}"})"
        if [[ -n "$LATEST" && "$(printf '%s\n%s\n' "$LATEST" "$VERSION" | sort -V | tail -n 1)" != "$VERSION" ]]; then
            fail "$VERSION is not newer than the latest release $LATEST"
        fi
    fi
    pass
    echo

    if [[ -n "$RELEASE_COMMIT" ]]; then
        echo "Resuming: '$MESSAGE' is already on origin/main ($(git rev-parse --short "$RELEASE_COMMIT")) but untagged."
        echo "Tagging that commit; no new commit is created."
        COMMIT_PUBLISHED="$RELEASE_COMMIT"
    else
        if [[ "$RESUME_PUSH" == true ]]; then
            echo "Resuming: '$MESSAGE' was committed locally ($(git rev-parse --short HEAD)) but never pushed."
            echo "Pushing that commit; no new commit is created."
            LOCAL_COMMIT="$(git rev-parse HEAD)"
        else
            # ---- 3. Update every version file ----------------------------------------------------------------------
            RESTORE_FILES=true
            stage "Updating Directory.Build.props"
            "${RV[@]}" set "$REQUESTED" > /dev/null
            [[ "$("${RV[@]}" show)" == "$REQUESTED" ]] || fail "Directory.Build.props does not read back as $REQUESTED"
            pass
            stage "Updating other version files"
            grep -qx "swoovrVersion=$REQUESTED" containers/keycloak/themes/swoovr/login/theme.properties \
                || fail "Keycloak theme swoovrVersion was not updated"
            pass

            # ---- 4. Validate the updated files ---------------------------------------------------------------------
            stage "Validating version references"
            "${RV[@]}" verify "$REQUESTED" > /dev/null
            "${RV[@]}" msbuild-args "$REQUESTED" > /dev/null
            python3 scripts/check_version.py > /dev/null
            CHANGED="$(git diff --name-only | sort | tr '\n' ' ')"
            [[ "$CHANGED" == "$(printf '%s\n' "${VERSION_FILES[@]}" | sort | tr '\n' ' ')" ]] \
                || fail "unexpected files changed: $CHANGED"
            pass
            LOG="$(mktemp -t swoovr-release.XXXXXX)"
            stage "Running tests"
            python3 -m unittest discover -s tests/release -p 'test_*.py' > "$LOG" 2>&1 || fail "release-version tests failed (log: $LOG)"
            pass
            stage "Build validation"
            ./scripts/check.sh > "$LOG" 2>&1 || fail "./scripts/check.sh failed (log: $LOG):
$(tail -n 15 "$LOG")"
            pass
            grep '^SKIP' "$LOG" | sed 's/^/  gap: /' || true

            # ---- 5. Commit -----------------------------------------------------------------------------------------
            echo
            echo "Creating commit:"
            echo "  $MESSAGE"
            git add -- "${VERSION_FILES[@]}"
            git commit --quiet -m "$MESSAGE"
            RESTORE_FILES=false
            LOCAL_COMMIT="$(git rev-parse HEAD)"
            echo
        fi

        stage "Pushing to origin/main"
        git push --quiet origin "$LOCAL_COMMIT:refs/heads/main" \
            || fail "push rejected; nothing was published. Re-run the same command to retry this commit (if origin/main moved, drop it first with 'git reset --keep origin/main')"
        pass
        stage "Verifying remote commit"
        [[ "$(git ls-remote origin refs/heads/main | cut -f1)" == "$LOCAL_COMMIT" ]] || fail "origin/main is not $LOCAL_COMMIT after the push"
        git fetch --quiet origin "+refs/heads/main:refs/remotes/origin/main"
        COMMIT_PUBLISHED="$LOCAL_COMMIT"
        RELEASE_COMMIT="$LOCAL_COMMIT"
        pass
    fi
    echo

    # ---- 6. Tag the version commit --------------------------------------------------------------------------------
    stage "Creating tag $VERSION"
    git fetch --quiet origin "+refs/heads/main:refs/remotes/origin/main"
    git merge-base --is-ancestor "$RELEASE_COMMIT" origin/main || fail "$RELEASE_COMMIT is not on origin/main"
    [[ "$(git log -1 --format=%s "$RELEASE_COMMIT")" == "$MESSAGE" ]] || fail "$RELEASE_COMMIT is not the '$MESSAGE' commit"
    check_tag_absent
    if [[ "$REUSE_TAG" == true ]]; then
        [[ "$(git cat-file -t "$VERSION")" == tag ]] || fail "local $VERSION is not an annotated tag"
        echo -n " (reusing the unpushed local tag)"
    else
        SIGN=(-a)
        [[ -n "$(git config --get user.signingkey || true)" ]] && SIGN=(-s)
        git tag "${SIGN[@]}" -m "Swoovr $REQUESTED" "$VERSION" "$RELEASE_COMMIT"
    fi
    pass
    stage "Verifying tagged commit"
    [[ "$(git rev-parse "$VERSION^{commit}")" == "$RELEASE_COMMIT" ]] || fail "$VERSION does not point at $RELEASE_COMMIT"
    "${RV[@]}" verify "$REQUESTED" "$VERSION" > /dev/null || fail "the commit tagged $VERSION does not hold $REQUESTED"
    pass
    stage "Pushing release tag"
    gh variable set DEPLOYMENT_MODE --body upgrade > /dev/null || fail "could not set DEPLOYMENT_MODE=upgrade; the tag was not pushed"
    git push --quiet origin "refs/tags/$VERSION" || fail "tag push rejected (another release may have published $VERSION)"
    TAG_PUBLISHED=true
    [[ "$(git ls-remote origin "refs/tags/$VERSION^{}" | cut -f1)" == "$RELEASE_COMMIT" ]] || fail "origin's $VERSION does not point at $RELEASE_COMMIT"
    pass
    echo

    # ---- 7. Watch the deployment ----------------------------------------------------------------------------------
    echo "GitHub Actions: Deploy UAT"
    stage "Finding workflow run"
    RUN_ID=""
    for (( attempt = 0; attempt < POLL_ATTEMPTS; attempt++ )); do
        RUN_ID="$(gh run list --workflow "$WORKFLOW" --branch "$VERSION" --event push --limit 10 --json databaseId,headSha \
            | python3 -c 'import json,sys; print(next((str(r["databaseId"]) for r in json.load(sys.stdin) if r["headSha"] == sys.argv[1]), ""))' "$RELEASE_COMMIT")"
        [[ -n "$RUN_ID" ]] && break
        sleep "$POLL_SECONDS"
    done
    [[ -n "$RUN_ID" ]] || fail "no Deploy UAT run started for $VERSION ($RELEASE_COMMIT); check the Actions tab"
    pass
    echo "  run $RUN_ID"
    STAGE="Deploy UAT"
    gh run watch "$RUN_ID" --exit-status --compact --interval 10 > /dev/null 2>&1 || true
    CONCLUSION="$(gh run view "$RUN_ID" --json conclusion | python3 -c 'import json,sys; print(json.load(sys.stdin).get("conclusion") or "unknown")')"
    if [[ "$CONCLUSION" != success ]]; then
        echo "Status: $(tr '[:lower:]' '[:upper:]' <<< "$CONCLUSION")"
        gh run view "$RUN_ID" --log-failed 2> /dev/null | grep -E '##\[error\]|error:' | tail -n 5 | sed 's/^/  /' >&2 || true
        echo "  details: gh run view $RUN_ID --web" >&2
        echo "  retry a transient failure: gh run rerun $RUN_ID --failed" >&2
        fail_after "the deployment did not succeed"
    fi
    echo "Status: SUCCESS"
    echo
    echo "Release $VERSION completed successfully."
}

latest_tag() {
    printf '%s\n' "$@" $(git tag --list 'v*') | grep -E '^v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$' | sort -V | tail -n 1 || true
}

stage() { STAGE="$1"; printf '%-34s' "$1..."; }
pass() { echo " PASS"; }

check_tag_absent() {
    local remote
    remote="$(git ls-remote origin "refs/tags/$VERSION^{}" "refs/tags/$VERSION" | tail -n 1 | cut -f1)"
    [[ -z "$remote" ]] || fail "$VERSION already exists on origin$(tag_hint "$remote")"
    if git rev-parse -q --verify "refs/tags/$VERSION" > /dev/null; then
        # An interrupted run may have created the tag on the published version commit without pushing it: reuse it.
        [[ -n "$RELEASE_COMMIT" && "$(git rev-parse "$VERSION^{commit}")" == "$RELEASE_COMMIT" ]] && { REUSE_TAG=true; return 0; }
        fail "$VERSION already exists locally$(tag_hint "$(git rev-parse "$VERSION^{commit}")")"
    fi
}

tag_hint() {
    local target="$1" props=""
    git cat-file -e "$target^{commit}" 2> /dev/null && props="$(python3 scripts/release_version.py show "$target" 2> /dev/null || true)"
    if [[ "$props" == "$REQUESTED" ]]; then
        echo " and holds $REQUESTED: it is released. To redeploy it, re-run its Deploy UAT run (gh run list --workflow $WORKFLOW --branch $VERSION)"
    else
        echo " on a commit that holds ${props:-another version}, so it can never deploy. Tags are never moved or reused: release a higher version (for example $(python3 scripts/release_version.py next))"
    fi
}

repository_name() {
    local url
    url="$(git remote get-url origin 2> /dev/null || echo unknown)"
    url="${url%.git}"
    echo "${url##*[:/]github.com[:/]}"
}

published_state() {
    echo "  version commit published: ${COMMIT_PUBLISHED:-no}" >&2
    echo "  release tag published:    $([[ "$TAG_PUBLISHED" == true ]] && echo "yes ($VERSION)" || echo no)" >&2
    if [[ -n "$COMMIT_PUBLISHED" && "$TAG_PUBLISHED" == false ]]; then
        echo "  recover: run ./scripts/update-release.sh $REQUESTED again; it tags the published commit (no new commit)" >&2
    elif [[ -z "$COMMIT_PUBLISHED" && -n "$LOCAL_COMMIT" ]]; then
        echo "  recover: run ./scripts/update-release.sh $REQUESTED again; it pushes the existing local commit $(git rev-parse --short "$LOCAL_COMMIT")" >&2
    fi
}

fail() {
    trap - ERR
    echo " FAIL" >&2
    echo "error [${STAGE:-release}]: $*" >&2
    published_state
    exit 1
}

fail_after() {
    trap - ERR
    echo "error [${STAGE:-release}]: $*" >&2
    published_state
    exit 1
}

on_error() {
    local status=$?
    (( BASH_SUBSHELL == 0 )) || exit "$status"     # report once, from the main shell
    trap - ERR
    echo " FAIL" >&2
    echo "error [${STAGE:-release}]: command failed (exit $status) at line ${BASH_LINENO[0]}" >&2
    published_state
    exit "$status"
}

cleanup() {
    if [[ "$RESTORE_FILES" == true ]]; then
        git checkout --quiet -- "${VERSION_FILES[@]}" 2> /dev/null || true   # unpublished edits only
    fi
    [[ -n "$LOCK" ]] && rmdir "$LOCK" 2> /dev/null || true
}

main "$@"
