#!/usr/bin/env python3
"""Measure GitHub Actions usage and its list-price cost over dated windows, from the Actions REST API.

    python tools/ci-cost/ci_cost.py [--repo owner/name] [--window A=2026-10-10:2026-10-23 ...] [--json]
"""

from __future__ import annotations

import argparse
import base64
import http.client
import json
import math
import os
import re
import subprocess
import sys
import time
import urllib.error
import urllib.request
from collections import Counter
from concurrent.futures import ThreadPoolExecutor
from dataclasses import dataclass, field
from datetime import date, datetime, timedelta, timezone
from pathlib import Path
from typing import Callable
from urllib.parse import quote

# USD per minute of a standard runner, from docs.github.com/en/billing/reference/actions-runner-pricing as read on
# RATES_READ; that page names no self-hosted charge, so none is applied. Public repositories pay none of it.
RATES_READ = "2026-11-09"
RATES = {"Linux": 0.006, "Windows": 0.010, "macOS": 0.062, "self-hosted": 0.0}
# Included minutes a month, per plan, from the same documentation; they are spent at the Linux rate's value.
ALLOWANCES = {"Free": 2000, "Pro": 3000, "Team": 3000, "Enterprise": 50000}
PAGE = 100
SMALL_PAGE = 20
# The runs endpoint returns at most this many results for one query, whatever the pages say.
SEARCH_CAP = 1000
WORKERS = 16
# A 14-day window reads about 9,000 runs' jobs, past the 5,000 requests an hour a user token is allowed, so a
# refused call waits and retries; GitHub asks a secondary limit to wait at least a minute.
SECONDARY_WAIT = 60
# A window's thousands of calls meet the odd dropped connection or 5xx, which one retry usually clears.
TRANSIENT_TRIES = 4
TRANSIENT_WAIT = 10
EVENTS = ("pull_request", "push", "schedule", "workflow_dispatch")
# The workflow broken down by job and conclusion, which is what tells the changes to it apart: most of the bill.
MAIN_WORKFLOW = "CI"
WINDOW = re.compile(r"(?:([^=]+)=)?(\d{4}-\d{2}-\d{2}):(\d{4}-\d{2}-\d{2})")

# The registry answers a manifest in whichever form it holds only when the request accepts that form.
MANIFEST_TYPES = ", ".join(("application/vnd.oci.image.index.v1+json",
                            "application/vnd.oci.image.manifest.v1+json",
                            "application/vnd.docker.distribution.manifest.list.v2+json",
                            "application/vnd.docker.distribution.manifest.v2+json"))
REGISTRY = "https://ghcr.io"
REGISTRY_TIMEOUT = 30

Fetch = Callable[[str], object]
Manifest = Callable[[str, str], dict]


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
    linux_minutes_by_day: Counter = field(default_factory=Counter)
    equivalent_by_day: Counter = field(default_factory=Counter)
    main_pr_runs: int = 0
    main_pr_heads: set = field(default_factory=set)
    main_conclusions: Counter = field(default_factory=Counter)
    main_minutes_by_conclusion: Counter = field(default_factory=Counter)
    main_minutes_by_job: Counter = field(default_factory=Counter)
    main_runs_by_job: Counter = field(default_factory=Counter)
    prs: int | None = None
    pushes: int = 0
    label_events: int | None = None
    packages: dict | None = None
    package_bytes: int | None = None
    package_unread: int = 0
    actions_storage: dict | None = None
    unavailable: list = field(default_factory=list)

    @property
    def dollars(self) -> float:
        return sum(RATES[os] * minutes for os, minutes in self.minutes.items())

    @property
    def linux_equivalent(self) -> float:
        return self.dollars / RATES["Linux"]

    def monthly(self, value: float) -> float:
        return value * 30 / self.window.days


def gh(path: str, method: str = "GET") -> object:
    """One `gh api` call, so the repository's visibility and the token's scopes are gh's concern."""
    transient = 0
    while True:
        done = subprocess.run(["gh", "api", "--method", method, path], capture_output=True, encoding="utf-8")
        if done.returncode == 0:
            # A DELETE answers 204 with no body.
            return json.loads(done.stdout) if done.stdout.strip() else None
        message = done.stderr.strip() or done.stdout.strip()
        if "rate limit" in message.lower():
            time.sleep(rate_limit_wait())
            continue
        # gh names the status of every answer it got, so a message without one is retried as a dropped connection;
        # a refusal of gh's own, such as no login, fails the same way each time and ends the call after the tries.
        status = re.search(r"\(HTTP (\d{3})\)", message)
        transient += 1
        if (status and not status[1].startswith("5")) or transient >= TRANSIENT_TRIES:
            raise LookupError(f"{path}: {message}")
        time.sleep(TRANSIENT_WAIT * transient)


def rate_limit_wait() -> int:
    """Seconds until the hourly limit resets when it is spent, else a secondary limit's minute, as GitHub asks."""
    done = subprocess.run(["gh", "api", "rate_limit"], capture_output=True, encoding="utf-8")
    if done.returncode != 0:
        return SECONDARY_WAIT
    core = json.loads(done.stdout)["resources"]["core"]
    return max(SECONDARY_WAIT, core["reset"] - int(time.time()) + 1) if core["remaining"] == 0 else SECONDARY_WAIT


def paged(fetch: Fetch, path: str, key: str | None = None):
    """Every item of a listing, paged by number: the proxy some sessions run behind refuses gh's Link URLs."""
    separator = "&" if "?" in path else "?"

    def read(size: int, number: int) -> list:
        body = fetch(f"{path}{separator}per_page={size}&page={number}")
        return body[key] if key else body

    # A listing newest first moves down a row when one arrives between two pages, which repeats the row at the
    # boundary: docs/ci-cost-comparison.md, *Window A reproduced*, is what that does to a count.
    seen = set()
    page = 1
    while True:
        try:
            items = read(PAGE, page)
        except LookupError as error:
            if "(HTTP 5" not in str(error):
                raise
            # A run re-run twice holds over 100 jobs, whose full page answered 502 on every try on 2026-11-09.
            items = []
            for small in range((page - 1) * PAGE // SMALL_PAGE + 1, page * PAGE // SMALL_PAGE + 1):
                chunk = read(SMALL_PAGE, small)
                items += chunk
                if len(chunk) < SMALL_PAGE:
                    break
        for item in items:
            identity = item.get("id")
            if identity is None or identity not in seen:
                seen.add(identity)
                yield item
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
    if total > SEARCH_CAP:
        raise LookupError(f"{total} runs in {stamp(start)}, more than a query answers: the count would be short")
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
    """PRs opened in the window, from the repository's own listing: some tokens are refused the search API."""
    count = 0
    for pull in paged(fetch, f"repos/{repo}/pulls?state=all&sort=created&direction=desc"):
        created = parse(pull["created_at"])
        if created < window.start:
            break
        count += created < window.end
    return count


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


def package_root(fetch: Fetch, repo: str) -> str:
    owner = repo.split("/")[0]
    kind = fetch(f"users/{owner}")["type"]
    return f"{'orgs' if kind == 'Organization' else 'users'}/{owner}/packages"


def versions_path(root: str, package: str) -> str:
    # A package named owner-repo/image is one path segment, so its slash is escaped.
    return f"{root}/container/{quote(package, safe='')}/versions"


def package_versions(fetch: Fetch, repo: str) -> dict[str, list[dict]]:
    """Each of the repository's container packages, with every version the listing holds."""
    root = package_root(fetch, repo)
    return {package["name"]: list(paged(fetch, versions_path(root, package["name"])))
            for package in paged(fetch, f"{root}?package_type=container")
            if (package.get("repository") or {}).get("full_name") == repo}


def registry(repo: str) -> Manifest:
    """A manifest reader over the registry API, anonymous but for GH_TOKEN: the REST API reports no size."""
    owner = repo.split("/")[0].lower()
    tokens: dict[str, str] = {}

    def ask(url: str, headers: dict) -> dict:
        with urllib.request.urlopen(urllib.request.Request(url, headers=headers), timeout=REGISTRY_TIMEOUT) as answer:
            return json.load(answer)

    def token(package: str) -> str:
        if package not in tokens:
            secret = os.environ.get("GH_TOKEN")
            basic = base64.b64encode(f"token:{secret}".encode()).decode() if secret else None
            tokens[package] = ask(f"{REGISTRY}/token?scope=repository:{owner}/{package}:pull",
                                  {"Authorization": f"Basic {basic}"} if basic else {})["token"]
        return tokens[package]

    def manifest(package: str, reference: str) -> dict:
        for attempt in range(1, TRANSIENT_TRIES + 1):
            try:
                return ask(f"{REGISTRY}/v2/{owner}/{package}/manifests/{reference}",
                           {"Authorization": f"Bearer {token(package)}", "Accept": MANIFEST_TYPES})
            except urllib.error.HTTPError as error:
                # A registry token lasts minutes and a walk outlasts it.
                if error.code == 401:
                    tokens.pop(package, None)
                elif error.code < 500:
                    raise LookupError(f"{package}@{reference}: HTTP {error.code}") from error
                failure = error
            except (OSError, ValueError, http.client.HTTPException) as error:
                failure = error
            time.sleep(TRANSIENT_WAIT * attempt)
        raise LookupError(f"{package}@{reference}: {failure}")

    return manifest


def registry_bytes(manifest: Manifest, versions: dict[str, list[dict]], workers: int = WORKERS) -> tuple[int, int]:
    """The bytes of every blob the versions' manifests name, each counted once, and the manifests left unread."""

    def read(pair: tuple[str, str]) -> dict | None:
        try:
            return manifest(*pair)
        except LookupError:
            return None

    pairs = [(package, row["name"]) for package, rows in versions.items() for row in rows if "name" in row]
    blobs: dict[str, int] = {}
    unread = 0
    with ThreadPoolExecutor(workers) as pool:
        for body in pool.map(read, pairs):
            if body is None:
                unread += 1
                continue
            for blob in (body.get("config") or {}, *body.get("layers", [])):
                if "digest" in blob:
                    blobs[blob["digest"]] = blob.get("size", 0)
    return sum(blobs.values()), unread


def storage(fetch: Fetch, repo: str) -> dict:
    """The live artifacts and the active caches, which Actions bills as a level."""
    live = [row for row in paged(fetch, f"repos/{repo}/actions/artifacts", "artifacts") if not row.get("expired")]
    return {"artifacts": len(live), "artifact_bytes": sum(row["size_in_bytes"] for row in live),
            "cache_bytes": fetch(f"repos/{repo}/actions/cache/usage")["active_caches_size_in_bytes"]}


def measure(fetch: Fetch, repo: str, window: Window, *, current: bool = False, workers: int = WORKERS,
            manifest: Manifest | None = None) -> Usage:
    usage = Usage(window)
    runs = runs_between(fetch, repo, window.start, window.end)
    usage.runs = len(runs)
    usage.by_workflow.update(run["name"] for run in runs)
    usage.by_event.update(run["event"] for run in runs)
    usage.pushes = len({run["head_sha"] for run in runs if run["event"] == "push"})
    with ThreadPoolExecutor(workers) as pool:
        for run, jobs in zip(runs, pool.map(lambda run: jobs_of(fetch, repo, run), runs)):
            main = run["name"] == MAIN_WORKFLOW
            conclusion = run.get("conclusion") or "unfinished"
            if main:
                usage.main_conclusions[conclusion] += 1
                if run["event"] == "pull_request":
                    usage.main_pr_runs += 1
                    usage.main_pr_heads.add(run["head_sha"])
                usage.main_runs_by_job.update({job["name"].split(" (")[0] for job in jobs if billed_minutes(job)})
            for job in jobs:
                minutes = billed_minutes(job)
                equivalent = minutes * RATES[runner_os(job)] / RATES["Linux"]
                if minutes:
                    day = parse(job["started_at"]).date().isoformat()
                    usage.equivalent_by_day[day] += equivalent
                    usage.linux_minutes_by_day[day] += minutes if runner_os(job) == "Linux" else 0
                usage.jobs += 1
                usage.minutes[runner_os(job)] += minutes
                usage.minutes_by_workflow[run["name"]] += equivalent
                if main:
                    usage.main_minutes_by_conclusion[conclusion] += equivalent
                    usage.main_minutes_by_job[job["name"].split(" (")[0]] += equivalent
    for name, read, target in (("PRs", count_prs, "prs"), ("label events", count_label_events, "label_events")):
        try:
            setattr(usage, target, read(fetch, repo, window))
        except LookupError as error:
            usage.unavailable.append(f"{name}: {error}")
    # Storage is a level, not a flow: only a window ending now can read it, since the API keeps no history.
    if current:
        try:
            versions = package_versions(fetch, repo)
            usage.packages = {package: len(rows) for package, rows in versions.items()}
            usage.package_bytes, usage.package_unread = registry_bytes(manifest or registry(repo), versions, workers)
        except LookupError as error:
            usage.unavailable.append(f"GHCR packages: {error}")
        try:
            usage.actions_storage = storage(fetch, repo)
        except LookupError as error:
            usage.unavailable.append(f"Actions storage: {error}")
    return usage


def money(value: float) -> str:
    return f"${value:,.2f}"


def gigabytes(value: float) -> str:
    return f"{value / 1e9:,.2f} GB"


def package_size(usage: Usage) -> str:
    if usage.package_bytes is None:
        return "n/a"
    floor = f", a floor: {usage.package_unread:,} manifests unread" if usage.package_unread else ""
    return gigabytes(usage.package_bytes) + floor


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
        lambda u: f"{u.monthly(u.linux_equivalent) / ALLOWANCES[plan]:,.0%}")
    row("List price if private", lambda u: money(u.dollars))
    row("List price if private, per 30 days", lambda u: money(u.monthly(u.dollars)))
    row(f"Over the {plan} allowance, per 30 days",
        lambda u: money(max(0.0, u.monthly(u.dollars) - ALLOWANCES[plan] * RATES["Linux"])))
    row("PRs opened", lambda u: "n/a" if u.prs is None else f"{u.prs:,}")
    row("Commits pushed that ran a workflow", lambda u: f"{u.pushes:,}")
    row("Label events", lambda u: "n/a" if u.label_events is None else f"{u.label_events:,}")
    row("List price per run", lambda u: per(u.dollars, u.runs))
    row("List price per PR", lambda u: per(u.dollars, u.prs))
    row("GHCR container versions, at the window's end",
        lambda u: "n/a" if u.packages is None else f"{sum(u.packages.values()):,} in {len(u.packages)} packages")
    row("GHCR blobs, each counted once, at the window's end", package_size)
    row("Actions artifacts live, at the window's end",
        lambda u: "n/a" if u.actions_storage is None else
        f"{u.actions_storage['artifacts']:,} holding {gigabytes(u.actions_storage['artifact_bytes'])}")
    row("Actions caches, at the window's end",
        lambda u: "n/a" if u.actions_storage is None else gigabytes(u.actions_storage["cache_bytes"]))
    row(f"{MAIN_WORKFLOW}'s runs on `pull_request`", lambda u: f"{u.main_pr_runs:,}")
    row(f"{MAIN_WORKFLOW}'s `pull_request` runs per head commit",
        lambda u: f"{u.main_pr_runs / len(u.main_pr_heads):.2f}" if u.main_pr_heads else "n/a")
    for conclusion in sorted({c for u in usages for c in u.main_conclusions}):
        row(f"{MAIN_WORKFLOW}'s runs that ended `{conclusion}`, and their Linux-equivalent minutes",
            lambda u, c=conclusion: f"{u.main_conclusions[c]:,}, {u.main_minutes_by_conclusion[c]:,.0f}")

    workflows = sorted({w for u in usages for w in u.by_workflow}, key=lambda w: -usages[0].minutes_by_workflow[w])
    lines += ["", "| Workflow | " + " | ".join(f"{u.window.name} runs | {u.window.name} Linux-eq. minutes"
                                                for u in usages) + " |",
              "|---" * (2 * len(usages) + 1) + "|"]
    for workflow in workflows:
        lines.append(f"| {workflow} | " + " | ".join(
            f"{u.by_workflow[workflow]:,} | {u.minutes_by_workflow[workflow]:,.0f}" for u in usages) + " |")
    main_jobs = sorted({j for u in usages for j in u.main_minutes_by_job},
                       key=lambda j: -usages[0].main_minutes_by_job[j])
    lines += ["", f"| {MAIN_WORKFLOW}'s job | "
              + " | ".join(f"{u.window.name} Linux-eq. minutes | {u.window.name} runs it billed in" for u in usages)
              + " |",
              "|---" * (2 * len(usages) + 1) + "|"]
    for main_job in main_jobs:
        lines.append(f"| {main_job} | " + " | ".join(
            f"{u.main_minutes_by_job[main_job]:,.0f} | {u.main_runs_by_job[main_job]:,}" for u in usages) + " |")
    for usage in usages:
        lines += ["", f"| {usage.window.name}'s day, by when a job started | Linux minutes | Linux-eq. minutes |",
                  "|---|---|---|"]
        for day in sorted(usage.equivalent_by_day):
            lines.append(f"| {day} | {usage.linux_minutes_by_day[day]:,} | {usage.equivalent_by_day[day]:,.0f} |")
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
                           "linux_minutes_by_day": dict(u.linux_minutes_by_day),
                           "linux_equivalent_by_day": {k: round(v, 1) for k, v in u.equivalent_by_day.items()},
                           "prs": u.prs, "pushes": u.pushes, "label_events": u.label_events,
                           "main_workflow": {
                               "name": MAIN_WORKFLOW, "pr_runs": u.main_pr_runs, "pr_heads": len(u.main_pr_heads),
                               "conclusions": dict(u.main_conclusions),
                               "minutes_by_conclusion": {
                                   k: round(v, 1) for k, v in u.main_minutes_by_conclusion.items()},
                               "minutes_by_job": {k: round(v, 1) for k, v in u.main_minutes_by_job.items()},
                               "runs_by_job": dict(u.main_runs_by_job)},
                           "packages": u.packages, "package_bytes": u.package_bytes,
                           "package_unread": u.package_unread, "actions_storage": u.actions_storage,
                           "unavailable": u.unavailable}
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
    url = subprocess.run(["git", "remote", "get-url", "origin"], capture_output=True, encoding="utf-8",
                         cwd=Path(__file__).parent).stdout.strip()
    match = re.search(r"([^/:]+/[^/]+?)(?:\.git)?$", url)
    if not match:
        raise SystemExit("no --repo given and no origin remote to read one from")
    return match[1]


def main(argv: list[str] | None = None, fetch: Fetch = gh, today: date | None = None,
         manifest: Manifest | None = None) -> int:
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
    usages = [measure(fetch, repo, w, current=w.last >= today - timedelta(days=1), manifest=manifest)
              for w in windows]
    print(as_json(usages, args.plan) if args.json else table(usages, args.plan))
    return 0


if __name__ == "__main__":
    sys.exit(main())
