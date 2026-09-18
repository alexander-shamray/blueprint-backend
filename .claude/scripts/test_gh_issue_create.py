"""gh-issue-create.sh: the issue a sweep files, with no free parameter.

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


class IssueHelperHasNoFreeParameter(unittest.TestCase):
    """The issue helper leaves a sweep no free parameter.

    `-R` unpinned puts the issue in whichever repository a finding names, and
    `--label` unpinned reaches any label in any spelling. A title beginning
    with `/` needs `MSYS2_ARG_CONV_EXCL` or it files as a Windows path, and an
    env-prefixed `gh` no longer matches a `gh issue create` prefix grant, so
    the helper sets it.

    A refusing `gh` sits first on PATH for every case. The negatives exit
    before `gh repo view` is reached, so they need no network — and the stub is
    what proves it, because a validation that regressed would otherwise reach a
    real `gh` and file a real issue. The positive control answers the calls the
    helper and its label sibling make, and records the argv and the
    environment the `issue create` child actually received.
    """

    HELPER = SCRIPTS / "gh-issue-create.sh"

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
                  *"repo view"*)
                    echo 'acme/widgets'; exit 0
                    ;;
                  *"label list"*)
                    printf '%s\\n' security bug critical high medium low; exit 0
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

    def run_helper(self, *args, body=""):
        env = dict(os.environ)
        env["PATH"] = self.dir + os.pathsep + env["PATH"]
        return subprocess.run(
            [BASH, str(self.HELPER), *args],
            capture_output=True, text=True, input=body, env=env,
        )

    def calls(self):
        f = Path(self.dir) / "argv"
        return f.read_text(encoding="utf-8").splitlines() if f.exists() else []

    def assert_refused_before_gh(self, result):
        # Exit 2 is the validation code, and an empty argv file is the proof
        # that the refusal happened before `gh repo view` rather than after it.
        self.assertEqual(2, result.returncode, result.stderr)
        self.assertEqual([], self.calls())

    TRAILER = "Filed by an authorised sweep and verified at filing by a second read-only auditor."
    HAND_TRAILER = "Filed by hand rather than by a sweep: no second auditor verified it at filing."
    STDIN = f"a title\n\nthe body\n\n{TRAILER}\n"
    HAND_STDIN = f"a title\n\nthe body\n\n{HAND_TRAILER}\n"

    def test_no_arguments_prints_the_usage_line(self):
        result = self.run_helper()
        self.assert_refused_before_gh(result)
        self.assertIn(
            "usage: gh-issue-create.sh <security|bug> <critical|high|medium|low>"
            " <sweep|hand> < title, blank line, body ending in the trailer",
            result.stderr,
        )

    def test_the_argument_count_is_exactly_three(self):
        # A title on the command line is a free parameter: it crosses the
        # parent's shell before the helper runs.
        self.assert_refused_before_gh(self.run_helper("bug", body=self.STDIN))
        self.assert_refused_before_gh(self.run_helper("bug", "low", body=self.STDIN))
        self.assert_refused_before_gh(self.run_helper("a title", "bug", "low", "sweep", body=self.STDIN))
        self.assert_refused_before_gh(self.run_helper("bug", "low", "sweep", "--repo", body=self.STDIN))

    def test_a_kind_outside_the_vocabulary_is_refused(self):
        # `documentation` is a real label on this tracker and is refused on
        # purpose: neither sweep files one, so the helper's vocabulary is the
        # sweeps' and not the tracker's. The `hand` route does not widen it:
        # `documentation` is one of GitHub's defaults that `gh-label-ensure.sh`
        # must not re-create, and `CLAUDE.md` says the issue vocabulary is wider
        # than the helper.
        for kind in ("documentation", "Security", "security --force", "-R other/repo", ""):
            with self.subTest(kind=kind):
                self.assert_refused_before_gh(self.run_helper(kind, "high", "sweep", body=self.STDIN))

    def test_a_severity_outside_the_four_is_refused(self):
        for severity in ("info", "High", "high --force", "-R other/repo", ""):
            with self.subTest(severity=severity):
                self.assert_refused_before_gh(self.run_helper("bug", severity, "sweep", body=self.STDIN))

    def test_an_empty_title_is_refused(self):
        self.assert_refused_before_gh(self.run_helper("bug", "low", "sweep", body="\n\nthe body\n"))
        self.assert_refused_before_gh(self.run_helper("bug", "low", "sweep", body=""))

    def test_a_body_without_the_blank_separator_is_refused(self):
        # A body piped without its title line would otherwise file under its
        # own first sentence, with the second sentence lost into the title.
        self.assert_refused_before_gh(self.run_helper("bug", "low", "sweep", body="the body\nand more\n"))

    def test_a_stdin_that_ends_before_the_separator_is_refused(self):
        # `read` fails at EOF and leaves the separator unset, which an
        # `|| true` would read as blank and file with an empty body.
        self.assert_refused_before_gh(self.run_helper("bug", "low", "sweep", body="a title\n"))
        self.assert_refused_before_gh(self.run_helper("bug", "low", "sweep", body="a title"))

    def test_a_route_outside_the_two_is_refused(self):
        # The route decides which fixed line the body must end with, and it is
        # a closed set like the other two. A spelling outside it is refused
        # rather than defaulted, because a default is an unconditional
        # provenance claim.
        for route in ("sweeps", "Sweep", "auto", "sweep --force", "-R other/repo", ""):
            with self.subTest(route=route):
                self.assert_refused_before_gh(
                    self.run_helper("bug", "low", route, body=self.STDIN))

    def test_each_route_requires_the_line_that_is_true_of_it(self):
        # Neither trailer is accepted under the other's route, so a hand filing
        # cannot claim that a sweep filed it and a second auditor confirmed it:
        # a claim every issue makes distinguishes nothing.
        self.assert_refused_before_gh(
            self.run_helper("bug", "low", "hand", body=self.STDIN))
        self.assert_refused_before_gh(
            self.run_helper("bug", "low", "sweep", body=self.HAND_STDIN))

    def test_a_hand_filing_reaches_gh_with_its_own_trailer(self):
        # And the positive control on the other half: the hand route files,
        # rather than merely refusing the sweep's sentence. The body reaching
        # `gh` is the one that was piped in, trailer included.
        result = self.run_helper("bug", "low", "hand", body=self.HAND_STDIN)
        self.assertEqual(0, result.returncode, result.stderr)
        d = Path(self.dir)
        self.assertEqual(
            f"the body\n\n{self.HAND_TRAILER}\n",
            (d / "body").read_text(encoding="utf-8"),
        )

    def test_a_body_without_the_trailer_is_refused(self):
        # The detector for an early heredoc close: a body cut short by a
        # repository line equal to the delimiter has lost its last line.
        self.assert_refused_before_gh(self.run_helper("bug", "low", "sweep", body="a title\n\nthe body\n"))
        self.assert_refused_before_gh(self.run_helper("bug", "low", "sweep", body="a title\n\n"))
        self.assert_refused_before_gh(
            self.run_helper("bug", "low", "sweep", body=f"a title\n\n{self.TRAILER}\n\nmore after it\n")
        )

    def test_a_repository_line_equal_to_a_naive_delimiter_is_the_hazard_and_the_token_is_the_rule(self):
        # The real composition again, with the payload the sweeps' rule is
        # written for: a quoted repository line that reads `EOF`, followed by
        # a substitution that would run in the parent if the heredoc closed
        # there. Under the token delimiter the rule prescribes, the whole
        # payload reaches the stub and the marker is never created. The naive
        # delimiter is not run here on purpose, because its failure mode is the
        # parent's shell executing the tail; the helper's part of it is the
        # trailer case.
        d = Path(self.dir)
        marker = (d / "pwned").as_posix()
        body = f"the affected lines:\n\n    EOF\n    $(touch {marker})\n\n{self.TRAILER}\n"
        env = dict(os.environ)
        env["PATH"] = self.dir + os.pathsep + env["PATH"]
        script = (
            'case "$(command -v gh)" in */issue-stub-*/gh) ;; *) exit 97 ;; esac\n'
            f"bash {str(self.HELPER)!r} bug high sweep <<'ISSUE_BODY_END'\n"
            "a title\n"
            "\n"
            f"{body}"
            "ISSUE_BODY_END\n"
        )
        result = subprocess.run([BASH, "-c", script], capture_output=True, text=True, env=env)
        self.assertNotEqual(97, result.returncode, "the stub gh was not first on PATH")
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertFalse((d / "pwned").exists())
        self.assertEqual(body, (d / "body").read_text(encoding="utf-8"))

    def test_the_title_never_crosses_the_parents_command_line(self):
        # The real boundary: the parent is a shell composing a command string
        # around a quoted heredoc, and the title carries every expansion a
        # verdict record could smuggle. The stub must receive the bytes as
        # written, and the marker file the substitution would create must not
        # exist — the argv-array cases above cannot show either.
        d = Path(self.dir)
        marker = (d / "pwned").as_posix()
        title = f"`touch {marker}` and $(touch {marker}) and \"quoted\" and $HOME"
        # The stub is put on PATH through the environment, the way run_helper
        # does it, and the script refuses to go on unless `gh` resolves to it:
        # a test of a filing helper that reaches the real `gh` files a real
        # issue.
        env = dict(os.environ)
        env["PATH"] = self.dir + os.pathsep + env["PATH"]
        script = (
            'case "$(command -v gh)" in */issue-stub-*/gh) ;; *) exit 97 ;; esac\n'
            f"bash {str(self.HELPER)!r} security high sweep <<'ISSUE_BODY_END'\n"
            f"{title}\n"
            "\n"
            "the body\n"
            "\n"
            f"{self.TRAILER}\n"
            "ISSUE_BODY_END\n"
        )
        result = subprocess.run([BASH, "-c", script], capture_output=True, text=True, env=env)
        self.assertNotEqual(97, result.returncode, "the stub gh was not first on PATH")
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertFalse((d / "pwned").exists())
        create = [c for c in self.calls() if c.startswith("issue create ")]
        self.assertEqual(1, len(create), self.calls())
        self.assertIn(f"--title {title}", create[0])
        self.assertEqual(f"the body\n\n{self.TRAILER}\n", (d / "body").read_text(encoding="utf-8"))

    def test_a_valid_filing_reaches_gh_with_every_parameter_pinned(self):
        # The positive control the negatives need: a helper that refused
        # everything would pass every case above.
        title = "`/security-sweep` files a title that begins with a slash"
        body = f"the body\n\n{self.TRAILER}\n"
        result = self.run_helper("security", "high", "sweep", body=f"{title}\n\n{body}")
        self.assertEqual(0, result.returncode, result.stderr)
        create = [c for c in self.calls() if c.startswith("issue create ")]
        self.assertEqual(1, len(create), self.calls())
        self.assertIn("--repo acme/widgets", create[0])
        self.assertIn(f"--title {title}", create[0])
        self.assertIn("--label security", create[0])
        self.assertIn("--label high", create[0])
        self.assertIn("--body-file -", create[0])
        self.assertNotIn("--force", create[0])
        d = Path(self.dir)
        self.assertEqual(body, (d / "body").read_text(encoding="utf-8"))
        self.assertEqual("*", (d / "conv").read_text(encoding="utf-8").strip())

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

    def test_the_command_shape_is_the_one_the_sweeps_describe(self):
        # Each of these is a claim a sweep's step 4 makes about the helper, and
        # a source assertion is what stops the two drifting apart silently.
        text = self.HELPER.read_text(encoding="utf-8")
        self.assertIn("MSYS2_ARG_CONV_EXCL='*' gh issue create", text)
        self.assertIn('--repo "$repo"', text)
        self.assertIn('--label "$kind"', text)
        self.assertIn('--label "$severity"', text)
        self.assertIn("--body-file -", text)

    def test_both_labels_go_through_the_sibling_helper(self):
        text = self.HELPER.read_text(encoding="utf-8")
        self.assertIn('"$here/gh-label-ensure.sh" "$kind"', text)
        self.assertIn('"$here/gh-label-ensure.sh" "$severity"', text)



if __name__ == "__main__":
    unittest.main()
