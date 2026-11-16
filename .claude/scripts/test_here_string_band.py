"""No here-string in the harness's shell scripts: Git Bash 5.2 hangs on one of about 64 KiB (docs/harness/pitfalls.md).

The walk is the case, so a script added later with `cmd <<<"$list"` fails it. The list helpers also run on a stub `gh`
printing 65,541 bytes, the size that hung; one size pins only that band, and the walk covers the rest.
"""

import json
import os
import re
import shutil
import subprocess
import tempfile
import textwrap
import unittest
from pathlib import Path

from dispatch_board import run_bounded
from review_helpers import BASH, HOOKS, SCRIPTS, setUpModule  # noqa: F401

# The scripts that held a here-string when the rule was set, so a walk that stops reaching them fails.
REWRITTEN = ("gh-issue-create.sh", "gh-issue-harness.sh", "gh-issue-list.sh", "gh-issue-owned.sh",
             "gh-label-ensure.sh", "gh-pr-merge.sh", "git-branch-drop.sh", "git-rebase-onto-main.sh",
             "git-trailer-pr.sh", "git-worktree-detach.sh", "git-worktree-fork.sh", "git-worktree-remove.sh",
             "main-red-open.sh", "pr-for-branch.sh", "pr-locality.sh", "sweep-slices.sh")
# A skill's bash, which no `.sh` glob under scripts or hooks reaches: `cbx` by its shebang, `gate.sh` by its name.
SKILL_SCRIPTS = ("cbx", "gate.sh")
# `<<<` outside a comment; `<<` and `<<-` are heredocs, which bash writes to a file once they pass a pipe's size.
HERE_STRING = re.compile(r"^(?!\s*#).*<<<")
# The byte count of the list `gh-issue-list.sh` read the night it hung.
HUNG_AT = 65_541
LIMIT = 60


def is_bash(path):
    if path.suffix == ".sh":
        return True
    with path.open("rb") as handle:
        first = handle.readline()
    return first.startswith(b"#!") and b"sh" in first.split(b"/")[-1]


def shell_scripts():
    skills = (path for path in SCRIPTS.parent.glob("skills/*/scripts/*") if path.is_file() and is_bash(path))
    return sorted([*(path for root in (SCRIPTS, HOOKS) for path in root.glob("*.sh")), *skills])


def here_strings(text):
    return [n for n, line in enumerate(text.splitlines(), 1) if HERE_STRING.match(line)]


def issue_list(size):
    """A JSON list of issues exactly `size` bytes long, as `gh issue list --json number,title,state,labels` prints."""
    empty = json.dumps([{"number": 1, "title": "", "state": "OPEN", "labels": []}], separators=(",", ":"))
    return empty.replace('"title":""', '"title":"' + "x" * (size - len(empty)) + '"')


class NoHereString(unittest.TestCase):
    def test_no_shell_script_feeds_a_here_string(self):
        offenders = {path.name: lines for path in shell_scripts()
                     if (lines := here_strings(path.read_text(encoding="utf-8")))}
        self.assertEqual({}, offenders, "feed it as docs/harness/pitfalls.md's two entries on Git Bash input say")

    def test_the_walk_reaches_every_rewritten_script(self):
        reached = {path.name for path in shell_scripts()}
        self.assertEqual(set(), set(REWRITTEN + SKILL_SCRIPTS) - reached)

    def test_the_pattern_catches_a_here_string_and_passes_a_heredoc(self):
        self.assertEqual([2, 3], here_strings('x=1\njq length <<<"$list"\ndone <<< "$rows"\n'))
        self.assertEqual([], here_strings("# cmd <<<\"$x\" hangs\ncat <<'EOF'\nbody\nEOF\ncat <<-EOF\nEOF\n"))


# Each `< <(` outside a comment is judged by the command segment in front of it, the text after the last `;`, `|`,
# `&`, `(`, `do` or `then`: the shell reads it when that segment is the builtin `read` (after any assignments) or
# the `done` of a loop.
PROCESS_SUBSTITUTION = re.compile(r"<\s*<\(")
SEGMENT_BREAK = re.compile(r"[;|&(]|\bdo\b|\bthen\b")
SHELL_READS = re.compile(r"^\s*(\w+=\S*\s+)*(read(\s|$)|done\s*$)")


def tools_fed_by_process_substitution(text):
    found = []
    for n, line in enumerate(text.splitlines(), 1):
        if line.lstrip().startswith("#"):
            continue
        for match in PROCESS_SUBSTITUTION.finditer(line):
            if not SHELL_READS.match(SEGMENT_BREAK.split(line[:match.start()])[-1]):
                found.append(n)
                break
    return found


class NoToolFedByProcessSubstitution(unittest.TestCase):
    """Git Bash reported such a tool as `Child exited` on a loaded Windows runner.

    Only the shell's own `read` may take the form. The cases beside each script prove what a dead tool does there; this
    walk is the one that proves a site was moved, and covers a script added later.
    """

    def test_no_shell_script_feeds_a_tool_by_process_substitution(self):
        offenders = {path.name: lines for path in shell_scripts()
                     if (lines := tools_fed_by_process_substitution(path.read_text(encoding="utf-8")))}
        self.assertEqual({}, offenders, "judge it in bash or pipe it, as docs/harness/pitfalls.md's entry says")

    def test_the_pattern_catches_a_tool_and_passes_the_shells_own_read(self):
        self.assertEqual([1, 3, 4, 5, 6], tools_fed_by_process_substitution(
            'count=$(jq length < <(printf x) | tr -d y)\ndone < <(printf x)\n  grep -q a < <(printf x)\n'
            'read -r a < <(x); jq . < <(y)\nwhile read -r l; do grep -c . < <(printf "$l"); done\n'
            'read-config < <(printf x)\n'))
        self.assertEqual([], tools_fed_by_process_substitution(
            'IFS=, read -r -a a < <(printf x)\n  done < <(printf "$m")\n# jq length < <(printf x)\n'))


class TheBand(unittest.TestCase):
    """The list helpers return on a list of the size that hung, under a bound that ends the whole tree."""

    def run_with_list(self, script, issues_json):
        stub = Path(tempfile.mkdtemp(prefix="band-stub-"))
        self.addCleanup(shutil.rmtree, stub, ignore_errors=True)
        (stub / "issues.json").write_bytes(issues_json.encode("utf-8"))
        gh = stub / "gh"
        with open(gh, "w", encoding="utf-8", newline="\n") as handle:
            handle.write(textwrap.dedent(f"""\
                #!/usr/bin/env bash
                if [ "$1" = repo ]; then echo ada; exit 0; fi
                if [ "$1 $2" = "issue list" ]; then cat {(stub / 'issues.json').as_posix()!r}; exit 0; fi
                echo "unexpected: $*" >&2; exit 99
                """))
        gh.chmod(0o755)
        env = {**os.environ, "PATH": f"{stub}{os.pathsep}{os.environ['PATH']}"}
        try:
            return run_bounded([BASH, str(SCRIPTS / script)], SCRIPTS, LIMIT, env=env)
        except subprocess.TimeoutExpired:
            self.fail(f"{script} did not return within {LIMIT} s on a {len(issues_json)}-byte list")

    def test_the_fixture_is_the_size_that_hung(self):
        self.assertEqual(HUNG_AT, len(issue_list(HUNG_AT).encode("utf-8")))

    def test_gh_issue_list_returns_on_the_size_that_hung(self):
        listed = issue_list(HUNG_AT)
        code, out, err = self.run_with_list("gh-issue-list.sh", listed)
        self.assertEqual(0, code, err.decode("utf-8", errors="replace"))
        self.assertEqual(json.loads(listed), json.loads(out))

    def test_gh_issue_owned_returns_on_the_size_that_hung(self):
        owned = json.dumps([{"number": 1, "author": {"login": "ada"}}], separators=(",", ":"))
        owned = owned.replace('"login":"ada"', '"login":"ada","name":"' + "x" * (HUNG_AT - len(owned) - 10) + '"')
        self.assertEqual(HUNG_AT, len(owned))
        code, out, err = self.run_with_list("gh-issue-owned.sh", owned)
        self.assertEqual(0, code, err.decode("utf-8", errors="replace"))
        self.assertEqual([1], json.loads(out))


if __name__ == "__main__":
    unittest.main()
