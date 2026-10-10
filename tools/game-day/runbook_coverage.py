"""Which runbooks have a game-day script, and why each of the others has none.

The coverage test (test_coverage.py) reads docs/runbooks/ and keeps no list of runbooks of its own: a runbook
dropped into that directory is uncovered until it has a script or an entry below.
"""

from __future__ import annotations

import importlib.util
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
RUNBOOKS = ROOT / "docs" / "runbooks"
SCENARIOS = Path(__file__).resolve().parent / "scenarios"

# Runbooks that cannot be run on Compose, permanently, each with the reason. A script that can still run a half
# of such a runbook (a first step that is SQL) is not excluded by an entry here; it is simply not required.
NOT_ON_COMPOSE: dict[str, str] = {
    "stuck-saga.md": "its rule is in awaiting-signal.yaml, which the Compose Prometheus does not load (§13.6)",
    "order-review.md": "its rule is in awaiting-signal.yaml, which the Compose Prometheus does not load (§13.6)",
    "redis-cold.md": "its rule is in awaiting-signal.yaml, which the Compose Prometheus does not load (§13.6)",
    "migration-failure.md": "its rule reads kube_job_*, and Compose has no kube-state-metrics",
    "business-volume.md": "its rule compares against `offset 1w`, which needs a week of history",
}

# Runbooks whose script a later pull request of the series delivers, each named. A runbook two share names both.
# The last of them empties this and deletes it, with its case in test_coverage.py.
OWED_PULL_REQUESTS = {"PR-2", "PR-3", "PR-4"}
OWED: dict[str, tuple[str, ...]] = {
    "address-refused.md": ("PR-2",),
    "contact-refused.md": ("PR-2",),
    "unscanned-shipment.md": ("PR-2",),
    "unattributed-order.md": ("PR-2",),
    "queue-backlog.md": ("PR-2", "PR-4"),
    "error-rate.md": ("PR-3",),
    "latency.md": ("PR-3",),
    "outbox-growth.md": ("PR-3",),
    "error-queue.md": ("PR-4",),
    "skipped-queue.md": ("PR-4",),
}


def _gate():
    path = ROOT / "deploy" / "observability" / "check.py"
    spec = importlib.util.spec_from_file_location("observability_check", path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def not_a_runbook() -> set[str]:
    """check.py's NOT_A_RUNBOOK, read from where it is declared rather than copied."""
    return set(_gate().NOT_A_RUNBOOK)


def shared_runbooks() -> set[str]:
    """check.py's SHARED_RUNBOOKS: the runbooks two rules name, which one script may cover half of."""
    return set(_gate().SHARED_RUNBOOKS)


def module_name(runbook: str) -> str:
    """`outbox-broker.md` is scenarios/outbox_broker.py."""
    return runbook.removesuffix(".md").replace("-", "_")


def runbooks(directory: Path = RUNBOOKS, excluded: set[str] | None = None) -> set[str]:
    excluded = not_a_runbook() if excluded is None else excluded
    return {path.name for path in directory.iterdir() if path.is_file() and path.name not in excluded}


def scripts(directory: Path = SCENARIOS) -> set[str]:
    """The module names present, without any claim about what they cover."""
    return {path.stem for path in directory.glob("*.py") if path.stem != "__init__"}


def problems(runbook_names: set[str], script_names: set[str], not_on_compose: dict[str, str],
             owed: dict[str, tuple[str, ...]], shared: set[str]) -> list[str]:
    found: list[str] = []
    for name in sorted(runbook_names):
        has_script = module_name(name) in script_names
        reasons = [label for label, table in (("NOT_ON_COMPOSE", not_on_compose), ("OWED", owed)) if name in table]
        if has_script and "NOT_ON_COMPOSE" in reasons:
            found.append(f"{name}: has a script and is listed NOT_ON_COMPOSE, which says it cannot run")
        # A runbook two rules share may have a script for one rule and be owed the other's.
        if has_script and "OWED" in reasons and name not in shared:
            found.append(f"{name}: has a script and is still listed OWED")
        if not has_script and not reasons:
            found.append(f"{name}: no script and no stated reason; add scenarios/{module_name(name)}.py "
                         f"or an entry in NOT_ON_COMPOSE or OWED")
        if len(reasons) > 1:
            found.append(f"{name}: listed in both NOT_ON_COMPOSE and OWED")
    for name, reason in not_on_compose.items():
        if not reason.strip():
            found.append(f"{name}: NOT_ON_COMPOSE carries no reason")
    for name, pulls in owed.items():
        bad = [pull for pull in pulls if pull not in OWED_PULL_REQUESTS]
        if not pulls or bad:
            found.append(f"{name}: OWED must name PR-2, PR-3 or PR-4, not {pulls or 'nothing'}")
    for name in sorted((set(not_on_compose) | set(owed)) - runbook_names):
        found.append(f"{name}: listed, but docs/runbooks/ has no such file")
    for name in sorted(script_names - {module_name(r) for r in runbook_names}):
        found.append(f"scenarios/{name}.py: no runbook of that name in docs/runbooks/")
    return found


def check() -> list[str]:
    return problems(runbooks(), scripts(), NOT_ON_COMPOSE, OWED, shared_runbooks())
