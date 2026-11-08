# Sourced by the fixed-field pull request helpers, never run: the bound on every `gh` read they make. A stalled
# request held pr-locality.sh, and each step chained behind it, with nothing printed. Past the bound a read
# fails with timeout's 124 and a line naming the call, as loudly as any other `gh` failure does under `set -e`.
# Why it is bounded is docs/harness-boundaries.md's (*Pull request reads and the locality verdict*).
GH_READ_BOUND_SECONDS="${GH_READ_BOUND_SECONDS:-60}"

# `bounded <seconds> <command>...`: the command under the bound, 124 past it, as GNU `timeout` answers. macOS ships
# no `timeout`, so there a watchdog sends TERM to the command alone, not its process group as `timeout` does, which
# `gh`, one process, does not need; gh-issue-harness.sh and hint-bound.sh run under it too. GH_READ_TIMEOUT names the
# program, so a case can take the watchdog where `timeout` is installed. Each status is caught, since a caller may
# hold `set -e` on.
bounded() {
  local seconds="$1" rc=0 pid dog fired=0
  shift
  if command -v "${GH_READ_TIMEOUT:-timeout}" > /dev/null 2>&1; then
    "${GH_READ_TIMEOUT:-timeout}" "$seconds" "$@" || rc=$?
    return "$rc"
  fi
  # Stdin named, since a background command is otherwise handed /dev/null; the watchdog's streams are /dev/null, so
  # a substitution reading this call does not wait on its `sleep`. Stopped early, the watchdog exits 1; once it
  # fires it ignores the stop, so its 0 is what tells the bound from the command's own failure.
  "$@" <&0 &
  pid=$!
  ( sleep "$seconds" & nap=$!
    trap 'kill "$nap" 2> /dev/null; exit 1' TERM
    wait "$nap" && trap '' TERM && kill -TERM "$pid" 2> /dev/null ) < /dev/null > /dev/null 2>&1 &
  dog=$!
  wait "$pid" || rc=$?
  kill -TERM "$dog" 2> /dev/null || true
  wait "$dog" && fired=1 || true
  [ "$fired" -eq 0 ] || rc=124
  return "$rc"
}

gh_read() {
  local rc=0
  bounded "$GH_READ_BOUND_SECONDS" gh "$@" || rc=$?
  [ "$rc" -ne 124 ] || echo "gh $1 $2 did not answer within $GH_READ_BOUND_SECONDS s" >&2
  return "$rc"
}
