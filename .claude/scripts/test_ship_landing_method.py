"""The landing method is one choice, and several places have to agree on it.

A grant and the invocation that uses it are one fact, because a permission
rule is a prefix match (`docs/harness-boundaries.md`); step 0's predicate and
the fenced reads that perform it are another. Each test below reads the place
rather than the outcome, which is what keeps a gate covering the surface it
was written for.
"""

import re
import subprocess
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SHIP = ROOT / ".claude" / "commands" / "ship.md"

# Alternatives rather than the one expected value, so a silent change to a
# method nobody considered fails here too. Every pattern is built from this.
METHOD_FLAGS = ("--merge", "--squash", "--rebase")
METHOD_RE = re.compile(r"gh pr merge (" + "|".join(re.escape(f) for f in METHOD_FLAGS) + r")")

# The two reads step 0 makes about commits, and the one they replaced. The
# retired spelling is held here so a revert to it is a failure rather than a
# silence — the whole reason this file exists.
PATCH_READ = "git cherry origin/main HEAD"
MERGE_READ = 'git log --merges --cc --format="" origin/main..HEAD'
RETIRED_READ = "git log origin/main..HEAD"


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


class TheGrantAndTheInvocationAgree(unittest.TestCase):

    def test_the_method_is_spelled_the_same_way_everywhere_in_the_file(self):
        found = set(METHOD_RE.findall(SHIP.read_text(encoding="utf-8")))
        self.assertEqual(found, {"--rebase"}, "the grant, the invocation and the prose name one method")

    def test_every_invocation_starts_with_a_grant(self):
        prefixes = [g[:-2] for g in granted_bash(SHIP) if g.startswith("gh pr merge") and g.endswith(":*")]
        self.assertTrue(prefixes, "no `gh pr merge` grant: the last step of the chain cannot run")
        invocations = [ln for ln in fenced_lines(SHIP) if ln.startswith("gh pr merge")]
        self.assertTrue(invocations, "no fenced invocation found: the reader below would pass on nothing")
        for line in invocations:
            self.assertTrue(any(line.startswith(p) for p in prefixes),
                            f"{line!r} does not start with any of {prefixes}, so it is denied")

    def test_the_grant_pins_a_method_rather_than_leaving_it_open(self):
        for grant in granted_bash(SHIP):
            if grant.startswith("gh pr merge"):
                self.assertIn(grant[:-2], [f"gh pr merge {flag}" for flag in METHOD_FLAGS],
                              f"{grant!r} leaves the method to the caller")

    def test_the_reads_step_0_makes_are_granted(self):
        granted = granted_bash(SHIP)
        self.assertIn("git cherry:*", granted)
        self.assertIn("git log:*", granted)


class TheStepZeroReadsAreTheOnesTheProseArgues(unittest.TestCase):
    """The predicate is prose, so nothing but this class reads what it says.

    Reverting either fenced line leaves every other test here green, which is
    the failure `CLAUDE.md` calls this repository's most-repeated.
    """

    def test_the_patch_read_is_there(self):
        self.assertIn(PATCH_READ, " ".join(fenced_lines(SHIP)))

    def test_the_merge_read_is_there(self):
        self.assertIn(MERGE_READ, " ".join(fenced_lines(SHIP)))

    def test_the_retired_ancestry_read_is_not_performed_anywhere(self):
        for line in fenced_lines(SHIP):
            self.assertFalse(line.startswith(RETIRED_READ),
                             f"{line!r} is the read a rebase landing makes permanently non-empty")


class TheFinishedPredicateSurvivesTheLandingMethod(unittest.TestCase):
    """A landed branch must read finished, and an unlanded one must not.

    The repositories are built rather than described, because the claim is
    about what git answers and not about what the flags are documented to mean.
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

    def unlanded(self, branch):
        """The `+` lines of `git cherry`: commits whose patch `main` lacks."""
        return [ln for ln in git(self.work, "cherry", "origin/main", branch).splitlines()
                if ln.startswith("+")]

    def inventive_merges(self, branch):
        """The combined diff of the range's merges: what no parent supplied."""
        return git(self.work, "log", "--merges", "--cc", "--format=",
                   f"origin/main..{branch}").strip()

    def behind(self, branch):
        """The commits of `git log origin/main..<branch>`: the retired read."""
        return [ln for ln in git(self.work, "log", "--oneline", f"origin/main..{branch}").splitlines() if ln]

    def land_by_rebase(self, branch):
        """What GitHub's rebase method does: replay onto a moved base, no merges.

        `main` gains a commit first, and it has to: replaying a commit onto the
        parent it already had reproduces it byte for byte, so git hands back
        the SHA it started with and nothing was rebased. The replay skips merge
        commits because the landing skips them, which is the whole reason a
        merge can hold content that never reaches `main`.
        """
        git(self.work, "checkout", "main")
        self.commit("meanwhile.txt", "another branch landed first", "a commit main gained")
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

    def test_a_rebase_landed_branch_holds_no_unlanded_patch(self):
        git(self.work, "checkout", "-b", "feat/x")
        self.commit("b.txt", "two", "first")
        self.commit("c.txt", "three", "second")
        self.land_by_rebase("feat/x")
        self.assert_really_rebased("feat/x")

        self.assertEqual(len(self.behind("feat/x")), 2, "the retired read still reports work, which is the point")
        self.assertEqual(self.unlanded("feat/x"), [], "every patch is upstream")
        self.assertEqual(self.inventive_merges("feat/x"), "", "and no merge invented anything")

    def test_a_merge_landed_branch_reads_the_same_way(self):
        git(self.work, "checkout", "-b", "feat/y")
        self.commit("d.txt", "four", "only")
        git(self.work, "checkout", "main")
        git(self.work, "merge", "--no-ff", "--message", "Merge pull request", "feat/y")
        git(self.work, "push", "origin", "main")
        git(self.work, "checkout", "feat/y")
        git(self.work, "fetch", "origin")

        self.assertEqual(self.behind("feat/y"), [], "a merge commit empties the range")
        self.assertEqual(self.unlanded("feat/y"), [], "and the patch read agrees, so changing method is safe")

    def test_an_unlanded_branch_is_not_mistaken_for_a_finished_one(self):
        git(self.work, "checkout", "-b", "feat/z")
        self.commit("e.txt", "five", "unlanded")
        self.assertEqual(len(self.unlanded("feat/z")), 1, "nothing landed, so the workspace must be kept")

    def test_an_ordinary_merge_forward_leaves_the_workspace_removable(self):
        # A branch brought up to date the way this repository does it. The
        # merge supplies nothing of its own, so it must not keep the workspace.
        git(self.work, "checkout", "-b", "feat/forward")
        self.commit("f.txt", "six", "the branch's work")
        git(self.work, "checkout", "main")
        self.commit("g.txt", "seven", "main moved")
        git(self.work, "checkout", "feat/forward")
        git(self.work, "merge", "--no-edit", "main")
        self.land_by_rebase("feat/forward")

        self.assertEqual(self.unlanded("feat/forward"), [])
        self.assertEqual(self.inventive_merges("feat/forward"), "", "a clean merge-forward is not work")

    def test_a_merge_that_invented_content_keeps_the_workspace(self):
        # The case `git cherry` cannot see: it prints a line only for a commit
        # with one parent, so content that exists in neither parent — a
        # conflict resolution, or an edit made while resolving — is invisible
        # to it. Without the merge read, this workspace is removed.
        git(self.work, "checkout", "-b", "feat/evil")
        self.commit("h.txt", "eight", "the branch's work")
        git(self.work, "checkout", "main")
        self.commit("i.txt", "nine", "main moved")
        git(self.work, "checkout", "feat/evil")
        git(self.work, "merge", "--no-edit", "main")
        (self.work / "only-in-the-merge.txt").write_text("resolved by hand\n", encoding="utf-8")
        git(self.work, "add", "--all")
        git(self.work, "commit", "--amend", "--no-edit")
        self.land_by_rebase("feat/evil")

        self.assertEqual(self.unlanded("feat/evil"), [], "the patch read is clear, and it is wrong")
        self.assertNotEqual(self.inventive_merges("feat/evil"), "", "the merge read is what keeps the workspace")


if __name__ == "__main__":
    unittest.main()
