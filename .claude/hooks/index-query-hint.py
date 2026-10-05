#!/usr/bin/env python3
"""Point a locate, explain, references, impact or named-symbol prompt at the
code index; any other prompt, and any slash command but `/ship` and `/branch`,
gets nothing. Exit 2 would erase the prompt, so it returns 0 whatever happens."""

from __future__ import annotations

import json
import re
import sys

# Enough for a question; a pasted log behind it is not one.
CAP = 4096

CBX = "bash .claude/skills/codebase-index/scripts/cbx"

# Checked in order, so a prompt carrying two questions is routed by the one
# that asks the most of the index. Each command is a row of SKILL.md's route
# table, `search` with the `--limit 3` the paragraph under it sets.
ROUTES = (
    ("change-impact", r"\b(?:what (?:breaks|depends)|impact of)\b",
     f'{CBX} impact "X" --json'),
    ("references", r"\b(?:who calls|what (?:calls|uses))\b",
     f'{CBX} refs "X" --json'),
    # A sentence end stops `how does … work`; a dot inside a name does not.
    ("how-it-works", r"\bhow does (?:[^.?!]|\.[^\s.?!])* work(?:s|ing)?\b",
     f'{CBX} explain "X" --session <tag> --json'),
    ("named-symbol", r"\bfind (?:the )?(?:class|method|handler)(?:es|s)?\b",
     f'{CBX} symbol "X" --json'),
    ("locate", r"\bwhere (?:is|are|does)\b",
     f'{CBX} search "X" --limit 3 --session <tag> --json'),
)

# The two commands whose arguments are a task rather than an option.
TASKS = re.compile(r"^/(?:ship|branch)(?=\s|$)")


def question(prompt: str) -> str:
    """The text matched: capped, lowered, spaced once, and a task's command
    stripped; any other slash command leaves nothing to match."""
    text = " ".join(prompt[:CAP].lower().split())
    if text.startswith("/"):
        if not TASKS.match(text):
            return ""
        text = TASKS.sub("", text, count=1)
    return text


def hint(prompt: str) -> str | None:
    text = question(prompt)
    for kind, pattern, command in ROUTES:
        if re.search(pattern, text):
            return (
                f"This reads as a {kind} question, so load the codebase-index "
                "skill and query the index before any Grep or Read of the tree, "
                f"starting with `{command}`. Pick one session tag for this "
                "conversation and pass it to every search and explain; start a "
                "new one after a clear or compaction, and never hand it to a "
                "subagent. Read only the recommended line ranges, and treat an "
                "empty refs or impact result as inconclusive.")
    return None


def main() -> int:
    # Bytes, decoded here: Python reads a Windows pipe in the ANSI code page,
    # which would garble a prompt holding anything outside it.
    try:
        event = json.loads(sys.stdin.buffer.read().decode("utf-8", "replace") or "{}")
        prompt = event.get("prompt") if isinstance(event, dict) else None
        found = hint(prompt) if isinstance(prompt, str) else None
        if found:
            print(json.dumps({"hookSpecificOutput": {
                "hookEventName": "UserPromptSubmit",
                "additionalContext": found}}))
    except Exception:  # noqa: BLE001 - a hint that fails says nothing
        pass
    return 0


if __name__ == "__main__":
    sys.exit(main())
