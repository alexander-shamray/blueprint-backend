"""Add the TODO.md row to /branch and its deletion to /ship, in .claude/commands/."""
import pathlib
import sys

EDITS = {
    "branch.md": (
        "   Then **`EnterWorktree`** with the new directory as `path`.",
        "   Whichever row ran, before any `EnterWorktree`, add the change to *In\n"
        "   progress* in the main checkout's `TODO.md` (`#n`, what, branch and\n"
        "   worktree, `started`); from a linked worktree, carry it in the report\n"
        "   as owed (why: docs/commands/branch.md, *Step 5*).\n"
        "\n"
        "   Then **`EnterWorktree`** with the new directory as `path`.",
    ),
    "ship.md": (
        "   - Never delete the merged branch: `git branch -d` is denied; name it in\n"
        "     the report.\n",
        "   - Back in the main checkout, delete this change's row from *In progress*\n"
        "     in `TODO.md`, never from the worktree (why: docs/commands/ship.md,\n"
        "     *Step 6: the teardown*).\n"
        "   - Never delete the merged branch: `git branch -d` is denied; name it in\n"
        "     the report.\n",
    ),
}


def patch(root):
    for name, (old, new) in EDITS.items():
        path = pathlib.Path(root) / ".claude" / "commands" / name
        with open(path, newline="") as f:
            text = f.read()
        nl = "\r\n" if "\r\n" in text else "\n"
        old, new = old.replace("\n", nl), new.replace("\n", nl)
        if text.count(old) != 1:
            sys.exit(f"{name}: anchor found {text.count(old)} times, expected 1")
        with open(path, "w", newline="") as f:
            f.write(text.replace(old, new))
        print(f"patched {name}")


if __name__ == "__main__":
    patch(sys.argv[1] if len(sys.argv) > 1 else ".")
