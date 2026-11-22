"""Q129 / D320, D383: release_version.py, exercised in a throwaway git repository (never this repository's tags)."""
import re
import shutil
import subprocess
import tempfile
import unittest
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
THEME = "containers/keycloak/themes/swoovr/login/theme.properties"


class ReleaseVersion(unittest.TestCase):
    def setUp(self):
        self.dir = Path(tempfile.mkdtemp())
        (self.dir / "scripts").mkdir()
        shutil.copy(REPO / "scripts/release_version.py", self.dir / "scripts/release_version.py")
        shutil.copy(REPO / "Directory.Build.props", self.dir / "Directory.Build.props")
        (self.dir / THEME).parent.mkdir(parents=True)
        shutil.copy(REPO / THEME, self.dir / THEME)
        self.run_script("set", "1.0.13")      # a fixed baseline, whatever this repository is on
        for args in (["init", "-q"], ["config", "user.email", "fictional@example.test"], ["config", "user.name", "Fictional"],
                     ["config", "commit.gpgsign", "false"], ["config", "tag.gpgsign", "false"], ["add", "."], ["commit", "-qm", "init"]):
            subprocess.run(["git", "-C", str(self.dir), *args], check=True, capture_output=True)

    def tearDown(self):
        shutil.rmtree(self.dir)

    def run_script(self, *args, ok=True):
        result = subprocess.run(["python3", str(self.dir / "scripts/release_version.py"), *args], capture_output=True, text=True)
        if ok: self.assertEqual(result.returncode, 0, result.stderr)
        return result.stdout.strip(), result

    def refused(self, *args):
        return self.run_script(*args, ok=False)[1].returncode != 0

    def tag(self, name):
        subprocess.run(["git", "-C", str(self.dir), "tag", "-a", name, "-m", name], check=True, capture_output=True)

    def commit(self):
        subprocess.run(["git", "-C", str(self.dir), "commit", "-qam", "change", "--allow-empty"], check=True, capture_output=True)

    def test_set_writes_the_full_version_to_every_version_file_only(self):
        before = (self.dir / "Directory.Build.props").read_text()
        self.assertEqual(self.run_script("set", "2.3.7")[0], "2.3.7")
        props = (self.dir / "Directory.Build.props").read_text()
        self.assertIn("<SwoovrVersionMajor>2</SwoovrVersionMajor>", props)
        self.assertIn("<SwoovrVersionMinor>3</SwoovrVersionMinor>", props)
        self.assertIn("== ''\">7</SwoovrVersionBuild>", props)
        self.assertEqual(re.findall(r"<SwoovrNativeBuild[^>]*>\d+<", props), re.findall(r"<SwoovrNativeBuild[^>]*>\d+<", before))
        self.assertIn("\nswoovrVersion=2.3.7\n", "\n" + (self.dir / THEME).read_text())
        self.assertEqual(self.run_script("show")[0], "2.3.7")
        self.assertEqual(self.run_script("verify", "2.3.7")[0], "2.3.7")
        self.assertEqual(self.run_script("show", "HEAD")[0], "1.0.13", "show REV reads the committed file")
        self.assertTrue(self.refused("verify", "2.3.7", "HEAD"))

    def test_verify_catches_drift_between_version_files(self):
        theme = self.dir / THEME
        theme.write_text(theme.read_text().replace("swoovrVersion=1.0.13", "swoovrVersion=1.0.12"))
        self.assertTrue(self.refused("verify", "1.0.13"))

    def test_progression(self):
        self.tag("v1.0.13")
        for good in ("1.0.14", "1.0.20", "1.1.0", "1.1.5", "2.0.0", "2.3.7"):
            self.assertEqual(self.run_script("check-new", good)[0], good)
        for bad in ("1.0.13", "1.0.12", "0.99.99", "1.0.0"):
            self.assertTrue(self.refused("check-new", bad), bad)

    def test_numeric_comparison(self):
        self.run_script("set", "1.9.99"); self.commit()
        self.assertEqual(self.run_script("check-new", "1.10.0")[0], "1.10.0")
        self.assertTrue(self.refused("check-new", "1.9.98"))
        self.tag("v1.10.0")
        self.assertTrue(self.refused("check-new", "1.9.100"), "1.9.100 < 1.10.0")
        self.assertTrue(self.refused("check-new", "1.2.0", "v1.10.0"))

    def test_malformed_versions(self):
        for bad in ("", "1.0", "1.0.0.0", "01.0.0", "1.00.0", "1.0.01", "-1.0.0", "1.0.0-rc1", "v1.1.0", "1.1.x", "1.1.0 ", "1.1.0+b"):
            self.assertTrue(self.refused("check-new", bad), repr(bad))
            self.assertTrue(self.refused("set", bad), repr(bad))
        self.assertEqual(self.run_script("show")[0], "1.0.13", "a refused set changes nothing")

    def test_tags_from_the_remote_count(self):
        self.assertTrue(self.refused("check-new", "1.1.0", "v1.1.0"), "an existing (remote) tag")
        self.assertTrue(self.refused("check-new", "1.0.20", "v1.1.0"), "older than a remote tag")
        self.assertEqual(self.run_script("check-new", "1.1.1", "v1.1.0", "not-a-tag", "v1.0.0-rc1")[0], "1.1.1")

    def test_next_and_for_head(self):
        self.assertEqual(self.run_script("next")[0], "1.0.14")
        self.tag("v1.1.0")
        self.assertEqual(self.run_script("next")[0], "1.1.1", "highest of committed version and tags")
        self.assertEqual(self.run_script("for-head")[0], "1.1.0", "a tagged commit reports its tag")
        self.commit()
        self.assertEqual(self.run_script("for-head")[0], "1.0.13", "an untagged build reports the committed version")

    def test_next_minor_bumps_minor_and_resets_build(self):
        self.assertEqual(self.run_script("next-minor")[0], "1.1.0")
        self.tag("v1.1.2")
        self.assertEqual(self.run_script("next-minor")[0], "1.2.0", "from the highest of committed version and tags")
        self.tag("v1.9.4")
        self.assertEqual(self.run_script("next-minor")[0], "1.10.0", "numeric, not lexical")

    def test_msbuild_args_require_the_exact_committed_version(self):
        self.assertIn("-p:SwoovrVersionBuild=13 ", self.run_script("msbuild-args", "1.0.13")[0])
        for bad in ("1.0.14", "1.1.13", "2.0.13"):
            self.assertTrue(self.refused("msbuild-args", bad), f"{bad}: the deploy workflow must refuse a tag that differs")


if __name__ == "__main__":
    unittest.main()
