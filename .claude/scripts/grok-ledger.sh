#!/usr/bin/env bash
# The Grok check ledger: reserves a check slot on a PR, releases one, or
# counts what stands.
#
# The write is a helper rather than a prefix grant on `gh pr comment`, which
# would also license --edit-last, --delete-last, --body-file and --repo. The
# helper fixes the operation and shape-checks its parameters, and the Edit deny
# on this directory keeps the invoking session from rewriting it first.
#
# The read lives here because PR comments are unauthenticated state: on a
# public PR anyone can post a reserved line to jam the cap shut, or a released
# line to hold it open. `count` accepts only whole bodies matching the shapes
# this file writes, from authors whose repository permission is verified as
# write or better — any such account, so a run resumed under another login
# still reads the ledger. A 404 from the collaborators API is an untrusted
# author; any other verification failure stops the helper, because a cap whose
# trust check silently drops rows re-arms on a network error. The fold takes
# the last event per N, so a released slot can be re-spent and a stale release
# cannot hide a later one.
#
# `reserve` and `release` are grok-review.sh's verbs, not the caller's: it
# posts the reservation immediately before the model call it accounts for, so
# invoking a review and spending a slot are one operation. .claude/settings.json
# denies both spellings to a session; they stay because `count` still parses
# released rows, and a human reconciling a wrongly spent slot needs them.
#
# `reserve` is an election, not just a write. Two resumed runs can read the
# same count and claim the same slot, and posting is not atomic, so the claim
# is settled after the fact: the first reservation posted after the slot's
# most recent release wins, and a later claimant exits 4 without running
# anything. A losing claim is a concurrent run mid-check on this PR, and two
# Grok runs share one root suggestions.md, so the loser stops its loop rather
# than taking the next slot. `count` folds duplicate claims into one spend.
set -euo pipefail

# The ceiling, declared once and enforced here, so the bound is a limit a
# machine imposes rather than a rule ship.md states.
CEILING=6

# Reading is wider than writing: the denominator is part of the comment shape
# `count` folds on, so narrowing the read to a new ceiling would stop matching
# rows already posted, and the cap would re-arm on a PR that has spent it.
# Only retired denominators are listed; the current one is derived, because a
# second literal lets a ceiling change hide every new reservation from `count`.
# A ceiling change moves `CEILING` and appends the value it replaced here.
LEDGER_RETIRED_DENOMINATORS='12'
LEDGER_DENOMINATORS="$CEILING|$LEDGER_RETIRED_DENOMINATORS"

# Every slot this ledger could ever have written: 1..12 while twelve is the
# largest denominator above, so a ceiling above twelve widens this in the same
# edit or its own rows stop matching. Not `[1-9][0-9]*`, which admits `13/12`,
# a row this file never wrote, and lets a write-verified author inflate the
# count past any ceiling that has existed.
LEDGER_READ_SLOTS='[1-9]|1[0-2]'

usage() {
  echo "usage: grok-ledger.sh <pr-number> reserve <n> <full|recheck>" >&2
  echo "       grok-ledger.sh <pr-number> release <n>" >&2
  echo "       grok-ledger.sh <pr-number> converge <n>" >&2
  echo "       grok-ledger.sh <pr-number> count" >&2
  echo "       grok-ledger.sh <pr-number> status" >&2
  exit 2
}

pr="${1:-}"
op="${2:-}"
n="${3:-}"
mode="${4:-}"

# The PR number keeps gh pointed at an explicit target. N's domain is checked
# further down, against $CEILING for a write and against $LEDGER_READ_SLOTS for
# a read.
[[ "$pr" =~ ^[0-9]+$ ]] || usage

# One fixed read, shared by count and the election: whole comment bodies that
# match a ledger shape, by write-verified authors, oldest first (the REST
# endpoint returns issue comments in posting order). Shape filtering happens
# on the whole body in jq — anchored test(), no multiline flag — so a
# ledger-looking line buried inside a longer comment is not state, and every
# row that survives is one line. The regex backslashes are doubled because a
# jq string spends one level on its own escaping: \\( reaches the regex
# engine as \(, where a bare \( would be jq's interpolation syntax.
ledger_rows() {
  local id login body verdict perm out row_slot row_den
  declare -A seen=()
  gh api "repos/{owner}/{repo}/issues/$pr/comments" --paginate \
    --jq '.[]
      | select(.body | test("^Grok check ('"$LEDGER_READ_SLOTS"')/('"$LEDGER_DENOMINATORS"') — (reserved \\((full|recheck)\\)|released: skipped on limits|converged: loop clean)$"))
      | "\(.id)\t\(.user.login)\t\(.body)"' |
  while IFS=$'\t' read -r id login body; do
    verdict="${seen[$login]:-}"
    if [ -z "$verdict" ]; then
      if out=$(gh api "repos/{owner}/{repo}/collaborators/$login/permission" \
                 --jq .permission 2>&1); then
        case "$out" in
          admin|maintain|write) verdict=trusted ;;
          *) verdict=untrusted ;;
        esac
      elif grep -q "HTTP 404" <<<"$out"; then
        verdict=untrusted
      else
        echo "cannot verify $login's repository permission: $out" >&2
        exit 3
      fi
      seen[$login]=$verdict
    fi
    [ "$verdict" = trusted ] || continue
    # The pairing check lives in the shared reader so no consumer can miss it:
    # the slot and denominator alternations are independent, so the jq filter
    # admits `9/6`, which no writer of this file emits, and a trusted
    # `converged` row of that shape would skip review on a resumed run.
    row_slot="${body#Grok check }"
    row_den="${row_slot#*/}"
    row_slot="${row_slot%%/*}"
    row_den="${row_den%% *}"
    [ "$row_slot" -le "$row_den" ] || continue
    printf '%s\t%s\n' "$id" "$body"
  done
}

# Buffered rather than piped: `exit 3` inside ledger_rows' `while` ends only
# the pipeline's subshell, so `ledger_rows | awk` would hand awk an EOF and let
# it print 0 — an empty ledger's answer, which re-arms the cap — before the
# failure surfaced. Command substitution returns the status with nothing yet
# written to stdout, and every consumer below reads through these two
# functions, never a pipe from ledger_rows.
rows=""
read_rows() {
  # No `local`: the caller needs the value. `|| return` rather than leaning on
  # set -e, because an assignment's failure inside a function is not reliably
  # fatal and observing this failure here is the whole point.
  rows=$(ledger_rows) || return $?
}

# printf, not a here-string: `<<<` appends a newline, so an empty ledger would
# reach awk as one blank line rather than as no input at all, and the fold's
# empty case would be folding a row that does not exist. Zero bytes in, END
# out, 0 printed — which is what a fresh PR's ledger means.
emit_rows() {
  [ -z "$rows" ] || printf '%s\n' "$rows"
}

if [ "$op" = "count" ]; then
  [ -z "$n" ] || usage
  # POSIX awk only — no gawk match(..., m) — and empty input must still reach
  # END and print 0, because a fresh PR's ledger is legitimately empty.
  read_rows ||
    { echo "the ledger's trust check failed; refusing to print a count" >&2; exit 3; }
  emit_rows | awk -F'\t' '
    $2 ~ /converged/ { next }
    {
      split($2, a, "/")
      sub(/^Grok check /, "", a[1])
      state[a[1] + 0] = ($2 ~ /released/) ? "released" : "reserved"
    }
    END {
      max = 0
      for (i in state)
        if (state[i] == "reserved" && i + 0 > max)
          max = i + 0
      print max
    }'
  exit 0
fi

if [ "$op" = "status" ]; then
  [ -z "$n" ] || usage
  # count folds spend and deliberately ignores converged rows, so the marker
  # needs its own verified read — raw comments would bypass the author
  # check that makes the ledger state at all. A converged marker stands
  # until a later reservation supersedes it; releases change nothing, since
  # a skip neither spends nor converges.
  read_rows ||
    { echo "the ledger's trust check failed; refusing to print a status" >&2; exit 3; }
  emit_rows | awk -F'\t' '
    $2 ~ /converged/ { conv = 1 }
    $2 ~ /reserved/  { conv = 0 }
    END { print conv ? "converged" : "unconverged" }'
  exit 0
fi

# The write side, and the only place the ceiling binds: refusing to write a
# slot above it is a different act from refusing to see a higher slot the
# read still honours under a retired denominator.
[[ "$n" =~ ^[1-9][0-9]*$ ]] && [ "$n" -le "$CEILING" ] ||
  { echo "slot must be 1..$CEILING — the ceiling grok-ledger.sh declares: $n" >&2; usage; }

case "$op" in
  reserve)
    case "$mode" in
      full|recheck) ;;
      *) usage ;;
    esac
    body="Grok check $n/$CEILING — reserved ($mode)"
    ;;
  release)
    [ -z "$mode" ] || usage
    body="Grok check $n/$CEILING — released: skipped on limits"
    ;;
  converge)
    # Spend alone cannot distinguish a loop that converged on its last
    # allowed check from one the ceiling cut off — both read as N spent.
    # The marker says which; any later reservation supersedes it.
    [ -z "$mode" ] || usage
    body="Grok check $n/$CEILING — converged: loop clean"
    ;;
  *) usage ;;
esac

url=$(gh pr comment "$pr" --body "$body")
mine="${url##*issuecomment-}"
[[ "$mine" =~ ^[0-9]+$ ]] ||
  { echo "posted, but could not read the comment id back from: $url" >&2; exit 3; }

if [ "$op" = "reserve" ]; then
  # The election. A slot's winner is the first reservation posted after its
  # most recent release — not the first ever: a released slot is legitimately
  # re-spent, and an election that kept honouring the dead claim would refuse
  # the slot forever while count kept naming it as next. Rows arrive in
  # posting order, so a release resets the candidate and the first
  # reservation after it takes the slot; later claims lose.
  read_rows ||
    { echo "the ledger's trust check failed after posting; slot $n stands as reserved" >&2; exit 3; }
  # The slot is parsed, as `count` parses it, rather than matched against a
  # literal denominator, so a row posted under a retired ceiling still takes
  # part in the election and two runs across a ceiling change cannot both win
  # a slot. `index()` rather than a regex because the separator is an em dash
  # and this stays POSIX awk.
  winner=$(emit_rows |
    awk -F'\t' -v n="$n" '
      {
        slot = $2
        sub(/^Grok check /, "", slot)
        sub(/\/.*$/, "", slot)
      }
      slot + 0 != n + 0 { next }
      index($2, " — released") { cand = "" }
      index($2, " — reserved") && cand == "" { cand = $1 }
      END { print cand }')
  if [ "$winner" != "$mine" ]; then
    echo "slot $n was claimed first by comment $winner — a concurrent run is mid-check on this PR; stop this loop and let it finish" >&2
    exit 4
  fi
fi

echo "$body"
