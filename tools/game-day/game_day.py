"""Cause a runbook's alert on the Compose stack and check the runbook's first step.
Usage: py -3.12 tools/game-day/game_day.py outbox-broker (or --list, or --matrix all for CI's plan); add --cause to
cause and leave, and --restore to put the stack back. The stack must be up; README.md owns the rest.
"""

from __future__ import annotations

import argparse
import contextlib
import importlib
import json
import os
import re
import signal
import subprocess
import sys
import threading
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


def quiet_start(world: harness.World, alert: str) -> str | None:
    """Why the cause must not start, or None. A rule nobody loaded is also quiet, and a quiet start is what makes
    "it fired" mean the cause did it."""
    if not world.alerts.loaded(alert):
        return f"{alert}: Prometheus has no such rule loaded, so it cannot fire"
    if world.alerts.state(alert) != "inactive":
        return f"{alert}: already {world.alerts.state(alert)} before the cause; restore the stack and rerun"
    return None


def run(scenario: ModuleType, world: harness.World, *, clock=time.monotonic, sleep=time.sleep) -> list[str]:
    """One scenario, start to restore. Returns the findings; an empty list is a pass.

    The restore runs whatever the cause did, including when the cause itself raised half-way, because a
    scenario that left rabbitmq stopped would turn every one after it into a finding about the wrong thing.
    """
    findings: list[str] = []
    say = world.say
    alert = scenario.ALERT
    fired = False

    refused = quiet_start(world, alert)
    if refused:
        return [refused]

    try:
        say(f"{scenario.RUNBOOK}: causing {alert}; deadline {scenario.DEADLINE.derivation}")
        world.caused_at = time.time()
        scenario.cause(world)
        try:
            took = harness.wait_until(
                lambda: _is(world, alert, "firing"), scenario.DEADLINE.seconds, f"{alert} firing",
                clock=clock, sleep=sleep)
            fired = True
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
        restored = _restore(scenario, world, fired, clock, sleep)
        findings.extend(restored)
        # After the settle and not before it: a ratio or a quantile over a window that has no requests in it is
        # empty, and an empty rule is quiet whether or not the cause went. The stop is the runner's, so a scenario
        # that raised half-way cannot leave a loop running into the next.
        if world.traffic.running:
            say(f"traffic: {world.traffic.counts()}")
        world.traffic.stop()
    # A scenario with a second alert of its own runbook (error-rate.md's two rules) hands it on as NEXT, and it runs
    # only on a stack the first restored: one that did not settle would make the second a finding about the first.
    follow_up = getattr(scenario, "NEXT", None)
    if follow_up is not None:
        if restored:
            findings.append(f"{follow_up.ALERT}: not run, because {alert}'s restore did not settle")
        else:
            findings.extend(run(follow_up, world, clock=clock, sleep=sleep))
    return findings


def _is(world: harness.World, alert: str, wanted: str) -> tuple[bool, str]:
    state = world.alerts.state(alert)
    return state == wanted, f"{alert} is {state}"


def _restore(scenario: ModuleType, world: harness.World, fired: bool, clock, sleep) -> list[str]:
    try:
        scenario.restore(world)
        # Settling is bounded by the cause's derivation: the gauge has to be exported and judged again. A rule
        # that reads a window (an `increase` over 30m) names its own, as SETTLE, because it outlasts that sum.
        bound = getattr(scenario, "SETTLE", scenario.DEADLINE).seconds + 120
        started = clock()

        # An alert never seen firing reads `inactive` as one not yet reported does (README.md, *Reporting is a
        # second evaluation*), so `settled` is believed only once a reporting lag has passed.
        def quiet() -> tuple[bool, str]:
            ok, observed = scenario.settled(world)
            if ok and not fired and clock() - started < harness.REPORT_LAG_SECONDS:
                return False, f"{observed}, but the alert was never seen firing and may not have reported yet"
            return ok, observed

        harness.wait_until(quiet, bound + (0 if fired else harness.REPORT_LAG_SECONDS),
                           f"{scenario.ALERT} resolving after the restore", clock=clock, sleep=sleep)
    except Exception as error:  # noqa: BLE001 - a restore that fails for any reason is the finding
        return [f"the restore did not settle, so the next scenario starts poisoned: {error}"]
    world.say(f"{scenario.ALERT} resolved; the stack is back")
    return []


def phases(scenario: ModuleType) -> list:
    """The scenario and each phase its NEXT hands on to, in the order a full run causes them."""
    found = []
    while scenario is not None:
        found.append(scenario)
        scenario = getattr(scenario, "NEXT", None)
    return found


def phase_of(scenario: ModuleType, alert: str | None):
    if alert is None:
        return scenario
    for phase in phases(scenario):
        if phase.ALERT == alert:
            return phase
    raise harness.GameDayError(
        f"{scenario.RUNBOOK}: no phase causes {alert}; it has {', '.join(p.ALERT for p in phases(scenario))}")


# --cause and --restore are two processes, so a cause that has to stay alive (a traffic loop, a top-up) is a detached
# child of the first and the second ends it. README.md, *Cause and restore apart*, owns the argument.
STATE = harness.ROOT / "artifacts" / "game-day"
HOLD_LIMIT_SECONDS = 2 * 60 * 60
HOLD_POLL_SECONDS = 2.0
# --cause waits this long for the child to say its cause landed. A cause that is itself a loop (queue-backlog's lag
# half) never returns to say so, so the child calls it landed once it has run this long without failing.
LANDING_SECONDS = 120.0
LANDING_GRACE_SECONDS = 20.0
LANDING_POLL_SECONDS = 0.5


def _pid_file(runbook: str, state: Path) -> Path:
    return state / f"{coverage.module_name(runbook)}.pid"


def _status_file(runbook: str, state: Path) -> Path:
    return state / f"{coverage.module_name(runbook)}.status"


def _is_ours(pid: int, runbook: str, run=subprocess.run) -> bool:
    """Whether `pid` is this runbook's --hold child, because a pid file outlives a reboot and the number may be
    anyone's, a person's own game_day.py run included."""
    if os.name == "nt":
        argv = ["powershell", "-NoProfile", "-Command",
                f"(Get-CimInstance Win32_Process -Filter 'ProcessId={pid}').CommandLine"]
    else:
        argv = ["ps", "-p", str(pid), "-o", "command="]
    shown = run(argv, capture_output=True, text=True, encoding="utf-8", errors="replace", check=False).stdout
    # Windows quotes the script's path when it holds a space, so a quote may sit between the script and its arguments.
    return re.search(rf"game_day\.py\"?\s+{re.escape(runbook)}\s+--hold\b", shown) is not None


def holder(runbook: str, state: Path = STATE, run=subprocess.run) -> int | None:
    """The detached child a sustained --cause left running, if there is one."""
    try:
        pid = int(_pid_file(runbook, state).read_text(encoding="utf-8").strip())
    except (OSError, ValueError):
        return None
    return pid if _is_ours(pid, runbook, run) else None


def spawn_hold(runbook: str, alert: str, state: Path = STATE, popen=subprocess.Popen) -> int:
    """Start `game_day.py RUNBOOK --hold` detached, its output in artifacts/game-day/<name>.log."""
    state.mkdir(parents=True, exist_ok=True)
    # A report left by an earlier child would be read as this one's.
    _status_file(runbook, state).unlink(missing_ok=True)
    options: dict = {}
    if os.name == "nt":
        options["creationflags"] = (subprocess.DETACHED_PROCESS | subprocess.CREATE_NEW_PROCESS_GROUP
                                    | subprocess.CREATE_NO_WINDOW)
    else:
        options["start_new_session"] = True
    with (state / f"{coverage.module_name(runbook)}.log").open("ab") as log:
        child = popen([sys.executable, "-u", str(Path(__file__).resolve()), runbook, "--hold", "--phase", alert],
                      stdin=subprocess.DEVNULL, stdout=log, stderr=log, close_fds=True, **options)
    _pid_file(runbook, state).write_text(str(child.pid), encoding="utf-8")
    return child.pid


def release(runbook: str, state: Path = STATE, run=subprocess.run) -> str | None:
    """End the detached child, whole tree, and forget it. Returns what was done, or None when nothing was held."""
    pid = holder(runbook, state, run)
    _pid_file(runbook, state).unlink(missing_ok=True)
    _status_file(runbook, state).unlink(missing_ok=True)
    if pid is None:
        return None
    if os.name == "nt":
        run(["taskkill", "/PID", str(pid), "/T", "/F"], capture_output=True, check=False)
    else:
        # It may have ended on its own between the read above and this.
        with contextlib.suppress(ProcessLookupError, PermissionError):
            os.killpg(pid, signal.SIGTERM)
    return f"ended the process (pid {pid}) that was holding the cause"


def report_landing(runbook: str, findings: list[str], state: Path = STATE) -> None:
    """The child's word to --cause: its cause landed (no findings) or failed. Written whole, then moved into place."""
    state.mkdir(parents=True, exist_ok=True)
    target = _status_file(runbook, state)
    scratch = target.with_suffix(".status.tmp")
    scratch.write_text(json.dumps({"findings": findings}), encoding="utf-8")
    scratch.replace(target)


def await_landing(runbook: str, state: Path = STATE, *, sleep=time.sleep, clock=time.monotonic,
                  alive=holder, limit: float = LANDING_SECONDS) -> list[str]:
    """What the detached child reports of its cause: no findings once it landed, else why it did not."""
    def reported() -> list[str] | None:
        try:
            return list(json.loads(_status_file(runbook, state).read_text(encoding="utf-8"))["findings"])
        except (OSError, ValueError, KeyError, TypeError):
            return None

    log = state / f"{coverage.module_name(runbook)}.log"
    started = clock()
    while clock() - started < limit:
        found = reported()
        if found is not None:
            return found
        if alive(runbook, state) is None:
            # It may have reported and ended between the two reads above.
            found = reported()
            return found if found is not None else [
                f"{runbook}: the background cause ended without saying it landed; --restore, and see {log}"]
        sleep(LANDING_POLL_SECONDS)
    return [f"{runbook}: the background cause has not landed after {limit:.0f}s; --restore, and see {log}"]


def cause_only(scenario: ModuleType, world: harness.World, *, spawn=spawn_hold, held=holder,
               landed=await_landing) -> list[str]:
    """--cause: the quiet start and the cause, then out. The stack stays broken until --restore.

    A cause that has to stay alive to be one (SUSTAINED) is handed to a detached child and this returns at once; the
    others have done their work when `cause` returns. Neither waits for the alert, runs the step or restores.
    """
    alert = scenario.ALERT
    refused = quiet_start(world, alert)
    if refused:
        return [refused]
    world.say(f"{scenario.RUNBOOK}: causing {alert}; it fires within {scenario.DEADLINE.seconds}s once caused")
    if getattr(scenario, "SUSTAINED", False):
        if held(scenario.RUNBOOK) is not None:
            return [f"{scenario.RUNBOOK}: a cause is already being held; --restore first"]
        pid = spawn(scenario.RUNBOOK, alert)
        world.say(f"{alert}: the cause is running in the background (pid {pid}); --restore ends it")
        return landed(scenario.RUNBOOK)
    world.caused_at = time.time()
    try:
        scenario.cause(world)
    except harness.GameDayError as error:
        return [f"the cause failed half-way, and --restore puts back what it did: {error}"]
    return []


def hold(scenario: ModuleType, world: harness.World, *, sleep=time.sleep, clock=time.monotonic,
         limit: float = HOLD_LIMIT_SECONDS, report=lambda findings: None,
         grace: float = LANDING_GRACE_SECONDS) -> list[str]:
    """--hold, the detached child: the cause, then alive for as long as a thread of the cause's has work to do.

    `report` tells the --cause that started it whether the cause landed, once: when `cause` returns or raises, or
    when it has run `grace` seconds without either. A failure after that is in the log and not the report.
    """
    lock = threading.Lock()
    told: list[bool] = []

    def tell(findings: list[str]) -> None:
        with lock:
            if not told:
                told.append(True)
                report(findings)

    timer = threading.Timer(grace, tell, args=([],))
    timer.daemon = True
    timer.start()
    world.caused_at = time.time()
    try:
        scenario.cause(world)
    except harness.GameDayError as error:
        failed = [f"the cause failed half-way, and --restore puts back what it did: {error}"]
        tell(failed)
        return failed
    except Exception as error:  # noqa: BLE001 - anything else is a failed cause too, and the traceback goes to the log
        tell([f"the cause raised {type(error).__name__}: {error}; --restore puts back what it did"])
        raise
    finally:
        timer.cancel()
    tell([])
    started = clock()
    while clock() - started < limit and any(
            thread.is_alive() and thread.name.startswith("game-day-") for thread in threading.enumerate()):
        sleep(HOLD_POLL_SECONDS)
    return []


def restore_only(scenario: ModuleType, world: harness.World, *, end=release) -> list[str]:
    """--restore: end what --cause left running and put the stack back, whether or not --cause ever ran.

    Every phase restores, last first, because the second alert is caused on top of the first; one that raises is a
    finding and the others still run. It does not wait for an alert to resolve, only reports where it stands.
    """
    findings: list[str] = []
    ended = end(scenario.RUNBOOK)
    if ended:
        world.say(ended)
    for phase in reversed(phases(scenario)):
        try:
            phase.restore(world)
        except Exception as error:  # noqa: BLE001 - a restore that fails for any reason is the finding
            findings.append(f"{phase.ALERT}: the restore failed, so the stack is not back: {error}")
    for phase in phases(scenario):
        try:
            window = getattr(phase, "SETTLE", None)
            clears = f"; its window clears it within {window.seconds}s" if window else ""
            world.say(f"{phase.ALERT} is {world.alerts.state(phase.ALERT)} after the restore{clears}")
        except harness.GameDayError as error:
            world.say(f"{phase.ALERT}: its state could not be read ({str(error)[:120]})")
    return findings


def catalog() -> list[dict]:
    """Every runbook, for a console: its alerts, whether a script causes them, and what causing them costs."""
    rules = coverage.all_rules()
    rows = []
    for name in sorted(coverage.runbooks()):
        row: dict = {"runbook": name.removesuffix(".md"), "file": name, "alerts": sorted(rules.get(name, ()))}
        if coverage.module_name(name) in coverage.scripts():
            row["script"] = True
            row["phases"] = [
                {"alert": phase.ALERT, "impact": phase.IMPACT, "confirmation": harness.CONFIRMATION[phase.IMPACT],
                 "sustained": bool(getattr(phase, "SUSTAINED", False)),
                 "fires_within_seconds": phase.DEADLINE.seconds,
                 "clears_within_seconds": getattr(phase, "SETTLE", phase.DEADLINE).seconds}
                for phase in phases(load(name))]
        else:
            row["script"] = False
            row["reason"] = coverage.NOT_ON_COMPOSE.get(name, "?")
        rows.append(row)
    return rows


def verdict(findings: list[str], seconds: float, mode: str | None = None) -> str:
    """The last line: the outcome and the whole run's wall clock, in the form README.md's Runtime column holds."""
    minutes, rest = divmod(round(seconds), 60)
    return f"game day: {mode + ' ' if mode else ''}{'FAILED' if findings else 'OK'} in {minutes}m{rest:02d}s"


def matrix(names: str) -> list[str]:
    """The dispatch's runbooks, one job each: `all` is every script, and a named one without a script is refused
    here, before a stack is started for it."""
    if names.strip() == "all":
        return sorted(name.removesuffix(".md") for name in coverage.runbooks()
                      if coverage.module_name(name) in coverage.scripts())
    chosen = list(dict.fromkeys(name.removesuffix(".md") for name in names.split()))
    for name in chosen:
        load(name)
    if not chosen:
        raise harness.GameDayError("no runbook named; give `all` or file names from docs/runbooks/")
    return chosen


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("runbook", nargs="?", help="a file name from docs/runbooks/, with or without .md")
    parser.add_argument("--list", action="store_true", help="the runbooks that have a script, and why the rest do not")
    parser.add_argument("--matrix", metavar="NAMES", help="print the workflow's matrix for `all` or named runbooks")
    parser.add_argument("--json", action="store_true", help="with --list: one JSON document, for a console")
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument("--cause", action="store_true", help="quiet start and cause, then exit; the stack stays broken")
    mode.add_argument("--restore", action="store_true", help="put the stack back; safe twice, and if --cause never ran")
    mode.add_argument("--hold", action="store_true", help=argparse.SUPPRESS)
    parser.add_argument("--phase", metavar="ALERT", help="with --cause: the alert of a runbook's second rule")
    args = parser.parse_args(argv)
    if args.phase and not (args.cause or args.hold):
        parser.error("--phase belongs to --cause")
    if args.json and not args.list:
        parser.error("--json belongs to --list")

    if args.matrix is not None:
        try:
            print("runbooks=" + json.dumps(matrix(args.matrix)))
        except harness.GameDayError as error:
            print(f"game day: {error}", file=sys.stderr)
            return 2
        return 0
    if args.list and args.json:
        print(json.dumps(catalog(), indent=2))
        return 0
    if args.list:
        for message in coverage.check():
            print(f"coverage: {message}")
        for name in sorted(coverage.runbooks()):
            state = ("script" if coverage.module_name(name) in coverage.scripts()
                     else f"NOT_ON_COMPOSE: {coverage.NOT_ON_COMPOSE.get(name, '?')}")
            print(f"{name:26} {state}")
        return 0
    if not args.runbook:
        parser.error("name a runbook, or --list")

    orders = harness.Orders()
    compose = harness.Compose()
    world = harness.World(compose, harness.Alerts(), orders, harness.Logs(), print, realm=harness.Realm(),
                          traffic=harness.Traffic(token=orders.token), broker=harness.Broker(compose))
    label = "cause" if args.cause else "restore" if args.restore else None
    started = time.monotonic()
    if args.hold:
        return _held(args, world)
    if label:
        try:
            scenario = load(args.runbook)
            if args.cause:
                findings = cause_only(phase_of(scenario, args.phase), world)
            else:
                findings = restore_only(scenario, world)
        except harness.GameDayError as error:
            print(f"game day: {error}", file=sys.stderr)
            return 2
    else:
        findings = run(load(args.runbook), world)
    elapsed = time.monotonic() - started
    for finding in findings:
        print(f"FINDING: {finding}", file=sys.stderr)
    print(verdict(findings, elapsed, label))
    return 1 if findings else 0


def _held(args: argparse.Namespace, world: harness.World) -> int:
    """The detached child's whole life: the cause, the wait, and its pid file removed on the way out."""
    try:
        for finding in hold(phase_of(load(args.runbook), args.phase), world,
                            report=lambda found: report_landing(args.runbook, found)):
            print(f"FINDING: {finding}", file=sys.stderr)
    finally:
        pid_file = _pid_file(args.runbook, STATE)
        # Only its own: a --restore and a new --cause may have put another child's there already.
        if pid_file.exists() and pid_file.read_text(encoding="utf-8").strip() == str(os.getpid()):
            pid_file.unlink()
    return 0


if __name__ == "__main__":
    sys.exit(main())
