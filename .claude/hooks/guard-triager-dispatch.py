#!/usr/bin/env python3
"""Let the /review-grok triager dispatch the adjudicator and nothing else.

**The profile cannot say this, and neither can the command that spawns it.**
`review-grok-triager` holds `Agent` because `/review-grok`'s method starts by
dispatching `review-adjudicator`, and a type list inside a subagent's `Agent`
grant is ignored — so the profile admits every type. No command's deny list
binds it either: `/review-grok`'s names the types and is not applied inside
an agent, and `/ship` could not deny `review-grok-triager` itself, the one
type it grants. So the triager could spawn another triager: an editing agent
dispatched by one that has been reading an untrusted review, which is the
shape the split between adjudication and application exists to refuse.

**So the rule is one comparison, and it is wired only where it applies.** This
hook sits in the triager profile's own `hooks:`, not in `settings.json`, so it
judges the triager's dispatches and nobody else's: `/ship` spawning the
triager, or a sweep spawning its auditor, never reaches it. A dispatch whose
`subagent_type` is exactly `review-adjudicator` passes; anything else — the
triager itself, a built-in, a missing type, which the harness would default to
`general-purpose` — is refused.

**It fails closed, unlike the two session-wide guards.** They fail open on an
unreadable event because refusing every call would turn a defect in the guard
into a dead session. This one guards one agent's dispatches, so the cost of
refusing is a stopped triage — which `/ship` reports as a step that did not
run — and the cost of admitting is the recursion above. Exit 2 is the only
code that blocks a `PreToolUse` call; any other non-zero exit lets it through.
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


if __name__ == "__main__":
    sys.exit(main())
