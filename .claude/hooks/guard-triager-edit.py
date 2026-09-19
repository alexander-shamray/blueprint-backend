#!/usr/bin/env python3
"""Refuse the /review-grok triager every edit `/review-grok` refuses itself.

**A frontmatter `disallowed-tools` binds only the turn it was loaded in, and
not at all inside an agent**, and `/ship` step 5 runs the triage in one, so
the trees `review-grok.md` denies `Edit` would bind nothing there — in the
rounds reading an untrusted review, holding `Edit`.
`docs/harness-boundaries.md` owns the measurement.

**So the boundary sits on the profile, where a turn cannot end it.** This hook
is wired in `review-grok-triager.md`'s own `hooks:`, like
`guard-triager-dispatch.py` beside it, so it judges the triager's edits and
nobody else's.

**It holds no copy of the list.** The trees are read from `/review-grok`'s
`disallowed-tools` on every call — the `Edit(...)` entries, from the
`review-grok.md` beside this file — so the list has one owner and a path
added there is refused here with no second edit to forget.

**The triager edits the checkout it was spawned in and nothing else.** The
root is the checkout holding the event's `cwd`; a target outside it — a
sibling worktree, another repository, a temp path — is refused, and so is
any target when `cwd` is in no checkout. Inside it, the target is judged
relative to that root against the patterns, both as spelled and as resolved.

**Matching ignores case on every host.** Windows and macOS file systems do,
so `readme.md` there is `README.md`; on a case-sensitive Linux file system
this deliberately over-refuses a distinct file differing only in case, which
is the safe direction for a boundary. For the same reason the trailing dots,
spaces and `:stream` suffixes Windows drops are folded before matching.

**One question, and another guard answers the rest.** Whether a path is the
file it spells — a link, a junction, an alternate spelling — is
`guard-edit-target.py`'s subject, wired session-wide in `settings.json` and
so in force here too.

**It fails closed, like the dispatch guard and unlike the session-wide two.**
An unreadable event, or a `review-grok.md` yielding no `Edit(...)` patterns,
refuses
the edit: a guard that cannot find its rules and admits everything is no
boundary. Exit 2 is the only code that blocks a `PreToolUse` call.
"""
import json
import os
import re
import sys

EDIT_TOOLS = ("Edit", "Write", "MultiEdit", "NotebookEdit")
HERE = os.path.dirname(os.path.dirname(os.path.dirname(
    os.path.abspath(__file__))))
OWNER = os.path.join(HERE, ".claude", "commands", "review-grok.md")


def patterns():
    """`/review-grok`'s `Edit(...)` denies as patterns, or `None`."""
    try:
        with open(OWNER, encoding="utf-8") as handle:
            text = handle.read().replace("\r\n", "\n")
    except (OSError, UnicodeDecodeError):
        return None
    if not text.startswith("---\n"):
        return None
    front = text[4:].split("\n---", 1)[0]
    line = next((entry for entry in front.split("\n")
                 if entry.startswith("disallowed-tools:")), None)
    if line is None:
        return None
    found = []
    for spelled in re.findall(r"Edit\(([^)]*)\)", line):
        spelled = spelled.strip()
        while spelled.startswith("./"):
            spelled = spelled[2:]
        if spelled and spelled not in found:
            found.append(spelled)
    return [(spelled, compile_glob(spelled)) for spelled in found] or None


def compile_glob(glob):
    """A permission-rule glob as an anchored, case-insensitive regex.

    `**/` is zero or more directories, a trailing `**` everything beneath,
    `*` and `?` stay within one component. A pattern with no `**/` prefix is
    anchored at the checkout root, as the rule it copies is.
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
    return re.compile("".join(out) + r"\Z", re.IGNORECASE)


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
    """`path` relative to `root`, `/`-separated and folded, or `None`."""
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
    lexical = os.path.abspath(
        spelled if os.path.isabs(spelled) else os.path.join(cwd, spelled))
    for target, base in ((lexical, root),
                         (os.path.realpath(lexical), os.path.realpath(root))):
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
