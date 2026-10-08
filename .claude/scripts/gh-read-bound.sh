# Sourced by the fixed-field pull request helpers, never run: the bound on every `gh` read they make. A stalled
# request held pr-locality.sh, and each step chained behind it, with nothing printed. Past the bound a read
# fails with timeout's 124 and a line naming the call, as loudly as any other `gh` failure does under `set -e`.
# Why it is bounded is docs/harness-boundaries.md's (*Pull request reads and the locality verdict*).
GH_READ_BOUND_SECONDS="${GH_READ_BOUND_SECONDS:-60}"
gh_read() {
  local rc=0
  timeout "$GH_READ_BOUND_SECONDS" gh "$@" || rc=$?
  [ "$rc" -ne 124 ] || echo "gh $1 $2 did not answer within $GH_READ_BOUND_SECONDS s" >&2
  return "$rc"
}
