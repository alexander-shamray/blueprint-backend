"""Which runbooks have a game-day script, and why each of the others has none.

The coverage test reads docs/runbooks/ and the loaded rules and keeps no list of its own: a new runbook is uncovered
until it has a script or a reason, and a rule naming a scripted runbook until that script causes it.
"""

from __future__ import annotations

import importlib
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


def _gate():
    path = ROOT / "deploy" / "observability" / "check.py"
    spec = importlib.util.spec_from_file_location("observability_check", path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def not_a_runbook() -> set[str]:
    """check.py's NOT_A_RUNBOOK, read from where it is declared rather than copied."""
    return set(_gate().NOT_A_RUNBOOK)


def loaded_rules() -> dict[str, set[str]]:
    """Each runbook a rule the Compose Prometheus loads names, and those rules, through check.py's own parser."""
    gate = _gate()
    found: dict[str, set[str]] = {}
    for rule in gate.parse_rules(gate.LOADED_RULES):
        if rule["runbook"]:
            found.setdefault(Path(str(rule["runbook"])).name, set()).add(str(rule["alert"]))
    return found


def caused(script_names: set[str]) -> dict[str, set[str]]:
    """Each script's alerts: its ALERT and the ALERT of each phase its NEXT hands on to."""
    found: dict[str, set[str]] = {}
    for name in script_names:
        phase = importlib.import_module(f"scenarios.{name}")
        while phase is not None:
            found.setdefault(name, set()).add(phase.ALERT)
            phase = getattr(phase, "NEXT", None)
    return found


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
             rules: dict[str, set[str]], causes: dict[str, set[str]]) -> list[str]:
    found: list[str] = []
    for name in sorted(runbook_names):
        has_script = module_name(name) in script_names
        if has_script and name in not_on_compose:
            found.append(f"{name}: has a script and is listed NOT_ON_COMPOSE, which says it cannot run")
        if not has_script and name not in not_on_compose:
            found.append(f"{name}: no script and no stated reason; add scenarios/{module_name(name)}.py "
                         f"or an entry in NOT_ON_COMPOSE")
        # A runbook two rules share (check.py's SHARED_RUNBOOKS) is caused whole, its second rule as a NEXT.
        uncaused = rules.get(name, set()) - causes.get(module_name(name), set())
        if has_script and uncaused:
            found.append(f"{name}: a loaded rule sends {', '.join(sorted(uncaused))} here and its script does not "
                         f"cause it; add a phase as NEXT")
    for name, reason in not_on_compose.items():
        if not reason.strip():
            found.append(f"{name}: NOT_ON_COMPOSE carries no reason")
    for name in sorted(set(not_on_compose) - runbook_names):
        found.append(f"{name}: listed, but docs/runbooks/ has no such file")
    for name in sorted(script_names - {module_name(r) for r in runbook_names}):
        found.append(f"scenarios/{name}.py: no runbook of that name in docs/runbooks/")
    return found


def check() -> list[str]:
    names = scripts()
    return problems(runbooks(), names, NOT_ON_COMPOSE, loaded_rules(), caused(names))
