#!/usr/bin/env bash
# List a PR's inline review comments as JSON — /review-copilot's intake.
# Read-only, fixed endpoint.
#
# Filtered by author: this feed is open to any GitHub account on a public PR,
# and /review-copilot reaches it holding `Edit` while /ship runs that command
# unattended in a loop. The filter is code rather than the command's prose,
# because a triage that skipped a prose rule is indistinguishable from one
# that ran it.
#
# stdout is the admitted subset, in the same JSON array shape as the
# unfiltered feed. The dropped count and the dropped items' locations go to
# stderr; their bodies go nowhere.
#
# Pages are slurped before filtering: with --paginate, gh emits one array per
# page, and a per-page filter would hand the caller several arrays where it
# expects one.
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/copilot-authors.sh"
pr="${1:?usage: pr-review-comments.sh <pr-number>}"
[[ "$pr" =~ ^[0-9]+$ ]] || { echo "pr must be a number" >&2; exit 2; }
# Resolved before the feed is fetched: with `set -e` a failure here stops the
# run, where the same call inline would reach jq as an empty --argjson and
# report a parse error instead of the missing owner.
admitted=$(copilot_admitted_json)
gh api "repos/{owner}/{repo}/pulls/$pr/comments" --paginate |
  jq -s 'add // []' |
  copilot_partition "$admitted" '.user.login' '.html_url' 'inline comments'
