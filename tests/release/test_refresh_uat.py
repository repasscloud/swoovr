"""scripts/refresh-uat.sh end to end, in a throwaway git repository with fake gh, ssh, curl, clipboard and release.

Nothing here touches GitHub, the UAT server or the network. The fakes share one JSON state file so a check can see
what the script did: the DEPLOYMENT_MODE at every moment, whether the run was re-run (and in which mode), what was
released, and what went to the clipboard.
"""
import json
import os
import shutil
import subprocess
import tempfile
import textwrap
import unittest
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]

FAKE_GH = r'''#!/usr/bin/env python3
import json, os, sys
state_path = os.environ["FAKE_STATE"]
state = json.load(open(state_path))
args = sys.argv[1:]
state["gh"].append(args)
def save():
    json.dump(state, open(state_path, "w"))
def out(text=""):
    save(); print(text); sys.exit(0)
def has(*words):
    return all(w in args for w in words)
if args[:2] == ["auth", "status"]: out()
if args[:2] == ["variable", "get"]:
    name = args[2]
    if name == "DEPLOYMENT_MODE": out(state["mode"])
    out({"UAT_INITIAL_ADMIN_EMAIL": "root@example.com", "UAT_EXPECTED_HOSTNAME": "uat-01"}[name])
if args[:2] == ["variable", "set"]:
    assert args[2] == "DEPLOYMENT_MODE"
    state["mode"] = args[args.index("--body") + 1]; state["modes"].append(state["mode"]); out()
if args[:2] == ["run", "list"]:
    workflow = args[args.index("--workflow") + 1]
    if has("--status", "success"): out(state["run_for_tag"].get(args[args.index("--branch") + 1], ""))
    if "--branch" in args:  # newest run of the tag, any result
        out(state.get("latest_run_for_tag", state["run_for_tag"]).get(args[args.index("--branch") + 1], ""))
    out(state["busy"].get(workflow, ""))
if args[:2] == ["run", "rerun"]:
    if state.get("rerun_refused"): save(); sys.exit(1)
    state["attempt"] += 1; state["rerun_mode"] = state["mode"]; out()
if args[:2] == ["run", "watch"]: out()
if args[:2] == ["run", "view"]:
    if "--log-failed" in args: out()
    if "--log" in args:
        mode = state.get("rerun_mode") or "upgrade"
        tag = state["tag"]
        lines = [f"deploy\tstep\t2026-11-22T01:00:00Z SWOOVR_RESULT {k}" for k in (
            f"mode={mode}", f"backup=/opt/swoovr/backups/20261122T010000Z-pre-{mode}-{tag}",
            "root-account=created-invite-sent", "initial-admin=invite-not-completed",
            "bootstrap-admin=kept-no-personal-admin", "smoke=passed", f"deployed={tag}")]
        out("\n".join(lines))
    query = args[args.index("--json") + 1]
    if query == "attempt": out(str(state["attempt"]))
    if query == "attempt,jobs":
        out(f"Deploy to UAT ({state['rerun_mode']})" if state.get("rerun_mode") else "")
    if query == "attempt,status,conclusion": out()
    if query == "conclusion": out(state["conclusion"])
sys.exit("fake gh: unexpected " + " ".join(args))
'''

FAKE_SSH = r'''#!/usr/bin/env python3
import json, os, sys
state = json.load(open(os.environ["FAKE_STATE"]))
args = [a for a in sys.argv[1:]]
while args and args[0] == "-o": args = args[2:]
target, command = args[0], " ".join(args[1:])
state["ssh"].append(command); json.dump(state, open(os.environ["FAKE_STATE"], "w"))
if command == "hostname": print(state["hostname"])
elif command.startswith("df "): print(state["free"])
elif command.endswith("./bin/swoovr status"): print("PASS api live\nSWOOVR_RESULT smoke=passed")
elif command.startswith("python3 -I -"):
    sys.stdin.read()
    if state.get("rerun_mode") and state["conclusion"] == "success":
        print(f"{state['tag']} 222222222222 {state['rerun_mode']} success")
    else:
        print("v1.1.2 111111111111 upgrade success")
elif "KC_BOOTSTRAP_ADMIN_PASSWORD" in command: print("temporary-kc-secret")
else: sys.exit("fake ssh: unexpected " + command)
'''

FAKE_CURL = r'''#!/usr/bin/env python3
import json, os, sys
state = json.load(open(os.environ["FAKE_STATE"]))
url = sys.argv[-1]
if url.endswith("/version"): print('{"version":"%s"}' % state["tag"][1:])
elif url.endswith("/health/ready"): print('{"status":"ready"}')
else: print("200", end="")
'''


class RefreshUatTests(unittest.TestCase):
    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp(prefix="swoovr-refresh-test-"))
        self.bin = self.tmp / "bin"
        self.bin.mkdir()
        self.state_path = self.tmp / "state.json"
        self.clipboard = self.tmp / "clipboard"
        self.released = self.tmp / "released"
        for name, body in (("gh", FAKE_GH), ("ssh", FAKE_SSH), ("curl", FAKE_CURL)):
            self.write(self.bin / name, body, executable=True)
        self.write(self.bin / "fake-clipboard", f"#!/bin/sh\ncat > '{self.clipboard}'\n", executable=True)
        self.write(self.bin / "fake-release", f"#!/bin/sh\necho \"$1\" >> '{self.released}'\nexit ${{FAKE_RELEASE_EXIT:-0}}\n",
                   executable=True)
        self.profiles = self.tmp / "profiles.ini"
        self.profiles.write_text("[Profile1]\nName=swoovr-uat-admin\n")

        origin, work = self.tmp / "origin.git", self.tmp / "work"
        subprocess.run(["git", "init", "-q", "--bare", "-b", "main", str(origin)], check=True)
        subprocess.run(["git", "init", "-q", "-b", "main", str(work)], check=True)
        for rel in ("scripts/refresh-uat.sh", "scripts/release_version.py", "Directory.Build.props",
                    "containers/keycloak/themes/swoovr/login/theme.properties"):
            (work / rel).parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(REPO / rel, work / rel)
        self.write(work / ".github/workflows/deploy-uat.yml", "# stub\n")
        self.write(work / "scripts/uat/setup-github-uat.sh", textwrap.dedent("""\
            #!/bin/sh
            printf 'Environment uat secrets set:\\n  UAT_ARACHNID_PASSWORD\\n  UAT_ARACHNID_USERNAME\\n'
            [ -n "$FAKE_MISSING" ] && printf '  MISSING UAT_SSH_HOST\\n'
            exit 0
            """), executable=True)
        self.git(work, "add", "-A")
        self.git(work, "-c", "user.name=t", "-c", "user.email=t@example.invalid", "commit", "-q", "-m", "init")
        self.git(work, "remote", "add", "origin", str(origin))
        self.git(work, "push", "-q", "origin", "main")
        committed = subprocess.run(["python3", "scripts/release_version.py", "show"], cwd=work, check=True,
                                   capture_output=True, text=True).stdout.strip()
        self.latest = f"v{committed}"
        self.git(work, "tag", self.latest)
        self.git(work, "push", "-q", "origin", self.latest)
        major, minor, _ = committed.split(".")
        self.next_tag = f"v{major}.{int(minor) + 1}.0"
        self.work = work
        self.set_state(mode="upgrade", modes=[], gh=[], ssh=[], busy={}, attempt=1, conclusion="success",
                       hostname="uat-01", free="541", tag=self.next_tag,
                       run_for_tag={self.next_tag: "4242", self.latest: "4141"})

    def tearDown(self):
        shutil.rmtree(self.tmp, ignore_errors=True)

    def write(self, path, content, executable=False):
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(content)
        if executable:
            path.chmod(0o755)

    def git(self, repo, *args):
        subprocess.run(["git", "-C", str(repo), *args], check=True, capture_output=True, text=True)

    def set_state(self, **values):
        state = json.loads(self.state_path.read_text()) if self.state_path.exists() else {}
        state.update(values)
        self.state_path.write_text(json.dumps(state))

    def state(self):
        return json.loads(self.state_path.read_text())

    def refresh(self, *args, **env):
        environment = {**os.environ, "PATH": f"{self.bin}{os.pathsep}{os.environ['PATH']}",
                       "FAKE_STATE": str(self.state_path), "SWOOVR_UAT_SSH": "fake-uat",
                       "SWOOVR_REFRESH_RELEASE_CMD": str(self.bin / "fake-release"),
                       "SWOOVR_REFRESH_CLIPBOARD": str(self.bin / "fake-clipboard"),
                       "SWOOVR_REFRESH_FIREFOX_PROFILES": str(self.profiles),
                       "SWOOVR_REFRESH_POLL_SECONDS": "0", "SWOOVR_REFRESH_POLL_ATTEMPTS": "3", **env}
        result = subprocess.run(["bash", "scripts/refresh-uat.sh", *args], cwd=self.work, env=environment,
                                capture_output=True, text=True, stdin=subprocess.DEVNULL, timeout=120)
        return result.returncode, result.stdout + result.stderr

    def assert_nothing_changed(self):
        state = self.state()
        self.assertEqual(state["modes"], [], "DEPLOYMENT_MODE never touched")
        self.assertNotIn("rerun_mode", state, "no run re-run")
        self.assertFalse(self.released.exists(), "nothing released")

    def test_releases_next_minor_refreshes_relocks_and_prepares_manual_steps(self):
        code, out = self.refresh("--confirm-refresh", self.next_tag)
        self.assertEqual(code, 0, out)
        state = self.state()
        self.assertEqual(self.released.read_text().split(), [self.next_tag[1:]], "update-release.sh X.(Y+1).0")
        self.assertEqual(state["rerun_mode"], "refresh", "the run was re-run while unlocked")
        self.assertEqual(state["modes"], ["refresh", "upgrade"], "unlocked once, relocked once")
        self.assertEqual(state["mode"], "upgrade")
        self.assertEqual(self.clipboard.read_text(), "temporary-kc-secret")
        self.assertNotIn("temporary-kc-secret", out, "the password is never printed")
        for text in (f"UAT is refreshed to {self.next_tag}", "ssh fake-uat -D 1080 -N", "root@example.com",
                     f"--ref {self.next_tag} -f action=admin-finalize", "zammad-admin-login off"):
            self.assertIn(text, out)

    def test_existing_tag_skips_the_release(self):
        self.set_state(tag=self.latest)
        code, out = self.refresh("--existing", self.latest, "--confirm-refresh", self.latest)
        self.assertEqual(code, 0, out)
        self.assertFalse(self.released.exists())
        self.assertEqual(self.state()["rerun_mode"], "refresh")

    def test_existing_tag_whose_only_run_failed_is_refreshed(self):
        self.set_state(tag=self.latest, run_for_tag={}, latest_run_for_tag={self.latest: "4141"})
        code, out = self.refresh("--existing", self.latest, "--confirm-refresh", self.latest)
        self.assertEqual(code, 0, out)
        self.assertIn("run 4141", out)
        self.assertEqual(self.state()["rerun_mode"], "refresh")

    def test_existing_tag_without_a_finished_run_changes_nothing(self):
        self.set_state(tag=self.latest, run_for_tag={}, latest_run_for_tag={})
        code, out = self.refresh("--existing", self.latest, "--confirm-refresh", self.latest)
        self.assertNotEqual(code, 0)
        self.assertIn("no finished Deploy UAT run", out)
        self.assert_nothing_changed()

    def test_wrong_or_missing_confirmation_changes_nothing(self):
        code, out = self.refresh("--confirm-refresh", "v9.9.9")
        self.assertNotEqual(code, 0)
        self.assertIn("does not match", out)
        self.assert_nothing_changed()
        code, out = self.refresh()
        self.assertNotEqual(code, 0)
        self.assertIn("pass --confirm-refresh", out)
        self.assert_nothing_changed()

    def test_preflight_failures_change_nothing(self):
        cases = [({"busy": {"uat-maintenance.yml": "77"}}, {}, "still running"),
                 ({"hostname": "some-other-box"}, {}, "not the UAT server"),
                 ({"free": "9"}, {}, "at least 15 GB"),
                 ({}, {"FAKE_MISSING": "1"}, "setting is missing")]
        for values, env, message in cases:
            with self.subTest(message):
                saved = self.state()
                self.set_state(**values)
                code, out = self.refresh("--confirm-refresh", self.next_tag, **env)
                self.assertNotEqual(code, 0)
                self.assertIn(message, out)
                self.assert_nothing_changed()
                self.state_path.write_text(json.dumps(saved))

    def test_failed_release_wipes_nothing(self):
        code, out = self.refresh("--confirm-refresh", self.next_tag, FAKE_RELEASE_EXIT="1")
        self.assertNotEqual(code, 0)
        self.assertIn("nothing was wiped", out)
        self.assertEqual(self.state()["modes"], [])

    def test_rerun_that_reads_upgrade_is_refused_and_relocked(self):
        # The re-run picks up "upgrade" (as if the mode was switched back too early): refuse, and keep it locked.
        fake = (self.bin / "gh").read_text().replace(
            'state["attempt"] += 1; state["rerun_mode"] = state["mode"]; out()',
            'state["attempt"] += 1; state["rerun_mode"] = "upgrade"; out()')
        self.write(self.bin / "gh", fake, executable=True)
        code, out = self.refresh("--confirm-refresh", self.next_tag)
        self.assertNotEqual(code, 0)
        self.assertIn("is an UPGRADE", out)
        self.assertEqual(self.state()["mode"], "upgrade")
        self.assertIn("RELOCKED", out)

    def test_refused_rerun_relocks(self):
        self.set_state(rerun_refused=True)
        code, out = self.refresh("--confirm-refresh", self.next_tag)
        self.assertNotEqual(code, 0)
        self.assertIn("30 days", out)
        self.assertEqual(self.state()["modes"], ["refresh", "upgrade"])

    def test_failed_refresh_is_already_relocked_and_names_the_retry(self):
        self.set_state(conclusion="failure")
        code, out = self.refresh("--confirm-refresh", self.next_tag)
        self.assertNotEqual(code, 0)
        self.assertIn(f"--existing {self.next_tag}", out)
        self.assertEqual(self.state()["mode"], "upgrade")
        self.assertFalse(self.clipboard.exists())


if __name__ == "__main__":
    unittest.main()
