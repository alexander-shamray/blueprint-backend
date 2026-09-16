#!/usr/bin/env bash
# `dotnet test` for /review-branch, with no free parameter anywhere.
#
# A trailing argument would choose which project to build, and an MSBuild
# property chooses a file to import: `/p:CustomBeforeMicrosoftCommonTargets=`
# executes whatever XML it is pointed at, including a file this command may
# legitimately write. So the solution, the filter and the flags are fixed, and
# the only variable is one word out of two.
#
# This closes the executor, not the import: a `Directory.Build.targets` at the
# repository root runs its `Exec` in any build beneath it, so keeping that file
# out of reach is `disallowed-tools`' job in review-branch.md.
set -euo pipefail

mode="${1:-all}"
root="$(git rev-parse --show-toplevel)"
cd "$root"

case "$mode" in
  all)
    exec dotnet test Platform.slnx
    ;;
  fast)
    exec dotnet test Platform.slnx --filter "Category!=Integration"
    ;;
  *)
    echo "usage: dotnet-test.sh [all|fast]" >&2
    exit 2
    ;;
esac
