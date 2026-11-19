"""D383: scripts/update-release.sh end to end, in throwaway git repositories with a fake `gh`.

Every test builds its own bare "origin" and clone under a temporary directory and puts a mock `gh` first on PATH, so no
real tag is published and no deployment is triggered. Global and system git config are ignored (no signing, no hooks).
"""
import json
import os
import re
import shutil
import stat
import subprocess
import tempfile
import textwrap
import unittest
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]

PROPS = """<Project>
  <PropertyGroup>
    <SwoovrVersionMajor>{0}</SwoovrVersionMajor>
    <SwoovrVersionMinor>{1}</SwoovrVersionMinor>
    <SwoovrVersionBuild Condition="'$(SwoovrVersionBuild)' == ''">{2}</SwoovrVersionBuild>
    <SwoovrNativeBuild Condition="'$(SwoovrNativeBuild)' == ''">7</SwoovrNativeBuild>
    <SwoovrVersion>$(SwoovrVersionMajor).$(SwoovrVersionMinor).$(SwoovrVersionBuild)</SwoovrVersion>
    <Version>$(SwoovrVersion)</Version>
    <PackageReference Include="Unrelated" Version="9.8.7" />
  </PropertyGroup>
</Project>
"""

# Fake gh: records every call; the run it reports is for whatever commit origin's tag points at.
GH = r'''#!/usr/bin/env python3
import json, os, subprocess, sys
args = sys.argv[1:]
with open(os.environ["MOCK_GH_LOG"], "a") as log: log.write(json.dumps(args) + "\n")
if args[:2] == ["variable", "set"]:
    sys.exit(int(os.environ.get("MOCK_GH_VARIABLE_STATUS", "0")))
if args[:2] == ["run", "list"]:
    tag = args[args.index("--branch") + 1]
    sha = subprocess.run(["git", "-C", os.environ["MOCK_ORIGIN"], "rev-parse", "-q", "--verify", tag + "^{commit}"],
                         capture_output=True, text=True).stdout.strip()
    print(json.dumps([{"databaseId": 4242, "headSha": sha}] if sha and os.environ.get("MOCK_GH_NO_RUN") != "1" else []))
    sys.exit(0)
if args[:2] == ["run", "watch"]:
    sys.exit(0 if os.environ.get("MOCK_GH_CONCLUSION", "success") == "success" else 1)
if args[:2] == ["run", "view"]:
    if "--log-failed" in args: print("validate\tstep\t##[error]Process completed with exit code 1.")
    else: print(json.dumps({"conclusion": os.environ.get("MOCK_GH_CONCLUSION", "success")}))
    sys.exit(0)
sys.exit("mock gh: unexpected " + " ".join(args))
'''

CHECK = """#!/usr/bin/env bash
[[ "${MOCK_CHECK_FAIL:-}" == 1 ]] && { echo "FAIL: stub build"; exit 1; }
# The release gate never uses this machine's local database (D383).
[[ "${SWOOVR_CHECK_PERSISTENCE:-}" == 0 && -z "${SWOOVR_TEST_DATABASE:-}" ]] || { echo "FAIL: local database used"; exit 1; }
echo "PASS: stub build"
echo "SKIP: stub persistence"
"""

CHECK_VERSION = """#!/usr/bin/env python3
import subprocess, sys
v = subprocess.run([sys.executable, "scripts/release_version.py", "show"], capture_output=True, text=True).stdout.strip()
sys.exit(subprocess.run([sys.executable, "scripts/release_version.py", "verify", v], capture_output=True).returncode)
"""


class UpdateRelease(unittest.TestCase):
    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp())
        self.origin, self.work = self.tmp / "origin.git", self.tmp / "work"
        self.bin = self.tmp / "bin"; self.bin.mkdir()
        self.gh_log = self.tmp / "gh.log"
        self.write(self.bin / "gh", GH, executable=True)
        self.env = {**os.environ, "GIT_CONFIG_GLOBAL": os.devnull, "GIT_CONFIG_NOSYSTEM": "1",
                    "PATH": f"{self.bin}{os.pathsep}{os.environ['PATH']}", "MOCK_GH_LOG": str(self.gh_log),
                    "MOCK_ORIGIN": str(self.origin), "SWOOVR_RELEASE_POLL_SECONDS": "0", "SWOOVR_RELEASE_POLL_ATTEMPTS": "3"}
        for key in ("SWOOVR_RELEASE_REEXEC", "MOCK_CHECK_FAIL", "MOCK_GH_CONCLUSION", "MOCK_GH_VARIABLE_STATUS"):
            self.env.pop(key, None)
        self.git(self.tmp, "init", "-q", "--bare", "-b", "main", str(self.origin))
        self.git(self.tmp, "clone", "-q", str(self.origin), str(self.work))
        self.configure(self.work)
        # Display only: origin is named by an SSH-style GitHub URL that git rewrites to the local bare repository.
        self.git(self.work, "config", "url." + str(self.origin) + ".insteadOf", "git@github.com:example/swoovr.git")
        self.git(self.work, "remote", "set-url", "origin", "git@github.com:example/swoovr.git")
        w = self.work
        (w / "scripts").mkdir(); (w / "tests/release").mkdir(parents=True)
        shutil.copy(REPO / "scripts/update-release.sh", w / "scripts/update-release.sh")
        shutil.copy(REPO / "scripts/release_version.py", w / "scripts/release_version.py")
        self.write(w / "scripts/check.sh", CHECK, executable=True)
        self.write(w / "scripts/check_version.py", CHECK_VERSION)
        self.write(w / "tests/release/test_stub.py", "import unittest\nclass T(unittest.TestCase):\n    def test(self): pass\n")
        self.write(w / ".gitignore", "__pycache__/\n")
        self.set_files(w, "1.0.13")
        self.git(w, "add", "."); self.git(w, "commit", "-qm", "initial")
        self.git(w, "tag", "-a", "v1.0.13", "-m", "Swoovr 1.0.13")
        self.git(w, "push", "-q", "origin", "main", "v1.0.13")

    def tearDown(self):
        shutil.rmtree(self.tmp)

    # helpers ---------------------------------------------------------------------------------------------------------
    def write(self, path, content, executable=False):
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(content)
        if executable: path.chmod(path.stat().st_mode | stat.S_IXUSR)

    def configure(self, repo):
        for key, value in (("user.name", "Fictional Releaser"), ("user.email", "releaser@example.test")):
            self.git(repo, "config", key, value)

    def set_files(self, repo, version):
        self.write(repo / "Directory.Build.props", PROPS.format(*version.split(".")))
        self.write(repo / "containers/keycloak/themes/swoovr/login/theme.properties",
                   f"parent=keycloak.v2\nswoovrVersion={version}\nlocales=en\n")

    def git(self, repo, *args, ok=True):
        result = subprocess.run(["git", "-C", str(repo), *args], capture_output=True, text=True, env=getattr(self, "env", None))
        if ok: self.assertEqual(result.returncode, 0, f"git {' '.join(args)}: {result.stderr}")
        return result.stdout.strip()

    def release(self, version, **env):
        result = subprocess.run(["./scripts/update-release.sh", version], cwd=self.work, capture_output=True, text=True,
                                env={**self.env, **env}, timeout=120)
        return result.returncode, result.stdout + result.stderr

    def origin_main(self):
        return self.git(self.origin, "rev-parse", "main")

    def origin_tags(self):
        return set(self.git(self.origin, "tag", "--list").split())

    def version_commits(self, version):
        return [line for line in self.git(self.origin, "log", "main", "--format=%s").splitlines() if line == f"updating to v{version}"]

    def gh_calls(self):
        return [json.loads(line) for line in self.gh_log.read_text().splitlines()] if self.gh_log.exists() else []

    def assert_released(self, version):
        tag_commit = self.git(self.origin, "rev-parse", f"v{version}^{{commit}}")
        self.assertEqual(self.git(self.origin, "log", "-1", "--format=%s", tag_commit), f"updating to v{version}")
        self.assertEqual(self.git(self.origin, "cat-file", "-t", f"v{version}"), "tag", "annotated tag")
        props = self.git(self.origin, "show", f"v{version}:Directory.Build.props")
        major, minor, build = version.split(".")
        for name, value in (("Major", major), ("Minor", minor)):
            self.assertIn(f"<SwoovrVersion{name}>{value}</SwoovrVersion{name}>", props)
        self.assertIn(f"== ''\">{build}</SwoovrVersionBuild>", props)
        self.assertIn('Version="9.8.7"', props, "unrelated dependency versions are untouched")
        self.assertIn("<SwoovrNativeBuild Condition=\"'$(SwoovrNativeBuild)' == ''\">7<", props, "store build untouched")
        theme = self.git(self.origin, "show", f"v{version}:containers/keycloak/themes/swoovr/login/theme.properties")
        self.assertIn(f"swoovrVersion={version}\n", theme + "\n")
        changed = self.git(self.origin, "diff", "--name-only", f"{tag_commit}~1", tag_commit).split()
        self.assertEqual(sorted(changed), ["Directory.Build.props", "containers/keycloak/themes/swoovr/login/theme.properties"])
        self.assertEqual(self.version_commits(version), [f"updating to v{version}"], "exactly one version commit")
        return tag_commit

    def assert_nothing_published(self, main_before, tags_before):
        self.assertEqual(self.origin_main(), main_before, "origin/main unchanged")
        self.assertEqual(self.origin_tags(), tags_before, "no tag published")
        self.assertFalse(any(call[:2] == ["variable", "set"] for call in self.gh_calls()), "no deployment configured")

    # behaviour -------------------------------------------------------------------------------------------------------
    def test_minor_release_updates_commits_pushes_tags_and_watches(self):
        status, out = self.release("1.1.0", SWOOVR_TEST_DATABASE="Host=127.0.0.1;Password=stale")
        self.assertEqual(status, 0, out)
        commit = self.assert_released("1.1.0")
        self.assertIn("Repository: example/swoovr\n", out)
        self.assertEqual(self.origin_main(), commit, "the version commit is origin/main and is the tagged commit")
        self.assertEqual(self.git(self.work, "rev-parse", "main"), commit, "local main matches")
        for line in ("Current version:   1.0.13", "Requested version: 1.1.0", "updating to v1.1.0",
                     "Status: SUCCESS", "Release v1.1.0 completed successfully."):
            self.assertIn(line, out)
        for stage in ("Synchronising main", "Validating version", "Checking existing tags", "Updating Directory.Build.props",
                      "Updating other version files", "Validating version references", "Running tests", "Build validation",
                      "Pushing to origin/main", "Verifying remote commit", "Creating tag v1.1.0", "Verifying tagged commit",
                      "Pushing release tag"):
            self.assertRegex(out, rf"(?m)^{re.escape(stage)}\.\.\. +PASS$")
        self.assertLess(out.index("Pushing to origin/main"), out.index("Creating tag v1.1.0"), "commit before tag")
        calls = self.gh_calls()
        self.assertIn(["variable", "set", "DEPLOYMENT_MODE", "--body", "upgrade"], calls)
        self.assertTrue(any(c[:2] == ["run", "watch"] and "4242" in c for c in calls), calls)
        self.assertEqual(self.git(self.work, "status", "--porcelain"), "")
        self.assertFalse((self.work / ".git/swoovr-release.lock").exists(), "lock released")

    def test_accepted_progressions(self):
        for version in ("1.0.14", "1.0.20", "1.1.5", "2.0.0", "2.3.7", "10.0.0"):
            with self.subTest(version=version):
                status, out = self.release(version)
                self.assertEqual(status, 0, out)
                self.assert_released(version)

    def test_numeric_not_lexical_comparison(self):
        self.set_files(self.work, "1.9.99"); self.git(self.work, "commit", "-qam", "1.9.99")
        self.git(self.work, "push", "-q", "origin", "main")
        status, out = self.release("1.10.0")
        self.assertEqual(status, 0, out)
        self.assert_released("1.10.0")
        main, tags = self.origin_main(), self.origin_tags()
        status, out = self.release("1.9.100")
        self.assertNotEqual(status, 0, out)
        self.assertEqual((self.origin_main(), self.origin_tags()), (main, tags))

    def test_committed_version_behind_the_tags(self):
        # Today's repository: Directory.Build.props says 1.0.0 while tags reach v1.0.13 (Build came from tags).
        self.set_files(self.work, "1.0.0"); self.git(self.work, "commit", "-qam", "props behind tags")
        self.git(self.work, "push", "-q", "origin", "main")
        main, tags = self.origin_main(), self.origin_tags()
        for version, reason in (("1.0.13", "v1.0.13 already exists on origin"), ("1.0.5", "not newer than the latest release v1.0.13")):
            status, out = self.release(version)
            self.assertNotEqual(status, 0, out)
            self.assertIn(reason, out)
        self.assertEqual((self.origin_main(), self.origin_tags()), (main, tags))
        status, out = self.release("1.0.14")
        self.assertEqual(status, 0, out)
        self.assert_released("1.0.14")

    def test_rejected_versions_publish_nothing(self):
        main, tags = self.origin_main(), self.origin_tags()
        for version in ("1.0.13", "1.0.12", "0.99.99", "1.0", "1.0.0.0", "01.1.0", "1.01.0", "1.1.00", "-1.0.0",
                        "1.1.0-rc1", "v1.1.0", "1.1.x", "1.1.0 ", " 1.1.0", ""):
            with self.subTest(version=repr(version)):
                status, out = self.release(version)
                self.assertNotEqual(status, 0, out)
                self.assert_nothing_published(main, tags)
                self.assertEqual(self.git(self.work, "status", "--porcelain"), "", "files restored")
        self.assertEqual(self.release("1.0.12")[0], 1)

    def test_existing_remote_tag_on_an_old_commit_is_never_moved(self):
        # The 19 November failure: v1.1.0 pushed on a commit that still said 1.0.
        self.git(self.work, "tag", "-a", "v1.1.0", "-m", "bad"); self.git(self.work, "push", "-q", "origin", "v1.1.0")
        self.git(self.work, "tag", "-d", "v1.1.0")
        bad = self.git(self.origin, "rev-parse", "v1.1.0^{commit}")
        main, tags = self.origin_main(), self.origin_tags()
        status, out = self.release("1.1.0")
        self.assertNotEqual(status, 0, out)
        self.assertIn("v1.1.0 already exists", out)
        self.assertIn("never moved or reused", out)
        self.assert_nothing_published(main, tags)
        self.assertEqual(self.git(self.origin, "rev-parse", "v1.1.0^{commit}"), bad)
        status, out = self.release("1.1.1")
        self.assertEqual(status, 0, out)
        self.assert_released("1.1.1")

    def test_existing_local_only_tag_is_refused(self):
        self.git(self.work, "tag", "-a", "v1.2.0", "-m", "local only")
        main, tags = self.origin_main(), self.origin_tags()
        status, out = self.release("1.2.0")
        self.assertNotEqual(status, 0, out)
        self.assertIn("v1.2.0 already exists", out)
        self.assert_nothing_published(main, tags)

    def test_dirty_tree_and_wrong_branch_are_refused(self):
        main, tags = self.origin_main(), self.origin_tags()
        (self.work / "stray.txt").write_text("x")
        status, out = self.release("1.1.0")
        self.assertNotEqual(status, 0); self.assertIn("not clean", out)
        (self.work / "stray.txt").unlink()
        self.git(self.work, "checkout", "-q", "-b", "feature")
        status, out = self.release("1.1.0")
        self.assertNotEqual(status, 0); self.assertIn("check out main", out)
        self.assert_nothing_published(main, tags)

    def test_outdated_main_is_fast_forwarded_first(self):
        other = self.tmp / "other"
        self.git(self.tmp, "clone", "-q", str(self.origin), str(other)); self.configure(other)
        (other / "feature.txt").write_text("new"); self.git(other, "add", "."); self.git(other, "commit", "-qm", "feature")
        self.git(other, "push", "-q", "origin", "main")
        feature = self.origin_main()
        status, out = self.release("1.0.14")
        self.assertEqual(status, 0, out)
        commit = self.assert_released("1.0.14")
        self.assertEqual(self.git(self.origin, "rev-parse", f"{commit}~1"), feature, "built on the latest origin/main")

    def test_unpushed_local_commits_are_refused(self):
        (self.work / "local.txt").write_text("x"); self.git(self.work, "add", "."); self.git(self.work, "commit", "-qm", "local")
        main, tags = self.origin_main(), self.origin_tags()
        status, out = self.release("1.1.0")
        self.assertNotEqual(status, 0); self.assertIn("not on origin/main", out)
        self.assert_nothing_published(main, tags)

    def test_failed_validation_commits_nothing_and_restores_files(self):
        main, tags = self.origin_main(), self.origin_tags()
        status, out = self.release("1.1.0", MOCK_CHECK_FAIL="1")
        self.assertNotEqual(status, 0, out)
        self.assertIn("error [Build validation]", out)
        self.assertIn("version commit published: no", out)
        self.assert_nothing_published(main, tags)
        self.assertEqual(self.git(self.work, "status", "--porcelain"), "")
        self.assertEqual(self.git(self.work, "rev-parse", "main"), main)

    def test_failed_push_then_retry_pushes_the_same_commit(self):
        hook = self.origin / "hooks/pre-receive"
        self.write(hook, "#!/bin/sh\necho 'refusing (test)' >&2\nexit 1\n", executable=True)
        main, tags = self.origin_main(), self.origin_tags()
        status, out = self.release("1.1.0")
        self.assertNotEqual(status, 0, out)
        self.assertIn("error [Pushing to origin/main]", out)
        self.assertIn("nothing was published", out)
        self.assert_nothing_published(main, tags)
        local = self.git(self.work, "rev-parse", "main")
        hook.unlink()
        status, out = self.release("1.1.0")
        self.assertEqual(status, 0, out)
        self.assertIn("never pushed", out)
        self.assertEqual(self.assert_released("1.1.0"), local, "the original commit, not a second one")

    def test_interrupted_after_commit_push_retry_tags_that_commit(self):
        status, out = self.release("1.1.0", MOCK_GH_VARIABLE_STATUS="1")
        self.assertNotEqual(status, 0, out)
        self.assertIn("version commit published: ", out)
        self.assertIn("release tag published:    no", out)
        self.assertIn("it tags the published commit", out)
        self.assertNotIn("v1.1.0", self.origin_tags())
        version_commit = self.origin_main()
        # Someone merges more work before the retry; the tag must still go on the version commit.
        (self.work / "later.txt").write_text("x"); self.git(self.work, "add", "."); self.git(self.work, "commit", "-qm", "later")
        self.git(self.work, "push", "-q", "origin", "main")
        status, out = self.release("1.1.0")
        self.assertEqual(status, 0, out)
        self.assertIn("already on origin/main", out)
        self.assertEqual(self.assert_released("1.1.0"), version_commit)

    def test_failed_workflow_exits_non_zero_and_retry_is_refused(self):
        status, out = self.release("1.1.0", MOCK_GH_CONCLUSION="failure")
        self.assertNotEqual(status, 0, out)
        self.assertIn("Status: FAILURE", out)
        self.assertIn("release tag published:    yes (v1.1.0)", out)
        self.assertIn("gh run rerun 4242 --failed", out)
        self.assertNotIn("completed successfully", out)
        self.assert_released("1.1.0")
        main, tags = self.origin_main(), self.origin_tags()
        status, out = self.release("1.1.0")
        self.assertNotEqual(status, 0, out)
        self.assertIn("it is released", out)
        self.assertEqual((self.origin_main(), self.origin_tags()), (main, tags))

    def test_missing_workflow_run_is_a_failure(self):
        status, out = self.release("1.1.0", MOCK_GH_NO_RUN="1")
        self.assertNotEqual(status, 0, out)
        self.assertIn("no Deploy UAT run started", out)

    def test_concurrent_release_is_refused(self):
        (self.work / ".git/swoovr-release.lock").mkdir()
        main, tags = self.origin_main(), self.origin_tags()
        status, out = self.release("1.1.0")
        self.assertNotEqual(status, 0); self.assertIn("another release is running", out)
        self.assert_nothing_published(main, tags)
        self.assertTrue((self.work / ".git/swoovr-release.lock").exists(), "someone else's lock is left alone")

    def test_updated_release_tooling_on_main_is_used(self):
        other = self.tmp / "other"
        self.git(self.tmp, "clone", "-q", str(self.origin), str(other)); self.configure(other)
        script = other / "scripts/update-release.sh"
        script.write_text(script.read_text().replace('echo "Release Version Update"', 'echo "Release Version Update (new tooling)"'))
        self.git(other, "commit", "-qam", "tooling"); self.git(other, "push", "-q", "origin", "main")
        status, out = self.release("1.0.14")
        self.assertEqual(status, 0, out)
        self.assertIn("restarting with the new version", out)
        self.assertIn("(new tooling)", out)
        self.assert_released("1.0.14")


if __name__ == "__main__":
    unittest.main()
