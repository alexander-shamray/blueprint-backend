#!/usr/bin/env bash
# Ensure one of the sweeps' labels exists on this repository, and nothing
# else: /security-sweep and /bug-sweep each need a kind label and a severity
# label, created once if absent and never touched again.
#
# A prefix grant on `gh label create` reaches two things this refuses:
#
#   --force   updates an existing label's colour and description, so `create`
#             is create-or-overwrite
#   -R/--repo unpinned, so the write lands wherever an argument from an
#             audited, prompt-injectable tree names
#
# Here the name comes out of a fixed case with its colour and description,
# `--force` is never spelled, and the repository is the one `gh repo view`
# resolves from the checkout.
#
# Idempotent by asking first: `gh label create` on an existing label exits
# non-zero without `--force`, which would stop a sweep on a label that is
# already correct.
set -euo pipefail

[ "$#" -eq 1 ] ||
  { echo "usage: gh-label-ensure.sh <security|bug|critical|high|medium|low>" >&2; exit 2; }
label="$1"

# The whole vocabulary, with the colour and text each label carries today, so a
# create in a fresh clone reproduces what this repository already has rather
# than inventing a second appearance for the same label. A name outside this
# case is refused rather than passed to gh — that refusal is the point of the
# file.
case "$label" in
  security) colour=d93f0b; description="Security finding" ;;
  bug)      colour=d73a4a; description="Something isn't working" ;;
  critical) colour=b60205; description="Severity: silent wrong data, or a protection that cannot protect" ;;
  high)     colour=e36209; description="Severity: reachable path that crashes, corrupts, leaks or answers wrongly" ;;
  medium)   colour=fbca04; description="Severity: needs unusual configuration, or a dev-tool-only blast radius" ;;
  low)      colour=0e8a16; description="Severity: latent (no current caller), hardening, or deferred follow-up" ;;
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
