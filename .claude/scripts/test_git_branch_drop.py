"""git-branch-drop.sh: a local branch deleted only once it has landed.

The helper deletes a branch when `pr-for-branch.sh` reports a pull request
for it MERGED into `main` at the branch's own tip, and the branch is checked
out nowhere. The repositories are real; GitHub is a `gh` shim on PATH that
answers the two reads `pr-for-branch.sh` makes, so the rows go through that
helper's own `jq` filter as they would in a session. Each refusal is paired
with the drop succeeding on the same fixture, so a refusal is not the helper
failing for another reason.
"""

import json
import os
import shutil
import tempfile
import unittest
from pathlib import Path

from review_helpers import SCRIPTS, setUpModule, run_bash as _run_bash  # noqa: F401

HELPER = SCRIPTS / "git-branch-drop.sh"
OWNER = "acme/widgets"

SPELLINGS = ("main", "Main", "MAIN", "heads/main", "refs/heads/main",
             "origin/main", "refs/remotes/origin/main")


def run_bash(script, **env):
    """`review_helpers.run_bash` with the caller's git configuration shut out."""
    return _run_bash("set -eu\n" + script,
                     GIT_CONFIG_GLOBAL="/dev/null",
                     GIT_CONFIG_SYSTEM="/dev/null",
                     GIT_CONFIG_NOSYSTEM="1",
                     **env)


# A remote, a checkout of it on `main`, and a branch with one commit, published.
BUILD = '''
root=$(mktemp -d)
git init -q --bare --initial-branch=main "$root/remote.git"
git init -q --initial-branch=main "$root/work"
cd "$root/work"
git config user.email test@example.invalid
git config user.name Test
git remote add origin "$root/remote.git"
echo base > a.txt && git add -A && git commit -qm "the base"
git push -q -u origin main
git checkout -qb feat/x
echo work > b.txt && git add -A && git commit -qm "the branch work"
git push -q -u origin feat/x
git checkout -q main
printf %s "$root"
'''


class Fixture(unittest.TestCase):

    def setUp(self):
        built = run_bash(BUILD)
        self.assertEqual(0, built.returncode, built.stderr)
        self.root = built.stdout.strip()
        self.assertTrue(self.root, "the fixture produced no path")
        self.addCleanup(lambda: run_bash('rm -rf "$R"', R=self.root))
        self.work = self.root + "/work"
        self.bin = tempfile.mkdtemp(prefix="ghstub-")
        self.addCleanup(shutil.rmtree, self.bin, ignore_errors=True)
        stub = Path(self.bin, "gh")
        stub.write_text(
            "#!/usr/bin/env bash\n"
            'if [ "$1 $2" = "repo view" ]; then printf "%s" "$STUB_OWNER"; exit 0; fi\n'
            # STUB_MOVE commits to the branch while its pull requests are
            # read, standing for work landing between the read and the delete.
            'if [ "$1 $2" = "pr list" ]; then\n'
            '  if [ -n "${STUB_MOVE:-}" ]; then\n'
            '    moved=$(git commit-tree -p "$STUB_MOVE" -m "landed during the read" "$STUB_MOVE^{tree}")\n'
            '    git update-ref "$STUB_MOVE" "$moved"\n'
            '  fi\n'
            '  printf "%s" "$STUB_ROWS"; exit "${STUB_EXIT:-0}"\n'
            'fi\n'
            'echo "unexpected gh call: $*" >&2; exit 9\n',
            encoding="utf-8", newline="\n")
        stub.chmod(0o755)

    def at(self, script):
        result = run_bash('cd "$W"\n' + script, W=self.work, R=self.root)
        self.assertEqual(0, result.returncode, f"{script}: {result.stderr}")
        return result.stdout.strip()

    def tip(self, branch="feat/x"):
        return self.at(f"git rev-parse refs/heads/{branch}")

    def exists(self, branch="feat/x"):
        return run_bash(f'cd "$W"\ngit show-ref --verify --quiet "refs/heads/{branch}"',
                        W=self.work).returncode == 0

    @staticmethod
    def row(state="MERGED", oid="", base="main", number=7, owner=OWNER):
        return {"number": number, "state": state, "url": f"u{number}", "headRefOid": oid,
                "baseRefName": base, "headRepository": {"nameWithOwner": owner}}

    def helper(self, branch="feat/x", rows=None, exit_code="0", move=""):
        if rows is None:
            rows = [self.row(oid=self.tip())]
        return run_bash(
            f'cd "$W"\nbash "$H" "$B"',
            W=self.work, H=str(HELPER), B=branch,
            STUB_OWNER=OWNER, STUB_ROWS=json.dumps(rows), STUB_EXIT=exit_code,
            STUB_MOVE=move, PATH=self.bin + os.pathsep + os.environ["PATH"])


class TheHelperKeepsWhatHasNotLanded(Fixture):

    def test_main_is_refused_however_it_is_spelled(self):
        # The fake answers a MERGED row at main's own tip, so the name is the
        # only thing that stops the drop.
        landed = [self.row(oid=self.tip("main"))]
        self.at("git checkout -q feat/x")
        for spelling in SPELLINGS:
            with self.subTest(spelling=spelling):
                result = self.helper(spelling, rows=landed)
                self.assertEqual(3, result.returncode, f"{spelling!r}: {result.stderr}")
                self.assertIn("refusing to delete main", result.stderr)
        self.assertTrue(self.exists("main"), "main was deleted")

    def test_a_symbolic_ref_to_main_is_refused(self):
        # `rev-parse` follows a symbolic ref, so feat/alias reads main's tip;
        # a MERGED row at that tip would then pass, and a delete that followed
        # the link too would take main.
        self.at("git checkout -q feat/x && git symbolic-ref refs/heads/feat/alias refs/heads/main")
        main_tip = self.tip("main")
        result = self.helper("feat/alias", rows=[self.row(oid=main_tip)])
        self.assertEqual(2, result.returncode, result.stderr)
        self.assertIn("symbolic ref", result.stderr)
        self.assertTrue(self.exists("main"), "main was deleted through the link")
        self.assertEqual(main_tip, self.tip("main"))
        self.assertEqual("refs/heads/main", self.at("git symbolic-ref refs/heads/feat/alias"))

    def test_an_unmerged_branch_is_kept(self):
        result = self.helper(rows=[self.row(state="OPEN", oid=self.tip())])
        self.assertEqual(7, result.returncode, result.stderr)
        self.assertIn("has not landed", result.stderr)
        self.assertTrue(self.exists())

    def test_a_branch_with_no_pull_request_is_kept(self):
        result = self.helper(rows=[])
        self.assertEqual(7, result.returncode, result.stderr)
        self.assertTrue(self.exists())

    def test_a_tip_after_the_merge_is_kept(self):
        # The pull request merged at the commit before this one, so the tip
        # carries work no merge holds.
        merged_at = self.tip()
        self.at("git checkout -q feat/x && echo more > c.txt && git add -A "
                "&& git commit -qm 'after the merge' && git checkout -q main")
        result = self.helper(rows=[self.row(oid=merged_at)])
        self.assertEqual(7, result.returncode, result.stderr)
        self.assertIn("has not landed", result.stderr)
        self.assertTrue(self.exists())

    def test_a_merge_into_another_base_is_kept(self):
        result = self.helper(rows=[self.row(oid=self.tip(), base="release")])
        self.assertEqual(7, result.returncode, result.stderr)
        self.assertTrue(self.exists())

    def test_a_forks_merged_row_is_not_this_branchs(self):
        # pr-for-branch.sh drops a row from another repository, so a
        # stranger's same-named pull request cannot land this branch.
        result = self.helper(rows=[self.row(oid=self.tip(), owner="mallory/widgets")])
        self.assertEqual(7, result.returncode, result.stderr)
        self.assertTrue(self.exists())

    def test_a_branch_checked_out_elsewhere_is_kept(self):
        self.at('git worktree add -q "$R/elsewhere" feat/x')
        result = self.helper()
        self.assertEqual(4, result.returncode, result.stderr)
        self.assertIn("checked out", result.stderr)
        self.assertTrue(self.exists())

    def test_a_branch_under_a_conflicted_rebase_elsewhere_is_kept(self):
        # What git-rebase-onto-main.sh start leaves: HEAD detached mid-replay,
        # so `git worktree list` says `detached`, and the branch's name is
        # only in the rebase state, where `git branch -D` would look.
        self.at('echo mine > a.txt && git commit -qam "main edits a.txt" '
                '&& git worktree add -q "$R/elsewhere" feat/x && cd "$R/elsewhere" '
                '&& echo theirs > a.txt && git commit -qam "the branch edits a.txt" '
                '&& { git rebase -q main > /dev/null 2>&1 || true; }')
        self.assertEqual("detached", self.at(
            'cd "$R/elsewhere" && { git symbolic-ref -q HEAD || echo detached; }'),
            "the rebase did not stop, so this test proves nothing")
        result = self.helper()
        self.assertEqual(4, result.returncode, result.stderr)
        self.assertIn("rebase", result.stderr)
        self.assertTrue(self.exists())

    def test_a_branch_a_stacked_rebase_elsewhere_will_update_is_kept(self):
        # `rebase --update-refs` on a branch stacked over feat/x names feat/x
        # only in rebase-merge/update-refs, and `git branch -D` (2.38 and
        # later) refuses it there too. The conflict is in the top commit, so
        # the replay stops after feat/x's commit with feat/x not yet moved.
        version = self.at("git version").split()[2]
        major, minor = (int(part) for part in version.split(".")[:2])
        if (major, minor) < (2, 38):
            self.skipTest(f"git {version} predates `rebase --update-refs` (2.38)")
        self.at('echo mine > a.txt && git commit -qam "main edits a.txt" '
                '&& git worktree add -q -b feat/top "$R/elsewhere" feat/x && cd "$R/elsewhere" '
                '&& echo theirs > a.txt && git commit -qam "the top edits a.txt" '
                '&& { git rebase -q --update-refs main > /dev/null 2>&1 || true; }')
        self.assertEqual("detached", self.at(
            'cd "$R/elsewhere" && { git symbolic-ref -q HEAD || echo detached; }'),
            "the rebase did not stop, so this test proves nothing")
        self.assertIn("refs/heads/feat/x", self.at(
            'cat "$(git rev-parse --git-common-dir)"/worktrees/elsewhere/rebase-merge/update-refs'),
            "feat/x is not among the refs the replay will update")
        result = self.helper()
        self.assertEqual(4, result.returncode, result.stderr)
        self.assertIn("update", result.stderr)
        self.assertTrue(self.exists())

    def test_a_branch_under_a_bisect_elsewhere_is_kept(self):
        self.at('git worktree add -q "$R/elsewhere" feat/x && cd "$R/elsewhere" '
                '&& for n in 1 2 3; do echo "$n" > "n$n.txt" && git add -A && git commit -qm "step $n"; done '
                '&& git bisect start feat/x main > /dev/null 2>&1')
        self.assertEqual("detached", self.at(
            'cd "$R/elsewhere" && { git symbolic-ref -q HEAD || echo detached; }'),
            "the bisect checked nothing out, so this test proves nothing")
        result = self.helper()
        self.assertEqual(4, result.returncode, result.stderr)
        self.assertIn("bisect", result.stderr)
        self.assertTrue(self.exists())

    def test_a_branch_checked_out_here_is_kept(self):
        self.at("git checkout -q feat/x")
        result = self.helper()
        self.assertEqual(4, result.returncode, result.stderr)
        self.assertTrue(self.exists())

    def test_a_commit_landing_during_the_read_is_kept(self):
        # The row is MERGED at the tip the helper read, and the branch moves
        # while the read runs: the delete must be of that tip, not of
        # whatever the branch has become.
        merged_at = self.tip()
        result = self.helper(rows=[self.row(oid=merged_at)], move="refs/heads/feat/x")
        self.assertNotEqual(0, result.returncode, result.stdout)
        self.assertIn("moved", result.stderr)
        self.assertTrue(self.exists(), "the branch was deleted with the commit on it")
        self.assertNotEqual(merged_at, self.tip())
        self.assertEqual(merged_at, self.at("git rev-parse refs/heads/feat/x~1"))

    def test_an_unanswered_read_keeps_it(self):
        result = self.helper(exit_code="1")
        self.assertEqual(12, result.returncode, result.stderr)
        self.assertTrue(self.exists())

    def test_a_branch_that_is_not_there_is_refused_by_its_own_message(self):
        result = self.helper("feat/gone", rows=[])
        self.assertEqual(6, result.returncode, result.stderr)
        self.assertIn("no local branch feat/gone", result.stderr)
        # The symbolic-ref probe before it has nothing to say about a ref
        # that is not there.
        self.assertNotIn("fatal", result.stderr)

    def test_a_name_that_could_be_a_flag_is_refused(self):
        for name in ("-D", "--force", "-rfoo"):
            with self.subTest(name=name):
                result = self.helper(name, rows=[])
                self.assertEqual(2, result.returncode, result.stderr)

    def test_it_takes_exactly_one_argument(self):
        result = run_bash('cd "$W"\nbash "$H"', W=self.work, H=str(HELPER))
        self.assertEqual(2, result.returncode, result.stderr)
        self.assertIn("usage: git-branch-drop.sh", result.stderr)


class TheHelperDropsWhatLanded(Fixture):

    def test_a_landed_branch_is_dropped(self):
        # The positive control for every refusal above.
        result = self.helper()
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn("dropped feat/x", result.stdout)
        self.assertFalse(self.exists())
        self.assertTrue(self.exists("main"))
        # `update-ref -d` leaves the section `branch -D` would take; the
        # fixture's `push -u` wrote one.
        self.assertEqual("", self.at("git config --get branch.feat/x.remote || true"))

    def test_one_landed_row_among_others_is_enough(self):
        # A branch reopened after a closed attempt has several rows.
        rows = [self.row(state="CLOSED", oid="0" * 40, number=5), self.row(oid=self.tip())]
        result = self.helper(rows=rows)
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertFalse(self.exists())


if __name__ == "__main__":
    unittest.main()
