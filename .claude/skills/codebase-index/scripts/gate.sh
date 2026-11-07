# Sourced by cbx, never run: `gate_index <scripts-dir>`. Judges the index
# with gate-index.py under the first Python 3.12 found, as the fallbacks
# pick one; no such Python is a refusal, exit 4, since the CLI would otherwise
# open an index nobody judged (docs/harness-boundaries.md, *Index refresh and
# worktree seeding*).
gate_index() {
  gate_probe='import sys; raise SystemExit(0 if sys.version_info[:2] == (3, 12) else 1)'
  for gate_python in 'py -3.12' python3 python; do
    # Unquoted on purpose: 'py -3.12' is a command and its argument.
    if $gate_python -I -c "$gate_probe" >/dev/null 2>&1; then
      $gate_python -I "$1/gate-index.py"
      return $?
    fi
  done
  echo "gate-index: no Python 3.12, so the index cannot be judged; refusing to open it" >&2
  return 4
}
