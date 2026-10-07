"""sweep-slices.sh and sweep-mark.sh: the partition, the budget and the ref.

The partition runs against this repository's own tree, so a new top-level
directory no row owns fails here before a sweep passes over it.
"""

import shutil
import tempfile
import unittest
from pathlib import Path

from review_helpers import SCRIPTS, setUpModule, run_bash

SLICES = SCRIPTS / "sweep-slices.sh"
MARK = SCRIPTS / "sweep-mark.sh"
DETACH = SCRIPTS / "git-worktree-detach.sh"
DROP = SCRIPTS / "git-worktree-drop.sh"
REPO = str(SCRIPTS.parent.parent)
BUDGET = 240000
GIT_ID = "-c user.name=t -c user.email=t@example.invalid"


def git(repo, args):
    result = run_bash(f'cd "$R" && git {GIT_ID} {args}', R=repo)
    assert result.returncode == 0, result.stderr
    return result.stdout.strip()


class Sweep:
    """A detached sweep worktree of `repo`, dropped with its slices afterwards."""

    def __init__(self, case, repo):
        self.repo = repo
        sha = git(repo, "rev-parse HEAD")
        made = run_bash(
            'cd "$R" && bash "$DETACH" "$SHA" 2>/dev/null', R=repo, DETACH=str(DETACH), SHA=sha
        )
        case.assertEqual(0, made.returncode, made.stderr)
        self.path = made.stdout.strip()
        case.addCleanup(
            lambda: run_bash(
                'cd "$R" && bash "$DROP" "$W" >/dev/null 2>&1; rm -rf "$W.slices"',
                R=repo, DROP=str(DROP), W=self.path,
            )
        )

    def slices(self, kind, *extra):
        return run_bash(
            'cd "$R" && bash "$SLICES" "$KIND" "$W" $EXTRA',
            R=self.repo, SLICES=str(SLICES), KIND=kind, W=self.path, EXTRA=" ".join(extra),
        )


def parse(stdout):
    head, slices = {}, []
    for line in stdout.splitlines():
        word, _, rest = line.partition(" ")
        if word == "slice":
            n, row, files, size, listing = rest.split(" ")
            slices.append((row, int(files), int(size), listing))
        else:
            head[word] = rest
    return head, slices


def listed(slices):
    return [p for *_, listing in slices for p in Path(listing).read_text().splitlines()]


class TheRealTree(unittest.TestCase):
    """Both partitions hold over this repository, within the budget."""

    def test_every_tracked_path_has_a_row_and_every_slice_fits(self):
        sweep = Sweep(self, REPO)
        for kind in ("bug", "security"):
            with self.subTest(kind=kind):
                run_bash('rm -rf "$W.slices"', W=sweep.path)
                result = sweep.slices(kind)
                self.assertEqual(0, result.returncode, result.stderr)
                head, slices = parse(result.stdout)
                self.assertEqual("full", head["mode"])
                self.assertGreater(len(slices), 1)
                for row, files, size, _ in slices:
                    self.assertTrue(size <= BUDGET or files == 1, (row, files, size))
                paths = listed(slices)
                self.assertEqual(len(paths), len(set(paths)), "a path in two slices")
                self.assertTrue(any(p.startswith("src/") for p in paths))
                self.assertFalse(any(p.startswith("docs/superpowers/") for p in paths))


class AClone(unittest.TestCase):
    """A shared clone, so a commit and a ref never touch the working repository."""

    def setUp(self):
        self.repo = tempfile.mkdtemp(prefix="sweepslices-")
        self.addCleanup(shutil.rmtree, self.repo, ignore_errors=True)
        cloned = run_bash('git clone -q --shared "$SRC" "$DST"', SRC=REPO, DST=self.repo)
        self.assertEqual(0, cloned.returncode, cloned.stderr)

    def commit(self, relative, text):
        target = Path(self.repo, relative)
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(text)
        git(self.repo, f'add -- "{relative}"')
        git(self.repo, f'commit -q -m "add {relative}"')
        return git(self.repo, "rev-parse HEAD")

    def mark(self, kind, sha):
        return run_bash(
            'cd "$R" && bash "$MARK" "$KIND" "$SHA"', R=self.repo, MARK=str(MARK), KIND=kind, SHA=sha
        )

    def test_a_top_level_directory_no_row_owns_refuses_the_run(self):
        self.commit("newtree/x.txt", "x\n")
        result = Sweep(self, self.repo).slices("bug")
        self.assertEqual(3, result.returncode)
        self.assertIn("no row owns: newtree/x.txt", result.stderr)

    def test_a_marked_ancestor_narrows_round_one_to_the_change(self):
        base = git(self.repo, "rev-parse HEAD")
        self.assertEqual(0, self.mark("bug", base).returncode)
        self.commit("tools/added.py", "print(1)\n")
        result = Sweep(self, self.repo).slices("bug")
        self.assertEqual(0, result.returncode, result.stderr)
        head, slices = parse(result.stdout)
        self.assertEqual(f"since {base}", head["mode"])
        self.assertEqual(["tools/added.py"], listed(slices))

    def test_full_and_a_non_ancestor_ref_both_read_everything(self):
        self.commit("tools/added.py", "print(1)\n")
        tip = git(self.repo, "rev-parse HEAD")
        sweep = Sweep(self, self.repo)
        self.assertEqual(0, self.mark("bug", tip).returncode)
        result = sweep.slices("bug", "full")
        self.assertEqual("full", parse(result.stdout)[0]["mode"])
        # A side commit is no ancestor of the swept one.
        git(self.repo, "checkout -q --detach HEAD~1")
        side = self.commit("tools/side.py", "print(2)\n")
        self.assertEqual(0, self.mark("bug", side).returncode)
        run_bash('rm -rf "$W.slices"', W=sweep.path)
        result = sweep.slices("bug")
        self.assertEqual("full", parse(result.stdout)[0]["mode"], result.stderr)

    def test_mark_takes_a_closed_kind_and_a_full_existing_commit(self):
        sha = git(self.repo, "rev-parse HEAD")
        for kind, commit, code in (
            ("other", sha, 2), ("bug", sha[:12], 2), ("bug", "0" * 40, 3), ("bug", "HEAD", 2)
        ):
            with self.subTest(kind=kind, commit=commit):
                self.assertEqual(code, self.mark(kind, commit).returncode)
        self.assertEqual(0, self.mark("security", sha).returncode)
        self.assertEqual(sha, git(self.repo, "rev-parse refs/sweeps/security"))

    def test_the_slices_go_with_the_worktree(self):
        sweep = Sweep(self, self.repo)
        self.assertEqual(0, sweep.slices("security").returncode)
        dropped = run_bash(
            'cd "$R" && bash "$DROP" "$W" && [ ! -e "$W.slices" ] && echo gone',
            R=self.repo, DROP=str(DROP), W=sweep.path,
        )
        self.assertEqual("gone", dropped.stdout.strip(), dropped.stderr)


class Arguments(unittest.TestCase):
    def test_a_path_not_shaped_like_a_sweep_worktree_is_refused(self):
        result = run_bash('bash "$SLICES" bug "$P"', SLICES=str(SLICES), P=REPO)
        self.assertEqual(2, result.returncode)

    def test_kind_and_mode_are_closed_sets(self):
        for args in ("other /tmp", "bug /tmp since", "bug"):
            with self.subTest(args=args):
                result = run_bash(f'bash "$SLICES" {args}', SLICES=str(SLICES))
                self.assertEqual(2, result.returncode)


if __name__ == "__main__":
    unittest.main()
