#!/usr/bin/env python3
"""Measure GitHub Actions usage and its list-price cost over dated windows, from the Actions REST API.

    python tools/ci-cost/ci_cost.py [--repo owner/name] [--window A=2026-10-10:2026-10-23 ...] [--json]
"""

from __future__ import annotations

import argparse
import json
import math
import re
import subprocess
import sys
from collections import Counter
from concurrent.futures import ThreadPoolExecutor
from dataclasses import dataclass, field
from datetime import date, datetime, timedelta, timezone
from pathlib import Path
from typing import Callable

# USD per minute of a standard runner, from docs.github.com/en/billing/reference/actions-runner-pricing as read on
# RATES_READ; that page names no self-hosted charge, so none is applied. Public repositories pay none of it.
RATES_READ = "2026-10-24"
RATES = {"Linux": 0.006, "Windows": 0.010, "macOS": 0.062, "self-hosted": 0.0}
# Included minutes a month, per plan, from the same documentation; they are spent at the Linux rate's value.
ALLOWANCES = {"Free": 2000, "Pro": 3000, "Team": 3000, "Enterprise": 50000}
PAGE = 100
# The runs endpoint returns at most this many results for one query, whatever the pages say.
SEARCH_CAP = 1000
WORKERS = 16
EVENTS = ("pull_request", "push", "schedule", "workflow_dispatch")
WINDOW = re.compile(r"(?:([^=]+)=)?(\d{4}-\d{2}-\d{2}):(\d{4}-\d{2}-\d{2})")

Fetch = Callable[[str], object]


@dataclass(frozen=True)
class Window:
    name: str
    first: date
    last: date

    @property
    def start(self) -> datetime:
        return datetime(self.first.year, self.first.month, self.first.day, tzinfo=timezone.utc)

    @property
    def end(self) -> datetime:
        return self.start + timedelta(days=self.days)

    @property
    def days(self) -> int:
        return (self.last - self.first).days + 1


@dataclass
class Usage:
    window: Window
    runs: int = 0
    by_workflow: Counter = field(default_factory=Counter)
    by_event: Counter = field(default_factory=Counter)
    jobs: int = 0
    minutes: Counter = field(default_factory=Counter)
    minutes_by_workflow: Counter = field(default_factory=Counter)
    prs: int | None = None
    pushes: int = 0
    label_events: int | None = None
    packages: dict | None = None
    unavailable: list = field(default_factory=list)

    @property
    def dollars(self) -> float:
        return sum(RATES[os] * minutes for os, minutes in self.minutes.items())

    @property
    def linux_equivalent(self) -> float:
        return self.dollars / RATES["Linux"]

    def monthly(self, value: float) -> float:
        return value * 30 / self.window.days


def gh(path: str) -> object:
    """One `gh api` call, so the repository's visibility and the token's scopes are gh's concern."""
    done = subprocess.run(["gh", "api", path], capture_output=True, text=True)
    if done.returncode != 0:
        raise LookupError(f"{path}: {done.stderr.strip() or done.stdout.strip()}")
    return json.loads(done.stdout)


def paged(fetch: Fetch, path: str, key: str | None = None):
    """Every item of a listing, paged by number: the proxy some sessions run behind refuses gh's Link URLs."""
    separator = "&" if "?" in path else "?"
    page = 1
    while True:
        body = fetch(f"{path}{separator}per_page={PAGE}&page={page}")
        items = body[key] if key else body
        yield from items
        if len(items) < PAGE:
            return
        page += 1


def stamp(moment: datetime) -> str:
    return moment.strftime("%Y-%m-%dT%H:%M:%SZ")


def runs_between(fetch: Fetch, repo: str, start: datetime, end: datetime) -> list[dict]:
    """The runs created in [start, end), halving the span until each query fits under SEARCH_CAP."""
    query = f"repos/{repo}/actions/runs?created={stamp(start)}..{stamp(end - timedelta(seconds=1))}"
    total = fetch(f"{query}&per_page=1")["total_count"]
    if total > SEARCH_CAP and end - start > timedelta(minutes=1):
        middle = start + (end - start) / 2
        middle = middle.replace(microsecond=0)
        return runs_between(fetch, repo, start, middle) + runs_between(fetch, repo, middle, end)
    return list(paged(fetch, query, "workflow_runs"))


def runner_os(job: dict) -> str:
    labels = " ".join(job.get("labels") or []).lower()
    if "self-hosted" in labels:
        return "self-hosted"
    if "windows" in labels:
        return "Windows"
    if "macos" in labels:
        return "macOS"
    return "Linux"


def parse(moment: str | None) -> datetime | None:
    return datetime.fromisoformat(moment.replace("Z", "+00:00")) if moment else None


def billed_minutes(job: dict) -> int:
    """A job's run time rounded up to the whole minute, as GitHub bills it; a job that never started bills none."""
    started, completed = parse(job.get("started_at")), parse(job.get("completed_at"))
    if not started or not completed or completed <= started:
        return 0
    return math.ceil((completed - started).total_seconds() / 60)


def jobs_of(fetch: Fetch, repo: str, run: dict) -> list[dict]:
    # filter=all also returns the jobs of earlier attempts, which a re-run billed as well.
    return list(paged(fetch, f"repos/{repo}/actions/runs/{run['id']}/jobs?filter=all", "jobs"))


def count_prs(fetch: Fetch, repo: str, window: Window) -> int:
    query = f"repo:{repo}+is:pr+created:{window.first.isoformat()}..{window.last.isoformat()}"
    return fetch(f"search/issues?q={query}&per_page=1")["total_count"]


def count_label_events(fetch: Fetch, repo: str, window: Window) -> int:
    """Labels added or removed in the window, read newest first until the listing passes the window's start."""
    count = 0
    for event in paged(fetch, f"repos/{repo}/issues/events"):
        created = parse(event["created_at"])
        if created < window.start:
            break
        if created < window.end and event["event"] in ("labeled", "unlabeled"):
            count += 1
    return count


def packages(fetch: Fetch, repo: str) -> dict:
    """Each container package's version count: the REST API reports no size, which only the billing report holds."""
    owner, name = repo.split("/")
    kind = fetch(f"users/{owner}")["type"]
    root = f"{'orgs' if kind == 'Organization' else 'users'}/{owner}/packages"
    found = {}
    for package in paged(fetch, f"{root}?package_type=container"):
        if (package.get("repository") or {}).get("full_name", repo) != repo:
            continue
        found[package["name"]] = sum(1 for _ in paged(fetch, f"{root}/container/{package['name']}/versions"))
    return found


def measure(fetch: Fetch, repo: str, window: Window, *, current: bool = False, workers: int = WORKERS) -> Usage:
    usage = Usage(window)
    runs = runs_between(fetch, repo, window.start, window.end)
    usage.runs = len(runs)
    usage.by_workflow.update(run["name"] for run in runs)
    usage.by_event.update(run["event"] for run in runs)
    usage.pushes = len({run["head_sha"] for run in runs if run["event"] == "push"})
    with ThreadPoolExecutor(workers) as pool:
        for run, jobs in zip(runs, pool.map(lambda run: jobs_of(fetch, repo, run), runs)):
            for job in jobs:
                minutes = billed_minutes(job)
                usage.jobs += 1
                usage.minutes[runner_os(job)] += minutes
                usage.minutes_by_workflow[run["name"]] += minutes * RATES[runner_os(job)] / RATES["Linux"]
    for name, read, target in (("PRs", count_prs, "prs"), ("label events", count_label_events, "label_events")):
        try:
            setattr(usage, target, read(fetch, repo, window))
        except LookupError as error:
            usage.unavailable.append(f"{name}: {error}")
    # Storage is a level, not a flow: only a window ending now can read it, since the API keeps no history.
    if current:
        try:
            usage.packages = packages(fetch, repo)
        except LookupError as error:
            usage.unavailable.append(f"GHCR packages: {error}")
    return usage


def money(value: float) -> str:
    return f"${value:,.2f}"


def per(value: float, count: int | None) -> str:
    return money(value / count) if count else "n/a"


def table(usages: list[Usage], plan: str) -> str:
    header = "| | " + " | ".join(f"{u.window.name} ({u.window.first}..{u.window.last})" for u in usages) + " |"
    lines = [header, "|---" * (len(usages) + 1) + "|"]

    def row(label: str, cell: Callable[[Usage], object]):
        lines.append(f"| {label} | " + " | ".join(str(cell(u)) for u in usages) + " |")

    row("Days", lambda u: u.window.days)
    row("Runs", lambda u: f"{u.runs:,}")
    for event in EVENTS:
        row(f"Runs on `{event}`", lambda u, e=event: f"{u.by_event[e]:,}")
    row("Runs on other events", lambda u: f"{sum(n for e, n in u.by_event.items() if e not in EVENTS):,}")
    row("Jobs, every attempt", lambda u: f"{u.jobs:,}")
    for os in RATES:
        row(f"Billed minutes, {os}", lambda u, o=os: f"{u.minutes[o]:,}")
    row("Linux-equivalent minutes", lambda u: f"{u.linux_equivalent:,.0f}")
    row("Linux-equivalent minutes, per 30 days", lambda u: f"{u.monthly(u.linux_equivalent):,.0f}")
    row(f"Share of the {plan} allowance, per 30 days",
        lambda u: f"{u.monthly(u.linux_equivalent) / ALLOWANCES[plan]:.0%}")
    row("List price if private", lambda u: money(u.dollars))
    row("List price if private, per 30 days", lambda u: money(u.monthly(u.dollars)))
    row(f"Over the {plan} allowance, per 30 days",
        lambda u: money(max(0.0, u.monthly(u.dollars) - ALLOWANCES[plan] * RATES["Linux"])))
    row("PRs opened", lambda u: "n/a" if u.prs is None else u.prs)
    row("Commits pushed that ran a workflow", lambda u: u.pushes)
    row("Label events", lambda u: "n/a" if u.label_events is None else u.label_events)
    row("List price per run", lambda u: per(u.dollars, u.runs))
    row("List price per PR", lambda u: per(u.dollars, u.prs))
    row("GHCR container versions, at the window's end",
        lambda u: "n/a" if u.packages is None else f"{sum(u.packages.values()):,} in {len(u.packages)} packages")

    workflows = sorted({w for u in usages for w in u.by_workflow}, key=lambda w: -usages[0].minutes_by_workflow[w])
    lines += ["", "| Workflow | " + " | ".join(f"{u.window.name} runs | {u.window.name} Linux-eq. minutes"
                                                for u in usages) + " |",
              "|---" * (2 * len(usages) + 1) + "|"]
    for workflow in workflows:
        lines.append(f"| {workflow} | " + " | ".join(
            f"{u.by_workflow[workflow]:,} | {u.minutes_by_workflow[workflow]:,.0f}" for u in usages) + " |")
    notes = [f"{u.window.name}: {note}" for u in usages for note in u.unavailable]
    return "\n".join(lines + ([""] + [f"- {note}" for note in notes] if notes else []))


def as_json(usages: list[Usage], plan: str) -> str:
    return json.dumps({"rates": RATES, "rates_read": RATES_READ, "plan": plan, "allowance": ALLOWANCES[plan],
                       "windows": [{
                           "name": u.window.name, "first": u.window.first.isoformat(),
                           "last": u.window.last.isoformat(), "runs": u.runs, "by_event": dict(u.by_event),
                           "by_workflow": dict(u.by_workflow), "jobs": u.jobs, "minutes": dict(u.minutes),
                           "linux_equivalent_by_workflow": {k: round(v, 1) for k, v in u.minutes_by_workflow.items()},
                           "linux_equivalent": round(u.linux_equivalent, 1), "dollars": round(u.dollars, 2),
                           "prs": u.prs, "pushes": u.pushes, "label_events": u.label_events,
                           "packages": u.packages, "unavailable": u.unavailable}
                           for u in usages]}, indent=2)


def window_of(text: str, index: int) -> Window:
    match = WINDOW.fullmatch(text)
    if not match:
        raise argparse.ArgumentTypeError(f"{text!r} is not NAME=YYYY-MM-DD:YYYY-MM-DD")
    first, last = date.fromisoformat(match[2]), date.fromisoformat(match[3])
    if last < first:
        raise argparse.ArgumentTypeError(f"{text!r} ends before it starts")
    return Window(match[1] or chr(ord("A") + index), first, last)


def origin_repo() -> str:
    url = subprocess.run(["git", "remote", "get-url", "origin"], capture_output=True, text=True,
                         cwd=Path(__file__).parent).stdout.strip()
    match = re.search(r"([^/:]+/[^/]+?)(?:\.git)?$", url)
    if not match:
        raise SystemExit("no --repo given and no origin remote to read one from")
    return match[1]


def main(argv: list[str] | None = None, fetch: Fetch = gh, today: date | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--repo", help="owner/name; the origin remote's by default")
    parser.add_argument("--window", action="append", default=[],
                        help="NAME=FIRST:LAST, both days inclusive and in UTC; the last 14 full days by default")
    parser.add_argument("--plan", choices=ALLOWANCES, default="Free")
    parser.add_argument("--json", action="store_true")
    args = parser.parse_args(argv)
    today = today or datetime.now(timezone.utc).date()
    try:
        windows = [window_of(text, i) for i, text in enumerate(args.window)]
    except argparse.ArgumentTypeError as error:
        parser.error(str(error))
    windows = windows or [Window("A", today - timedelta(days=14), today - timedelta(days=1))]
    repo = args.repo or origin_repo()
    usages = [measure(fetch, repo, w, current=w.last >= today - timedelta(days=1)) for w in windows]
    print(as_json(usages, args.plan) if args.json else table(usages, args.plan))
    return 0


if __name__ == "__main__":
    sys.exit(main())
