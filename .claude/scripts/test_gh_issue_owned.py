"""gh-issue-owned.sh: what /dispatch may launch, decided in code, with `gh` stubbed and the script's own `jq` run."""

import json
import os
import shutil
import subprocess
import tempfile
import textwrap
import unittest
from pathlib import Path

from review_helpers import BASH, SCRIPTS, setUpModule  # noqa: F401

OWNED = SCRIPTS / "gh-issue-owned.sh"


class OwnedIssues(unittest.TestCase):
    def run_it(self, issues=(), *args, owner="ada", owner_code=0, list_code=0):
        stub = Path(tempfile.mkdtemp(prefix="owned-stub-"))
        self.addCleanup(shutil.rmtree, stub, ignore_errors=True)
        (stub / "issues.json").write_text(json.dumps(list(issues)), encoding="utf-8")
        gh = stub / "gh"
        gh.write_text(textwrap.dedent(f"""\
            #!/usr/bin/env bash
            if [ "$1" = repo ]; then echo {owner!r}; exit {owner_code}; fi
            if [ "$1 $2" = "issue list" ]; then cat {(stub / 'issues.json').as_posix()!r}; exit {list_code}; fi
            echo "unexpected: $*" >&2; exit 99
            """), encoding="utf-8")
        gh.chmod(0o755)
        env = {**os.environ, "PATH": f"{stub}{os.pathsep}{os.environ['PATH']}"}
        return subprocess.run([BASH, str(OWNED), *args], capture_output=True, text=True, encoding="utf-8", env=env)

    def test_only_the_owners_numbers_come_back(self):
        issues = [{"number": 1, "author": {"login": "ada"}}, {"number": 2, "author": {"login": "mallory"}},
                  {"number": 3, "author": {"login": "app/github-actions", "is_bot": True}},
                  {"number": 4, "author": None}, {"number": 5, "author": {"login": "ada"}}]
        done = self.run_it(issues)
        self.assertEqual(0, done.returncode, done.stderr)
        self.assertEqual([1, 5], json.loads(done.stdout))
        self.assertNotIn("mallory", done.stdout)

    def test_no_owners_issue_is_an_empty_list_not_a_failure(self):
        self.assertEqual("[]\n", self.run_it([{"number": 2, "author": {"login": "mallory"}}]).stdout)

    def test_what_cannot_be_read_never_reads_as_a_list(self):
        for kwargs in ({"owner_code": 1}, {"owner": ""}, {"list_code": 1}):
            with self.subTest(**kwargs):
                done = self.run_it([{"number": 1, "author": {"login": "ada"}}], **kwargs)
                self.assertNotEqual(0, done.returncode)
                self.assertEqual("", done.stdout)

    def test_a_list_as_long_as_the_limit_is_refused(self):
        done = self.run_it([{"number": n + 1, "author": {"login": "ada"}} for n in range(5000)])
        self.assertEqual(3, done.returncode)
        self.assertIn("limit", done.stderr)

    def test_a_jq_that_does_not_finish_is_no_owned_list(self):
        # Git Bash reported a tool fed by a process substitution as `Child exited`: a failed count and
        # a failed filter each end the run with nothing printed, never an empty list `/dispatch` would read as
        # "no owned issue". The stand-in dies on the count, then on the filter only.
        for dies_on, expect in (("length", "count"), ("-c", "filter")):
            with self.subTest(dies_on=dies_on):
                stub = Path(tempfile.mkdtemp(prefix="owned-stub-"))
                self.addCleanup(shutil.rmtree, stub, ignore_errors=True)
                (stub / "gh").write_text(textwrap.dedent("""\
                    #!/usr/bin/env bash
                    if [ "$1" = repo ]; then echo ada; exit 0; fi
                    echo '[{"number":1,"author":{"login":"ada"}}]'
                    """), encoding="utf-8", newline="\n")
                (stub / "jq").write_text(textwrap.dedent(f"""\
                    #!/usr/bin/env bash
                    case "$1" in {dies_on}) echo "Child exited" >&2; exit 148 ;; esac
                    PATH=${{PATH#*:}}
                    exec jq "$@"
                    """), encoding="utf-8", newline="\n")
                for name in ("gh", "jq"):
                    (stub / name).chmod(0o755)
                env = {**os.environ, "PATH": f"{stub}{os.pathsep}{os.environ['PATH']}"}
                done = subprocess.run([BASH, str(OWNED)], capture_output=True, text=True, encoding="utf-8", env=env)
                self.assertNotEqual(0, done.returncode)
                self.assertIn("Child exited", done.stderr, "the stand-in jq was not the one the helper ran")
                self.assertEqual("", done.stdout)

    def test_it_takes_no_argument(self):
        self.assertEqual(2, self.run_it((), "--repo").returncode)


if __name__ == "__main__":
    unittest.main()
