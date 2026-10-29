#!/usr/bin/env bash
# Ensure one of the sweeps' labels exists here, and nothing else: a prefix grant
# on `gh label create` reaches `--force`, which overwrites, and `-R`; here the
# name comes from a fixed case, `--force` is never spelled, the repository is
# resolved, and it asks first, since a plain create fails on an existing label.
set -euo pipefail

[ "$#" -eq 1 ] || {
  echo "usage: gh-label-ensure.sh" \
       "<security|bug|test-gap|critical|high|medium|low|harness|code|documentation|test>" >&2
  exit 2
}
label="$1"

# The whole vocabulary, with the colour and text each label carries today, so a
# create in a fresh clone reproduces what this repository already has rather
# than inventing a second appearance for the same label. A name outside this
# case is refused rather than passed to gh — that refusal is the point of the
# file.
case "$label" in
  security) colour=d93f0b; description="Security finding" ;;
  bug)      colour=d73a4a; description="Something isn't working" ;;
  test-gap) colour=1d76db; description="Behaviour the suite does not detect: a mutant it let through" ;;
  critical) colour=b60205; description="Severity: silent wrong data, or a protection that cannot protect" ;;
  high)     colour=e36209; description="Severity: reachable path that crashes, corrupts, leaks or answers wrongly" ;;
  medium)   colour=fbca04; description="Severity: needs unusual configuration, or a dev-tool-only blast radius" ;;
  low)      colour=0e8a16; description="Severity: latent (no current caller), hardening, or deferred follow-up" ;;
  harness)  colour=1d76db
            description="The Claude Code harness under .claude/: commands, hooks, scripts, agents, settings" ;;
  code)     colour=006b75; description="Code and configuration: src/, tests/, tools/, deploy/ and the CI workflows" ;;
  documentation) colour=0075ca; description="Improvements or additions to documentation" ;;
  test)     colour=c5def5; description="Test suites under tests/: a test that cannot fail, a flake, a coverage gap" ;;
  *) echo "not a label this helper will create: $label" >&2; exit 2 ;;
esac

# The repository is resolved, never accepted. `gh repo view` reads the checkout
# this process is standing in, so the answer is a property of the filesystem
# rather than of anything a finding could have said.
repo=$(gh repo view --json nameWithOwner --jq .nameWithOwner) ||
  { echo "cannot resolve this checkout's repository" >&2; exit 3; }

# --search is a match, not an equality, so the exact name is checked with
# grep -x afterwards: searching for `low` also returns `slow` if one ever
# exists, and a helper that concluded "already there" from a near miss would
# leave the sweep filing against a label that does not exist.
existing=$(gh label list --repo "$repo" --search "$label" --json name --jq '.[].name') ||
  { echo "cannot list $repo's labels" >&2; exit 3; }
if grep -qx -- "$label" <<<"$existing"; then
  echo "label $label already exists in $repo"
  exit 0
fi

# Check-then-create is not atomic, and two sweeps can run at once: the loser's
# create exits non-zero, since `--force` is never used, over a label that is
# now exactly what it asked for. So a failed create is resolved by re-reading —
# present means satisfied, absent means the caller hears the failure — because
# assuming success fails open and assuming failure aborts a sound sweep.
if ! gh label create "$label" \
  --repo "$repo" \
  --color "$colour" \
  --description "$description"; then
  again=$(gh label list --repo "$repo" --search "$label" --json name --jq '.[].name') ||
    { echo "cannot confirm whether $label exists in $repo after a failed create" >&2; exit 3; }
  grep -qx -- "$label" <<<"$again" ||
    { echo "could not create label $label in $repo" >&2; exit 3; }
  echo "label $label was created concurrently in $repo"
fi
