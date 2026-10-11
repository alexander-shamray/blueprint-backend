#!/usr/bin/env python3
"""Patch .claude/commands/validate-blueprint.md so its main session only dispatches (token-plan step 7b).

    patch_validate_blueprint.py ROOT [--check]  (--check prints the size and writes nothing)
"""

import sys
from pathlib import Path

EDITS = [
    # The dispatch needs the agent type; the frontmatter's Edit denies are the session's, so they stay as they are.
    ("Bash(ls:*)\ndisallowed-tools:", "Bash(ls:*), Agent(general-purpose)\ndisallowed-tools:"),
    (
        "- Fix every named site **inside the change's touch set** in one pass (Class\n"
        "  C: the new ADR and the section it amends; no class: the three paths, a\n"
        "  restatement fixed by citing the owner); report one outside and leave it\n"
        "  (`docs/change-locality.md` §2). Never half-apply a reconciliation.\n",
        "- Name the fix for every site **inside the change's touch set** (Class C:\n"
        "  the new ADR and the section it amends; no class: the three paths, a\n"
        "  restatement fixed by citing the owner); report one outside and leave it\n"
        "  (`docs/change-locality.md` §2). Never propose half a reconciliation.\n",
    ),
    (
        "Keep going until a full pass produces no findings.\n",
        "## Dispatch\n"
        "\n"
        "This session only dispatches. It spawns one `general-purpose` agent per pass\n"
        "(checks 1–3, 4–6, 7–9, 10), each handed the scope, the three paths and the\n"
        "*Method* bullets and told to edit nothing, and holds only the JSON each\n"
        "returns: `claim`, `sites` as `file:line`, `conflict`, `owner`. It reads no\n"
        "chapter whole, opens only a `file:line` a finding cites, and drops one that\n"
        "does not hold. One dispatch, not a loop: a fix is made by the caller — a\n"
        "separate task after `/clear`, or /ship's step 2 — never by an implementer\n"
        "spawned here. (why: docs/commands/validate-blueprint.md, *Dispatch*)\n",
    ),
    ("  Resolved: <what you changed and why that side won>\n", "  Resolved: <the edit to make and why that side wins>\n"),
    (
        "End with the pass count and whether the last pass was clean — do\nnot round up.\n",
        "End with the passes dispatched and the findings each returned — do\nnot round up.\n",
    ),
    ("- Edit `src/` or `tests/` (report code findings), or touch `.remember/`.\n",
     "- Edit anything, or spawn an implementer (report code findings), or touch `.remember/`.\n"),
    ("The frontmatter enforces this. (why:", "The frontmatter enforces the paths it lists. (why:"),
]


def main(argv: list[str]) -> int:
    root = Path(argv[1])
    path = root / ".claude" / "commands" / "validate-blueprint.md"
    text = path.read_bytes().decode("utf-8")
    crlf = "\r\n" in text
    if crlf:
        text = text.replace("\r\n", "\n")
    for old, new in EDITS:
        if text.count(old) != 1:
            print(f"passage found {text.count(old)} times, not once: {old[:60]!r}", file=sys.stderr)
            return 1
        text = text.replace(old, new)
    print(f"{len(text.encode('utf-8'))} bytes")
    if "--check" not in argv:
        path.write_bytes((text.replace("\n", "\r\n") if crlf else text).encode("utf-8"))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
