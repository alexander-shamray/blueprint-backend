#!/usr/bin/env bash
# The image jobs' one retry. An action cannot sit under a shell, so it takes a
# second attempt step and calls `note`; `pipeline-gate images` holds that shape.
set -uo pipefail

WAIT_SECONDS="${IMAGE_RETRY_WAIT_SECONDS:-60}"

usage() {
  echo "usage: image_retry.sh run <label> <command> [<argument>...] | note <label>" >&2
  exit 2
}

# In RUNNER_TEMP because a build's context is the repository root, where a record
# could land in the image the second attempt builds.
note() {
  echo "$1" >> "$RUNNER_TEMP/image-retries.txt"
  echo "::warning::$1 failed; one more attempt in $WAIT_SECONDS s."
  sleep "$WAIT_SECONDS"
}

[ "$#" -ge 2 ] || usage
action=$1
label=$2
shift 2

case "$action" in
  note)
    [ "$#" -eq 0 ] || usage
    note "$label"
    ;;
  run)
    [ "$#" -ge 1 ] || usage
    "$@" && exit 0
    note "$label"
    exec "$@"
    ;;
  *)
    usage
    ;;
esac
