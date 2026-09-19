#!/usr/bin/env python3
"""Refuse the /review-grok triager every edit `/review-grok` refuses itself.

A frontmatter `disallowed-tools` is not applied inside an agent, so the trees
are read from `review-grok.md`'s `Edit(...)` entries on every call: one owner,
no copy here. A target outside the checkout holding the event's `cwd` is
refused too. It fails closed, because a guard that cannot find its rules and
admits everything is no boundary, and exit 2 is the only code that blocks a
`PreToolUse` call. `docs/harness-boundaries.md` owns the argument.
"""
import json
import os
import re
import sys

EDIT_TOOLS = ("Edit", "Write", "MultiEdit", "NotebookEdit")
HERE = os.path.dirname(os.path.dirname(os.path.dirname(
    os.path.abspath(__file__))))
COMMAND = os.path.join(".claude", "commands", "review-grok.md")
# Beside this file, so the guard and the list it enforces are one checkout's.
OWNER = os.path.join(HERE, COMMAND)
FRONTMATTER = re.compile(r"---\n(.*?)\n---[ \t]*(?:\n|\Z)", re.DOTALL)
EVERYTHING = "**"


def patterns(owner=None):
    """A command's `Edit(...)` denies as patterns, or `None`.

    A bare `Edit` deny is every path. Frontmatter with no closing line is no
    frontmatter, so a body line cannot pass for the list.
    """
    try:
        with open(owner or OWNER, encoding="utf-8") as handle:
            text = handle.read().replace("\r\n", "\n")
    except (OSError, UnicodeDecodeError):
        return None
    front = FRONTMATTER.match(text)
    if front is None:
        return None
    line = next((entry for entry in front.group(1).split("\n")
                 if entry.startswith("disallowed-tools:")), None)
    if line is None:
        return None
    line = line.split(":", 1)[1]
    found = []
    if re.search(r"(?:^|,)\s*Edit\s*(?:,|$)", line):
        found.append(EVERYTHING)
    for spelled in re.findall(r"Edit\(([^)]*)\)", line):
        spelled = spelled.strip()
        while spelled.startswith("./"):
            spelled = spelled[2:]
        if spelled and spelled not in found:
            found.append(spelled)
    return [(spelled, compile_glob(spelled)) for spelled in found] or None


def compile_glob(glob):
    """A permission-rule glob as an anchored, case-insensitive regex.

    A double star with a slash is zero or more directories, a trailing
    double star everything beneath, and `*` and `?` stay within one
    component. A pattern that does not open with a double star is anchored
    at the checkout root, as the rule it copies is.
    """
    out = []
    i = 0
    while i < len(glob):
        if glob.startswith("**/", i):
            out.append("(?:.*/)?")
            i += 3
        elif glob.startswith("**", i):
            out.append(".*")
            i += 2
        elif glob[i] == "*":
            out.append("[^/]*")
            i += 1
        elif glob[i] == "?":
            out.append("[^/]")
            i += 1
        else:
            out.append(re.escape(glob[i]))
            i += 1
    # DOTALL, because a POSIX file name may hold a newline and `.` alone
    # would stop at it.
    return re.compile("".join(out) + r"\Z", re.IGNORECASE | re.DOTALL)


def checkout_root(path):
    """The nearest ancestor of `path` holding a `.git` entry, or `None`."""
    current = os.path.abspath(path)
    while True:
        if os.path.exists(os.path.join(current, ".git")):
            return current
        parent = os.path.dirname(current)
        if parent == current:
            return None
        current = parent


def relative(path, root):
    """`path` relative to `root`, `/`-separated and folded, or `None`.

    Folded means without the trailing dots, spaces and `:stream` suffixes
    Windows drops; with the case-blind match, that over-refuses on a
    case-sensitive file system, which is the safe direction for a boundary.
    """
    try:
        rel = os.path.relpath(path, root)
    except ValueError:
        # Another drive on Windows: no relative path exists.
        return None
    rel = rel.replace(os.sep, "/")
    if rel == "." or rel == ".." or rel.startswith("../"):
        return None
    parts = [part.split(":", 1)[0].rstrip(". ") for part in rel.split("/")]
    return "/".join(parts)


def refusal(reason):
    json.dump(
        {
            "hookSpecificOutput": {
                "hookEventName": "PreToolUse",
                "permissionDecision": "deny",
                "permissionDecisionReason": (
                    f"the /review-grok triager {reason} "
                    "(.claude/hooks/guard-triager-edit.py)"),
            }
        },
        sys.stdout,
    )
    return 0


def main():
    try:
        event = json.loads(sys.stdin.buffer.read().decode("utf-8"))
    except (json.JSONDecodeError, ValueError, UnicodeDecodeError):
        print("guard-triager-edit: unreadable hook event; refusing",
              file=sys.stderr)
        return 2
    if not isinstance(event, dict):
        print("guard-triager-edit: hook event is not an object; refusing",
              file=sys.stderr)
        return 2

    if event.get("tool_name") not in EDIT_TOOLS:
        return 0
    tool_input = event.get("tool_input")
    if not isinstance(tool_input, dict):
        print("guard-triager-edit: tool_input is not an object; refusing",
              file=sys.stderr)
        return 2
    spelled = tool_input.get("file_path") or tool_input.get("notebook_path")
    if not isinstance(spelled, str) or not spelled:
        print("guard-triager-edit: no target path; refusing", file=sys.stderr)
        return 2

    rules = patterns()
    if rules is None:
        print(f"guard-triager-edit: no Edit(...) denies read from {OWNER}; "
              "refusing", file=sys.stderr)
        return 2

    cwd = event.get("cwd")
    if not isinstance(cwd, str) or not cwd:
        cwd = os.getcwd()
    root = checkout_root(cwd)
    if root is None:
        return refusal(f"edits inside its checkout only; cwd {cwd!r} is in "
                       "no checkout")
    # The checkout being edited may carry a newer list than this guard's own,
    # a sibling worktree's for one. Its denies are added, which can only
    # narrow; a list there that cannot be read refuses.
    local = os.path.join(root, COMMAND)
    if (os.path.exists(local)
            and os.path.realpath(local) != os.path.realpath(OWNER)):
        added = patterns(local)
        if added is None:
            print("guard-triager-edit: no Edit(...) denies read from "
                  f"{local}; refusing", file=sys.stderr)
            return 2
        held = {glob for glob, _ in rules}
        rules = rules + [rule for rule in added if rule[0] not in held]
    joined = spelled if os.path.isabs(spelled) else os.path.join(cwd, spelled)
    # `realpath` of the spelling, not of its lexical form: `abspath` collapses
    # a `..` that follows a link before the link is ever read.
    for target, base in ((os.path.abspath(joined), root),
                         (os.path.realpath(joined), os.path.realpath(root))):
        rel = relative(target, base)
        if rel is None:
            return refusal(f"edits inside {root!r} only; refused {spelled!r}")
        for glob, pattern in rules:
            if pattern.match(rel):
                return refusal(
                    f"may not edit {rel!r}: it is under /review-grok's "
                    f"Edit({glob}) deny")
    return 0


def run():
    """`main()`, with any unexpected exception refused rather than admitted.

    A crash exits 1, which a `PreToolUse` hook treats as non-blocking, so an
    uncaught error anywhere below would let the edit through.
    """
    try:
        return main()
    except Exception as error:
        print(f"guard-triager-edit: {type(error).__name__}: {error}; "
              "refusing", file=sys.stderr)
        return 2


if __name__ == "__main__":
    sys.exit(run())
