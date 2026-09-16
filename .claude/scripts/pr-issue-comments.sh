#!/usr/bin/env bash
# List a PR's issue comments as JSON — /review-copilot's step-3 intake.
#
# Copilot's login on this feed is inferred rather than observed, a caveat
# review-copilot.md's feed table owns. The feed is open to every other
# account, so unfiltered it is an intake of strangers' text into a command
# that holds `Edit`.
#
# stdout is the admitted subset as a JSON array — the `comments` array
# unwrapped, matching the other feed helpers. Dropped count and locations go
# to stderr; bodies nowhere.
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/copilot-authors.sh"
pr="${1:?usage: pr-issue-comments.sh <pr-number>}"
[[ "$pr" =~ ^[0-9]+$ ]] || { echo "pr must be a number" >&2; exit 2; }
# Resolved before the feed is fetched: with `set -e` a failure here stops the
# run, where the same call inline would reach jq as an empty --argjson and
# report a parse error instead of the missing owner.
admitted=$(copilot_admitted_json)
gh pr view "$pr" --json comments |
  jq '.comments // []' |
  copilot_partition "$admitted" '.author.login' '.url' 'issue comments'
