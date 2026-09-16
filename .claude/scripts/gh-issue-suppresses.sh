#!/usr/bin/env bash
# Does this issue suppress a sweep finding?
#
# The sweeps' rule, enforced in code rather than followed as prose: an open
# issue suppresses a candidate only if the repository owner opened it, and
# anything else is untracked, so the finding files normally.
#
# The test is authorship alone, not a maintainer's label: a label is applied
# to an issue, not to its contents, and the author can rewrite the title and
# body while the label stays. Authorship is not editable.
#
# The owner is resolved, never accepted: a login taken as a parameter is one a
# prompt-injected finding chooses, and this helper decides whether to believe
# an issue. The only argument is the issue number, and the repository is the
# checkout the sweep is looking at.
#
# Suppressing is the dangerous answer — one suppressed candidate ends the sweep
# and reports convergence — so anything this helper cannot establish is not
# tracking, and a failed lookup exits 3 rather than 1 so the caller can say
# that it could not find out.
#
# Exit codes, which are the whole interface:
#   0  tracking      — the owner opened it; the candidate may be suppressed
#   1  not tracking  — someone else opened it; the candidate files normally
#   2  usage         — the argument is not an issue number
#   3  undetermined  — the lookup failed; treat as not tracking and say so
set -euo pipefail

[ "$#" -eq 1 ] ||
  { echo "usage: gh-issue-suppresses.sh <issue-number>" >&2; exit 2; }

issue="$1"
# No flags, no `--repo`, no login: an issue number is the entire parameter
# surface, and a number cannot be talked into pointing somewhere else.
[[ "$issue" =~ ^[1-9][0-9]*$ ]] ||
  { echo "issue must be a positive number: $issue" >&2; exit 2; }

owner=$(gh repo view --json owner --jq .owner.login) ||
  { echo "could not resolve the repository owner from this checkout" >&2; exit 3; }
[ -n "$owner" ] ||
  { echo "the repository owner resolved to an empty login" >&2; exit 3; }

# One fixed field set. A caller that could choose fields could ask for the body
# and route untrusted text back through a helper whose job is to keep a decision
# out of the model's hands.
author=$(gh issue view "$issue" --json author --jq '.author.login // ""') ||
  { echo "could not read issue #$issue" >&2; exit 3; }
[ -n "$author" ] ||
  { echo "issue #$issue reports no author; refusing to call it tracking" >&2; exit 3; }

# Printed on both paths, so a suppression is auditable rather than asserted and
# a near miss can be named in the round summary without the caller holding the
# `author` field itself.
if [ "$author" = "$owner" ]; then
  echo "tracking: #$issue was opened by the repository owner ($owner)"
  exit 0
fi

echo "not tracking: #$issue was opened by $author, not the repository owner ($owner)"
exit 1
