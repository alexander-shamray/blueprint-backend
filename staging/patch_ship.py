"""Patch /ship's step 6 teardown to read the main checkout's branch before it pulls, with the case that pins it.

Run by apply.sh as `patch_ship.py <root> test` and then `patch_ship.py <root> runbook`, so the case is seen to fail
first.
"""

import sys
from pathlib import Path


def rewrite(path, change):
    """Apply `change` to the LF text of `path`, keeping the file's own line endings."""
    text = path.read_bytes().decode("utf-8")
    newline = "\r\n" if "\r\n" in text else "\n"
    lf = text.replace("\r\n", "\n")
    out = change(lf)
    assert out != lf, f"{path}: nothing changed"
    path.write_bytes(out.replace("\n", newline).encode("utf-8"))


def once(text, anchor, replacement):
    assert text.count(anchor) == 1, f"anchor not found exactly once: {anchor[:60]!r}"
    return text.replace(anchor, replacement)


TEST_ANCHOR = '''        self.assertNotIn("git cherry:*", granted, "a grant nothing uses is a grant something else will")
'''

TEST = '''
    def test_the_teardown_reads_the_branch_before_it_pulls(self):
        # /branch leaves another session's branch in the main checkout when main
        # was dirty, so the forked teardown asks before it pulls, and checks main.
        text = SHIP.read_text(encoding="utf-8")
        start = text.index("# 1. MERGED")
        block = text[start:text.index("git worktree prune", start)]
        self.assertLess(block.index("git branch --show-current"), block.index("git pull --ff-only"))
        self.assertIn("git fetch origin main:main", block)
        self.assertIn("--is-ancestor <merge-oid> main ", block)
'''

RUNBOOK = [
    (
        "   the instruction**, and three of the seven lines depend on which outcome\n",
        "   the instruction**, and three of the eight lines depend on which outcome\n",
    ),
    (
        "   git pull --ff-only                                   # 4. main, now containing the merge\n"
        "   git merge-base --is-ancestor <merge-oid> HEAD        # 5. and it really does contain it\n"
        "   bash .claude/scripts/git-worktree-remove.sh .claude/worktrees/<slug>  # 6. forked runs only\n"
        "   git worktree prune                                   # 7.\n",
        "   git branch --show-current                            # 4. main, or a branch another session left here\n"
        "   git pull --ff-only                                   # 5. on main; elsewhere git fetch origin main:main\n"
        "   git merge-base --is-ancestor <merge-oid> main        # 6. and main really does contain it\n"
        "   bash .claude/scripts/git-worktree-remove.sh .claude/worktrees/<slug>  # 7. forked runs only\n"
        "   git worktree prune                                   # 8.\n",
    ),
    (
        "   - Forked: line 2 returns the session to the main checkout, already on\n"
        "     `main`, so skip line 3. In place: skip lines 2 and 6.\n"
        "   - Run the pull, the ancestry check and the prune on both paths; report\n"
        "     containment, never equality: the HEAD `main` landed on, and that the\n"
        "     merge is in its history.\n",
        "   - Forked: line 2 returns the session to the main checkout, so skip line\n"
        "     3. `/branch` leaves another session's branch there when `main` was\n"
        "     dirty, so off `main` line 5 fetches and leaves that branch alone. In\n"
        "     place: skip lines 2 and 7.\n"
        "   - Run lines 4 to 6 and the prune on both paths; report containment,\n"
        "     never equality: the oid `main` landed on, and that the merge is in\n"
        "     its history.\n",
    ),
]


def runbook(text):
    for old, new in RUNBOOK:
        text = once(text, old, new)
    return text


def main(root, part):
    claude = Path(root) / ".claude"
    if part == "test":
        rewrite(claude / "scripts" / "test_ship_landing_method.py",
                lambda t: once(t, TEST_ANCHOR, TEST_ANCHOR + TEST))
    elif part == "runbook":
        rewrite(claude / "commands" / "ship.md", runbook)
    else:
        raise SystemExit(f"unknown part {part!r}")


if __name__ == "__main__":
    main(*sys.argv[1:])
