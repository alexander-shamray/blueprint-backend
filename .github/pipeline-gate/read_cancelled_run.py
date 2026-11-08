"""Why a cancelled push run on main is red, or nothing where a newer run replaced it (ADR-196).

Usage: read_cancelled_run.py <owner/repo> <run-id>, with `gh` signed in. A read
that fails exits non-zero, which `main-red.yml` files rather than reading as
replaced.
"""

from __future__ import annotations

import subprocess
import sys
from collections.abc import Callable

HIT_TIME_LIMIT = "A job hit its time limit."
NOT_REPLACED = "Nothing says a newer run replaced it. Cancelled by hand, it owes no revert: run the head again."

_TIME_LIMIT_WORDS = "exceeded the maximum execution time"
_REPLACED_WORDS = "higher priority waiting request"
_PASSED = ("success", "skipped")

Gh = Callable[..., str]


def read_reason(messages: list[str]) -> str:
    """The sentence a red run files, "" for a replaced one.

    Replaced has to be read rather than assumed: the platform rewording it costs
    an issue too many, where assuming it would cost the alert.
    """
    if any(_TIME_LIMIT_WORDS in message for message in messages):
        return HIT_TIME_LIMIT
    if any(_REPLACED_WORDS in message for message in messages):
        return ""
    return NOT_REPLACED


def read_run(gh: Gh, repo: str, run_id: str) -> str:
    """The reason for one run, from the failure annotations of each job that did not pass.

    A run with no job was replaced while it queued, or cancelled by hand before it started,
    and no annotation tells the two apart; ADR-196 accepts the second.
    """
    jobs = gh("api", "--paginate", f"repos/{repo}/actions/runs/{run_id}/jobs?per_page=100",
              "--jq", '.jobs[] | [.id, .conclusion] | @tsv').splitlines()
    if not jobs:
        return ""
    messages: list[str] = []
    for line in jobs:
        job, conclusion = line.split("\t")
        if conclusion in _PASSED:
            continue
        notes = gh("api", "--paginate", f"repos/{repo}/check-runs/{job}/annotations?per_page=100",
                   "--jq", '.[] | select(.annotation_level == "failure") | .message')
        messages.extend(notes.splitlines())
    return read_reason(messages)


def run_gh(*arguments: str) -> str:
    return subprocess.run(["gh", *arguments], capture_output=True, encoding="utf-8", check=True).stdout


def main(argv: list[str], gh: Gh = run_gh) -> int:
    repo, run_id = argv[1:]
    print(read_run(gh, repo, run_id))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
