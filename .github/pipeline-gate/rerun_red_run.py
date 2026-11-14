"""Rerun a red first attempt of a push run on main once, and say how the second attempt ended (ADR-210).

Usage: rerun_red_run.py <owner/repo> <run-id>; it writes `second`, `conclusion` and `why` to `$GITHUB_OUTPUT`
and exits 0 whatever the rerun did, as `main-red.yml`'s `file` job files every `second` but `passed`,
`replaced` and `superseded`."""

from __future__ import annotations

import json
import os
import secrets
import subprocess
import sys
import time
from collections.abc import Callable, Mapping

import read_cancelled_run

TRACKER = "Flaky tests tracker"
POLL_SECONDS = 60
# Under the job's `timeout-minutes`, so a rerun that outlasts it is filed rather than cut off unheard.
DEADLINE_MINUTES = 300
UNREAD = "- its jobs could not be read"

_PASSED = ("success", "skipped", "neutral")
# Each job a JSON line, with the names of its steps that did not pass.
_JOBS = ('.jobs[] | {name, conclusion, steps: [(.steps // [])[]'
         ' | select(.conclusion | IN("failure", "cancelled", "timed_out")) | .name]} | @json')

Gh = Callable[..., str]


def read_failed(gh: Gh, repo: str, run_id: str) -> str:
    """Each job of the first attempt that did not pass, and its steps that did not, a Markdown line each.

    Read before the rerun, which makes a second attempt the run's latest.
    """
    try:
        lines = gh("api", "--paginate", f"repos/{repo}/actions/runs/{run_id}/attempts/1/jobs?per_page=100",
                   "--jq", _JOBS).splitlines()
        jobs = [json.loads(line) for line in lines if line.strip()]
    except (subprocess.CalledProcessError, ValueError):
        return UNREAD
    failed = [
        f"- `{job['name']}` ({job['conclusion']}): "
        + (", ".join(f"`{step}`" for step in job["steps"]) or "no step failed")
        for job in jobs if job["conclusion"] not in _PASSED
    ]
    return "\n".join(failed) or UNREAD


def comment_on_tracker(gh: Gh, repo: str, body: str) -> None:
    """A flake that reruns green is still written down; a tracker that cannot take it is a warning.

    The rerun, which is what unblocks `main`, has already started by then.
    """
    try:
        issues = json.loads(gh("issue", "list", "-R", repo, "--state", "open", "--label", "flaky",
                               "--limit", "100", "--json", "number,title"))
        tracker = next((issue["number"] for issue in issues if issue["title"] == TRACKER), None)
        if tracker is None:
            print(f"::warning::no open {TRACKER} issue, so the first red attempt is not recorded")
            return
        gh("issue", "comment", str(tracker), "-R", repo, "--body", body)
    except (subprocess.CalledProcessError, ValueError):
        print(f"::warning::the {TRACKER} could not be commented on, so the first red attempt is not recorded")


def wait_for_second(gh: Gh, repo: str, run_id: str,
                    sleep: Callable[[float], None], clock: Callable[[], float], deadline: float) -> str | None:
    """The conclusion of the run's second attempt once it completes, None past the deadline."""
    while True:
        try:
            attempt, status, conclusion = gh(
                "api", f"repos/{repo}/actions/runs/{run_id}",
                "--jq", '[.run_attempt, .status, (.conclusion // "")] | @tsv').rstrip("\n").split("\t")
            if int(attempt) >= 2 and status == "completed":
                return conclusion
        except (subprocess.CalledProcessError, ValueError):
            pass  # A read that fails once is read again; the deadline bounds it.
        if clock() >= deadline:
            return None
        sleep(POLL_SECONDS)


def newer_runs(gh: Gh, repo: str, run_id: str, workflow_id: str) -> str | None:
    """The ids of the workflow's push runs on main newer than this one, None when they cannot be read.

    A newer run tests main's newer head and is heard on its own; rerunning this one beside it would, under
    the ref-keyed `cancel-in-progress` groups most watched workflows use, cancel it or its own rerun.
    """
    try:
        return gh("api", f"repos/{repo}/actions/workflows/{workflow_id}/runs?branch=main&event=push&per_page=20",
                  "--jq", f".workflow_runs[] | select(.id > {int(run_id)}) | .id").strip()
    except (subprocess.CalledProcessError, ValueError):
        return None


def rerun_attempt(gh: Gh, repo: str, run_id: str) -> int:
    """The run's latest attempt, 1 when it cannot be read."""
    try:
        return int(gh("api", f"repos/{repo}/actions/runs/{run_id}", "--jq", ".run_attempt").strip())
    except (subprocess.CalledProcessError, ValueError):
        return 1


def judge_second(gh: Gh, repo: str, run_id: str, conclusion: str) -> tuple[str, str]:
    """`passed`, `replaced` or `red`, and why a red one is red, read as ADR-196 reads a first attempt."""
    if conclusion in _PASSED:
        return "passed", ""
    if conclusion != "cancelled":
        return "red", ""
    try:
        reason = read_cancelled_run.read_run(gh, repo, run_id)
    except (subprocess.CalledProcessError, ValueError):
        reason = "Its jobs could not be read."
    return ("replaced", "") if not reason else ("red", reason)


def rerun(gh: Gh, repo: str, run_id: str, env: Mapping[str, str],
          sleep: Callable[[float], None], clock: Callable[[], float]) -> dict[str, str]:
    deadline = clock() + DEADLINE_MINUTES * 60
    failed = read_failed(gh, repo, run_id)
    newer = newer_runs(gh, repo, run_id, env.get("WORKFLOW_ID", ""))
    if newer is None:
        return {"second": "unread", "conclusion": "",
                "why": "Whether a newer run of it on main exists could not be read, and a rerun beside one "
                       f"could cancel it, so its failed jobs were not rerun.\n\n{failed}"}
    if newer:
        print(f"a newer push run on main ({newer.split()[0]}) is the one heard, so this one is not rerun")
        return {"second": "superseded", "conclusion": "", "why": ""}
    try:
        gh("run", "rerun", run_id, "--failed", "-R", repo)
    except subprocess.CalledProcessError:
        # A person may have rerun it first; then that attempt is the second one.
        if rerun_attempt(gh, repo, run_id) < 2:
            print("::error::the failed jobs could not be rerun, so the first attempt is filed")
            return {"second": "refused", "conclusion": "",
                    "why": f"GitHub refused to rerun its failed jobs, so this is its first attempt.\n\n{failed}"}

    workflow = env.get("WORKFLOW", "")
    comment_on_tracker(gh, repo, "\n".join((
        f"The `{workflow}` push run on `main` at {env.get('HEAD_SHA', '')} ended "
        f"`{env.get('CONCLUSION', '')}` on its first attempt, and its failed jobs were rerun once: "
        f"{env.get('RUN_URL', '')}",
        "",
        failed,
        "",
        f"A second attempt that passes makes it a flake; a red one files `main is red: {workflow}`.",
        "",
        "Commented by main-red.yml (ADR-210).",
    )))

    conclusion = wait_for_second(gh, repo, run_id, sleep, clock, deadline)
    if conclusion is None:
        return {"second": "unfinished", "conclusion": "",
                "why": f"Its failed jobs were rerun once, and the second attempt had not finished after "
                       f"{DEADLINE_MINUTES} minutes.\n\nWhat failed on the first attempt:\n{failed}"}
    second, reason = judge_second(gh, repo, run_id, conclusion)
    why = f"Its failed jobs were rerun once, and the second attempt ended `{conclusion}` too."
    if reason:
        why += f" {reason}"
    return {"second": second, "conclusion": conclusion,
            "why": f"{why}\n\nWhat failed on the first attempt:\n{failed}"}


def write_outputs(path: str, outputs: Mapping[str, str]) -> None:
    """In `$GITHUB_OUTPUT`'s delimited form, since `why` runs over several lines."""
    with open(path, "a", encoding="utf-8") as output:
        for key, value in outputs.items():
            delimiter = f"EOF_{secrets.token_hex(16)}"
            output.write(f"{key}<<{delimiter}\n{value}\n{delimiter}\n")


def run_gh(*arguments: str) -> str:
    return subprocess.run(["gh", *arguments], capture_output=True, encoding="utf-8", check=True).stdout


def main(argv: list[str], gh: Gh = run_gh, env: Mapping[str, str] = os.environ,
         sleep: Callable[[float], None] = time.sleep, clock: Callable[[], float] = time.monotonic) -> int:
    repo, run_id = argv[1:]
    outputs = rerun(gh, repo, run_id, env, sleep, clock)
    print(f"second attempt: {outputs['second']}")
    write_outputs(env["GITHUB_OUTPUT"], outputs)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
