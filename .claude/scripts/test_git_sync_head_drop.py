"""git-sync-head-drop.sh: a merged sync's branch deleted from origin, and nothing else.

Real repositories, and a `gh` shim answering `pr-for-branch.sh`'s two reads.
Each refusal leaves origin's branch where a merged row at its tip would have deleted it.
"""

import json
import tempfile
import unittest
from pathlib import Path

from review_helpers import SCRIPTS, setUpModule, temp_root, run_bash as _run_bash  # noqa: F401

HELPER = SCRIPTS / "git-sync-head-drop.sh"
OWNER = "acme/widgets"
SYNC = "chore/harness-sync-abcdef0"
OTHER_OID = "d84f1368f0000000000000000000000000000000"


def run_bash(script, **env):
    """`review_helpers.run_bash` with the caller's git configuration shut out."""
    return _run_bash("set -eu\n" + script, GIT_CONFIG_GLOBAL="/dev/null", GIT_CONFIG_SYSTEM="/dev/null",
                     GIT_CONFIG_NOSYSTEM="1", **env)


BUILD = f'''
git init -q --bare --initial-branch=main "$R/remote.git"
git init -q --initial-branch=main "$R/work"
cd "$R/work"
git config user.email test@example.invalid
git config user.name Test
git remote add origin "$R/remote.git"
echo base > a.txt && git add -A && git commit -qm "the base"
git push -q -u origin main
git checkout -qb {SYNC}
echo work > b.txt && git add -A && git commit -qm "the sync"
git push -q -u origin {SYNC}
git branch feat/x
git push -q origin feat/x
git checkout -q main
'''


class Fixture(unittest.TestCase):

    def setUp(self):
        self.root = temp_root(self)
        built = run_bash(BUILD, R=self.root)
        self.assertEqual(0, built.returncode, built.stderr)
        self.work = self.root + "/work"
        holder = tempfile.TemporaryDirectory(prefix="ghstub-")
        self.addCleanup(holder.cleanup)
        self.bin = holder.name
        stub = Path(self.bin, "gh")
        # STUB_MOVE pushes to the branch while its pull requests are read, standing for a push between read and delete.
        stub.write_text(
            "#!/usr/bin/env bash\n"
            'if [ "$1 $2" = "repo view" ]; then printf "%s" "$STUB_OWNER"; exit 0; fi\n'
            'if [ "$1 $2" = "pr list" ]; then\n'
            '  if [ -n "${STUB_MOVE:-}" ]; then\n'
            '    moved=$(git commit-tree -p "$STUB_MOVE" -m "pushed during the read" "$STUB_MOVE^{tree}")\n'
            '    git push -q origin "$moved:refs/heads/$STUB_MOVE"\n'
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

    def on_origin(self, branch=SYNC):
        return bool(self.at(f'git --git-dir="$R/remote.git" for-each-ref "refs/heads/{branch}"'))

    def tip(self):
        return self.at(f"git rev-parse refs/heads/{SYNC}")

    @staticmethod
    def row(state="MERGED", oid="", base="main", number=7, owner=OWNER):
        return {"number": number, "state": state, "url": f"u{number}", "headRefOid": oid,
                "baseRefName": base, "headRepository": {"nameWithOwner": owner}}

    def helper(self, branch=SYNC, rows=None, exit_code="0", move=""):
        if rows is None:
            rows = [self.row(oid=self.tip())]
        # The stubs go first inside the shell, as test_git_branch_drop.py says why.
        return run_bash(
            'cd "$W"\nPATH="$(cd "$STUB_BIN" && pwd):$PATH"\nbash "$H" "$B"',
            W=self.work, R=self.root, H=str(HELPER), B=branch, STUB_BIN=self.bin,
            STUB_OWNER=OWNER, STUB_ROWS=json.dumps(rows), STUB_EXIT=exit_code, STUB_MOVE=move)

    def assert_kept(self, result, code, words):
        self.assertEqual(code, result.returncode, result.stderr)
        self.assertIn(words, result.stderr)
        self.assertTrue(self.on_origin(), "a refusal deleted origin's branch")


class AMergedHeadGoes(Fixture):

    def test_a_merged_row_at_origins_tip_deletes_the_branch_from_origin_alone(self):
        tip = self.tip()
        result = self.helper()
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(f"deleted {SYNC} from origin, merged into main at {tip}", result.stdout.strip())
        self.assertFalse(self.on_origin())
        self.assertEqual(tip, self.tip(), "the local branch is git-branch-drop.sh's")
        self.assertTrue(self.on_origin("main"))
        self.assertTrue(self.on_origin("feat/x"))

    def test_a_closed_row_beside_the_merged_one_holds_nothing(self):
        rows = [self.row(state="CLOSED", oid=OTHER_OID, number=6), self.row(oid=self.tip())]
        self.assertEqual(0, self.helper(rows=rows).returncode)
        self.assertFalse(self.on_origin())


class AnythingElseStays(Fixture):

    def test_no_name_but_a_sync_heads(self):
        for name in ("feat/x", "main", "chore/harness-sync-workflow", "chore/harness-sync-ABCDEF0",
                     "chore/harness-sync-abcdef01", "refs/heads/" + SYNC, "--delete"):
            with self.subTest(name=name):
                self.assert_kept(self.helper(name, rows=[self.row(oid=self.tip())]), 2, "not a sync head")
        self.assertTrue(self.on_origin("feat/x"))
        self.assertTrue(self.on_origin("main"))

    def test_an_argument_more_or_less_is_usage(self):
        for arguments in ("", f"{SYNC} {SYNC}"):
            with self.subTest(arguments=arguments):
                result = run_bash(f'cd "$W"\nbash "$H" {arguments}', W=self.work, H=str(HELPER))
                self.assert_kept(result, 2, "usage: git-sync-head-drop.sh")

    def test_a_merged_row_at_another_tip_is_work_no_merge_holds(self):
        self.assert_kept(self.helper(rows=[self.row(oid=OTHER_OID)]), 7, "at origin's tip")

    def test_a_merge_into_another_base_has_not_landed(self):
        self.assert_kept(self.helper(rows=[self.row(oid=self.tip(), base="develop")]), 7, "at origin's tip")

    def test_a_closed_row_alone_is_not_a_merge(self):
        self.assert_kept(self.helper(rows=[self.row(state="CLOSED", oid=self.tip())]), 7, "at origin's tip")

    def test_no_row_is_not_a_merge(self):
        self.assert_kept(self.helper(rows=[]), 7, "at origin's tip")

    def test_an_open_row_beside_a_merged_one_is_a_sync_still_to_be_taken(self):
        rows = [self.row(oid=self.tip()), self.row(state="OPEN", oid=self.tip(), number=8)]
        self.assert_kept(self.helper(rows=rows), 7, "still open")

    def test_rows_it_cannot_read_are_not_an_answer(self):
        self.assert_kept(self.helper(exit_code="1"), 12, "whether it landed is unknown")

    def test_a_push_during_the_read_is_held_off_by_the_lease(self):
        result = self.helper(move=SYNC)
        self.assertNotIn(result.returncode, (0, 2, 6, 7, 12), result.stderr)
        self.assertIn("stale info", result.stderr)
        self.assertNotEqual(self.tip(), self.at(f'git --git-dir="$R/remote.git" rev-parse refs/heads/{SYNC}'))

    def test_a_head_origin_lacks_is_said_so(self):
        self.at(f"git push -q origin --delete {SYNC}")
        result = self.helper()
        self.assertEqual(6, result.returncode, result.stderr)
        self.assertIn(f"origin has no {SYNC}", result.stderr)

    def test_a_longer_ref_ending_in_the_name_does_not_stand_for_the_head(self):
        # `ls-remote` matches a pattern's tail, so without the whole-name read this one's oid is the tip leased.
        longer = f"archive/refs/heads/{SYNC}"
        self.at(f"git push -q origin refs/heads/main:refs/heads/{longer}")
        self.at(f"git push -q origin --delete {SYNC}")
        result = self.helper()
        self.assertEqual(6, result.returncode, result.stderr)
        self.assertTrue(self.on_origin(longer))


class ARewrittenOriginStays(Fixture):
    """The tip is read from where `get-url` says and pushed to where `--push` says: the two must be the one added."""

    def test_a_pushurl_elsewhere(self):
        self.at('git init -q --bare "$R/elsewhere.git"\ngit config remote.origin.pushurl "$R/elsewhere.git"')
        self.assert_kept(self.helper(), 7, "pushes to")

    def test_a_second_push_url(self):
        self.at('git init -q --bare "$R/elsewhere.git"\n'
                'git remote set-url --add --push origin "$R/remote.git"\n'
                'git remote set-url --add --push origin "$R/elsewhere.git"')
        self.assert_kept(self.helper(), 7, "pushes to")

    def test_an_insteadof_on_both_directions(self):
        self.at('git init -q --bare "$R/elsewhere.git"\n'
                'git config "url.$R/elsewhere.git.insteadOf" "$R/remote.git"')
        self.assert_kept(self.helper(), 7, "fetches from")


if __name__ == "__main__":
    unittest.main()
