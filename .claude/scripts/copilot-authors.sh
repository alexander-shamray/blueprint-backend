#!/usr/bin/env bash
# Who /review-copilot's feeds admit, declared once and sourced by each feed
# helper, so no helper carries a literal copy of the allow-list.
#
# Copilot's login is spelt by the API a feed came from, not by the reviewer;
# review-copilot.md's feed table names which feed carries which. The `[bot]`
# form is admitted though no helper here calls the API that sends it: a
# spelling nobody sends costs nothing, and a missed one reports a review body
# as a stranger's, which fails open.
#
# The repository owner is admitted too, because review-copilot.md's decision
# table reads the owner's replies to tell which threads are already handled.
# The owner is resolved from the checkout, never passed in: a login taken as a
# parameter is one a prompt-injected finding can choose.
#
# This is a filter, not authentication: it keeps unattended triage from acting
# on text any account can write. A collaborator-permission check, as in
# grok-ledger.sh, would drop Copilot, which is not a collaborator.
COPILOT_AUTHORS='Copilot
copilot-pull-request-reviewer
copilot-pull-request-reviewer[bot]'

# Copilot's logins alone, as a JSON array. Pure — no network, so the suite can
# exercise the partition without a token.
copilot_authors_json() {
  jq -R -s 'split("\n") | map(select(length > 0))' <<<"$COPILOT_AUTHORS"
}

# Copilot's logins plus the repository owner's, as a JSON array. Needs the
# network; this is the list the helpers actually pass to copilot_partition.
copilot_admitted_json() {
  local owner
  owner=$(gh repo view --json owner --jq .owner.login)
  [ -n "$owner" ] || { echo "could not resolve repository owner" >&2; return 1; }
  copilot_authors_json | jq --arg owner "$owner" '. + [$owner] | unique'
}

# Split a JSON array of feed items into admitted (stdout) and dropped (stderr).
#
#   $1  the admitted logins, as a JSON array
#   $2  jq expression selecting an item's author login
#   $3  jq expression labelling a dropped item, never its body
#   $4  the feed's name, for the report line
#
# stdin is the whole feed as one JSON array; stdout is the admitted subset in
# the same shape, logins kept so the caller can route Copilot's items apart
# from the owner's.
#
# A dropped item's body reaches neither stream: a stranger's comment is the
# injection vector /review-copilot holds `Edit` against, and author and
# location are enough to find it by hand.
#
# The label must be a field GitHub generates — `.html_url`, `.url`,
# `.submittedAt` — never `.path` or anything else the pull request supplies:
# its author chooses the filenames, git permits a newline in one, and `jq -r`
# prints a two-line prompt through the very report saying it was dropped.
#
# The count is reported even when it is zero, because a filter that prints
# nothing when it drops nothing is indistinguishable from one that never ran.

# Printable-ASCII coercion and truncation for every field the dropped report
# prints, so a caller passing the wrong label expression gets a mangled label
# rather than a working injection. The class is the literal range
# space-to-tilde because a `\u` escape does not survive a bash
# single-quoted string into jq: the doubled backslash builds a class of
# literal characters, which still looks as though it sanitises.
CLEAN_DEF='def clean: tostring | gsub("[^ -~]"; "?") | .[0:200];'

copilot_partition() {
  local authors="$1" author_expr="$2" label_expr="$3" feed="$4"
  local input admitted dropped_count dropped_lines
  input=$(cat)

  admitted=$(jq --argjson a "$authors" \
    "[ .[] | select((${author_expr}) as \$l | \$a | index(\$l)) ]" <<<"$input")
  dropped_lines=$(jq -r --argjson a "$authors" \
    "${CLEAN_DEF} [ .[] | select(((${author_expr}) as \$l | \$a | index(\$l)) | not) ] |
     .[] | \"  dropped \" + (((${author_expr}) // \"(no login)\") | clean) + \" at \" +
     (((${label_expr}) // \"(no location)\") | clean)" <<<"$input")
  dropped_count=$(jq --argjson a "$authors" \
    "[ .[] | select(((${author_expr}) as \$l | \$a | index(\$l)) | not) ] | length" \
    <<<"$input")

  {
    echo "copilot-filter [$feed]: admitted $(jq length <<<"$admitted"), dropped $dropped_count"
    [ -n "$dropped_lines" ] && echo "$dropped_lines"
  } >&2

  echo "$admitted"
}
