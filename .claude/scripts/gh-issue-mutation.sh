#!/usr/bin/env bash
# File one issue under the mutation route: gh-issue-create.sh with the route and
# the kind spelled here, so a command granted this entry point cannot name another
# route, and an undetected mutant files as the test gap the route's sentence says.
# docs/harness-boundaries.md, *Grant inventory and push helpers*, argues it.
set -euo pipefail

[ "$#" -eq 2 ] || {
  echo "usage: gh-issue-mutation.sh <critical|high|medium|low>" \
       "<harness|code|documentation|harness,test|code,test>" \
       "< title, blank line, body ending in the trailer" >&2
  exit 2
}
exec bash "$(dirname "$0")/gh-issue-create.sh" test-gap "$1" "$2" mutation
