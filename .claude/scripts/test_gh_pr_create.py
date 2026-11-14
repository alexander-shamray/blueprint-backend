"""gh-pr-create.sh: the one pull request `/pr` opens, with the repository, head and base fixed.

A stub `gh` sits first on PATH, so a refusal that regressed records a call rather than opening a real pull request;
the positive control records the argv, the environment and the body `gh pr create` read, from where it ran.
"""

import os
import re
import subprocess
import tempfile
import textwrap
import unittest
from pathlib import Path

from review_helpers import (
    sign_nothing,
    SCRIPTS,
    BASH,
    COMMANDS,
    setUpModule,
)
from test_link_skips import skip_only_without_links

HELPER = SCRIPTS / "gh-pr-create.sh"
TITLE = "artifacts/pr/title.txt"
BODY = "artifacts/pr/body.md"


def git(repo, *args):
    return subprocess.run(["git", "-C", str(repo), *args], check=True, capture_output=True, text=True,
                          encoding="utf-8")


class PrCreateHelper(unittest.TestCase):

    def setUp(self):
        sign_nothing(self)
        holder = tempfile.TemporaryDirectory(prefix="pr-create-")
        self.addCleanup(holder.cleanup)
        self.dir = Path(holder.name)
        self.stub = self.dir / "bin"
        self.stub.mkdir()
        self.calls = self.dir / "calls"
        self.repo = self.dir / "repo"
        subprocess.run(["git", "init", "-q", "-b", "feat/x", str(self.repo)], check=True, capture_output=True)
        # `/root.md` is ignored too, so its refusal is the prefix's and not the ignore read's.
        (self.repo / ".gitignore").write_text("artifacts/\n/root.md\n", encoding="utf-8")
        (self.repo / "artifacts" / "pr").mkdir(parents=True)
        self.write(TITLE, "fix(harness): read `guard.py`\n")
        self.write(BODY, "The body.\n")
        self.stub_gh(create_exit=0)

    def write(self, rel, text):
        path = self.repo / rel
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text, encoding="utf-8")
        return path

    def stub_gh(self, create_exit):
        record = self.dir.as_posix()
        gh = self.stub / "gh"
        gh.write_text(textwrap.dedent(f"""\
            #!/usr/bin/env bash
            echo "$1 $2" >> {(record + '/calls')!r}
            case "$1 $2" in
              "repo view") echo 'acme/widgets'; exit 0 ;;
              "pr create")
                for arg in "$@"; do printf '%s\\n' "$arg"; done > {(record + '/argv')!r}
                printf '%s' "${{MSYS2_ARG_CONV_EXCL-unset}}" > {(record + '/conv')!r}
                while [ "$#" -gt 0 ]; do
                  [ "$1" != --body-file ] || cat -- "$2" > {(record + '/body')!r}
                  shift
                done
                [ {create_exit} -ne 0 ] || echo 'https://github.com/acme/widgets/pull/7'
                exit {create_exit} ;;
            esac
            echo "stub gh: unexpected call: $*" >&2
            exit 99
            """), encoding="utf-8")
        gh.chmod(0o755)

    def run_helper(self, *args, cwd=None):
        env = dict(os.environ)
        env["PATH"] = str(self.stub) + os.pathsep + env["PATH"]
        return subprocess.run([BASH, str(HELPER), *args], capture_output=True, text=True, encoding="utf-8",
                              cwd=str(cwd or self.repo), env=env)

    def gh_calls(self):
        return self.calls.read_text(encoding="utf-8").splitlines() if self.calls.exists() else []

    def assert_refused(self, out, exit_code, words):
        self.assertEqual(exit_code, out.returncode, out.stderr)
        self.assertIn(words, out.stderr)
        self.assertEqual([], self.gh_calls(), "a refusal reached gh")

    def test_the_argument_count_is_exactly_two(self):
        for args in ((), (TITLE,), (TITLE, BODY, BODY), ("--repo", "elsewhere/repo", BODY)):
            with self.subTest(args=args):
                self.assert_refused(self.run_helper(*args), 2, "usage: gh-pr-create.sh <title-file> <body-file>")
        # Two words are two paths, so a flag in their place is a file that is not there.
        self.assert_refused(self.run_helper("--repo", "elsewhere/repo"), 2, "missing: --repo")

    def test_an_input_outside_artifacts_is_refused(self):
        self.write("root.md", "body\n")
        outside = self.dir / "outside.md"
        outside.write_text("body\n", encoding="utf-8")
        for body in ("root.md", "artifacts/../root.md", str(outside), outside.as_posix()):
            with self.subTest(body=body):
                self.assert_refused(self.run_helper(TITLE, body), 2, "a PR input lives under")

    def test_a_tracked_input_is_refused(self):
        git(self.repo, "add", "-f", BODY)
        self.assert_refused(self.run_helper(TITLE, BODY), 2, "is not ignored by git")

    def test_an_input_git_does_not_ignore_is_refused(self):
        # A target whose `.gitignore` names the title alone: the body under `artifacts/` is the branch's to add.
        (self.repo / ".gitignore").write_text(TITLE + "\n", encoding="utf-8")
        self.assert_refused(self.run_helper(TITLE, BODY), 2, "is not ignored by git")

    def test_a_symbolic_link_is_refused_and_so_is_a_linked_directory(self):
        target = self.dir / "secret.md"
        target.write_text("secret\n", encoding="utf-8")
        elsewhere = self.dir / "elsewhere"
        elsewhere.mkdir()
        (elsewhere / "body.md").write_text("secret\n", encoding="utf-8")
        try:
            os.symlink(target, self.repo / "artifacts" / "pr" / "link.md")
            os.symlink(elsewhere, self.repo / "artifacts" / "linked", target_is_directory=True)
        except (OSError, NotImplementedError) as refused:
            skip_only_without_links(refused, "symbolic link")
        self.assert_refused(self.run_helper(TITLE, "artifacts/pr/link.md"), 2, "a symbolic link is not a PR input")
        self.assert_refused(self.run_helper(TITLE, "artifacts/linked/body.md"), 2, "a PR input lives under")

    def test_a_missing_input_is_refused_by_name(self):
        self.assert_refused(self.run_helper("artifacts/pr/none.txt", BODY), 2, "missing: artifacts/pr/none.txt")

    def test_one_file_for_both_is_refused(self):
        self.assert_refused(self.run_helper(BODY, "artifacts/pr/../pr/body.md"), 2, "are one file")

    def test_a_title_that_is_not_one_line_is_refused(self):
        for title, words in (("a\nb\n", "more than one line"), ("a\nb", "more than one line"),
                             ("a\rb\n", "more than one line"), ("a\r\r\n", "more than one line"),
                             ("a\n\n", "more than one line"), ("", "the title is empty"),
                             ("\n", "the title is empty"), ("\r\n", "the title is empty")):
            with self.subTest(title=title):
                (self.repo / TITLE).write_bytes(title.encode("utf-8"))
                self.assert_refused(self.run_helper(TITLE, BODY), 2, words)

    def test_one_line_ending_lf_crlf_or_none_is_one_line(self):
        # The same bytes give the same title on every machine, whatever the editor's line ending.
        for raw in (b"fix: one\n", b"fix: one\r\n", b"fix: one"):
            with self.subTest(raw=raw):
                (self.repo / TITLE).write_bytes(raw)
                self.write(BODY, "The body.\n")
                out = self.run_helper(TITLE, BODY)
                self.assertEqual(0, out.returncode, out.stderr)
                argv = (self.dir / "argv").read_text(encoding="utf-8").splitlines()
                self.assertEqual("fix: one", argv[argv.index("--title") + 1])

    def test_main_and_a_detached_head_are_refused(self):
        git(self.repo, "checkout", "-q", "-b", "main")
        self.assert_refused(self.run_helper(TITLE, BODY), 3, "from main into main")
        git(self.repo, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "--allow-empty", "-m", "x")
        git(self.repo, "checkout", "-q", "--detach")
        self.assert_refused(self.run_helper(TITLE, BODY), 3, "detached HEAD")

    def test_main_is_refused_however_it_is_spelled(self):
        # On a case-insensitive filesystem `Main` is `main`'s ref; `symbolic-ref` takes every spelling on every one.
        git(self.repo, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "--allow-empty", "-m", "x")
        tip = git(self.repo, "rev-parse", "HEAD").stdout.strip()
        for spelling in ("main", "Main", "MAIN", "heads/main", "refs/heads/main", "origin/main", "refs/remotes/origin/main"):
            with self.subTest(spelling=spelling):
                git(self.repo, "update-ref", f"refs/heads/{spelling}", tip)
                git(self.repo, "symbolic-ref", "HEAD", f"refs/heads/{spelling}")
                self.assert_refused(self.run_helper(TITLE, BODY), 3, "from main into main")

    def test_a_valid_create_reaches_gh_with_every_flag_pinned_and_removes_its_inputs(self):
        (self.repo / "sub").mkdir()
        out = self.run_helper("../" + TITLE, "../" + BODY, cwd=self.repo / "sub")
        self.assertEqual(0, out.returncode, out.stderr)
        self.assertIn("/pull/7", out.stdout)
        self.assertEqual(["repo view", "pr create"], self.gh_calls())
        argv = (self.dir / "argv").read_text(encoding="utf-8").splitlines()
        self.assertEqual(["pr", "create", "--repo", "acme/widgets", "--base", "main", "--head", "feat/x",
                          "--title", "fix(harness): read `guard.py`", "--body-file", BODY], argv)
        # The relative body path is read from the checkout, where the helper runs gh, whatever the caller's cwd.
        self.assertEqual("The body.\n", (self.dir / "body").read_text(encoding="utf-8"))
        self.assertEqual("*", (self.dir / "conv").read_text(encoding="utf-8"))
        self.assertFalse((self.repo / TITLE).exists())
        self.assertFalse((self.repo / BODY).exists())

    def test_a_title_that_looks_like_a_flag_or_a_path_is_passed_as_a_title(self):
        for title in ("--repo elsewhere/repo", "/ship: one line"):
            with self.subTest(title=title):
                self.write(TITLE, title + "\n")
                self.write(BODY, "The body.\n")
                self.calls.unlink(missing_ok=True)
                out = self.run_helper(TITLE, BODY)
                self.assertEqual(0, out.returncode, out.stderr)
                argv = (self.dir / "argv").read_text(encoding="utf-8").splitlines()
                self.assertEqual(title, argv[argv.index("--title") + 1])
                self.assertEqual("acme/widgets", argv[argv.index("--repo") + 1])

    def test_a_failed_create_keeps_both_inputs_for_the_retry(self):
        self.stub_gh(create_exit=1)
        out = self.run_helper(TITLE, BODY)
        self.assertNotEqual(0, out.returncode)
        self.assertTrue((self.repo / TITLE).is_file())
        self.assertTrue((self.repo / BODY).is_file())


def frontmatter_grants(name):
    front = (COMMANDS / name).read_text(encoding="utf-8").split("---")[1]
    line = next(ln for ln in front.splitlines() if ln.startswith("allowed-tools:"))
    return [item.strip() for item in line.split(":", 1)[1].split(",")]


class TheCommandsOpenThroughTheHelper(unittest.TestCase):
    """The raw grant is withdrawn from every command, and the two that open a pull request are granted the helper."""

    GRANT = "Bash(bash .claude/scripts/gh-pr-create.sh:*)"

    def test_no_command_grants_the_raw_form(self):
        seen = 0
        for path in sorted(COMMANDS.glob("*.md")):
            seen += 1
            with self.subTest(command=path.name):
                self.assertNotIn("Bash(gh pr create", path.read_text(encoding="utf-8").split("---")[1])
        self.assertGreater(seen, 5, "found almost no commands")

    def test_pr_and_ship_are_granted_the_helper(self):
        # The positive control: withdrawing the raw grant and granting nothing would pass the case above and break
        # the chain at /pr's one create.
        for name in ("pr.md", "ship.md"):
            with self.subTest(command=name):
                self.assertIn(self.GRANT, frontmatter_grants(name))

    def test_pr_runs_the_helper_with_two_artifacts_paths(self):
        text = (COMMANDS / "pr.md").read_text(encoding="utf-8")
        calls = re.findall(r"(?m)^\s*bash \.claude/scripts/gh-pr-create\.sh.*$", text)
        self.assertEqual(["bash .claude/scripts/gh-pr-create.sh artifacts/pr/title.txt artifacts/pr/body.md"],
                         [c.strip() for c in calls])
        self.assertNotRegex(text, r"(?m)^\s*gh pr create")


if __name__ == "__main__":
    unittest.main()
