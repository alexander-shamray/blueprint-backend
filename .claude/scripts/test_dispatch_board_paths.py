"""`branch_paths` against real git, in a suite of its own so the hygiene check reads it as one that builds a repository."""

import tempfile
import unittest
from pathlib import Path

import dispatch_board as db


class BranchPaths(unittest.TestCase):
    def test_branch_paths_claim_a_moved_files_old_path_and_a_non_ascii_name(self):
        # Against real git, since the defaults this guards against (rename detection, quoted paths) are git's own.
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)

            def git_in(*args):
                db.git(root, "-c", "user.name=t", "-c", "user.email=t@example.invalid",
                       "-c", "commit.gpgsign=false", *args)
            git_in("init", "-q", "-b", "main")
            (root / "src" / "old").mkdir(parents=True)
            (root / "src" / "old" / "x.cs").write_text("class X {}\n" * 20, encoding="utf-8")
            git_in("add", "-A")
            git_in("commit", "-q", "-m", "base")
            git_in("update-ref", "refs/remotes/origin/main", "HEAD")
            git_in("checkout", "-q", "-b", "fix/move")
            git_in("mv", "src/old", "src/new")
            (root / "docs").mkdir()
            (root / "docs" / "caf\u00e9.md").write_text("menu\n", encoding="utf-8")
            git_in("add", "-A")
            git_in("commit", "-q", "-m", "move")
            self.assertEqual({"src/old/x.cs", "src/new/x.cs", "docs/caf\u00e9.md"},
                             db.branch_paths(root, "fix/move"))


if __name__ == "__main__":
    unittest.main()
