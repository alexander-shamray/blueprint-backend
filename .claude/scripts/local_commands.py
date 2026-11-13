"""This repository's own commands, as the payload's grant and runbook cases read them through `review_helpers`.

The payload's `test_command_grants.py` and `test_command_runbooks.py` own the cases; `docs/harness-local.md`
*Ship checks* names the chain.
"""

# The commands a /ship turn runs before it pushes, which *Ship checks* names, held to denying no push.
CHAINED_BEFORE_A_PUSH = ("validate-blueprint.md", "check-links.md", "new-adr.md", "new-chapter.md")

# Its split commands' runbook ceilings, in bytes with CRLF counted as LF: each landed size and about a tenth.
CEILINGS = {
    "mutation-sweep.md": 7700,
    "validate-blueprint.md": 9900,
    "review-branch.md": 8400,
}

# Its own suites that list the tree rather than name its files, as `suite_select.py`'s `(pattern, (selector, ...))`.
TREE_READERS = (
    ("**", ("test_harness_denies_backend.CommandsEnforceTheEditingBoundariesTheyState",)),
)
