#!/usr/bin/env bash
# Does this issue suppress a sweep finding? Only an open one the repository owner opened: authorship, not a label,
# which stays on an issue its author can rewrite; the owner is resolved, never a parameter (docs/commands/bug-sweep.md).
# Exit 0 tracking: the candidate may be suppressed. 1 not tracking, someone else's or closed: it files. 2 usage. 3 the
# lookup failed, which is never tracking, since one suppressed candidate ends the sweep: treat it as not tracking.
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
read=$(gh issue view "$issue" --json author,state --jq '[.author.login // "", .state // ""] | @tsv') ||
  { echo "could not read issue #$issue" >&2; exit 3; }
read=${read//$'\r'/}
author=${read%%$'\t'*}
state=""
[ "$author" = "$read" ] || state=${read#*$'\t'}
[ -n "$author" ] ||
  { echo "issue #$issue reports no author; refusing to call it tracking" >&2; exit 3; }
[ -n "$state" ] ||
  { echo "issue #$issue reports no state; refusing to call it tracking" >&2; exit 3; }

# A closed one does not suppress, the owner's included, so a finding that reproduces after the close re-files.
if [ "$state" != "OPEN" ]; then
  echo "not tracking: #$issue is $state, and only an open issue suppresses"
  exit 1
fi

# Printed on both paths, so a suppression is auditable rather than asserted and
# a near miss can be named in the round summary without the caller holding the
# `author` field itself.
if [ "$author" = "$owner" ]; then
  echo "tracking: #$issue was opened by the repository owner ($owner)"
  exit 0
fi

echo "not tracking: #$issue was opened by $author, not the repository owner ($owner)"
exit 1
