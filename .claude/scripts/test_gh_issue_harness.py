"""gh-issue-harness.sh: the issue /ship's sync row files in the harness, with no free parameter.

A refusing `gh` sits first on PATH for every case, so a check that regressed
cannot have filed a real issue; `review_helpers.py` is the shared harness.
"""

import os
import re
import shutil
import subprocess
import tempfile
import textwrap
import unittest
from pathlib import Path

from review_helpers import (
    SCRIPTS,
    BASH,
    setUpModule,
)

HARNESS_REPO = "alexander-shamray/blueprint-harness"


class HarnessIssueHelperHasNoFreeParameter(unittest.TestCase):
    HELPER = SCRIPTS / "gh-issue-harness.sh"

    HAND_TRAILER = "Filed by hand rather than by a sweep: no second auditor verified it at filing."
    SWEEP_TRAILER = "Filed by an authorised sweep and verified at filing by a second read-only auditor."
    REQUEST_TRAILER = "Filed by hand as a requested change, not a finding: nothing was verified at filing."

    def setUp(self):
        self.dir = tempfile.mkdtemp(prefix="issue-stub-")
        d = Path(self.dir)
        gh = d / "gh"
        gh.write_text(
            textwrap.dedent(
                f"""\
                #!/usr/bin/env bash
                printf '%s\\n' "$*" >> {(d / 'argv').as_posix()!r}
                case "$*" in
                  *"issue list"*)
                    [ ! -e {(d / 'listfail').as_posix()!r} ] || {{ echo "stub gh: list failed" >&2; exit 1; }}
                    cat {(d / 'list').as_posix()!r} 2>/dev/null
                    exit 0
                    ;;
                  *"issue comment"*)
                    cat > {(d / 'comment').as_posix()!r}
                    exit 0
                    ;;
                  *"issue create"*)
                    printf '%s\\n' "${{MSYS2_ARG_CONV_EXCL-unset}}" > {(d / 'conv').as_posix()!r}
                    cat > {(d / 'body').as_posix()!r}
                    exit 0
                    ;;
                esac
                echo "stub gh: unexpected call: $*" >&2
                exit 99
                """
            ),
            encoding="utf-8",
        )
        gh.chmod(0o755)

    def tearDown(self):
        shutil.rmtree(self.dir, ignore_errors=True)

    def run_helper(self, *args, body="", timeout_program=None):
        # `timeout_program` names the program gh-read-bound.sh's `bounded` looks for, so one that is not there takes
        # the watchdog a host without GNU `timeout` runs, as macOS does.
        env = dict(os.environ)
        env["PATH"] = self.dir + os.pathsep + env["PATH"]
        if timeout_program is not None:
            env["GH_READ_TIMEOUT"] = timeout_program
        return subprocess.run([BASH, str(self.HELPER), *args], capture_output=True, text=True,
                              encoding="utf-8", input=body, env=env)

    def calls(self):
        f = Path(self.dir) / "argv"
        return f.read_text(encoding="utf-8").splitlines() if f.exists() else []

    def creates(self):
        return [c for c in self.calls() if c.startswith("issue create ")]

    def open_issues(self, *rows):
        """The open issues the stub's `issue list` answers with, one `number<TAB>title` per row."""
        Path(self.dir, "list").write_text("".join(f"{n}\t{title}\n" for n, title in rows), encoding="utf-8")

    def stdin(self, trailer=None, title="a title"):
        return f"{title}\n\nthe body\n\n{trailer or self.HAND_TRAILER}\n"

    def refused(self, result, text):
        # Exit 2 before `gh` is reached, naming the reason: the empty argv file is the proof of the order.
        self.assertEqual(2, result.returncode, result.stderr)
        self.assertEqual([], self.calls())
        self.assertIn(text, result.stderr)

    def test_a_valid_filing_reaches_gh_with_every_parameter_pinned(self):
        title = "`/ship` files a title that begins with a slash"
        result = self.run_helper("bug", "high", "harness", body=self.stdin(title=title))
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(1, len(self.creates()), self.calls())
        create = self.creates()[0]
        self.assertTrue(create.startswith(f"issue create --repo {HARNESS_REPO} "), create)
        self.assertIn(f"--title {title}", create)
        for label in ("bug", "high", "harness"):
            self.assertIn(f"--label {label}", create)
        self.assertIn("--body-file -", create)
        self.assertNotIn("--force", create)
        d = Path(self.dir)
        self.assertEqual(f"the body\n\n{self.HAND_TRAILER}\n", (d / "body").read_text(encoding="utf-8"))
        self.assertEqual("*", (d / "conv").read_text(encoding="utf-8").strip())

    def test_no_call_resolves_or_creates_anything_in_the_checkout(self):
        # The harness is not the checkout's repository: no `repo view`, and no label sibling that would act there.
        self.run_helper("test-gap", "low", "harness,test", body=self.stdin())
        self.assertEqual(["issue list", "issue create"], [" ".join(c.split()[:2]) for c in self.calls()])
        create = self.creates()[0]
        self.assertIn("--label harness --label test", create)

    def test_the_argument_count_is_exactly_three(self):
        for args in ((), ("bug",), ("bug", "low"), ("bug", "low", "harness", "hand"),
                     ("a title", "bug", "low", "harness")):
            with self.subTest(args=args):
                self.refused(self.run_helper(*args, body=self.stdin()), "usage: gh-issue-harness.sh")

    def test_a_kind_outside_the_three_is_refused(self):
        for kind in ("security", "documentation", "Bug", "bugs", "bug --force", "-R other/repo", ""):
            with self.subTest(kind=kind):
                self.refused(self.run_helper(kind, "high", "harness", body=self.stdin()), "not a kind")

    def test_a_severity_outside_the_four_is_refused(self):
        for severity in ("info", "High", "high --force", "-R other/repo", ""):
            with self.subTest(severity=severity):
                self.refused(self.run_helper("bug", severity, "harness", body=self.stdin()), "not a severity")

    def test_an_area_outside_the_two_is_refused(self):
        for area in ("test", "harness,code", "code,harness", "Harness", "harness,", "documentation,test", "code,documentation",
                     "code,test,test", "test,code",
                     "harness,documentation", "documentation,",
                     ",test", "harness,test,test", "harness test", "harness,--repo", "--label bug", ""):
            with self.subTest(area=area):
                self.refused(self.run_helper("bug", "low", area, body=self.stdin()), "not an area")

    def test_each_area_the_tree_map_gives_is_filed_under_its_own_label(self):
        # working-rules: `.claude/` is harness, a suite beside it harness,test, other prose documentation.
        for area, labels in (("harness", ["harness"]), ("harness,test", ["harness", "test"]),
                             ("documentation", ["documentation"]), ("code", ["code"]), ("code,test", ["code", "test"])):
            with self.subTest(area=area):
                Path(self.dir, "argv").unlink(missing_ok=True)
                result = self.run_helper("bug", "low", area, body=self.stdin())
                self.assertEqual(0, result.returncode, result.stderr)
                for label in labels:
                    self.assertIn(f"--label {label}", self.creates()[0])
                self.assertEqual(len(labels) + 2, self.creates()[0].count("--label "), self.creates()[0])

    def test_an_open_issue_of_that_exact_title_gets_a_comment_and_no_second_issue(self):
        # A re-take of a sync meets the issue its first round filed: the one low issue is added to, not repeated.
        self.open_issues((41, "another title"), (42, "a title"))
        result = self.run_helper("bug", "low", "harness", body=self.stdin())
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual([], self.creates(), self.calls())
        comment = [c for c in self.calls() if c.startswith("issue comment ")]
        self.assertEqual([f"issue comment 42 --repo {HARNESS_REPO} --body-file -"], comment)
        self.assertEqual(f"the body\n\n{self.HAND_TRAILER}\n", (Path(self.dir) / "comment").read_text(encoding="utf-8"))

    def test_a_near_title_is_not_a_match(self):
        self.open_issues((41, "A title"), (42, "a title "), (43, "a title and more"), (44, "x a title"))
        self.assertEqual(0, self.run_helper("bug", "low", "harness", body=self.stdin()).returncode)
        self.assertEqual(1, len(self.creates()), self.calls())
        self.assertEqual([], [c for c in self.calls() if c.startswith("issue comment ")])

    def test_the_search_reads_open_issues_of_the_harness_only(self):
        self.run_helper("bug", "low", "harness", body=self.stdin())
        listed = [c for c in self.calls() if c.startswith("issue list ")]
        self.assertEqual(1, len(listed), self.calls())
        self.assertIn(f"--repo {HARNESS_REPO}", listed[0])
        self.assertIn("--state open", listed[0])

    def test_a_scoped_title_is_matched_by_a_list_that_carries_no_query(self):
        # GitHub reads `chore(harness):` in a search as a qualifier and finds nothing, so the helper
        # sends no title at all and compares the whole open list; the stub cannot tell a query from none.
        title = "Low findings left by #140: chore(harness): sync to blueprint-harness 2dfe03d"
        self.open_issues((141, title), (150, "chore(harness): sync to blueprint-harness 2dfe03d"))
        result = self.run_helper("bug", "low", "harness", body=self.stdin(title=title))
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual([], self.creates(), self.calls())
        self.assertEqual([f"issue comment 141 --repo {HARNESS_REPO} --body-file -"],
                         [c for c in self.calls() if c.startswith("issue comment ")])
        listed = [c for c in self.calls() if c.startswith("issue list ")]
        self.assertEqual(1, len(listed), self.calls())
        self.assertNotIn("--search", listed[0])
        self.assertNotIn("chore", listed[0])

    def test_a_list_that_fills_the_limit_files_nothing(self):
        # The match may be the row past the limit, and filing would then repeat it.
        self.open_issues(*[(n, f"title {n}") for n in range(1, 1001)])
        result = self.run_helper("bug", "low", "harness", body=self.stdin())
        self.assertEqual(3, result.returncode, result.stderr)
        self.assertIn("so the list may hide a match", result.stderr)
        self.assertEqual([], self.creates())

    def test_a_search_that_fails_files_nothing(self):
        # Filing on a failed search would repeat the issue a match would have taken the comment.
        Path(self.dir, "listfail").write_text("", encoding="utf-8")
        result = self.run_helper("bug", "low", "harness", body=self.stdin())
        self.assertEqual(3, result.returncode, result.stderr)
        self.assertIn("cannot search the harness's open issues", result.stderr)
        self.assertEqual([], self.creates())

    def test_every_gh_call_is_under_a_timeout(self):
        text = self.HELPER.read_text(encoding="utf-8")
        code = [ln.strip() for ln in text.splitlines() if not ln.lstrip().startswith("#")]
        calls = [ln for ln in code if re.search(r"(^|[^\w-])gh issue ", ln)]
        self.assertEqual(3, len(calls), "the search, the comment and the filing each call gh")
        for ln in calls:
            self.assertRegex(ln, r"bounded [0-9]+ gh ", ln)
        self.assertIn('. "$(dirname "${BASH_SOURCE[0]}")/gh-read-bound.sh"', code)

    def test_the_watchdog_hands_gh_the_body_and_the_conversion_switch(self):
        # Where no `timeout` is installed, as on macOS, every call runs under the watchdog, which backgrounds gh: a
        # background command is handed /dev/null unless its stdin is named, and the filing and the comment both
        # read the body there.
        result = self.run_helper("bug", "high", "harness", body=self.stdin(), timeout_program="no-such-timeout")
        self.assertEqual(0, result.returncode, result.stderr)
        d = Path(self.dir)
        self.assertEqual(f"the body\n\n{self.HAND_TRAILER}\n", (d / "body").read_text(encoding="utf-8"))
        self.assertEqual("*", (d / "conv").read_text(encoding="utf-8").strip())
        self.open_issues((41, "a title"))
        result = self.run_helper("bug", "high", "harness", body=self.stdin(), timeout_program="no-such-timeout")
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(f"the body\n\n{self.HAND_TRAILER}\n", (d / "comment").read_text(encoding="utf-8"))

    def test_the_trailer_is_the_hand_routes_and_no_other(self):
        for trailer in (self.SWEEP_TRAILER, self.REQUEST_TRAILER):
            with self.subTest(trailer=trailer[:20]):
                self.refused(self.run_helper("bug", "low", "harness", body=self.stdin(trailer)),
                             "does not end with the trailer")
        self.refused(self.run_helper("bug", "low", "harness", body="a title\n\nthe body\n"),
                     "does not end with the trailer")
        self.refused(self.run_helper("bug", "low", "harness", body=f"a title\n\n{self.HAND_TRAILER}\n\nmore\n"),
                     "does not end with the trailer")

    def test_an_enhancement_files_under_the_request_sentence_and_nothing_else(self):
        self.refused(self.run_helper("enhancement", "low", "harness", body=self.stdin()),
                     "does not end with the trailer")
        result = self.run_helper("enhancement", "low", "harness", body=self.stdin(self.REQUEST_TRAILER))
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn("--label enhancement", self.creates()[0])

    def test_the_title_and_body_shape_is_the_issue_helpers(self):
        self.refused(self.run_helper("bug", "low", "harness", body="\n\nthe body\n"), "the title is empty")
        self.refused(self.run_helper("bug", "low", "harness", body=self.stdin(title="## Summary")),
                     "begins with a hash")
        self.refused(self.run_helper("bug", "low", "harness", body="the body\nand more\n"),
                     "the second line of stdin must be blank")
        self.refused(self.run_helper("bug", "low", "harness", body="a title"), "stdin ended before the blank line")

    def test_the_title_never_crosses_the_parents_command_line(self):
        d = Path(self.dir)
        marker = (d / "pwned").as_posix()
        title = f"`touch {marker}` and $(touch {marker}) and \"quoted\" and $HOME"
        env = dict(os.environ)
        env["PATH"] = self.dir + os.pathsep + env["PATH"]
        script = (
            'case "$(command -v gh)" in */issue-stub-*/gh) ;; *) exit 97 ;; esac\n'
            f"bash {str(self.HELPER)!r} bug high harness <<'ISSUE_BODY_END'\n"
            f"{title}\n\nthe body\n\n{self.HAND_TRAILER}\nISSUE_BODY_END\n"
        )
        result = subprocess.run([BASH, "-c", script], capture_output=True, text=True, encoding="utf-8", env=env)
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertFalse((d / "pwned").exists())
        self.assertIn(f"--title {title}", self.creates()[0])

    def test_the_repository_is_a_literal_and_force_is_never_spelled(self):
        text = self.HELPER.read_text(encoding="utf-8")
        code = "\n".join(ln for ln in text.splitlines() if not ln.lstrip().startswith("#"))
        self.assertEqual([f"repo={HARNESS_REPO}"], [ln for ln in code.splitlines() if ln.startswith("repo=")])
        self.assertEqual(3, code.count('--repo "$repo"'), "the search, the comment and the filing")
        self.assertNotIn("repo view", code)
        self.assertNotIn("--force", code)
        self.assertNotIn("$1 ", code.split("--repo", 1)[1].split("\n", 1)[0])


if __name__ == "__main__":
    unittest.main()
