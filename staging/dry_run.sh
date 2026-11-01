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

# The scratch directory is emptied first; inside a work tree that would delete
# checked-out files, so a parent git recognises is refused. The scratch's own
# repository, the one the last dry run made, is not a parent.
mkdir -p -- "$scratch"
if (cd -- "$scratch/.." && git rev-parse --show-toplevel >/dev/null 2>&1); then
  echo "dry_run: $scratch is inside a git work tree; give a scratch directory" >&2
  exit 2
fi
rm -rf -- "${scratch:?}"
mkdir -p -- "$scratch"

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
