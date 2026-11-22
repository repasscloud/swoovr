#!/usr/bin/env python3
"""Q129 / D320, D383: Swoovr product version releases (Major.Minor.Build).

Directory.Build.props holds the full committed version X.Y.Z (D383, owner 20 November 2026): every release is a commit
"updating to vX.Y.Z" that sets it, tagged vX.Y.Z. The Keycloak theme's swoovrVersion mirrors it. Run a release with
./scripts/update-release.sh X.Y.Z; these commands are its building blocks:

  show [REV]           the committed version (at REV, default the working tree)
  next                 a suggestion: the highest of the committed version and every local vX.Y.Z tag, Build + 1
  next-minor           the same highest version with Minor + 1 and Build reset to 0 (scripts/refresh-uat.sh)
  check-new X.Y.Z [vA.B.C ...]
                       refuse unless X.Y.Z is well formed, newer than the committed version and newer than every
                       given/local release tag (numeric comparison: 1.10.0 > 1.9.99)
  set X.Y.Z            write X.Y.Z into Directory.Build.props and the Keycloak theme (nothing else changes)
  verify X.Y.Z [REV]   every version file (at REV, default the working tree) holds exactly X.Y.Z
  for-head             the version HEAD was released as (its vX.Y.Z tag), else the committed version
  msbuild-args [X.Y.Z] MSBuild properties for that version (default: for-head) plus the commit for diagnostics; refuses
                       a version that is not exactly the committed one (the deploy workflow's tag check)
"""
import re
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
PROPS = "Directory.Build.props"
THEME = "containers/keycloak/themes/swoovr/login/theme.properties"
VERSION = re.compile(r"(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)")
TAG = re.compile(r"v(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)")


def git(*args, check=True):
    return subprocess.run(["git", "-C", str(ROOT), *args], check=check, capture_output=True, text=True).stdout.strip()


def read(path, rev=None):
    if rev is None:
        return (ROOT / path).read_text()
    result = subprocess.run(["git", "-C", str(ROOT), "show", f"{rev}:{path}"], capture_output=True, text=True)
    if result.returncode != 0:
        sys.exit(f"cannot read {path} at {rev}")
    return result.stdout


def parse(value):
    match = VERSION.fullmatch(value or "")
    if not match:
        sys.exit(f"'{value}' is not X.Y.Z (digits only, no leading zeros, no prefix or suffix)")
    return tuple(int(part) for part in match.groups())


def text(version):
    return ".".join(str(part) for part in version)


def props(rev=None):
    source = read(PROPS, rev)
    value = lambda name: re.search(rf"<{name}(?: [^>]*)?>(\d+)</{name}>", source)
    parts = [value(name) for name in ("SwoovrVersionMajor", "SwoovrVersionMinor", "SwoovrVersionBuild")]
    if not all(parts):
        sys.exit(f"{PROPS} must define SwoovrVersionMajor, SwoovrVersionMinor and SwoovrVersionBuild")
    return tuple(int(part.group(1)) for part in parts)


def tags(extra=()):
    found = []
    for tag in [*git("tag", "--list", "v*").splitlines(), *extra]:
        match = TAG.fullmatch(tag.strip())
        if match:
            found.append(tuple(int(part) for part in match.groups()))
    return found


def check_new(value, extra):
    wanted, committed = parse(value), props()
    if wanted <= committed:
        sys.exit(f"{value} is not newer than the committed version {text(committed)}")
    released = tags(extra)
    if wanted in released:
        sys.exit(f"v{value} already exists")
    if released and wanted <= max(released):
        sys.exit(f"{value} is not newer than the latest release v{text(max(released))}")
    return value


def set_version(value):
    major, minor, build = parse(value)
    source = (ROOT / PROPS).read_text()
    for name, number in (("SwoovrVersionMajor", major), ("SwoovrVersionMinor", minor)):
        source, count = re.subn(rf"<{name}>\d+</{name}>", f"<{name}>{number}</{name}>", source)
        if count != 1:
            sys.exit(f"{PROPS} must contain exactly one <{name}>")
    source, count = re.subn(r"(<SwoovrVersionBuild Condition=\"[^\"]*\">)\d+(</SwoovrVersionBuild>)", rf"\g<1>{build}\g<2>", source)
    if count != 1:
        sys.exit(f"{PROPS} must contain exactly one conditional <SwoovrVersionBuild>")
    (ROOT / PROPS).write_text(source)
    theme, count = re.subn(r"(?m)^swoovrVersion=.*$", f"swoovrVersion={value}", (ROOT / THEME).read_text())
    if count != 1:
        sys.exit(f"{THEME} must contain exactly one swoovrVersion line")
    (ROOT / THEME).write_text(theme)
    return value


def verify(value, rev=None):
    wanted, problems = parse(value), []
    if props(rev) != wanted:
        problems.append(f"{PROPS} holds {text(props(rev))}")
    if not re.search(rf"(?m)^swoovrVersion={re.escape(value)}$", read(THEME, rev)):
        problems.append(f"{THEME} swoovrVersion is not {value}")
    if problems:
        sys.exit(f"version files {'at ' + rev if rev else ''} do not all hold {value}: " + "; ".join(problems))
    return value


def for_head():
    for tag in git("tag", "--points-at", "HEAD").splitlines():
        if TAG.fullmatch(tag.strip()):
            return tag.strip()[1:]
    return text(props())


def msbuild_args(value):
    if parse(value) != props():
        sys.exit(f"{value} does not match Directory.Build.props {text(props())}")
    sha = git("rev-parse", "HEAD", check=False) or "unknown"
    return f"-p:SwoovrVersionBuild={parse(value)[2]} -p:SourceRevisionId={sha}"


def main(argv):
    command, args = (argv[1] if len(argv) > 1 else "show"), argv[2:]
    if command == "show" and len(args) <= 1: print(text(props(*args)))
    elif command == "next" and not args:
        highest = max([props(), *tags()])
        print(text((highest[0], highest[1], highest[2] + 1)))
    elif command == "next-minor" and not args:
        highest = max([props(), *tags()])
        print(text((highest[0], highest[1] + 1, 0)))
    elif command == "check-new" and args: print(check_new(args[0], args[1:]))
    elif command == "set" and len(args) == 1: print(set_version(args[0]))
    elif command == "verify" and 1 <= len(args) <= 2: print(verify(*args))
    elif command == "for-head" and not args: print(for_head())
    elif command == "msbuild-args" and len(args) <= 1: print(msbuild_args(args[0] if args else for_head()))
    else: sys.exit(__doc__)


if __name__ == "__main__":
    main(sys.argv)
