#!/usr/bin/env bash
# Record the commit a clean sweep read, as refs/sweeps/<kind>, so the next sweep
# of that kind reads only what changed since; sweep-slices.sh reads it back.
# The ref is local: a checkout without it sweeps in full, never less.
set -euo pipefail
usage="usage: sweep-mark.sh <bug|security> <commit-sha>"
[ "$#" -eq 2 ] || { echo "$usage" >&2; exit 2; }
kind="$1" commit="$2"
case "$kind" in bug|security) : ;; *) echo "$usage" >&2; exit 2 ;; esac
[[ "$commit" =~ ^[0-9a-f]{40}$ ]] ||
  { echo "commit must be a full 40-character sha: $commit" >&2; exit 2; }
git rev-parse --verify --quiet "$commit^{commit}" >/dev/null ||
  { echo "no such commit: $commit" >&2; exit 3; }
git update-ref "refs/sweeps/$kind" "$commit"
echo "refs/sweeps/$kind $commit"
