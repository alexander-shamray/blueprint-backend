"""The harness the review loop's helper suites share.

The scripts in this directory carry the judgements the /ship loop and both
sweeps rest on, and the harness's grants and refusals are argued in
`docs/harness-boundaries.md`. Each `test_*.py` module is named for its
subject — one helper, a family of helpers, or the harness's deny list — and
each class there names the property it checks.

Negatives are paired with positive controls, because a negative that passes
while a pattern matches nothing is indistinguishable from one that works.

The engine under test is the engine that ships: every pattern assertion shells
out to the same `grep -E` or `jq` the scripts call, because re-implementing it
in Python's `re` would be a second specification. And a declared pattern needs
a test whose subject is where it is applied, so the pattern cases are paired
with structural cases over the call sites.

What the suite needs to run is `docs/testing.md`'s.
"""

import os
import re
import shutil
import subprocess
from pathlib import Path

SCRIPTS = Path(__file__).resolve().parent
REVIEW = SCRIPTS / "grok-review.sh"
LEDGER = SCRIPTS / "grok-ledger.sh"
NEWLINE = chr(10)  # spelled this way so patch scripts cannot mangle it
SETTINGS = SCRIPTS.parent / "settings.json"
COMMANDS = SCRIPTS.parent / "commands"
BASH = shutil.which("bash")
GREP = shutil.which("grep")
GIT = shutil.which("git")
JQ = shutil.which("jq")


def setUpModule():
    # Not a skip: a skip on a missing tool reports a pass, the fail-open ADR-010
    # refuses. Absent any of these, this suite has established nothing and says
    # so.
    missing = [
        name for name, path in
        (("bash", BASH), ("grep", GREP), ("git", GIT), ("jq", JQ))
        if path is None
    ]
    if missing:
        raise RuntimeError(
            f"{', '.join(missing)} required and not on PATH: these tests exercise "
            "the same tools the scripts do, and asserting through Python "
            "equivalents instead would be testing a second specification"
        )


def declared(name):
    """Read one single-quoted pattern out of grok-review.sh by its variable name.

    Read rather than restated, so the test and the script cannot disagree about
    what the pattern is, only about what it should match.
    """
    text = REVIEW.read_text(encoding="utf-8")
    found = re.findall(rf"^{re.escape(name)}='([^']*)'$", text, re.MULTILINE)
    if len(found) != 1:
        raise AssertionError(
            f"expected exactly one declaration of {name} in {REVIEW.name}, "
            f"found {len(found)}"
        )
    return found[0]


def declared_value(name):
    """Read one bare `name=value` assignment out of grok-review.sh."""
    text = REVIEW.read_text(encoding="utf-8")
    found = re.findall(rf"^{re.escape(name)}=(\S+)$", text, re.MULTILINE)
    if len(found) != 1:
        raise AssertionError(
            f"expected exactly one declaration of {name} in {REVIEW.name}, "
            f"found {len(found)}"
        )
    return found[0]


def run_bash(script, subject="", **env_extra):
    """Run a bash fragment with the subject on stdin and everything else in env.

    Nothing is passed as an argument: under MSYS an argv element crossing into
    `bash.exe` is re-parsed, so a pattern containing `"` arrives with its quotes
    eaten and silently matches nothing. Environment variables and stdin are not
    re-parsed, so they mean the same thing on both platforms.
    """
    env = dict(os.environ)
    env.update(env_extra)
    return subprocess.run(
        [BASH, "-c", script],
        input=subject,
        capture_output=True,
        text=True,
        env=env,
    )


def grep_matches(pattern, subject, ignore_case=True):
    """`grep -qE` (optionally -i) on the real engine the scripts call."""
    flag = "-i" if ignore_case else ""
    return run_bash(f'grep -q {flag} -E "$PAT"', subject, PAT=pattern).returncode == 0


def code_lines(text):
    """The executable lines of a shell script — comments and blanks dropped.

    An assertion that a line exists, made against the whole file, passes on a
    comment that mentions it, so deleting the executable line would not fail it.
    """
    return [
        line for line in text.splitlines()
        if line.strip() and not line.lstrip().startswith("#")
    ]
