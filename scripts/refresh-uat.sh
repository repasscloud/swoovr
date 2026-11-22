#!/usr/bin/env bash
# Swoovr UAT refresh, automated start (docs/delivery/UAT-REFRESH-SOP.md sections 3 to 8, and the start of 9).
#
#   ./scripts/refresh-uat.sh                         release the next minor version (X.Y.Z -> X.(Y+1).0), then refresh to it
#   ./scripts/refresh-uat.sh --existing vX.Y.Z       refresh an already-released tag (SOP section 4 Option A, or a retry)
#   ... --confirm-refresh vX.Y.Z                     skip the typed confirmation (the tag must match exactly)
#
# A refresh DELETES ALL UAT DATA (after a verified backup) and empties the R2 photo bucket. The script:
#
#  1. Preflight (SOP 3.2 to 3.7): GitHub sign-in, no Deploy UAT / UAT maintenance run in progress, every GitHub
#     setting present (incl. Arachnid), the root admin's address, the server reachable as the expected host, its
#     status, at least 15 GB free, and its current release.
#  2. Version (SOP 4): the highest of the committed version and every vX.Y.Z tag, Minor + 1, Build reset to 0.
#  3. Release (SOP 4 Option B): ./scripts/update-release.sh X.Y.0, an ordinary upgrade deployment, watched.
#  4. UNLOCK (SOP 5): DEPLOYMENT_MODE=refresh, re-run every job of that tag's successful Deploy UAT run (SOP 6).
#  5. RELOCK (SOP 7) as soon as the run's deploy job reads "Deploy to UAT (refresh)": the run read the mode once when
#     it started, so the rest of it stays a refresh. The mode is also relocked on ANY exit (failure, Control-C).
#  6. Check (SOP 8): the run's SWOOVR_RESULT lines, the public addresses, the API version, server status and history.
#  7. Prepare SOP 9: copies the temporary Keycloak administrator password to the clipboard (never printed), checks
#     the Firefox profile, and prints the remaining manual steps (9 to 14) with the tag filled in.
#
# Run it from the repository root on main with a clean tree. The SSH target is SWOOVR_UAT_SSH (default: the
# `swoovr-uat-root` host alias from ~/.ssh/config, for root on the UAT server).
#
# The whole script is one function, so bash parses all of it before update-release.sh fast-forwards main.

main() {
    set -Eeuo pipefail

    EXISTING="" ; CONFIRM=""
    while (( $# )); do
        case "$1" in
            --existing) EXISTING="${2:-}"; shift 2 || usage ;;
            --confirm-refresh) CONFIRM="${2:-}"; shift 2 || usage ;;
            -h|--help) usage 0 ;;
            *) usage ;;
        esac
    done

    WORKFLOW="deploy-uat.yml"
    SSH_TARGET="${SWOOVR_UAT_SSH:-swoovr-uat-root}"
    RELEASE_CMD="${SWOOVR_REFRESH_RELEASE_CMD:-./scripts/update-release.sh}"
    CLIPBOARD="${SWOOVR_REFRESH_CLIPBOARD:-pbcopy}"
    POLL_SECONDS="${SWOOVR_REFRESH_POLL_SECONDS:-10}"
    POLL_ATTEMPTS="${SWOOVR_REFRESH_POLL_ATTEMPTS:-180}"     # 30 minutes for the images before the deploy job runs
    LIVE=/opt/swoovr/swoovr-uat
    FIREFOX_PROFILES="${SWOOVR_REFRESH_FIREFOX_PROFILES:-$HOME/Library/Application Support/Firefox/profiles.ini}"

    ROOT="$(git rev-parse --show-toplevel 2> /dev/null)" || fail "run this from the Swoovr repository"
    cd "$ROOT"
    [[ -f scripts/release_version.py && -f .github/workflows/$WORKFLOW ]] || fail "$ROOT is not the Swoovr repository"

    STAGE="" ; UNLOCKED=false ; RUN="" ; TAG=""
    trap on_error ERR
    trap cleanup EXIT
    trap 'echo; fail "interrupted"' INT TERM

    echo "Swoovr UAT refresh"
    echo "------------------"
    echo "SSH target: $SSH_TARGET"
    echo

    # ---- 1. Preflight (SOP 3) -------------------------------------------------------------------------------------
    stage "GitHub sign-in"
    gh auth status > /dev/null 2>&1 || fail "not signed in to GitHub; run: gh auth login"
    pass

    if [[ -z "$EXISTING" ]]; then
        stage "Repository"
        [[ "$(git symbolic-ref --short -q HEAD || true)" == main ]] || fail "check out main first"
        [[ -z "$(git status --porcelain)" ]] || fail "the working tree is not clean; commit, stash or remove your changes first"
        pass
    fi

    stage "No deployment running"
    local wf busy
    for wf in "$WORKFLOW" uat-maintenance.yml; do
        busy="$(gh run list --workflow "$wf" --limit 10 --json databaseId,status \
            -q '.[] | select(.status != "completed") | .databaseId' | head -1)"
        [[ -z "$busy" ]] || fail "$wf run $busy is still running; wait for it (gh run watch $busy) and start again"
    done
    pass

    stage "Deployment mode"
    MODE_BEFORE="$(gh variable get DEPLOYMENT_MODE)"
    pass
    [[ "$MODE_BEFORE" == upgrade ]] || echo "  note: DEPLOYMENT_MODE was '$MODE_BEFORE' (left switched on); it is set back to upgrade before the release"

    stage "GitHub settings"
    local settings
    settings="$(scripts/uat/setup-github-uat.sh status 2>&1)" || fail "scripts/uat/setup-github-uat.sh status failed:
$settings"
    ! grep -q '^ *MISSING' <<< "$settings" || fail "a GitHub setting is missing (see scripts/uat/setup-github-uat.sh status):
$(grep '^ *MISSING' <<< "$settings")"
    grep -qx '  UAT_ARACHNID_USERNAME' <<< "$settings" && grep -qx '  UAT_ARACHNID_PASSWORD' <<< "$settings" \
        || fail "UAT_ARACHNID_USERNAME / UAT_ARACHNID_PASSWORD are not set (UAT operations SOP section 9)"
    ADMIN_EMAIL="$(gh variable get UAT_INITIAL_ADMIN_EMAIL --env uat)"
    EXPECTED_HOST="$(gh variable get UAT_EXPECTED_HOSTNAME --env uat)"
    [[ -n "$ADMIN_EMAIL" && -n "$EXPECTED_HOST" ]] || fail "UAT_INITIAL_ADMIN_EMAIL or UAT_EXPECTED_HOSTNAME is empty"
    pass

    stage "Server reachable"
    local host
    host="$(remote hostname)" || fail "cannot reach $SSH_TARGET over SSH (SOP 2.3)"
    [[ "$host" == "$EXPECTED_HOST" ]] || fail "$SSH_TARGET is '$host', not the UAT server '$EXPECTED_HOST'"
    pass

    stage "Server disk space"
    local free
    free="$(remote "df --output=avail -BG /var/lib/docker | tail -1 | tr -dc 0-9")"
    (( free >= 15 )) || fail "the server has ${free} GB free; a deployment needs at least 15 GB"
    pass
    echo "  ${free} GB free"

    stage "Server status"
    if remote "cd $LIVE && ./bin/swoovr status" 2>&1 | grep -qx 'SWOOVR_RESULT smoke=passed'; then
        pass
    else
        echo " WARN"
        echo "  UAT does not pass its checks right now; the refresh rebuilds it (see: ssh $SSH_TARGET $LIVE/bin/swoovr status)"
    fi

    stage "Current release"
    local current
    current="$(remote_history_last)" || fail "could not read $LIVE/state/releases.jsonl"
    pass
    echo "  running: ${current:-none recorded}"
    echo "  root admin set-up email goes to: $ADMIN_EMAIL (make sure you can open that mailbox)"
    echo

    # ---- 2. Version (SOP 4) ---------------------------------------------------------------------------------------
    if [[ -n "$EXISTING" ]]; then
        [[ "$EXISTING" =~ ^v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$ ]] || fail "--existing needs a tag like v1.2.0, not '$EXISTING'"
        TAG="$EXISTING"
        stage "Release tag on GitHub"
        [[ -n "$(git ls-remote --tags origin "refs/tags/$TAG")" ]] || fail "$TAG does not exist on origin"
        pass
    else
        stage "Next minor version"
        git fetch --quiet origin "+refs/heads/main:refs/remotes/origin/main" || fail "could not fetch origin/main"
        git fetch --quiet --tags origin || fail "could not fetch tags"
        git merge --quiet --ff-only origin/main || fail "could not fast-forward main to origin/main"
        TAG="v$(python3 scripts/release_version.py next-minor)"
        pass
        echo "  latest release: $(git tag --list 'v*' --sort=-v:refname | head -1)   new version: $TAG"
    fi
    VERSION="${TAG#v}"
    echo

    # ---- Confirmation ---------------------------------------------------------------------------------------------
    echo "This DELETES ALL UAT DATA (accounts, messages, photos, helpdesk tickets) after a verified backup,"
    echo "and empties the R2 photo bucket. UAT then runs $TAG."
    if [[ -n "$CONFIRM" ]]; then
        [[ "$CONFIRM" == "$TAG" ]] || fail "--confirm-refresh $CONFIRM does not match the version to refresh ($TAG); nothing was changed"
        echo "Confirmed by --confirm-refresh $CONFIRM."
    else
        [[ -t 0 ]] || fail "no terminal to confirm on; pass --confirm-refresh $TAG"
        local answer
        read -r -p "Type 'refresh $TAG' to continue: " answer
        [[ "$answer" == "refresh $TAG" ]] || fail "not confirmed; nothing was changed"
    fi
    echo

    # ---- 3. Release (SOP 4 Option B) ------------------------------------------------------------------------------
    if [[ -z "$EXISTING" ]]; then
        STAGE="Release $TAG (update-release.sh)"
        echo "== $STAGE: an ordinary upgrade deployment first"
        "$RELEASE_CMD" "$VERSION" || fail "the release did not complete; nothing was wiped. Fix it (docs/delivery/VERSIONING.md \"Releasing\"); if $TAG was published and deployed, continue with: ./scripts/refresh-uat.sh --existing $TAG"
        echo
    fi

    stage "Deploy UAT run for $TAG"
    RUN="$(gh run list --workflow "$WORKFLOW" --branch "$TAG" --status success --limit 1 --json databaseId -q '.[0].databaseId // empty')"
    [[ -n "$RUN" ]] || fail "no successful Deploy UAT run for $TAG; deploy it as an upgrade first"
    local attempt_before
    attempt_before="$(gh run view "$RUN" --json attempt -q .attempt)"
    pass
    echo "  run $RUN (attempt $attempt_before)"

    # ---- 4. UNLOCK and re-run (SOP 5, 6) --------------------------------------------------------------------------
    stage "UNLOCK: DEPLOYMENT_MODE=refresh"
    UNLOCKED=true
    gh variable set DEPLOYMENT_MODE --body refresh > /dev/null
    [[ "$(gh variable get DEPLOYMENT_MODE)" == refresh ]] || fail "DEPLOYMENT_MODE did not read back as refresh"
    pass

    stage "Re-running every job"
    gh run rerun "$RUN" > /dev/null 2>&1 || fail "GitHub refused to re-run run $RUN (runs older than 30 days cannot be re-run); release a new version instead: ./scripts/refresh-uat.sh"
    pass

    stage "Waiting for the deploy job"
    local name="" i conclusion=""
    for (( i = 0; i < POLL_ATTEMPTS; i++ )); do
        name="$(gh run view "$RUN" --json attempt,jobs \
            -q "select(.attempt > $attempt_before) | .jobs[] | select(.name | startswith(\"Deploy to UAT (\")) | .name" | head -1)"
        [[ "$name" == "Deploy to UAT (refresh)" || "$name" == "Deploy to UAT (upgrade)" ]] && break
        conclusion="$(gh run view "$RUN" --json attempt,status,conclusion -q "select(.attempt > $attempt_before and .status == \"completed\") | .conclusion")"
        [[ -z "$conclusion" ]] || fail "run $RUN ended ($conclusion) before its deploy job started; see: gh run view $RUN --web"
        sleep "$POLL_SECONDS"
    done
    [[ -n "$name" ]] || fail "run $RUN's deploy job did not start in time; see: gh run view $RUN --web"
    [[ "$name" == "Deploy to UAT (refresh)" ]] || fail "the re-run is an UPGRADE ('$name'): the mode was not picked up. Nothing is wiped by it; let it finish and start again"
    pass

    # ---- 5. RELOCK (SOP 7) ----------------------------------------------------------------------------------------
    relock

    stage "Watching the refresh"
    gh run watch "$RUN" --exit-status --compact --interval 10 > /dev/null 2>&1 || true
    conclusion="$(gh run view "$RUN" --json conclusion -q '.conclusion // "unknown"')"
    if [[ "$conclusion" != success ]]; then
        echo " FAIL ($conclusion)"
        gh run view "$RUN" --log-failed 2> /dev/null | grep -E '##\[error\]|error:' | tail -n 5 | sed 's/^/  /' >&2 || true
        fail_after "the refresh did not succeed. Read SOP section 15, then retry it: ./scripts/refresh-uat.sh --existing $TAG (details: gh run view $RUN --web)"
    fi
    pass

    # ---- 6. Check the result (SOP 8) ------------------------------------------------------------------------------
    echo
    echo "Deployment results (run $RUN):"
    local results
    results="$(gh run view "$RUN" --log 2> /dev/null | grep -o 'SWOOVR_RESULT [A-Za-z0-9_.-]*=[^ ]*' \
        | sed 's/^SWOOVR_RESULT //' | awk -F= '{ last[$1] = $0; if (!($1 in seen)) { seen[$1] = 1; order[++n] = $1 } }
            END { for (i = 1; i <= n; i++) print last[order[i]] }')"
    [[ -n "$results" ]] || fail "run $RUN has no SWOOVR_RESULT lines; read its summary: gh run view $RUN --web"
    sed 's/^/  /' <<< "$results"
    expect_result mode refresh
    expect_result smoke passed
    expect_result deployed "$TAG"
    grep -q "^backup=.*-pre-refresh-$TAG\$" <<< "$results" || fail "no pre-refresh backup recorded for $TAG"
    grep -q '^root-account=.*-email-failed' <<< "$results" \
        && echo "  note: the root admin set-up email failed; SOP 10 step 2 resends it"
    echo

    stage "Public addresses"
    local url code
    for url in https://uat.swoovr.app/ https://admin-uat.swoovr.app/ https://helpdesk-uat.swoovr.app/; do
        code="$(curl -sS -o /dev/null -w '%{http_code}' --max-time 30 "$url" || true)"
        [[ "$code" =~ ^[23] ]] || fail "$url answered $code"
    done
    curl -fsS --max-time 30 https://api-uat.swoovr.app/version | grep -q "\"version\":\"$VERSION\"" \
        || fail "https://api-uat.swoovr.app/version does not report $VERSION"
    curl -fsS --max-time 30 https://api-uat.swoovr.app/health/ready | grep -q '"status":"ready"' \
        || fail "https://api-uat.swoovr.app/health/ready is not ready"
    pass

    stage "Server status and history"
    remote "cd $LIVE && ./bin/swoovr status" 2>&1 | grep -qx 'SWOOVR_RESULT smoke=passed' \
        || fail "server status does not pass: ssh $SSH_TARGET 'cd $LIVE && ./bin/swoovr status'"
    current="$(remote_history_last)"
    [[ "$current" == "$TAG "*" refresh success" ]] || fail "server history's last line is '$current', not '$TAG … refresh success'"
    pass
    echo "  $current"

    stage "Deployment mode locked"
    [[ "$(gh variable get DEPLOYMENT_MODE)" == upgrade ]] || fail "DEPLOYMENT_MODE is not upgrade; run: gh variable set DEPLOYMENT_MODE --body upgrade"
    pass

    # ---- 7. Prepare the manual steps (SOP 9 onwards) --------------------------------------------------------------
    stage "Keycloak temporary password"
    local secret
    secret="$(remote "sed -n 's/^KC_BOOTSTRAP_ADMIN_PASSWORD=//p' $LIVE/env/keycloak.env")"
    [[ -n "$secret" ]] || fail "KC_BOOTSTRAP_ADMIN_PASSWORD is not in $LIVE/env/keycloak.env"
    printf '%s' "$secret" | "$CLIPBOARD" || fail "could not copy it to the clipboard; read it as in SOP 9.1"
    secret=""
    pass
    echo "  copied to the clipboard (not shown). Paste it at the Keycloak sign-in in step B."

    stage "Firefox profile swoovr-uat-admin"
    if [[ -f "$FIREFOX_PROFILES" ]] && grep -qx 'Name=swoovr-uat-admin' "$FIREFOX_PROFILES"; then
        pass
    else
        echo " MISSING"
        echo "  set it up first: SOP section 2.4"
    fi

    cat <<EOF

UAT is refreshed to $TAG and DEPLOYMENT_MODE is locked (upgrade). Your manual steps
(docs/delivery/UAT-REFRESH-SOP.md; the full detail for each step is there):

A. SOP 9.2  UNLOCK the Keycloak tunnel, in a new Terminal window (leave it running):
       ssh $SSH_TARGET -D 1080 -N
   Check it from another window (expect 302):
       curl -sk --socks5-hostname 127.0.0.1:1080 -o /dev/null -w '%{http_code}\\n' https://auth-uat.swoovr.app/admin/
B. SOP 9.3  Open the private Firefox profile and create your personal Keycloak administrator:
       /Applications/Firefox.app/Contents/MacOS/firefox -P swoovr-uat-admin --no-remote &
   https://auth-uat.swoovr.app/admin/ -> swoovr-bootstrap + the password on your clipboard.
C. SOP 9.4  RELOCK: Control-C in the tunnel window.
D. SOP 10   Break-glass root admin: open the set-up email sent to $ADMIN_EMAIL, finish it,
   sign in once at https://admin-uat.swoovr.app (email and password form), then:
       gh workflow run uat-maintenance.yml --ref $TAG -f action=admin-finalize
   (no email? gh workflow run uat-maintenance.yml --ref $TAG -f action=admin-invite)
E. SOP 11   Helpdesk mailbox:
       ssh $SSH_TARGET '$LIVE/bin/swoovr zammad-admin-login on'
   ... connect the mailbox and the notification sender as in SOP 11, then RELOCK:
       ssh $SSH_TARGET '$LIVE/bin/swoovr zammad-admin-login off'
F. SOP 12-14 Staff sign in again, re-add beta testers, final checks; then tell everyone UAT is ready.
EOF
}

usage() {
    echo "Usage: $(basename "$0") [--existing vX.Y.Z] [--confirm-refresh vX.Y.Z]" >&2
    exit "${1:-2}"
}

remote() { ssh -o BatchMode=yes -o ConnectTimeout=15 "$SSH_TARGET" "$@"; }

remote_history_last() { # prints "TAG COMMIT12 MODE RESULT" for the last recorded deployment, or nothing
    remote python3 -I - "$LIVE/state/releases.jsonl" <<'PY'
import json, os, sys
path = sys.argv[1]
lines = [line for line in open(path, encoding='utf-8') if line.strip()] if os.path.exists(path) else []
if lines:
    r = json.loads(lines[-1])
    print(r['tag'], (r.get('commit') or '')[:12], r['mode'], r['result'])
PY
}

expect_result() {
    grep -qx "$1=$2" <<< "$results" || fail "expected $1=$2 in the deployment results"
}

relock() {
    stage "RELOCK: DEPLOYMENT_MODE=upgrade"
    gh variable set DEPLOYMENT_MODE --body upgrade > /dev/null || fail "could not set DEPLOYMENT_MODE=upgrade; do it now: gh variable set DEPLOYMENT_MODE --body upgrade"
    [[ "$(gh variable get DEPLOYMENT_MODE)" == upgrade ]] || fail "DEPLOYMENT_MODE did not read back as upgrade; do it now: gh variable set DEPLOYMENT_MODE --body upgrade"
    UNLOCKED=false
    pass
}

stage() { STAGE="$1"; printf '%-36s' "$1..."; }
pass() { echo " PASS"; }

fail() {
    trap - ERR
    echo " FAIL" >&2
    fail_after "$@"
}

fail_after() {
    trap - ERR
    echo "error [${STAGE:-refresh}]: $*" >&2
    exit 1
}

on_error() {
    local status=$?
    (( BASH_SUBSHELL == 0 )) || exit "$status"
    trap - ERR
    echo " FAIL" >&2
    echo "error [${STAGE:-refresh}]: command failed (exit $status) at line ${BASH_LINENO[0]}" >&2
    exit "$status"
}

cleanup() {
    local status=$?
    if [[ "$UNLOCKED" == true ]]; then
        # Never leave the wipe switched on: a run that already started keeps the mode it read; a new run would wipe.
        if gh variable set DEPLOYMENT_MODE --body upgrade > /dev/null 2>&1 && [[ "$(gh variable get DEPLOYMENT_MODE 2> /dev/null)" == upgrade ]]; then
            echo "RELOCKED: DEPLOYMENT_MODE=upgrade" >&2
        else
            echo "WARNING: DEPLOYMENT_MODE may still be refresh. Run now: gh variable set DEPLOYMENT_MODE --body upgrade" >&2
        fi
    fi
    exit "$status"
}

main "$@"
