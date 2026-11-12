"""The latency report beside ci_cost's cost one: how long a pull request waits, rather than what the account pays.

    python tools/ci-cost/ci_cost.py --latency [--repo owner/name ...] [--window A=FIRST:LAST] [--day YYYY-MM-DD]
"""

from __future__ import annotations

import heapq
import json
import math
from collections import defaultdict
from concurrent.futures import ThreadPoolExecutor
from datetime import date, datetime, timedelta, timezone

from ci_cost import WORKERS, Fetch, Window, jobs_of, paged, parse, runs_between

# Newest successful runs of each workflow whose jobs and steps are read: a job listing costs one request a run.
SAMPLE = 40
# The slowest steps shown for each job, by p90.
STEPS = 3
# The concurrency caps a day is replayed under: GitHub's plan limits, so a plan change is a read of this row.
CAPS = (20, 40, 60)
# A job waiting longer than this was queued behind others, not just given a runner.
QUEUED_AFTER = 60


def percentile(values: list[float], share: float) -> float | None:
    """The nearest-rank percentile, so every figure printed is one that was measured."""
    ordered = sorted(values)
    return ordered[max(0, math.ceil(share * len(ordered)) - 1)] if ordered else None


def summary(values: list[float]) -> dict:
    p50, p90 = percentile(values, 0.5), percentile(values, 0.9)
    return {"n": len(values), "p50": None if p50 is None else round(p50, 1),
            "p90": None if p90 is None else round(p90, 1)}


def seconds(later: str | None, earlier: str | None) -> float | None:
    after, before = parse(later), parse(earlier)
    return (after - before).total_seconds() if after and before else None


def duration(value: float | None) -> str:
    if value is None:
        return "n/a"
    minutes, rest = divmod(round(value), 60)
    return f"{minutes}m {rest:02d}s" if minutes else f"{rest}s"


def pair(cell: dict) -> str:
    return f"{duration(cell['p50'])} / {duration(cell['p90'])}"


def matrix_free(name: str) -> str:
    return name.split(" (")[0]


def ran(job: dict) -> bool:
    """A job that took a runner: a skipped one carries a start and an end a moment apart, and no work."""
    return bool(job.get("started_at") and job.get("completed_at") and job.get("created_at")
                and job.get("conclusion") != "skipped")


def week_of(moment: str) -> str:
    """The Monday, in UTC, of the week a run was created in."""
    created = parse(moment).date()
    return (created - timedelta(days=created.weekday())).isoformat()


def weekly(runs: list[dict]) -> dict[str, list[float]]:
    """Wall-clock seconds of each successful run, by week: the last attempt's start to the run's last update."""
    weeks: dict[str, list[float]] = defaultdict(list)
    for run in runs:
        took = seconds(run.get("updated_at"), run.get("run_started_at"))
        if run.get("conclusion") == "success" and took is not None:
            weeks[week_of(run["created_at"])].append(took)
    return weeks


def critical_path(run: dict, jobs: list[dict]) -> dict[str, float] | None:
    """Where a run's time went: to its first job, across its jobs, and beyond its longest one."""
    done = [job for job in jobs if ran(job)]
    start = parse(run.get("run_started_at"))
    if not done or not start:
        return None
    first = min(parse(job["started_at"]) for job in done)
    last = max(parse(job["completed_at"]) for job in done)
    longest = max(seconds(job["completed_at"], job["started_at"]) for job in done)
    span = (last - first).total_seconds()
    return {"lag": max(0.0, (first - start).total_seconds()), "span": span, "longest": longest,
            "beyond": max(0.0, span - longest)}


def sampled(fetch: Fetch, repo: str, runs: list[dict], size: int, steps: int, workers: int) -> dict:
    """Queue waits, step times and the critical path of the newest `size` successful runs of one workflow."""
    newest = sorted((run for run in runs if run.get("conclusion") == "success"),
                    key=lambda run: run["created_at"], reverse=True)[:size]

    def read(run: dict) -> list[dict]:
        # The latest attempt only: an earlier one's jobs are not what the successful run waited on.
        return list(paged(fetch, f"repos/{repo}/actions/runs/{run['id']}/jobs?filter=latest", "jobs"))

    paths: dict[str, list[float]] = defaultdict(list)
    waits: dict[str, list[float]] = defaultdict(list)
    timings: dict[tuple[str, str], list[float]] = defaultdict(list)
    with ThreadPoolExecutor(workers) as pool:
        for run, jobs in zip(newest, pool.map(read, newest)):
            for key, value in (critical_path(run, jobs) or {}).items():
                paths[key].append(value)
            for job in filter(ran, jobs):
                job_name = matrix_free(job["name"])
                waits[job_name].append(max(0.0, seconds(job["started_at"], job["created_at"])))
                for step in job.get("steps") or []:
                    took = seconds(step.get("completed_at"), step.get("started_at"))
                    if step.get("conclusion") != "skipped" and took is not None:
                        timings[(job_name, step["name"])].append(took)
    jobs_out = {}
    for job_name, values in waits.items():
        slowest = sorted(((step, summary(v)) for (owner, step), v in timings.items() if owner == job_name),
                         key=lambda row: -(row[1]["p90"] or 0))[:steps]
        jobs_out[job_name] = {"wait": summary(values), "steps": [{"step": step, **cell} for step, cell in slowest]}
    return {"sampled": len(newest), "path": {key: summary(v) for key, v in paths.items()}, "jobs": jobs_out}


def repo_latency(fetch: Fetch, repo: str, window: Window, size: int = SAMPLE, steps: int = STEPS,
                 workers: int = WORKERS) -> dict:
    """Every workflow's runs in the window, listed per workflow, which groups them and skips a workflow without."""
    workflows = {}
    for workflow in paged(fetch, f"repos/{repo}/actions/workflows", "workflows"):
        runs = runs_between(fetch, repo, window.start, window.end, workflow=workflow["id"])
        if runs:
            weeks = weekly(runs)
            # Two workflows may share a `name:`; the path tells them apart rather than one replacing the other.
            label = workflow["name"]
            if label in workflows:
                label = f"{label} ({workflow['path']})"
            workflows[label] = {
                "runs": len(runs), "weekly": {week: summary(v) for week, v in sorted(weeks.items())},
                **sampled(fetch, repo, runs, size, steps, workers)}
    return {"repo": repo, "window": {"name": window.name, "first": window.first.isoformat(),
                                     "last": window.last.isoformat()}, "workflows": workflows}


def slots_of_day(fetch: Fetch, repos: list[str], day: date, workers: int = WORKERS) -> list[dict[str, datetime]]:
    """Each job of the runs created on the day, with when it was queued, started and ended."""
    window = Window(day.isoformat(), day, day)
    slots = []
    for repo in repos:
        runs = runs_between(fetch, repo, window.start, window.end)
        with ThreadPoolExecutor(workers) as pool:
            for jobs in pool.map(lambda run: jobs_of(fetch, repo, run), runs):
                slots += [{"created": parse(job["created_at"]), "started": parse(job["started_at"]),
                           "completed": parse(job["completed_at"])} for job in jobs if ran(job)]
    return slots


def running_by_minute(slots: list[dict], origin: datetime) -> list[int]:
    """The jobs running at any point of each minute from `origin`, by a sweep over their starts and ends."""
    delta = [0] * (2 + max((math.ceil((s["completed"] - origin).total_seconds() / 60) for s in slots), default=0))
    for slot in slots:
        first = max(0, int((slot["started"] - origin).total_seconds() // 60))
        last = max(first + 1, math.ceil((slot["completed"] - origin).total_seconds() / 60))
        delta[first] += 1
        delta[last] -= 1
    counts, level = [], 0
    for step in delta:
        level += step
        counts.append(level)
    return counts


def replay(slots: list[dict], cap: int) -> list[float]:
    """Each job's wait if at most `cap` ran at once, first come first served, each running as long as it did."""
    busy: list[datetime] = []
    waits = []
    for slot in sorted(slots, key=lambda s: s["created"]):
        while busy and busy[0] <= slot["created"]:
            heapq.heappop(busy)
        began = slot["created"] if len(busy) < cap else heapq.heappop(busy)
        heapq.heappush(busy, began + (slot["completed"] - slot["started"]))
        waits.append((began - slot["created"]).total_seconds())
    return waits


def wait_row(waits: list[float]) -> dict:
    return {**summary(waits), "max": round(max(waits), 1) if waits else None,
            "queued": sum(1 for wait in waits if wait > QUEUED_AFTER)}


def day_latency(fetch: Fetch, repos: list[str], day: date, caps: tuple[int, ...] = CAPS,
                workers: int = WORKERS) -> dict:
    slots = slots_of_day(fetch, repos, day, workers)
    origin = datetime(day.year, day.month, day.day, tzinfo=timezone.utc)
    running = running_by_minute(slots, origin)
    queued = [running[int((s["created"] - origin).total_seconds() // 60)] for s in slots
              if (s["started"] - s["created"]).total_seconds() > QUEUED_AFTER]
    hours = []
    for hour in range(24):
        begin = origin + timedelta(hours=hour)
        inside = [s for s in slots if begin <= s["started"] < begin + timedelta(hours=1)]
        hours.append({"hour": hour, "started": len(inside), "peak": max(running[hour * 60:hour * 60 + 60], default=0),
                      "queued": sum(1 for s in inside if (s["started"] - s["created"]).total_seconds() > QUEUED_AFTER),
                      "p90": summary([(s["started"] - s["created"]).total_seconds() for s in inside])["p90"]})
    return {"day": day.isoformat(), "repos": repos, "jobs": len(slots), "peak": max(running, default=0),
            "minutes_at_cap": {str(cap): sum(1 for count in running if count >= cap) for cap in caps},
            "running_when_queued": {**summary(queued), "max": max(queued, default=None)},
            "actual": wait_row([(s["started"] - s["created"]).total_seconds() for s in slots]),
            "replay": {str(cap): wait_row(replay(slots, cap)) for cap in caps}, "hours": hours,
            "running_by_minute": running}


def latency_table(result: dict) -> str:
    window = result["window"]
    lines = [f"### {result['repo']}, window {window['name']} ({window['first']}..{window['last']})", "",
             "Wall-clock time of a successful run by week of creation, p50 / p90:", "",
             "| Workflow | Week | Runs | p50 / p90 |", "|---|---|---|---|"]
    workflows = sorted(result["workflows"].items(), key=lambda row: -row[1]["runs"])
    for name, body in workflows:
        lines += [f"| {name} | {week} | {cell['n']:,} | {pair(cell)} |" for week, cell in body["weekly"].items()]
    lines += ["", "Critical path of the newest successful runs, p50 / p90:", "",
              "| Workflow | Runs read | Run start to first job | First job to last end | Longest job "
              "| Beyond the longest job |", "|---|---|---|---|---|---|"]
    for name, body in workflows:
        if body["path"]:
            lines.append(f"| {name} | {body['sampled']:,} | " + " | ".join(
                pair(body["path"][key]) for key in ("lag", "span", "longest", "beyond")) + " |")
    lines += ["", "Queue wait of each job, creation to start, p50 / p90:", "",
              "| Workflow | Job | Samples | Queue wait |", "|---|---|---|---|"]
    for name, body in workflows:
        for job, cell in sorted(body["jobs"].items(), key=lambda row: -(row[1]["wait"]["p90"] or 0)):
            lines.append(f"| {name} | {job} | {cell['wait']['n']:,} | {pair(cell['wait'])} |")
    lines += ["", "Slowest steps of each job by p90, p50 / p90:", "",
              "| Workflow | Job | Step | Samples | Time |", "|---|---|---|---|---|"]
    for name, body in workflows:
        for job, cell in body["jobs"].items():
            lines += [f"| {name} | {job} | {step['step']} | {step['n']:,} | {pair(step)} |" for step in cell["steps"]]
    return "\n".join(lines)


def day_table(result: dict) -> str:
    actual, queued = result["actual"], result["running_when_queued"]
    lines = [f"### {result['day']}, every job of the runs created that day in {', '.join(result['repos'])}", "",
             f"{result['jobs']:,} jobs; peak {result['peak']:,} running in one minute; "
             f"{queued['n']:,} waited over {QUEUED_AFTER} s, with {queued['p50'] or 0} (p50), "
             f"{queued['p90'] or 0} (p90) and {queued['max'] or 0} (max) running when they were queued.", "",
             f"| UTC hour | Jobs started | Peak running | Waited over {QUEUED_AFTER} s | Wait p90 |",
             "|---|---|---|---|---|"]
    lines += [f"| {h['hour']:02d} | {h['started']:,} | {h['peak']:,} | {h['queued']:,} | {duration(h['p90'])} |"
              for h in result["hours"]]
    lines += ["", f"| Concurrency cap | Minutes at the cap | Jobs waiting over {QUEUED_AFTER} s "
              "| Wait p50 / p90 | Longest wait |", "|---|---|---|---|---|",
              f"| as run | n/a | {actual['queued']:,} | {pair(actual)} | {duration(actual['max'])} |"]
    for cap, row in result["replay"].items():
        lines.append(f"| {cap} | {result['minutes_at_cap'][cap]:,} | {row['queued']:,} | {pair(row)} "
                     f"| {duration(row['max'])} |")
    return "\n".join(lines)


def report(fetch: Fetch, repos: list[str], windows: list[Window], day: date | None = None,
           caps: tuple[int, ...] = CAPS, size: int = SAMPLE, steps: int = STEPS, as_json: bool = False,
           workers: int = WORKERS) -> str:
    results = [repo_latency(fetch, repo, window, size, steps, workers) for window in windows for repo in repos]
    account = day_latency(fetch, repos, day, caps, workers) if day else None
    if as_json:
        return json.dumps({"windows": results, "day": account}, indent=2)
    parts = [latency_table(result) for result in results] + ([day_table(account)] if account else [])
    return "\n\n".join(parts)
