"""The landing method is one choice, and several places have to agree on it.

The helper that spells the merge and the invocation that reaches it are one
fact, because a permission rule is a prefix match
(`docs/harness-boundaries.md`); step 0's predicate and the fenced reads that
perform it are another. Each test below reads the place rather than the
outcome, which is what keeps a gate covering the surface it was written for.
"""

import os
import re
import shutil
import subprocess
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SHIP = ROOT / ".claude" / "commands" / "ship.md"
COMMANDS = ROOT / ".claude" / "commands"
MERGE_HELPER = ROOT / ".claude" / "scripts" / "gh-pr-merge.sh"
LOOKUP_HELPER = ROOT / ".claude" / "scripts" / "pr-for-branch.sh"
BASH = shutil.which("bash")
NEWLINE = chr(10)  # spelled this way so patch scripts cannot mangle it
TAB = chr(9)

# Alternatives rather than the one expected value, so a silent change to a
# method nobody considered fails here too. Every pattern is built from this.
METHOD_FLAGS = ("--merge", "--squash", "--rebase")
METHOD_RE = re.compile(r"gh pr merge (" + "|".join(re.escape(f) for f in METHOD_FLAGS) + r")")

MERGE_GRANT = "bash .claude/scripts/gh-pr-merge.sh:*"

# The reads step 0 makes, and the content reads they replaced. The retired
# spellings are held here so a revert to one is a failure, not a silence.
TIP_READ = "git rev-parse HEAD"
ROW_READ = "bash .claude/scripts/pr-for-branch.sh <branch>"
RETIRED_READS = ("git log origin/main..HEAD", "git cherry", "git log --merges --cc")


def setUpModule():
    # Not a skip: a skip on a missing tool reports a pass.
    if BASH is None:
        raise RuntimeError("bash required and not on PATH: the merge helper is run, not described")


def git(cwd, *args):
    """Run git and return stdout, refusing to continue on a failure.

    `encoding` is named because the default decodes as cp1252 on this host and
    drops what it cannot map without raising, which would let a comparison
    below pass on truncated output.
    """
    done = subprocess.run(("git",) + args, cwd=str(cwd), check=True, capture_output=True,
                          encoding="utf-8", errors="replace")
    return done.stdout


def granted_bash(path):
    """The `Bash(...)` entries of a command's frontmatter, as written."""
    frontmatter = path.read_text(encoding="utf-8").split("---")[1]
    line = next((ln for ln in frontmatter.splitlines() if ln.startswith("allowed-tools:")), "")
    return re.findall(r"Bash\(([^)]*)\)", line)


def fenced_lines(path):
    """Every line inside a fenced block, which is where a command is run.

    Prose naming a command argues about it; only a fenced line instructs a run,
    and the grant binds the second. A leading `>` is stripped because a fence
    inside a blockquote is still a fence, and step 0's residual callout is one.
    """
    inside, out = False, []
    for raw in path.read_text(encoding="utf-8").splitlines():
        line = re.sub(r"^\s*>\s?", "", raw).strip()
        if line.startswith("```"):
            inside = not inside
        elif inside:
            out.append(line)
    return out


def code_lines(path):
    """A script's lines that run: comments argue, and only code merges."""
    return [ln.strip() for ln in path.read_text(encoding="utf-8").splitlines()
            if ln.strip() and not ln.strip().startswith("#")]


class TheGrantAndTheInvocationAgree(unittest.TestCase):

    def test_no_command_holds_the_raw_merge_grant(self):
        # A prefix grant admits a trailing `--admin`, so no command may hold
        # the raw form, whichever one runs the merge.
        commands = sorted(COMMANDS.glob("*.md"))
        self.assertTrue(commands, "no command files found: the loop below would pass on nothing")
        for path in commands:
            for grant in granted_bash(path):
                self.assertFalse(grant.startswith("gh pr merge"), f"{path.name} grants {grant!r}")

    def test_every_invocation_starts_with_the_grant(self):
        self.assertIn(MERGE_GRANT, granted_bash(SHIP), "the last step of the chain cannot run")
        invocations = [ln for ln in fenced_lines(SHIP)
                       if "gh-pr-merge.sh" in ln or ln.startswith("gh pr merge")]
        self.assertTrue(invocations, "no fenced invocation found: the reader below would pass on nothing")
        for line in invocations:
            self.assertTrue(line.startswith(MERGE_GRANT[:-2]), f"{line!r} is outside the grant, so it is denied")

    def test_the_helper_spells_one_merge_and_pins_its_method(self):
        merges = [ln for ln in code_lines(MERGE_HELPER) if ln.startswith("gh pr merge")]
        self.assertEqual(len(merges), 1, "one endpoint, one merge")
        self.assertEqual(METHOD_RE.findall(merges[0]), ["--rebase"])
        self.assertIn('--match-head-commit "$oid"', merges[0])
        self.assertNotIn("--admin", " ".join(code_lines(MERGE_HELPER)))

    def test_the_prose_names_the_method_the_helper_runs(self):
        found = set(METHOD_RE.findall(SHIP.read_text(encoding="utf-8")))
        self.assertEqual(found, {"--rebase"}, "the helper and the prose name one method")

    def test_the_reads_step_0_makes_are_granted(self):
        granted = granted_bash(SHIP)
        self.assertIn("git rev-parse:*", granted)
        self.assertIn("bash .claude/scripts/pr-for-branch.sh:*", granted)
        self.assertNotIn("git cherry:*", granted, "a grant nothing uses is a grant something else will")


class TheMergeHelperRefusesWhatTheGrantAdmitted(unittest.TestCase):
    """The helper is run against a stubbed `gh`, and the stub records the merge.

    A refusal is asserted by its message and by the merge never being reached,
    because an exit code alone stays red when the guard is deleted and the
    script fails for another reason.
    """

    OID = "a" * 40
    OTHER = "b" * 40

    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        base = Path(self.tmp.name)
        self.repo = base / "repo"
        self.repo.mkdir()
        git(self.repo, "init", "--initial-branch=main")
        git(self.repo, "config", "user.email", "test@example.invalid")
        git(self.repo, "config", "user.name", "Test")
        git(self.repo, "commit", "--allow-empty", "--message", "the base")
        git(self.repo, "checkout", "-b", "feat/x")
        self.log = base / "merge.log"
        self.bin = base / "bin"
        self.bin.mkdir()
        stub = self.bin / "gh"
        stub.write_text(NEWLINE.join((
            "#!/usr/bin/env bash",
            'if [ "$1 $2" = "repo view" ]; then printf "%s" "$STUB_OWNER"; exit 0; fi',
            'if [ "$1 $2" = "pr view" ]; then printf "%s" "$STUB_VIEW"; exit 0; fi',
            'if [ "$1 $2" = "pr merge" ]; then printf "%s" "$*" > "$STUB_LOG"; exit 0; fi',
            'echo "unexpected gh call: $*" >&2; exit 9',
            "")), encoding="utf-8", newline=NEWLINE)
        stub.chmod(0o755)

    def tearDown(self):
        self.tmp.cleanup()

    def view(self, branch="feat/x", oid=None, cross="false", base="main"):
        return TAB.join((branch, oid or self.OID, cross, base))

    def run_helper(self, *args, view=None, owner="acme/widgets"):
        env = {
            **os.environ,
            "PATH": str(self.bin) + os.pathsep + os.environ["PATH"],
            "STUB_OWNER": owner,
            "STUB_VIEW": self.view() if view is None else view,
            "STUB_LOG": str(self.log),
        }
        return subprocess.run([BASH, str(MERGE_HELPER), *args], cwd=str(self.repo), env=env,
                              capture_output=True, encoding="utf-8", errors="replace")

    def assert_refused(self, result, message):
        self.assertNotEqual(0, result.returncode)
        self.assertIn(message, result.stderr)
        self.assertFalse(self.log.exists(), "the merge was reached anyway")

    def test_the_merge_it_runs_is_the_one_it_spells(self):
        # The positive control: without it every refusal below passes on a
        # helper that can no longer merge at all.
        result = self.run_helper("7", self.OID)
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(f"pr merge --rebase --repo acme/widgets --match-head-commit {self.OID} 7",
                         self.log.read_text(encoding="utf-8"))

    def test_a_trailing_flag_has_nowhere_to_go(self):
        self.assert_refused(self.run_helper("7", self.OID, "--admin"), "usage:")

    def test_a_flag_in_an_argument_position_is_not_a_number_or_an_oid(self):
        self.assert_refused(self.run_helper("--admin", self.OID), "pr must be a number")
        self.assert_refused(self.run_helper("7", "--admin"), "head oid must be a full")

    def test_the_oid_is_required_and_whole(self):
        self.assert_refused(self.run_helper("7"), "usage:")
        self.assert_refused(self.run_helper("7", self.OID[:12]), "head oid must be a full")

    def test_another_branchs_pull_request_is_refused(self):
        self.assert_refused(self.run_helper("7", self.OID, view=self.view(branch="feat/other")),
                            "not the checked-out feat/x")

    def test_a_fork_is_refused(self):
        self.assert_refused(self.run_helper("7", self.OID, view=self.view(cross="true")),
                            "comes from another repository")

    def test_a_base_other_than_main_is_refused(self):
        self.assert_refused(self.run_helper("7", self.OID, view=self.view(base="release")),
                            "targets release, not main")

    def test_a_head_that_moved_is_refused_before_github_is_asked_to(self):
        self.assert_refused(self.run_helper("7", self.OID, view=self.view(oid=self.OTHER)),
                            f"head is {self.OTHER}")

    def test_main_is_not_a_pull_request_branch(self):
        git(self.repo, "checkout", "main")
        self.assert_refused(self.run_helper("7", self.OID), "not on a PR branch")

    def test_an_unresolvable_repository_stops_the_helper(self):
        self.assert_refused(self.run_helper("7", self.OID, owner=""), "resolved to nothing")


class TheStepZeroReadsAreTheOnesTheProseArgues(unittest.TestCase):
    """The predicate is prose, so nothing but this class reads what it says.

    Reverting a fenced line leaves every other test here green, which is the
    failure `CLAUDE.md` calls this repository's most-repeated.
    """

    def test_the_tip_read_is_there(self):
        self.assertIn(TIP_READ, [ln.split("#")[0].strip() for ln in fenced_lines(SHIP)])

    def test_the_row_read_is_there_and_names_the_head(self):
        fenced = fenced_lines(SHIP)
        rows = [i for i, ln in enumerate(fenced) if ln.startswith(ROW_READ)]
        self.assertTrue(rows, "step 0 no longer asks for the branch's pull requests")
        self.assertTrue(any("headRefOid" in " ".join(fenced[i:i + 8]) for i in rows),
                        "the row is read, and the head it is read for is not named")

    def test_no_retired_content_read_is_performed_anywhere(self):
        for line in fenced_lines(SHIP):
            for retired in RETIRED_READS:
                self.assertFalse(line.startswith(retired), f"{line!r} compares content, which cannot stand here")

    def test_the_helper_publishes_the_head_the_predicate_compares(self):
        code = " ".join(code_lines(LOOKUP_HELPER))
        self.assertRegex(code, r"--json [a-zA-Z,]*headRefOid", "the field is never asked for")
        self.assertRegex(code, r"\{[^}]*headRefOid[^}]*\}", "and never projected")


class TheFinishedPredicateSurvivesTheLandingMethod(unittest.TestCase):
    """A landed branch must read finished, and later work must not.

    The repositories are built rather than described, because the claim is
    about what git answers and not about what the flags are documented to mean.
    The rows are what `pr-for-branch.sh` publishes, recorded at the push the
    pull request merged, which is when GitHub records them.
    """

    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        base = Path(self.tmp.name)
        self.remote = base / "remote.git"
        subprocess.run(("git", "init", "--bare", "--initial-branch=main", str(self.remote)),
                       check=True, capture_output=True)
        self.work = base / "work"
        self.work.mkdir()
        git(self.work, "init", "--initial-branch=main")
        git(self.work, "config", "user.email", "test@example.invalid")
        git(self.work, "config", "user.name", "Test")
        git(self.work, "remote", "add", "origin", str(self.remote))
        self.commit("a.txt", "one", "the base")
        git(self.work, "push", "--set-upstream", "origin", "main")

    def tearDown(self):
        self.tmp.cleanup()

    def commit(self, name, body, message):
        (self.work / name).write_text(body + "\n", encoding="utf-8")
        git(self.work, "add", "--all")
        git(self.work, "commit", "--message", message)

    def tip(self, ref="HEAD"):
        return git(self.work, "rev-parse", ref).strip()

    def row(self, state, number=1):
        """The row a pull request carries for the head it has right now."""
        return {"number": number, "state": state, "url": f"u{number}", "headRefOid": self.tip()}

    def finished(self, rows):
        """Step 0's commit reads: the tip is the head a MERGED row records."""
        return any(r["state"] == "MERGED" and r["headRefOid"] == self.tip() for r in rows)

    def unlanded(self, branch):
        """The `+` lines of `git cherry`: the retired patch read."""
        return [ln for ln in git(self.work, "cherry", "origin/main", branch).splitlines()
                if ln.startswith("+")]

    def behind(self, branch):
        """The commits of `git log origin/main..<branch>`: the retired range read."""
        return [ln for ln in git(self.work, "log", "--oneline", f"origin/main..{branch}").splitlines() if ln]

    def land_by_rebase(self, branch):
        """What GitHub's rebase method does: replay onto a moved base, no merges.

        `main` gains a commit first, and it has to: replaying a commit onto the
        parent it already had reproduces it byte for byte, so git hands back
        the SHA it started with and nothing was rebased.
        """
        git(self.work, "checkout", "main")
        self.commit(f"meanwhile-{branch.replace('/', '-')}.txt", "another branch landed first",
                    "a commit main gained")
        replayed = git(self.work, "log", "--no-merges", "--reverse", "--format=%H",
                       f"main..{branch}").split()
        git(self.work, "cherry-pick", *replayed)
        git(self.work, "push", "origin", "main")
        git(self.work, "checkout", branch)
        git(self.work, "fetch", "origin")

    def assert_really_rebased(self, branch):
        # The subject is the load-bearing half: it says the work reached `main`
        # under a SHA of its own. Comparing the two tips instead would pass on
        # any fixture where `main` moved again after the replay.
        upstream = git(self.work, "log", "--format=%H %s", "origin/main")
        for line in git(self.work, "log", "--no-merges", "--format=%H %s",
                        f"origin/main..{branch}").splitlines():
            sha, subject = line.split(" ", 1)
            self.assertNotIn(sha, upstream)
            self.assertIn(subject, upstream, "the replay did not reach main, so this is not a landing")

    def test_a_rebase_landed_branch_is_finished(self):
        git(self.work, "checkout", "-b", "feat/x")
        self.commit("b.txt", "two", "first")
        self.commit("c.txt", "three", "second")
        rows = [self.row("MERGED")]
        self.land_by_rebase("feat/x")
        self.assert_really_rebased("feat/x")

        self.assertEqual(len(self.behind("feat/x")), 2, "the range read still reports work, which is the point")
        self.assertTrue(self.finished(rows), "the tip is the head that landed, whatever SHAs main gave it")

    def test_a_merge_landed_branch_reads_the_same_way(self):
        git(self.work, "checkout", "-b", "feat/y")
        self.commit("d.txt", "four", "only")
        rows = [self.row("MERGED")]
        git(self.work, "checkout", "main")
        git(self.work, "merge", "--no-ff", "--message", "Merge pull request", "feat/y")
        git(self.work, "push", "origin", "main")
        git(self.work, "checkout", "feat/y")

        self.assertTrue(self.finished(rows), "the method never reaches the predicate")

    def test_an_open_pull_request_is_not_a_landing(self):
        # An OPEN row's head equals the tip on every pushed branch, so a read
        # that compared heads and skipped the state would remove live work.
        git(self.work, "checkout", "-b", "feat/z")
        self.commit("e.txt", "five", "unlanded")
        self.assertFalse(self.finished([self.row("OPEN")]))
        self.assertFalse(self.finished([]), "and neither is a branch that never opened one")

    def test_a_commit_after_the_landing_keeps_the_workspace_whatever_its_patch(self):
        # The case the patch read got wrong: `main` already carries this
        # patch, so `git cherry` reads clean while the tip holds a commit the
        # pull request never had.
        git(self.work, "checkout", "-b", "feat/after")
        self.commit("f.txt", "six", "the branch's work")
        rows = [self.row("MERGED")]
        self.land_by_rebase("feat/after")
        git(self.work, "checkout", "main")
        self.commit("g.txt", "seven", "a fix made on main")
        git(self.work, "push", "origin", "main")
        git(self.work, "checkout", "feat/after")
        git(self.work, "cherry-pick", "main")
        git(self.work, "fetch", "origin")

        self.assertEqual(self.unlanded("feat/after"), [], "the patch read calls this finished")
        self.assertFalse(self.finished(rows), "identity does not")

    def test_a_merge_that_invented_content_keeps_the_workspace(self):
        # The case the patch read could not see at all: it prints a line only
        # for a commit with one parent.
        git(self.work, "checkout", "-b", "feat/evil")
        self.commit("h.txt", "eight", "the branch's work")
        rows = [self.row("MERGED")]
        git(self.work, "checkout", "main")
        self.commit("m.txt", "thirteen", "main moved")
        git(self.work, "checkout", "feat/evil")
        git(self.work, "merge", "--no-edit", "main")
        (self.work / "only-in-the-merge.txt").write_text("resolved by hand\n", encoding="utf-8")
        git(self.work, "add", "--all")
        git(self.work, "commit", "--amend", "--no-edit")
        self.land_by_rebase("feat/evil")

        self.assertEqual(self.unlanded("feat/evil"), [], "the patch read is clear, and it is wrong")
        self.assertFalse(self.finished(rows))

    def test_a_checkout_behind_the_landed_head_is_kept(self):
        # The cost of one read, asserted so that it stays a decision: a second
        # read could admit this, and admitting it by accident would be a bug.
        git(self.work, "checkout", "-b", "feat/behind")
        self.commit("i.txt", "nine", "first")
        behind = self.tip()
        self.commit("j.txt", "ten", "pushed from elsewhere")
        rows = [self.row("MERGED")]
        git(self.work, "reset", "--hard", behind)

        self.assertFalse(self.finished(rows), "kept, and named in the report")

    def test_a_reused_branch_name_matches_only_the_use_it_is(self):
        git(self.work, "checkout", "-b", "feat/twice")
        self.commit("k.txt", "eleven", "the first use")
        first = self.row("MERGED", number=1)
        self.land_by_rebase("feat/twice")
        git(self.work, "reset", "--hard", "origin/main")
        self.commit("l.txt", "twelve", "the second use")

        self.assertFalse(self.finished([first]), "the earlier landing says nothing about this tip")
        self.assertTrue(self.finished([first, self.row("MERGED", number=2)]), "a row, not the row")


if __name__ == "__main__":
    unittest.main()
