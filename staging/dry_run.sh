#!/usr/bin/env bash
# Run .claude/scripts suites on the tree apply.sh would leave: HEAD's files with
# staging/.claude/ laid over .claude/, in a scratch directory, so the edit-denied
# .claude/ of this checkout is never touched.
# Usage: staging/dry_run.sh <scratch-dir> <test-module>...
set -euo pipefail

if [ "$#" -lt 2 ]; then
  echo "usage: staging/dry_run.sh <scratch-dir> <test-module>..." >&2
  exit 2
fi
scratch=$1
shift
root=$(cd "$(dirname "$0")/.." && pwd)

# The scratch directory is emptied first, so only one that is absent, empty or
# marked by an earlier dry run is taken; anything else may be a real tree.
marker=.dispatch-dry-run
if [ -e "$scratch" ] || [ -L "$scratch" ]; then
  if [ -L "$scratch" ] || [ ! -d "$scratch" ]; then
    echo "dry_run: refused: $scratch is not a directory" >&2
    exit 2
  fi
  entries=$(ls -A -- "$scratch")
  if [ -n "$entries" ] && [ ! -f "$scratch/$marker" ]; then
    echo "dry_run: refused: $scratch holds files and no $marker, so it is not a dry run's" >&2
    exit 2
  fi
fi
rm -rf -- "${scratch:?}"
mkdir -p -- "$scratch"
: > "$scratch/$marker"

(cd -- "$root" && git archive --format=tar HEAD) | (cd -- "$scratch" && tar -xf -)
cp -R -- "$root/staging/.claude/." "$scratch/.claude/"
find "$scratch/.claude" -type d -name __pycache__ -prune -exec rm -rf {} +
# apply.sh removes staging/ once its files are in place, so the copy does too.
rm -rf -- "$scratch/staging"

# Suites that list tracked files need a repository, holding the staged files as
# tracked, which is what the tree is once apply.sh has run.
(
  cd -- "$scratch"
  git init -q -b main
  git config core.longpaths true  # some ADR slugs pass Windows' 260 under a scratch path
  echo "/$marker" >> .git/info/exclude  # a tracked root file would fail test_harness_denies
  git add -A
  git -c user.name=dry-run -c user.email=dry-run@invalid -c commit.gpgsign=false \
    commit -q --no-verify -m "dry run"
)

# CI pins 3.12 and the local default is newer; py is the Windows launcher.
if command -v py >/dev/null 2>&1; then
  python=(py -3.12)
else
  python=(python3)
fi
cd -- "$scratch/.claude/scripts"
"${python[@]}" -m unittest "$@"
