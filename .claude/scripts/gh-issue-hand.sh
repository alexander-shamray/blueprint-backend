#!/usr/bin/env bash
# File one issue under the hand route: gh-issue-create.sh with the route spelled
# here, so a command granted this entry point cannot name another route.
# docs/harness-boundaries.md, *Grant inventory and push helpers*, argues it.
set -euo pipefail

[ "$#" -eq 3 ] || {
  echo "usage: gh-issue-hand.sh <security|bug|test-gap>" \
       "<critical|high|medium|low> <harness|code|documentation|harness,test|code,test>" \
       "< title, blank line, body ending in the trailer" >&2
  exit 2
}
exec bash "$(dirname "$0")/gh-issue-create.sh" "$1" "$2" "$3" hand
