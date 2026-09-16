#!/usr/bin/env bash
# List a PR's review bodies as JSON — /review-copilot's step-1 intake, where
# the `<details><summary>Suppressed comments</summary>` block arrives.
#
# This helper exists so that `gh pr view --json reviews` need not be granted
# to a command holding `Edit`: filtering the inline feed alone would read as a
# complete control while this one stays open.
#
# stdout is the admitted subset as a JSON array — the `reviews` array
# unwrapped, not the `{"reviews": [...]}` envelope, so every feed helper hands
# back the same shape. The dropped count and locations go to stderr; bodies go
# nowhere.
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/copilot-authors.sh"
pr="${1:?usage: pr-review-bodies.sh <pr-number>}"
[[ "$pr" =~ ^[0-9]+$ ]] || { echo "pr must be a number" >&2; exit 2; }
# Resolved before the feed is fetched: with `set -e` a failure here stops the
# run, where the same call inline would reach jq as an empty --argjson and
# report a parse error instead of the missing owner.
admitted=$(copilot_admitted_json)
gh pr view "$pr" --json reviews |
  jq '.reviews // []' |
  copilot_partition "$admitted" '.author.login' '.submittedAt' 'review bodies'
