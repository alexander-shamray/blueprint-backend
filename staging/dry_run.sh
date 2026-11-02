#!/usr/bin/env bash
# Run .claude/scripts suites on the tree apply.sh would leave: HEAD's files with
# staging/<n>/.claude/ laid over .claude/ in step order, in a scratch directory,
# so the edit-denied .claude/ of this checkout is never touched.
# Usage: staging/dry_run.sh <scratch-dir> [<test-module>...]
set -euo pipefail

if [ "$#" -lt 1 ]; then
  echo "usage: staging/dry_run.sh <scratch-dir> [<test-module>...]" >&2
  exit 2
fi
scratch=$1
shift
root=$(cd "$(dirname "$0")/.." && pwd)

# The scratch directory must be absent or empty. A marker that let an earlier
# dry run's directory be reused survived in it, so a real tree that a reused
# path later held would pass; a fresh path costs one argument.
if [ -e "$scratch" ] || [ -L "$scratch" ]; then
  if [ -L "$scratch" ] || [ ! -d "$scratch" ]; then
    echo "dry_run: refused: $scratch is not a directory" >&2
    exit 2
  fi
  if [ -n "$(ls -A -- "$scratch")" ]; then
    echo "dry_run: refused: $scratch is not empty; name a fresh directory" >&2
    exit 2
  fi
fi
mkdir -p -- "$scratch"

# shellcheck source=apply.sh
. "$root/staging/apply.sh"

(cd -- "$root" && git archive --format=tar HEAD) | (cd -- "$scratch" && tar -xf -)
# With no module named, apply.sh's own steps lay the files over one at a time.
if [ "$#" -gt 0 ]; then
  for n in $STEPS; do
    cp -R -- "$root/staging/$n/.claude/." "$scratch/.claude/"
  done
fi
find "$scratch/.claude" -type d -name __pycache__ -prune -exec rm -rf {} +
# apply.sh removes staging/ once its files are in place, so the copy does too.
rm -rf -- "$scratch/staging"

# Suites that list tracked files need a repository, holding the staged files as
# tracked, which is what the tree is once apply.sh has run.
dry_commit() {
  git -c user.name=dry-run -c user.email=dry-run@invalid -c commit.gpgsign=false \
    commit -q --no-verify -m "$1"
}
(
  cd -- "$scratch"
  git init -q -b main
  git config core.longpaths true  # some ADR slugs pass Windows' 260 under a scratch path
  git add -A
  dry_commit "dry run"
)

# CI pins 3.12 and the local default is newer; py is the Windows launcher.
if command -v py >/dev/null 2>&1; then
  PY=(py -3.12)
else
  PY=(python3)
fi
if [ "$#" -gt 0 ]; then
  cd -- "$scratch/.claude/scripts"
  "${PY[@]}" -m unittest "$@"
  exit
fi

# apply.sh's steps in order, each committed as apply.sh commits it, so a later
# step sees what the real tree would; a failed before or after exits 1 there.
cd -- "$scratch"
for n in $STEPS; do
  step "$n"
  echo "step $n: $SUBJECT"
  land "$root" "$n"
  git add -- $TESTS $CODE
  dry_commit "$SUBJECT"
done
all_landed "$root"
echo "the whole discovery, as CI runs it"
"${PY[@]}" -m unittest discover -s .claude/scripts
echo "the suites apply.sh runs after every step"
(cd .claude/scripts && "${PY[@]}" -m unittest $SUITES)
echo "dry run: every before failed and every after passed"
