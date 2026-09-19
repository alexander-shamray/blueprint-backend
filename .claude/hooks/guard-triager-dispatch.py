#!/usr/bin/env python3
"""Let the /review-grok triager dispatch the adjudicator and nothing else.

A type list inside a subagent's `Agent` grant is ignored, and no command's
deny list binds inside an agent, so the profile alone admits every type, the
triager itself included. Wired in the profile's own `hooks:`, this judges the
triager's dispatches and nobody else's. It fails closed, because a wrong
refusal costs one stopped triage, and exit 2 is the only code that blocks a
`PreToolUse` call. `docs/harness-boundaries.md` owns the argument.
"""
import json
import sys

ADMITTED = "review-adjudicator"
DISPATCH_TOOLS = ("Agent", "Task")


def main():
    try:
        event = json.loads(sys.stdin.buffer.read().decode("utf-8"))
    except (json.JSONDecodeError, ValueError, UnicodeDecodeError):
        print("guard-triager-dispatch: unreadable hook event; refusing",
              file=sys.stderr)
        return 2
    if not isinstance(event, dict):
        print("guard-triager-dispatch: hook event is not an object; refusing",
              file=sys.stderr)
        return 2

    if event.get("tool_name") not in DISPATCH_TOOLS:
        return 0
    tool_input = event.get("tool_input")
    wanted = tool_input.get("subagent_type") if isinstance(
        tool_input, dict) else None
    if wanted == ADMITTED:
        return 0

    json.dump(
        {
            "hookSpecificOutput": {
                "hookEventName": "PreToolUse",
                "permissionDecision": "deny",
                "permissionDecisionReason": (
                    f"the /review-grok triager dispatches {ADMITTED} and "
                    f"nothing else; refused {wanted!r} "
                    "(.claude/hooks/guard-triager-dispatch.py)"),
            }
        },
        sys.stdout,
    )
    return 0


def run():
    """`main()`, with any unexpected exception refused rather than admitted.

    A crash exits 1, which a `PreToolUse` hook treats as non-blocking.
    """
    try:
        return main()
    except Exception as error:
        print(f"guard-triager-dispatch: {type(error).__name__}: {error}; "
              "refusing", file=sys.stderr)
        return 2


if __name__ == "__main__":
    sys.exit(run())
