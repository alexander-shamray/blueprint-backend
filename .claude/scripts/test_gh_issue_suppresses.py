"""gh-issue-suppresses.sh and its readers: what de-duplicates a sweep's finding.

The shared harness and the reason it shells out rather than
re-implementing are `review_helpers.py`'s.
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
    COMMANDS,
    BASH,
    setUpModule,
    code_lines,
)


SUPPRESSES = SCRIPTS / "gh-issue-suppresses.sh"
ISSUE_TEXT = SCRIPTS / "gh-issue-text.sh"
ISSUE_LIST = SCRIPTS / "gh-issue-list.sh"


class BothSweepsAgreeOnWhatSuppresses(unittest.TestCase):
    """Both sweeps state the same de-duplication gate.

    An issue blocks a re-file only if the repository owner opened it. The
    repository is public, so without that test any account could file "<topic>
    is tracked" and have the next sweep suppress the real finding. A
    maintainer-applied label is not a second sufficient condition: a label is
    applied to an issue rather than to its contents, and the author can rewrite
    the body afterwards while it stays, whereas authorship cannot be edited.

    The predicate is prose an agent follows rather than code that runs, so
    these cases pin only that both files still state it and that neither has
    drifted back to the unconditional rule. `security-sweep.md` and
    `bug-sweep.md` carry the gate word for word, and a rule fixed at one site
    and not its neighbour is why a test exists at all.
    """

    SWEEPS = ("security-sweep.md", "bug-sweep.md")

    REQUIRED = (
        "opened by the repository owner",
        "is not tracking and blocks nothing",
        # The sentence that keeps a label out as a second sufficient condition.
        "deliberately NOT a second sufficient condition",
    )

    # Phrasings this gate has retired, each a literal because each is a string
    # rather than a rule: if one reappears, a condition that was deliberately
    # removed has come back. Every retired form needs its own entry — a
    # negative naming one and not the next passes while the editable-label
    # suppression path is still open.
    RETIRED = (
        "An open issue, a `wontfix`, or an accepted-risk record blocks a re-file",
        "neither the owner's nor labelled",
        "maintainer-applied label",
    )

    def sweep(self, name):
        """The file with its wrapping collapsed.

        These are 80-column prose files and the two copies wrap the same
        sentence at different points, so a literal match finds it in one and
        not the other.
        """
        text = (COMMANDS / name).read_text(encoding="utf-8")
        return " ".join(text.split())

    def test_both_sweeps_state_the_trust_condition(self):
        for name in self.SWEEPS:
            for phrase in self.REQUIRED:
                with self.subTest(sweep=name, phrase=phrase):
                    self.assertIn(phrase, self.sweep(name))

    def test_neither_sweep_carries_a_retired_rule(self):
        for name in self.SWEEPS:
            for retired in self.RETIRED:
                with self.subTest(sweep=name, retired=retired):
                    self.assertNotIn(retired, self.sweep(name))

    def test_an_untracked_match_files_rather_than_suppressing(self):
        # Reporting a candidate as suppressed-but-unclean leaves the finding
        # unfiled while the loop spins, so a stranger who cannot end the sweep
        # could still stop the issue from ever being written.
        for name in self.SWEEPS:
            with self.subTest(sweep=name):
                self.assertIn("files normally", self.sweep(name))

    def test_the_clean_round_rule_agrees_with_the_gate(self):
        # A qualifier four paragraphs below the summary it qualifies leaves
        # the summary as the rule.
        for name in self.SWEEPS:
            with self.subTest(sweep=name):
                self.assertIn("tracked by the gate's test", self.sweep(name))


class SuppressionStub:
    """A `gh` answering the two reads gh-issue-suppresses.sh makes.

    Either answer can be made to fail, because the helper's fail direction is
    the property under test: it must never call an issue "tracking" on a lookup
    it could not complete.
    """

    def __init__(self, owner, authors, owner_fails=False, issue_fails=False):
        self.dir = tempfile.mkdtemp(prefix="suppress-stub-")
        table = Path(self.dir) / "authors"
        table.write_text(
            "".join(f"{k} {v}\n" for k, v in authors.items()), encoding="utf-8"
        )
        gh = Path(self.dir) / "gh"
        gh.write_text(
            textwrap.dedent(
                f"""\
                #!/usr/bin/env bash
                if [ "${{1:-}}" = "repo" ]; then
                  {"exit 1" if owner_fails else f'echo {owner!r}; exit 0'}
                fi
                if [ "${{1:-}}" = "issue" ] && [ "${{2:-}}" = "view" ]; then
                  {"exit 1" if issue_fails else ""}
                  awk -v n="${{3:-}}" '$1 == n {{ print $2 }}' {table.as_posix()!r}
                  exit 0
                fi
                echo "stub gh: unexpected call: $*" >&2
                exit 99
                """
            ),
            encoding="utf-8",
        )
        gh.chmod(0o755)

    def run(self, *args):
        env = dict(os.environ)
        env["PATH"] = self.dir + os.pathsep + env["PATH"]
        return subprocess.run(
            [BASH, str(SUPPRESSES), *args],
            capture_output=True, text=True, env=env,
        )

    def cleanup(self):
        shutil.rmtree(self.dir, ignore_errors=True)


class WhatSuppressesIsDecidedByCodeNow(unittest.TestCase):
    """`gh-issue-suppresses.sh` decides de-duplication, not a reader.

    An open issue suppresses a sweep finding only if the repository owner
    opened it: this repository is public, so otherwise a stranger files
    "{topic} is being tracked" and the next sweep suppresses the real finding
    and reports convergence — worse than a missed filing, because a clean round
    is what stops the loop.

    A drift check over the two command files can pin only that both still say
    the rule; it cannot establish that a sweep applied it to an issue. The rule
    the helper implements is authorship alone, because a label is applied to an
    issue rather than to its contents and the author can rewrite the body
    afterwards while the label stays.
    """

    def stub(self, **kw):
        stub = SuppressionStub(**kw)
        self.addCleanup(stub.cleanup)
        return stub

    @staticmethod
    def granted_bash(path):
        """The `Bash(...)` grants on one command's allowed-tools line.

        The same reader `CopilotFeedHelpersAreTheOnlyIntake` uses, because the
        subject here is the same: what a command may run, not what its prose
        tells a reader to run.
        """
        frontmatter = path.read_text(encoding="utf-8").split("---")[1]
        line = next(
            (ln for ln in frontmatter.splitlines()
             if ln.startswith("allowed-tools:")), "")
        return re.findall(r"Bash\(([^)]*)\)", line)

    def test_an_issue_the_owner_opened_is_tracking(self):
        result = self.stub(owner="ada", authors={"42": "ada"}).run("42")
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn("tracking", result.stdout)
        self.assertIn("ada", result.stdout)

    def test_an_issue_a_stranger_opened_is_not_tracking(self):
        # The exploitable half, decided by code rather than by a reader.
        result = self.stub(owner="ada", authors={"42": "mallory"}).run("42")
        self.assertEqual(1, result.returncode)
        self.assertIn("not tracking", result.stdout)

    def test_the_near_miss_login_is_printed_so_the_summary_can_name_it(self):
        # Why the sweeps can drop `author` from their listing and still report
        # `#NN by <login>`: the helper hands back the one field they need, on
        # the one path they need it, without their ever holding it.
        result = self.stub(owner="ada", authors={"42": "mallory"}).run("42")
        self.assertIn("mallory", result.stdout)

    def test_an_unresolvable_owner_is_undetermined_and_not_tracking(self):
        # Fail direction. Suppressing is the dangerous answer, and 3 is not 1:
        # "somebody else opened it" and "I could not find out" are different
        # states, and the summary has to be able to say which.
        result = self.stub(owner="ada", authors={"42": "ada"}, owner_fails=True).run("42")
        self.assertEqual(3, result.returncode)

    def test_an_unreadable_issue_is_undetermined(self):
        result = self.stub(owner="ada", authors={"42": "ada"}, issue_fails=True).run("42")
        self.assertEqual(3, result.returncode)

    def test_an_issue_reporting_no_author_is_undetermined(self):
        # An empty field is not a mismatch and must not be read as one — nor as
        # a match. `// ""` makes a null author an empty string, and an empty
        # string compared against an empty owner would otherwise be equal.
        result = self.stub(owner="ada", authors={}).run("42")
        self.assertEqual(3, result.returncode)

    def test_it_takes_one_issue_number_and_nothing_else(self):
        stub = self.stub(owner="ada", authors={"42": "ada"})
        for args in ((), ("42", "extra"), ("0",), ("-1",), ("abc",),
                     ("--repo evil/x",), ("42 43",), ("",)):
            with self.subTest(args=args):
                self.assertEqual(2, stub.run(*args).returncode)

    def test_the_owner_is_resolved_and_never_a_parameter(self):
        # `gh-label-ensure.sh`'s rule, and the reason this is a helper at all: a
        # login taken as an argument is a login a prompt-injected finding gets
        # to choose, and the one thing this file decides is whether to believe
        # an issue.
        text = SUPPRESSES.read_text(encoding="utf-8")
        self.assertIn("gh repo view --json owner", text)
        code = "\n".join(code_lines(text))
        self.assertNotIn("--repo", code)
        self.assertEqual(1, code.count('[ "$#" -eq 1 ]'))

    def test_the_issue_read_fixes_its_field_set(self):
        # A caller that could choose fields could ask for the body, which is
        # attacker-written text, and route it back through the helper whose
        # whole job is keeping a decision out of the model's hands.
        code = "\n".join(code_lines(SUPPRESSES.read_text(encoding="utf-8")))
        self.assertIn("gh issue view \"$issue\" --json author", code)
        self.assertNotIn("--json body", code)

    def test_both_sweeps_grant_the_helper(self):
        for name in ("security-sweep.md", "bug-sweep.md"):
            with self.subTest(command=name):
                text = (COMMANDS / name).read_text(encoding="utf-8")
                self.assertIn(
                    "Bash(bash .claude/scripts/gh-issue-suppresses.sh:*)", text
                )

    def test_no_sweep_grant_can_choose_an_issue_field(self):
        # Asserted on the grant rather than on the instruction line: a listing
        # line that omits `author` is a rule a reader follows, and cannot see a
        # grant one command over that makes the rule irrelevant. `gh issue
        # list` alone returns `author` and `body` for every issue at once.
        for name in ("security-sweep.md", "bug-sweep.md"):
            for forbidden in ("gh issue list", "gh issue view", "gh repo view",
                              "gh api"):
                with self.subTest(command=name, grant=forbidden):
                    self.assertNotIn(
                        forbidden,
                        " ".join(self.granted_bash(COMMANDS / name)),
                        f"{forbidden} lets a caller choose fields the helpers withhold",
                    )

    def test_the_listing_helper_fixes_its_field_set(self):
        code = "\n".join(code_lines(ISSUE_LIST.read_text(encoding="utf-8")))
        self.assertIn("--json number,title,state,labels", code)
        self.assertNotIn("author", code)
        self.assertNotIn("body", code)
        # No free parameter at all: the sweeps need one listing, always the same
        # one, so the helper takes nothing rather than taking something narrow.
        self.assertIn('[ "$#" -eq 0 ]', code)

    def test_the_listing_helper_takes_no_arguments(self):
        for args in (("--json", "author"), ("--state", "open"), ("1",)):
            with self.subTest(args=args):
                result = subprocess.run(
                    [BASH, str(ISSUE_LIST), *args], capture_output=True, text=True
                )
                self.assertEqual(2, result.returncode)

    def test_both_sweeps_reach_the_issue_set_only_through_the_helper(self):
        for name in ("security-sweep.md", "bug-sweep.md"):
            with self.subTest(command=name):
                grants = " ".join(self.granted_bash(COMMANDS / name))
                self.assertIn("bash .claude/scripts/gh-issue-list.sh", grants)

    def test_neither_sweep_can_read_an_issues_author_at_all(self):
        # Dropping `author` from the listing is half a control: an unrestricted
        # `Bash(gh issue view:*)` returns `author` to the same session, so the
        # decision the helper exists to take stays takeable. A helper that fixes
        # its field set does not bind a caller who still holds the raw grant.
        # Matching needs the body, which is an argument for a second
        # fixed-field helper rather than for keeping the grant.
        for name in ("security-sweep.md", "bug-sweep.md"):
            with self.subTest(command=name):
                frontmatter = (COMMANDS / name).read_text(
                    encoding="utf-8"
                ).split("---")[1]
                self.assertNotIn("Bash(gh issue view:*)", frontmatter)
                self.assertIn(
                    "Bash(bash .claude/scripts/gh-issue-text.sh:*)", frontmatter
                )

    def test_the_text_helper_withholds_the_one_field_that_decides(self):
        # The field set is fixed, and `author` is the field it exists to
        # withhold — a caller that could choose fields could choose that one.
        code = "\n".join(code_lines(ISSUE_TEXT.read_text(encoding="utf-8")))
        self.assertIn("--json number,title,state,body", code)
        self.assertNotIn("author", code)
        self.assertNotIn("--repo", code)
        self.assertEqual(1, code.count('[ "$#" -eq 1 ]'))

    def test_the_text_helper_takes_one_issue_number_and_nothing_else(self):
        for args in ((), ("1", "2"), ("0",), ("-1",), ("abc",),
                     ("--json author",), ("",)):
            with self.subTest(args=args):
                result = subprocess.run(
                    [BASH, str(ISSUE_TEXT), *args],
                    capture_output=True, text=True,
                )
                self.assertEqual(2, result.returncode)

    def test_neither_sweep_still_holds_the_grant_the_helper_replaced(self):
        # Moving a decision into a script is what lets a grant shrink rather
        # than grow: `gh repo view` was in both frontmatters for owner
        # resolution and nothing else, and the helper resolves it now.
        for name in ("security-sweep.md", "bug-sweep.md"):
            with self.subTest(command=name):
                frontmatter = (COMMANDS / name).read_text(
                    encoding="utf-8"
                ).split("---")[1]
                self.assertNotIn("Bash(gh repo view:*)", frontmatter)

    def test_no_sweep_spells_a_raw_issue_listing(self):
        # The instruction half, kept for what it is: a rule a reader follows.
        # It fails differently from the grant case above — this one catches a
        # command that goes back to spelling its own listing, that one catches
        # the grant which would let it choose fields.
        for name in ("security-sweep.md", "bug-sweep.md"):
            with self.subTest(command=name):
                text = (COMMANDS / name).read_text(encoding="utf-8")
                for line in text.splitlines():
                    self.assertFalse(
                        line.startswith("gh issue list"),
                        "the issue set is enumerated through gh-issue-list.sh",
                    )

    def test_both_sweeps_actually_enumerate_the_issue_set(self):
        # The positive control, which would otherwise pass against a file that
        # had stopped listing issues at all.
        for name in ("security-sweep.md", "bug-sweep.md"):
            with self.subTest(command=name):
                text = (COMMANDS / name).read_text(encoding="utf-8")
                self.assertTrue(
                    any(l.startswith("bash .claude/scripts/gh-issue-list.sh")
                        for l in text.splitlines()),
                    "no issue-set enumeration found in this command",
                )



if __name__ == "__main__":
    unittest.main()
