#!/usr/bin/env bash
# Run Stryker over every domain project of a sweep's worktree and print the mutants that got through, for
# /mutation-sweep. The worktree is the one free word, held to the shape git-worktree-detach.sh makes; the
# projects come from mutation_survivors.py and the runner and mutators from stryker-config.json, both read
# beside this script rather than from the audited tree.
set -euo pipefail
usage="usage: mutation-run.sh <worktree>"
[ "$#" -eq 1 ] || { echo "$usage" >&2; exit 2; }
path="$1"
case "$path" in -*) echo "path may not start with '-'" >&2; exit 2 ;; esac
[ -d "$path" ] || { echo "not an existing directory: $path" >&2; exit 2; }

tmproot=$(cd "${TMPDIR:-/tmp}" 2>/dev/null && pwd -P) ||
  { echo "cannot resolve the temp root" >&2; exit 4; }
resolved=$(cd "$path" && pwd -P)
[ "$(dirname "$resolved")" = "$tmproot" ] ||
  { echo "not a direct child of the temp root: $path" >&2; exit 2; }
case "$(basename "$resolved")" in
  secsweep-??????) : ;;
  *) echo "not a sweep-shaped temp path: $path" >&2; exit 2 ;;
esac
out="$resolved.mutation"
[ ! -e "$out" ] || { echo "already run: $out" >&2; exit 3; }

here=$(cd "$(dirname "$0")" && pwd -P)
python=python3
command -v py >/dev/null 2>&1 && python="py -3.12"
plan=$($python -I "$here/mutation_survivors.py" plan "$resolved")
# Windows' py ends each line in CRLF and bash strips only the last, so a kept \r names a missing directory.
plan=${plan//$'\r'/}

mkdir "$out"
# Stryker's own output goes to stderr, so stdout carries the report alone.
(cd "$resolved" && dotnet tool restore >&2)
while IFS= read -r tests; do
  name=$(basename "$tests" .Tests)
  # Restored, and its framework named, before Stryker runs; docs/testing.md, Mutation, says why.
  (cd "$resolved/$tests" && dotnet restore >&2) || { echo "cannot restore $name" >&2; exit 3; }
  framework=$(cd "$resolved/$tests" && dotnet msbuild -getProperty:TargetFramework) ||
    { echo "cannot evaluate the target framework of $name" >&2; exit 3; }
  framework=${framework//$'\r'/}
  [ -n "$framework" ] || { echo "no single target framework for $name" >&2; exit 3; }
  (cd "$resolved/$tests" &&
    dotnet tool run dotnet-stryker --config-file "$here/stryker-config.json" \
      --target-framework "$framework" --output "$out/$name" --skip-version-check >&2) ||
    { echo "Stryker failed on $name" >&2; exit 3; }
done <<<"$plan"
exec $python -I "$here/mutation_survivors.py" report "$out" "$resolved"
