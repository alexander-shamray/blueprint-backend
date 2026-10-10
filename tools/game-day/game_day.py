"""Cause a runbook's alert on the Compose stack and check the runbook's first step.
Usage: py -3.12 tools/game-day/game_day.py outbox-broker (or --list). The stack must be up; README.md owns the rest.
"""

from __future__ import annotations

import argparse
import importlib
import sys
import time
from pathlib import Path
from types import ModuleType

sys.path.insert(0, str(Path(__file__).resolve().parent))

import runbook_coverage as coverage  # noqa: E402
import harness  # noqa: E402


def load(runbook: str) -> ModuleType:
    name = coverage.module_name(runbook if runbook.endswith(".md") else runbook + ".md")
    if name not in coverage.scripts():
        raise harness.GameDayError(f"{runbook}: no script. {coverage.module_name(runbook)} is not under scenarios/")
    return importlib.import_module(f"scenarios.{name}")


def run(scenario: ModuleType, world: harness.World, *, clock=time.monotonic, sleep=time.sleep) -> list[str]:
    """One scenario, start to restore. Returns the findings; an empty list is a pass.

    The restore runs whatever the cause did, including when the cause itself raised half-way, because a
    scenario that left rabbitmq stopped would turn every one after it into a finding about the wrong thing.
    """
    findings: list[str] = []
    say = world.say
    alert = scenario.ALERT

    # A rule nobody loaded is also quiet, and a quiet start is what makes "it fired" mean the cause did it.
    if not world.alerts.loaded(alert):
        return [f"{alert}: Prometheus has no such rule loaded, so it cannot fire"]
    if world.alerts.state(alert) != "inactive":
        return [f"{alert}: already {world.alerts.state(alert)} before the cause; restore the stack and rerun"]

    try:
        say(f"{scenario.RUNBOOK}: causing {alert}; deadline {scenario.DEADLINE.derivation}")
        world.caused_at = time.time()
        scenario.cause(world)
        try:
            took = harness.wait_until(
                lambda: _is(world, alert, "firing"), scenario.DEADLINE.seconds, f"{alert} firing",
                clock=clock, sleep=sleep)
            say(f"{alert} fired after {took:.0f}s")
        except harness.Timeout as error:
            findings.append(f"the alert did not fire: {error}")
        ok, detail = scenario.first_step(world)
        say(f"first step {'worked' if ok else 'FAILED'}: {detail}")
        if not ok:
            findings.append(f"the runbook's first step did not work as written: {detail}")
    except harness.GameDayError as error:
        findings.append(f"the cause or the step failed: {error}")
    finally:
        findings.extend(_restore(scenario, world, clock, sleep))
    return findings


def _is(world: harness.World, alert: str, wanted: str) -> tuple[bool, str]:
    state = world.alerts.state(alert)
    return state == wanted, f"{alert} is {state}"


def _restore(scenario: ModuleType, world: harness.World, clock, sleep) -> list[str]:
    try:
        scenario.restore(world)
        # Settling is bounded by the cause's derivation: the gauge has to be exported and judged again. A rule
        # that reads a window (an `increase` over 30m) names its own, as SETTLE, because it outlasts that sum.
        bound = getattr(scenario, "SETTLE", scenario.DEADLINE).seconds + 120
        harness.wait_until(lambda: scenario.settled(world), bound,
                           f"{scenario.ALERT} resolving after the restore", clock=clock, sleep=sleep)
    except Exception as error:  # noqa: BLE001 - a restore that fails for any reason is the finding
        return [f"the restore did not settle, so the next scenario starts poisoned: {error}"]
    world.say(f"{scenario.ALERT} resolved; the stack is back")
    return []


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("runbook", nargs="?", help="a file name from docs/runbooks/, with or without .md")
    parser.add_argument("--list", action="store_true", help="the runbooks that have a script, and why the rest do not")
    args = parser.parse_args(argv)

    if args.list:
        for message in coverage.check():
            print(f"coverage: {message}")
        for name in sorted(coverage.runbooks()):
            state = ("script" if coverage.module_name(name) in coverage.scripts()
                     else f"NOT_ON_COMPOSE: {coverage.NOT_ON_COMPOSE[name]}" if name in coverage.NOT_ON_COMPOSE
                     else f"OWED to {', '.join(coverage.OWED.get(name, ('?',)))}")
            print(f"{name:26} {state}")
        return 0
    if not args.runbook:
        parser.error("name a runbook, or --list")

    world = harness.World(harness.Compose(), harness.Alerts(), harness.Orders(), harness.Logs(), print, realm=harness.Realm())
    findings = run(load(args.runbook), world)
    for finding in findings:
        print(f"FINDING: {finding}", file=sys.stderr)
    print("game day: " + ("FAILED" if findings else "OK"))
    return 1 if findings else 0


if __name__ == "__main__":
    sys.exit(main())
