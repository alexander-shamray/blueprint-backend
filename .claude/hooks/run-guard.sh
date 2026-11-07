#!/usr/bin/env sh
# Runs one guard hook, `run-guard.sh <guard>.py`, with whichever Python 3.12 or newer this host has, exactly once, and
# leaves with 2 on every way it can fail, since 2 is the only status a PreToolUse hook blocks with. A raw
# `py -3.12 <guard>` that cannot start exits 127 and the tool runs unguarded. Why each part is here:
# docs/harness-boundaries.md (*The guard launcher*).
set -eu

# Every exit not a guard's verdict is 2: `set -e`, an unset name and a SIGPIPE leave otherwise, and none of them block.
trap 'exit 2' EXIT
trap 'exit 2' PIPE

[ "$#" -eq 1 ] ||
  { echo "usage: run-guard.sh <guard-git-argv.py|guard-index-argv.py|guard-helper-argv.py|guard-sed-crlf.py|guard-edit-target.py>" >&2; exit 2; }

# A closed set, as every helper's: a launcher that took any path would run any script through the hook wiring.
case "$1" in
  guard-git-argv.py|guard-index-argv.py|guard-helper-argv.py|guard-sed-crlf.py|guard-edit-target.py) ;;
  *) echo "run-guard.sh: not a guard this launcher runs: $1" >&2; exit 2 ;;
esac

# Beside this file, so the launcher and the guard come from one checkout. `pwd -W` hands a Windows interpreter a
# Windows path, which MSYS would not convert for one holding an apostrophe; a POSIX shell refuses it and `pwd` answers.
dir=$(CDPATH= cd -- "$(dirname -- "$0")" && { pwd -W 2>/dev/null || pwd; })

# The candidate the probes below reach first on this host, and the name its mark goes by.
if command -v py >/dev/null 2>&1; then first='py -3.12'; mark=py-3.12
elif command -v python3 >/dev/null 2>&1; then first=python3; mark=python3
elif command -v python >/dev/null 2>&1; then first=python; mark=python
else first=; mark=none
fi
mark="$dir/../cache/run-guard.$mark"

# The mark is an empty directory and is never opened: only whether it exists is read, so it can name no program.
remember() {
  [ "$1" = "$first" ] || return 0
  [ -f "$dir/run-guard.py" ] || return 0
  [ ! -e "$mark" ] || return 0
  [ -d "$dir/../cache" ] || mkdir "$dir/../cache" 2>/dev/null || return 0
  mkdir "$mark" 2>/dev/null || return 0
}

if [ -n "$first" ] && [ -d "$mark" ]; then
  # A remembered candidate runs as a child, through `run-guard.py`, and is believed only on a status it alone leaves.
  status=0
  case "$first" in
    'py -3.12') py -3.12 "$dir/run-guard.py" "$1" || status=$? ;;
    python3) python3 "$dir/run-guard.py" "$1" || status=$? ;;
    python) python "$dir/run-guard.py" "$1" || status=$? ;;
  esac
  case "$status" in
    91) trap - EXIT; exit 0 ;;
    92) trap - EXIT; exit 2 ;;
    93) trap - EXIT; exit 1 ;;
  esac
  # `rmdir` removes an empty directory and nothing else, so a mark something was put inside keeps refusing.
  rmdir "$mark" 2>/dev/null || :
  if [ -d "$mark" ]; then
    next="The mark could not be removed, so every call is refused until it is"
  else
    next="The mark is forgotten, so the next call probes again"
  fi
  echo "run-guard.sh: a mark for $first was found and $first has not proved it ran $1 on this call (status $status); refusing the call rather than running it unguarded. $next: $mark" >&2
  exit 2
fi

# Each candidate is run before it is chosen, since a name on PATH is not a working interpreter, and one `exec` runs
# the guard: a `||` chain would judge a refused event twice. `py` comes first because Windows' `python3` is the Store
# alias. `py -3` takes a host with no 3.12 but a newer one; it is never remembered, nor is `python` after `python3`.
probe='import sys; sys.exit(sys.version_info < (3, 12))'

if command -v py >/dev/null 2>&1 && py -3.12 -c "$probe" >/dev/null 2>&1; then
  remember 'py -3.12'
  exec py -3.12 "$dir/$1"
fi
if command -v py >/dev/null 2>&1 && py -3 -c "$probe" >/dev/null 2>&1; then
  exec py -3 "$dir/$1"
fi
if command -v python3 >/dev/null 2>&1 && python3 -c "$probe" >/dev/null 2>&1; then
  remember python3
  exec python3 "$dir/$1"
fi
if command -v python >/dev/null 2>&1 && python -c "$probe" >/dev/null 2>&1; then
  remember python
  exec python "$dir/$1"
fi
echo "run-guard.sh: no Python 3.12 or newer found as py -3.12, py -3, python3 or python; refusing the call rather than running it unguarded" >&2
exit 2
