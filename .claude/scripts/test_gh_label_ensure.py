"""gh-label-ensure.sh: the labels it creates and the ones it leaves alone.

The shared harness and the reason it shells out rather than
re-implementing are `review_helpers.py`'s.
"""

import os
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


class LabelStub:
    """A `gh` on PATH answering the three calls gh-label-ensure.sh makes.

    It records every argv it is handed, which is what turns "never spells
    `--force`" from a grep over the source into an assertion about the command
    that actually ran. `label list` answers from `labels_before` until a create
    has been attempted and from `labels_after` afterwards, which is how the
    concurrent-create branch gets a race to lose.
    """

    def __init__(self, before=(), after=None, repo="acme/widgets",
                 repo_fails=False, list_fails=False, create_fails=False):
        self.dir = tempfile.mkdtemp(prefix="label-stub-")
        d = Path(self.dir)
        (d / "before").write_text("".join(f"{x}\n" for x in before), encoding="utf-8")
        (d / "after").write_text(
            "".join(f"{x}\n" for x in (before if after is None else after)),
            encoding="utf-8",
        )
        gh = d / "gh"
        gh.write_text(
            textwrap.dedent(
                f"""\
                #!/usr/bin/env bash
                printf '%s\\n' "$*" >> {(d / 'argv').as_posix()!r}
                case "$*" in
                  *"repo view"*)
                    {"exit 7" if repo_fails else f'echo {repo!r}; exit 0'}
                    ;;
                  *"label list"*)
                    {"exit 7" if list_fails else ""}
                    if [ -f {(d / 'created').as_posix()!r} ]; then
                      cat {(d / 'after').as_posix()!r}
                    else
                      cat {(d / 'before').as_posix()!r}
                    fi
                    exit 0
                    ;;
                  *"label create"*)
                    touch {(d / 'created').as_posix()!r}
                    {"exit 1" if create_fails else "exit 0"}
                    ;;
                esac
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
            [BASH, str(SCRIPTS / "gh-label-ensure.sh"), *args],
            capture_output=True, text=True, env=env,
        )

    def calls(self):
        f = Path(self.dir) / "argv"
        return f.read_text(encoding="utf-8").splitlines() if f.exists() else []

    def cleanup(self):
        shutil.rmtree(self.dir, ignore_errors=True)


class LabelHelperBehaviour(unittest.TestCase):
    """The paths a source grep cannot reach.

    The class below asserts what the helper refuses and what its text does not
    contain, and every one of those cases exits before `gh` is reached; a
    branch no case reaches can regress with CI green.
    """

    def stub(self, **kw):
        s = LabelStub(**kw)
        self.addCleanup(s.cleanup)
        return s

    def test_an_existing_label_is_left_alone(self):
        s = self.stub(before=["security", "bug"])
        r = s.run("security")
        self.assertEqual(0, r.returncode, r.stderr)
        self.assertIn("already exists", r.stdout)
        self.assertFalse(
            [c for c in s.calls() if "label create" in c],
            "an existing label must never reach `gh label create`",
        )

    def test_a_missing_label_is_created_with_no_free_parameter(self):
        s = self.stub(before=[])
        r = s.run("medium")
        self.assertEqual(0, r.returncode, r.stderr)
        create = [c for c in s.calls() if "label create" in c]
        self.assertEqual(1, len(create))
        # The whole point of the helper, asserted against the command that ran
        # rather than against the source that spells it.
        self.assertIn("--repo acme/widgets", create[0])
        self.assertIn("--color fbca04", create[0])
        self.assertNotIn("--force", create[0])
        self.assertNotIn(" -f ", create[0])

    def test_a_create_lost_to_a_concurrent_sweep_succeeds(self):
        # Two sweeps race; this one loses. `gh label create` refuses a name that
        # exists, since --force is the flag this helper exists not to use, so
        # without the re-read the loser aborts over a label that is
        # now exactly what it asked for.
        s = self.stub(before=[], after=["low"], create_fails=True)
        r = s.run("low")
        self.assertEqual(0, r.returncode, r.stderr)
        self.assertIn("concurrently", r.stdout)

    def test_a_create_that_genuinely_failed_is_reported(self):
        # The other side of the same ambiguity: absent afterwards means the
        # create really failed, and assuming success there would be the
        # fail-open the re-read exists to avoid.
        s = self.stub(before=[], after=[], create_fails=True)
        r = s.run("high")
        self.assertNotEqual(0, r.returncode)
        self.assertIn("could not create", r.stderr)

    def test_a_near_miss_on_search_is_not_a_match(self):
        # `--search` matches rather than equals, so searching `low` also returns
        # `slow`. Concluding "already there" from that would leave the sweep
        # filing against a label that does not exist.
        s = self.stub(before=["slow", "lower"])
        r = s.run("low")
        self.assertEqual(0, r.returncode, r.stderr)
        self.assertTrue([c for c in s.calls() if "label create" in c])

    def test_an_unresolvable_repository_stops_the_helper(self):
        s = self.stub(repo_fails=True)
        r = s.run("bug")
        self.assertNotEqual(0, r.returncode)
        self.assertIn("cannot resolve", r.stderr)
        self.assertFalse([c for c in s.calls() if "label create" in c])

    def test_a_failed_listing_stops_the_helper(self):
        # Never fail open: an unreadable label list is not an absent label.
        s = self.stub(list_fails=True)
        r = s.run("bug")
        self.assertNotEqual(0, r.returncode)
        self.assertIn("cannot list", r.stderr)
        self.assertFalse([c for c in s.calls() if "label create" in c])

    def test_every_label_in_the_vocabulary_can_actually_be_created(self):
        # The class below asserts the names appear in the case; this asserts
        # each one reaches `gh` with its own colour, so a mistyped branch cannot
        # hide behind a source match.
        for label in ("security", "bug", "critical", "high", "medium", "low"):
            with self.subTest(label=label):
                s = self.stub(before=[])
                r = s.run(label)
                self.assertEqual(0, r.returncode, r.stderr)
                create = [c for c in s.calls() if "label create" in c]
                self.assertEqual(1, len(create))
                self.assertRegex(create[0], rf"label create {label} ")
                self.assertRegex(create[0], r"--color [0-9a-f]{6}")


class LabelHelperHasNoFreeParameter(unittest.TestCase):
    """The label helper pins the repository and never overwrites a label.

    `gh label create --force` updates an existing label's colour and
    description, and `-R` unpinned puts that write in any repository; prose in
    a command is a rule a reader enforces and a finding can talk past.
    """

    HELPER = SCRIPTS / "gh-label-ensure.sh"

    def run_helper(self, *args):
        return subprocess.run(
            [BASH, str(self.HELPER), *args], capture_output=True, text=True
        )

    def test_a_name_outside_the_vocabulary_never_reaches_gh(self):
        # Refused by the case, before `gh repo view` is called — so this test
        # needs no network and proves the refusal is the first thing that
        # happens rather than a message printed after a lookup.
        for name in ("nonsense", "security --force", "-R other/repo", ""):
            with self.subTest(name=name):
                result = self.run_helper(name)
                self.assertEqual(2, result.returncode)

    def test_it_takes_exactly_one_argument(self):
        self.assertEqual(2, self.run_helper().returncode)
        self.assertEqual(2, self.run_helper("security", "--force").returncode)

    def test_force_is_never_spelled(self):
        text = self.HELPER.read_text(encoding="utf-8")
        code = "\n".join(
            line for line in text.splitlines() if not line.lstrip().startswith("#")
        )
        self.assertNotIn("--force", code)
        self.assertNotIn(" -f ", code)

    def test_the_repository_is_resolved_rather_than_accepted(self):
        text = self.HELPER.read_text(encoding="utf-8")
        self.assertIn("gh repo view --json nameWithOwner", text)
        self.assertIn('--repo "$repo"', text)

    def test_the_vocabulary_is_the_six_labels_the_sweeps_apply(self):
        text = self.HELPER.read_text(encoding="utf-8")
        for label in ("security", "bug", "critical", "high", "medium", "low"):
            with self.subTest(label=label):
                self.assertRegex(text, rf"(?m)^\s*{label}\)\s+colour=")



if __name__ == "__main__":
    unittest.main()
