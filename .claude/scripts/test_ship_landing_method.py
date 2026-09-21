"""The landing method is one choice, and several places have to agree on it.

Two subjects, and the second is the reason this file exists. The first is the
grant: a permission rule is a prefix match, so `/ship`'s frontmatter and the
invocation it spells are one fact in two places, and a method changed in one
of them is a command that is simply denied at the last step of the chain.

The second is `/ship` step 0's *finished* predicate, which decides whether a
workspace is torn down. A rebase landing replays a branch's commits onto
`main` with new SHAs, so no ancestry test can see that the work arrived, and a
predicate built on one answers *unfinished* for ever without erroring. That is
the shape `CLAUDE.md` names as this repository's most-repeated failure, so the
test's subject is what the predicate looks at rather than what it found: the
fixtures below land a branch both ways and assert which read survives it.
"""

import re
import subprocess
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SHIP = ROOT / ".claude" / "commands" / "ship.md"

# The method `gh pr merge` is asked for. Spelled as alternatives rather than as
# the one expected value, so a silent change to a third method fails here too.
METHOD_FLAGS = ("--merge", "--squash", "--rebase")


def git(cwd, *args):
    """Run git and return its stdout, refusing to continue on a failure.

    `encoding` is named because the default decodes as cp1252 on this host and
    drops a byte it cannot map without raising, which would make a comparison
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

    Prose naming a command is an argument about it; only a fenced line is an
    instruction to run one, and the grant binds the second.
    """
    lines, inside, out = path.read_text(encoding="utf-8").splitlines(), False, []
    for line in lines:
        if line.strip().startswith("```"):
            inside = not inside
        elif inside:
            out.append(line.strip())
    return out


class TheGrantAndTheInvocationAgree(unittest.TestCase):

    def test_the_method_is_spelled_the_same_way_everywhere_in_the_file(self):
        found = set(re.findall(r"gh pr merge (--merge|--squash|--rebase)", SHIP.read_text(encoding="utf-8")))
        self.assertEqual(found, {"--rebase"}, "the grant, the invocation and the prose name one method between them")

    def test_every_invocation_starts_with_a_grant(self):
        prefixes = [g[:-2] for g in granted_bash(SHIP) if g.startswith("gh pr merge") and g.endswith(":*")]
        self.assertTrue(prefixes, "no `gh pr merge` grant: the last step of the chain cannot run")
        for line in fenced_lines(SHIP):
            if line.startswith("gh pr merge"):
                self.assertTrue(any(line.startswith(p) for p in prefixes),
                                f"{line!r} does not start with any of {prefixes}, so it is denied")

    def test_the_grant_pins_a_method_rather_than_leaving_it_open(self):
        for grant in granted_bash(SHIP):
            if grant.startswith("gh pr merge"):
                self.assertTrue(any(f"gh pr merge {flag}" == grant[:-2] for flag in METHOD_FLAGS),
                                f"{grant!r} leaves the method to the caller")

    def test_the_read_that_step_0_makes_is_granted(self):
        self.assertIn("git cherry:*", granted_bash(SHIP),
                      "step 0 asks `git cherry` and the frontmatter must admit it")


class TheFinishedPredicateSurvivesTheLandingMethod(unittest.TestCase):
    """A landed branch must read *finished*, and an unlanded one must not.

    The repository fixtures are built rather than described, because the claim
    is about what git answers and not about what the flags are documented to
    mean.
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
        out = git(self.work, "cherry", "origin/main", branch)
        return [ln for ln in out.splitlines() if ln.startswith("+")]

    def behind(self, branch):
        """The commits of `git log origin/main..<branch>`: the ancestry read."""
        return [ln for ln in git(self.work, "log", "--oneline", f"origin/main..{branch}").splitlines() if ln]

    def land_by_rebase(self, branch):
        """What GitHub's rebase method does: replay the range onto a moved base.

        `main` gains a commit first, and it has to. Replaying a commit onto the
        parent it already had reproduces it byte for byte — same tree, same
        parent, same author and committer — so git hands back the SHA it
        started with and nothing has been rebased. A base that has moved is
        also the only case in which a landing rebases anything.
        """
        git(self.work, "checkout", "main")
        self.commit("meanwhile.txt", "another branch landed first", "a commit main gained")
        git(self.work, "cherry-pick", f"main..{branch}")
        git(self.work, "push", "origin", "main")
        git(self.work, "checkout", branch)
        git(self.work, "fetch", "origin")

    def test_a_rebase_landed_branch_holds_no_unlanded_patch(self):
        git(self.work, "checkout", "-b", "feat/x")
        self.commit("b.txt", "two", "first")
        self.commit("c.txt", "three", "second")
        self.land_by_rebase("feat/x")

        # The fixture is only worth its assertions if the SHAs really moved.
        self.assertNotEqual(git(self.work, "rev-parse", "HEAD"), git(self.work, "rev-parse", "origin/main"))
        self.assertEqual(len(self.behind("feat/x")), 2, "the ancestry read still reports work, which is the point")
        self.assertEqual(self.unlanded("feat/x"), [], "every patch is upstream, so the branch is finished")

    def test_a_merge_landed_branch_reads_the_same_way(self):
        git(self.work, "checkout", "-b", "feat/y")
        self.commit("d.txt", "four", "only")
        git(self.work, "checkout", "main")
        git(self.work, "merge", "--no-ff", "--message", "Merge pull request", "feat/y")
        git(self.work, "push", "origin", "main")
        git(self.work, "checkout", "feat/y")
        git(self.work, "fetch", "origin")

        self.assertEqual(self.behind("feat/y"), [], "a merge commit empties the range")
        self.assertEqual(self.unlanded("feat/y"), [], "and the patch read agrees, so the change of method is safe")

    def test_an_unlanded_branch_is_not_mistaken_for_a_finished_one(self):
        git(self.work, "checkout", "-b", "feat/z")
        self.commit("e.txt", "five", "unlanded")
        self.assertEqual(len(self.unlanded("feat/z")), 1, "nothing landed, so the workspace must be kept")

    def test_a_landing_whose_conflict_was_resolved_fails_towards_keeping_the_workspace(self):
        # A rebase that resolved a conflict lands a different patch, so the read
        # answers *unfinished* for work that did arrive. That costs a directory
        # somebody removes by hand; the opposite error costs the work in it.
        git(self.work, "checkout", "-b", "feat/conflict")
        self.commit("a.txt", "branch", "the branch's line")
        git(self.work, "checkout", "main")
        self.commit("a.txt", "resolved", "the landing, with the conflict resolved")
        git(self.work, "push", "origin", "main")
        git(self.work, "checkout", "feat/conflict")
        git(self.work, "fetch", "origin")

        self.assertEqual(len(self.unlanded("feat/conflict")), 1)


if __name__ == "__main__":
    unittest.main()
