#!/usr/bin/env python3
"""Run the `.claude/scripts` suite as parallel class shards, covering what a serial run covers.

`docs/harness-boundaries.md`, *The suite's shard runner*, holds the argument: nothing reports a pass it has not earned.
"""

import contextlib
import getpass
import importlib
import os
import shutil
import subprocess
import sys
import tempfile
import time
import unittest
from pathlib import Path

if os.name == "nt":
    import msvcrt
else:
    import fcntl

HERE = Path(__file__).resolve()
ROOT = HERE.parents[2]

# The one tree every target holds. A gate's suite or a stack's root is that
# target's to run beside this, and `test_harness_shards.py` holds the list to it.
ROOTS = ((".claude/scripts", "test_*.py"),)

# The module a target keeps beside these suites, listed under `local`, to pin classes only it has: `SERIAL_CLASSES`, a
# collection of class names, and `TOGETHER_CLASSES`, a dict of class name to group. It is `local_commands`, which the
# payload's command suites read for the target's own commands, and a target with none pins nothing of its own.
LOCAL_PINS = "local_commands"


def local_pins(name=LOCAL_PINS):
    """The target's own `(serial, together)` pins, refusing a shape that would read as something other than it says.

    Absent the module, or either name in it, that pin is empty. A module whose own import fails is raised, since a
    pin that silently stopped applying reads exactly like one that is working.
    """
    scripts = str(HERE.parent)
    if scripts not in sys.path:
        sys.path.insert(0, scripts)
    try:
        module = importlib.import_module(name)
    except ModuleNotFoundError as error:
        if error.name != name:
            raise
        return frozenset(), {}
    serial = getattr(module, "SERIAL_CLASSES", ())
    together = getattr(module, "TOGETHER_CLASSES", {})
    if isinstance(serial, (str, bytes)) or not all(isinstance(n, str) for n in serial):
        raise TypeError(f"SERIAL_CLASSES must be a collection of class names, not {serial!r}")
    if not isinstance(together, dict) or not all(
            isinstance(n, str) and isinstance(g, str) for n, g in together.items()):
        raise TypeError(f"TOGETHER_CLASSES must map class names to group names, not {together!r}")
    return frozenset(serial), dict(together)


def pinned(serial, together, local):
    """The runner's tables with a target's `local` pins added.

    A class pinned both ways, or already pinned by the payload, is refused.
    """
    local_serial, local_together = local
    regrouped = sorted((local_serial & serial) | (set(local_together) & set(together)))
    if regrouped:
        raise ValueError(f"{regrouped} already pinned by the payload; a target pins only a class of its own")
    serial, together = serial | local_serial, {**together, **local_together}
    both = sorted(serial & set(together))
    if both:
        raise ValueError(f"{both} pinned both serial and together; a serial class is in no unit to share")
    return serial, together


# Whose subject is elapsed time, and so cannot share a box with seven workers.
# The wrapper's bound is the thing `HintBound` measures, its subclass runs the
# same cases under the watchdog, and the third asserts a stalled `gh` read ends
# inside twenty seconds, which it missed at thirty-two beside seven workers.
_SERIAL = frozenset({
    "test_hint_bound.HintBound",
    "test_hint_bound.HintBoundUnderTheWatchdog",
    "test_pr_helpers.AStalledGhReadEndsAtTheBound",
})

# Who shares state outside their own fixture, and therefore shares a unit. Each makes a detached sweep worktree of
# this checkout in the shared temp root's `secsweep-*` namespace, and the first lists that namespace. Classes using
# a repository and a temp root of their own, as `test_sweep_slices.ASmallRepository` does, run wherever dealt.
_TOGETHER = {
    "test_git_worktree_detach.SweepWorktreeShape": "the checkout",
    "test_sweep_slices.TheCheckout": "the checkout",
    "test_sweep_slices.TheCheckoutReadsEachFileOnce": "the checkout",
}

# What the plan reads: the two tables above and the target's own, whose names `test_harness_shards.py` resolves.
SERIAL, TOGETHER = pinned(_SERIAL, _TOGETHER, local_pins())

# Seconds a class took in one queued run of the whole suite on one Windows host, timed whole beside seven workers.
# They only order the queue, longest first, and a class dealt late still runs. Refresh the table whole from the
# class times every run prints, since a table half old and half new orders worse than either. That run listed a
# class at 5 s or at ten tests, and one added or sped up since costs `UNMEASURED` a test or its old seconds until the
# next refresh; SERIAL classes are absent, since `plan()` sets them aside before it reads this.
WEIGHT = {
    "test_sweep_slices.ASmallRepository": 824.8,
    "test_git_rebase_onto_main.TheHelperPublishesWhatItRebased": 777.0,
    "test_git_branch_drop.TheHelperKeepsWhatHasNotLanded": 637.6,
    "test_git_rebase_onto_main.AConflictIsTheCaseRebaseIsHereFor": 610.1,
    "test_git_argv_guard.TheGitArgvGuard": 517.0,
    "test_sync_pr.VerifyTests": 484.5,
    "test_git_pr_trailer_helpers.TrailerHelper": 466.8,
    "test_git_worktree_remove.RemoveShape": 383.4,
    "test_git_rebase_onto_main.ALegacyMergeForwardIsNotSilentlyDropped": 305.0,
    "test_sync_pr.RenderedSettingsTests": 260.2,
    "test_gh_pr_create.PrCreateHelper": 222.0,
    "test_gh_issue_create.IssueHelperHasNoFreeParameter": 188.7,
    "test_git_rebase_onto_main.TheHelperRefusesBeforeItRewrites": 185.4,
    "test_sync_pr.ReviewDiffTests": 181.3,
    "test_git_push_force.TheHelperRefusesBeforeItPushes": 178.9,
    "test_pr_for_branch.OnlyThisCheckoutsPullRequestsSurvive": 168.8,
    "test_git_rebase_onto_main.TheApplyBackendIsNeverTheOneThatRuns": 166.9,
    "test_git_worktree_fork.ForkShape": 150.3,
    "test_gh_issue_harness.HarnessIssueHelperHasNoFreeParameter": 149.4,
    "test_git_worktree_sync.SyncShape": 145.8,
    "test_run_guard.TheLauncherProvesWhatItRemembers": 136.3,
    "test_run_guard.ARememberedGuardAnswersAsAProbedOne": 134.8,
    "test_git_pr_trailer_helpers.RevertHelper": 131.4,
    "test_gh_label_ensure.LabelHelperBehaviour": 118.3,
    "test_git_argv_guard.AWriteIntoWhatTheTargetDenies": 113.8,
    "test_ship_landing_method.TheFinishedPredicateSurvivesTheLandingMethod": 108.8,
    "test_sync_pr.PinnedHeadTests": 103.6,
    "test_run_guard.TheHookWiringRunsOnMoreThanOneOperatingSystem": 98.5,
    "test_cbx.CbxWrapper": 89.9,
    "test_sweep_slices.TheCheckoutReadsEachFileOnce": 77.5,
    "test_git_push_force.TheHelperPublishesTheBranchInHand": 73.5,
    "test_index_search_hint.WhatCountsAsASearch": 72.7,
    "test_sync_pr.ConfigGlobTests": 70.2,
    "test_dispatch_sync.SyncLaunch": 64.1,
    "test_sweep_slices.TheCheckout": 60.8,
    "test_git_worktree_detach.StaleSweepWorktrees": 60.0,
    "test_git_worktree_detach.ATrackedLinkIsRefusedBeforeAnythingIsMade": 58.0,
    "test_ship_landing_method.TheMergeHelperRefusesWhatTheGrantAdmitted": 53.9,
    "test_index_mcp_root.WhatItNames": 53.0,
    "test_git_branch_drop.TheHelperDropsWhatLanded": 52.0,
    "test_sync_pr.LocalSuitesTests": 48.3,
    "test_sync_pr.LocalPathTests": 45.6,
    "test_git_trailer_pr.TheHelperRefusesMain": 44.6,
    "test_dispatch_sync.RunningASync": 44.0,
    "test_git_worktree_detach.SweepWorktreeShape": 37.7,
    "test_index_query_hint.TheHint": 37.5,
    "test_issue_decisions.Cli": 28.7,
    "test_index_search_hint.WhenItAsks": 27.5,
    "test_sed_crlf_guard.ARewriteOfATrackedCrlfFileIsRefused": 23.8,
    "test_index_gate.TheCliIsNotStartedOverAnIndexTheHookRefuses": 23.5,
    "test_helper_argv_guard.GuardHelperArgv": 22.3,
    "test_sync_pr.ListTests": 21.1,
    "test_harness_shards.TheRunnerRefusesRatherThanReportingAPass": 15.4,
    "test_refresh_index.IgnoredPaths": 15.3,
    "test_gh_issue_suppresses.WhatSuppressesIsDecidedByCodeNow": 15.1,
    "test_index_argv_guard.GuardIndexArgv": 14.7,
    "test_edit_target_guard.AWorktreeTheSessionHasNotEntered": 14.6,
    "test_gh_issue_owned.OwnedIssues": 11.7,
    "test_dispatch_run.MergeCheckTests": 11.0,
    "test_edit_target_guard.TheHomeDirectoryKeepsWhatRunsAtLogin": 11.0,
    "test_sync_pr.UnreadableConfigTests": 10.7,
    "test_refresh_index.TheIndexIsCompactedAfterARefresh": 10.2,
    "test_destructive_classes.DestructiveClasses": 9.7,
    "test_index_query_hint.TheWiring": 9.7,
    "test_edit_target_guard.TheOrdinaryWriteIsNotDisturbed": 8.1,
    "test_index_search_hint.TheWiring": 8.1,
    "test_sync_pr.HeadBlobsTests": 7.8,
    "test_git_rebase_onto_main.TheArgumentGuardsEachHaveTheirOwnCase": 7.4,
    "test_edit_target_guard.ALinkIsNotTheFileItIsSpelledAs": 7.1,
    "test_sed_crlf_guard.WhatSedLeavesAloneIsAdmitted": 6.5,
    "test_dispatch_board.BoardTests": 5.8,
    "test_index_mcp_root.WhenItRefuses": 5.6,
    "test_dispatch_run.WakeTests": 4.7,
    "test_python_hygiene.PythonHygiene": 3.5,
    "test_refresh_index.TheIndexerAKilledWorkerLeftBehind": 3.3,
    "test_dispatch_run.LaunchTests": 3.0,
    "test_refresh_index.OneRefreshAtATime": 2.2,
    "test_dispatch_run.ResultCommandTests": 2.1,
    "test_dispatch_run.BerserkTests": 1.3,
    "test_dispatch_board.WriteTests": 1.2,
    "test_dispatch_run.ReportTests": 0.9,
    "test_refresh_index.OnlyAnIndexAtRest": 0.8,
    "test_refresh_index.SeedingAWorktree": 0.6,
    "test_dispatch_run.ClassifyTests": 0.4,
    "test_refresh_index.ChoosingTheCheckout": 0.2,
    "test_command_runbooks.EachSplitCommandHasBothHalves": 0.1,
    "test_command_runbooks.ShipStopsAndContinuesWhereTheHelpersSay": 0.0,
    "test_dispatch_board.BerserkTests": 0.0,
    "test_dispatch_board.PathTests": 0.0,
    "test_issue_decisions.Shapes": 0.0,
    "test_ship_dispatched.TheDispatchedTokenChangesOnlyWhatTheSpecLists": 0.0,
    "test_ship_sync.TheSyncRow": 0.0,
}

# What an unmeasured class is assumed to cost per test. Deliberately coarse:
# the classes that decide the makespan are all in WEIGHT, and a wrong guess
# here costs a later turn in the queue and nothing else.
UNMEASURED = 1.0

# Eight workers was the knee: sixteen bought twenty-five seconds and produced
# failures. The host's own core count caps it below that.
CAP = 8


class NothingDiscovered(RuntimeError):
    """A root that matches nothing is not an empty suite to report green."""


def workers():
    """How many parallel workers this host gets."""
    return max(1, min(CAP, os.cpu_count() or 1))


def walk(suite):
    """Every test case under a suite, however deeply nested."""
    for item in suite:
        if isinstance(item, unittest.TestSuite):
            yield from walk(item)
        else:
            yield item


def discovered():
    """Every test the run covers, in discovery order.

    An import failure arrives as a loader placeholder, so a broken module stays a red test in some worker. A root
    yielding nothing refuses the run: the case that would catch a renamed directory is among the tests it lost.
    """
    found = []
    for start, pattern in ROOTS:
        where = str(ROOT / start)
        suite = unittest.TestLoader().discover(where, pattern=pattern,
                                               top_level_dir=where)
        ids = [test.id() for test in walk(suite)]
        if not ids:
            raise NothingDiscovered(
                f"{start} matching {pattern!r} discovered no tests at all")
        found.extend(ids)
    return found


def owner(test_id):
    """The class an id belongs to — the unit everything here schedules."""
    return test_id.rsplit(".", 1)[0]


def plan(found):
    """Cut every id into the units the queue deals, longest first, and the serial tail.

    A unit is one class, or every class one `TOGETHER` group names. Between them the units and the tail hold `found`
    exactly once, which `test_harness_shards.py` checks rather than takes.
    """
    tail = [test_id for test_id in found if owner(test_id) in SERIAL]

    members, cost = {}, {}
    for name in dict.fromkeys(owner(test_id) for test_id in found):
        if name in SERIAL:
            continue
        group = [test_id for test_id in found if owner(test_id) == name]
        unit = TOGETHER.get(name, name)
        members.setdefault(unit, []).extend(group)
        cost[unit] = cost.get(unit, 0.0) + WEIGHT.get(name,
                                                      UNMEASURED * len(group))

    order = sorted(members, key=lambda u: (-cost[u], u))
    return [members[unit] for unit in order], tail


def unplaced(found, phases):
    """What discovery holds that the runs do not, and the reverse.

    `plan()` covering everything and the runs covering everything are two claims; this is the second, checked where
    the plan becomes subprocesses.
    """
    placed = [test_id for _, ids in phases for test_id in ids]
    missing = sorted(set(found) - set(placed))
    extra = sorted(set(placed) - set(found))
    if not missing and not extra and len(placed) != len(found):
        # Same set, different multiset: something is placed twice.
        extra = sorted({i for i in placed if placed.count(i) > 1})
    return missing, extra


def unfinished(work, units):
    """The units of a queued run that never reached `done/`.

    A worker that died mid-unit leaves its unit in `taken/` alone, one that refused an id does too, and a unit no
    worker reached is in neither: all three are tests that did not run, however every worker exited.
    """
    done = {path.name for path in (work / "done").glob("*.ids")}
    return [name for name in units if name not in done]


def settle_streams():
    """Write UTF-8 to a log file, whatever the code page says.

    A worker's streams are a file, which on Windows makes them the ANSI code page. It is done here because an inherited
    `PYTHONIOENCODING` would reach every `bash`, `git` and hook the tests spawn, whose unaided behaviour is the subject.
    """
    sys.stdout.reconfigure(encoding="utf-8", errors="backslashreplace")
    sys.stderr.reconfigure(encoding="utf-8", errors="backslashreplace")


def loaded():
    """Every discovered test case, by id, in discovery order."""
    tests = {}
    for start, pattern in ROOTS:
        where = str(ROOT / start)
        suite = unittest.TestLoader().discover(where, pattern=pattern,
                                               top_level_dir=where)
        for test in walk(suite):
            tests[test.id()] = test
    return tests


def run_listed(wanted):
    """Run exactly these ids, alone, and refuse the run if discovery lost one.

    A worker handed a name nothing discovers, or handed nothing, has been told to run what does not exist, and
    reporting that as a pass is the failure this file exists to avoid. The serial tail runs this way.
    """
    settle_streams()
    if not wanted:
        print("shard-harness-suite.py: this worker was given no ids to run",
              file=sys.stderr)
        return 2
    tests = loaded()
    outstanding = sorted(set(wanted) - set(tests))
    if outstanding:
        print("shard-harness-suite.py: discovery does not hold "
              f"{len(outstanding)} of the ids this worker was given: "
              f"{outstanding[:5]}", file=sys.stderr)
        return 2
    wanted = set(wanted)
    chosen = [test for test_id, test in tests.items() if test_id in wanted]
    result = unittest.TextTestRunner(verbosity=2).run(unittest.TestSuite(chosen))
    return 0 if result.wasSuccessful() else 1


def claims(work):
    """The units this worker wins, longest first, each claimed once.

    Creating `taken/<unit>` exclusively is the claim, atomic on NTFS as on POSIX. A rename is not: on Windows two
    workers that opened one file both succeeded and ran the unit twice. Every worker walks the whole list.
    """
    for name in sorted(path.name for path in (work / "units").glob("*.ids")):
        try:
            os.close(os.open(work / "taken" / name,
                             os.O_CREAT | os.O_EXCL | os.O_WRONLY))
        except FileExistsError:
            continue
        yield name


def run_queue(work):
    """Run every unit in `work` this worker can claim.

    Each class runs as its own suite, so its time is measured whole, and module cleanups run between classes. A unit
    holding an id discovery does not hold is refused and never reaches `done/`, so the parent's check names it.
    """
    settle_streams()
    tests = loaded()
    status = 0
    for name in claims(work):
        ids = (work / "units" / name).read_text(encoding="utf-8").split()
        outstanding = sorted(set(ids) - set(tests))
        if not ids or outstanding:
            print(f"shard-harness-suite.py: unit {name}: discovery does not "
                  f"hold {len(outstanding)} of its ids: {outstanding[:5]}",
                  file=sys.stderr)
            status = 2
            continue
        times = []
        for cls in dict.fromkeys(owner(test_id) for test_id in ids):
            began = time.monotonic()
            result = unittest.TextTestRunner(verbosity=2).run(
                unittest.TestSuite(tests[i] for i in ids if owner(i) == cls))
            times.append(f"{time.monotonic() - began:.1f}\t{cls}\n")
            if not result.wasSuccessful():
                status = max(status, 1)
        (work / "done" / f"{name}.times").write_text("".join(times),
                                                     encoding="utf-8")
        # Exclusively, like the claim: a unit finished twice is a unit run
        # twice, and that fails this worker loudly rather than passing.
        os.close(os.open(work / "done" / name,
                         os.O_CREAT | os.O_EXCL | os.O_WRONLY))
    return status


def spawn(args, log):
    """One worker process, writing everything to one log.

    The environment is this process's, unchanged, since anything set here would reach every process the tests spawn.
    """
    return subprocess.Popen([sys.executable, str(HERE), *args],
                            cwd=str(ROOT), stdout=log,
                            stderr=subprocess.STDOUT)


def report(logs):
    """Print what each worker wrote, whatever ended the run.

    Called from a `finally`, so an interrupted run still shows its logs. Bytes straight through, since decoding a
    worker's output only to encode it again once killed the parent printing a run it had finished.
    """
    for title, log in logs:
        if not log.exists():
            continue
        print(f"\n===== {title} =====", flush=True)
        sys.stdout.buffer.write(log.read_bytes())
        sys.stdout.buffer.flush()


def slowest(work):
    """Every class's time, longest first, from every queued unit that finished; `SERIAL` classes are not queued.

    Printed at the end of every run so `WEIGHT` can be refreshed whole from data
    rather than from a profile somebody has to remember to take.
    """
    rows = []
    for path in (work / "done").glob("*.times"):
        for line in path.read_text(encoding="utf-8").splitlines():
            seconds, cls = line.split("\t")
            rows.append((float(seconds), cls))
    return sorted(rows, reverse=True)


def lock_path():
    """Where the lock lives: the user's temp directory, under the user's name.

    The temp directory, because two checkouts contend for one host's cores. The user's name, because opening another
    user's file in a shared `/tmp` would fail rather than wait.
    """
    try:
        user = getpass.getuser()
    except Exception:
        user = "unknown"
    safe = "".join(c if c.isalnum() or c in "-_." else "_" for c in user)
    return Path(tempfile.gettempdir()) / f"blueprint-harness-suite-{safe}.lock"


LOCK = lock_path()


class LockUnavailable(RuntimeError):
    """The lock file cannot be opened, so the run cannot know it is alone."""


def try_lock(handle):
    """Take the lock on <handle> if it is free; whether it was taken.

    Both are locks the operating system drops when the process holding them
    exits, killed or not, which is why there is no stale-lock recovery here.
    """
    try:
        if os.name == "nt":
            handle.seek(0)
            msvcrt.locking(handle.fileno(), msvcrt.LK_NBLCK, 1)
        else:
            fcntl.flock(handle.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
    except OSError:
        return False
    return True


def unlock(handle):
    if os.name == "nt":
        handle.seek(0)
        msvcrt.locking(handle.fileno(), msvcrt.LK_UNLCK, 1)
    else:
        fcntl.flock(handle.fileno(), fcntl.LOCK_UN)


@contextlib.contextmanager
def host_lock(path=LOCK, poll=5.0):
    """Hold the user's harness lock, waiting for another run to finish first."""
    try:
        handle = open(path, "a+b")
    except OSError as refused:
        raise LockUnavailable(f"cannot open the run lock {path}: {refused}")
    with handle:
        if not try_lock(handle):
            print(f"shard-harness-suite.py: another harness run of yours "
                  f"holds {path}; waiting for it rather than running beside "
                  f"it", flush=True)
            while not try_lock(handle):
                time.sleep(poll)
        try:
            yield
        finally:
            unlock(handle)


def main(argv):
    if len(argv) == 3 and argv[1] == "--ids":
        return run_listed(Path(argv[2]).read_text(encoding="utf-8").split())
    if len(argv) == 3 and argv[1] == "--queue":
        return run_queue(Path(argv[2]))
    if len(argv) != 1:
        print("usage: shard-harness-suite.py        (it takes no arguments)",
              file=sys.stderr)
        return 2

    # The lock before discovery, not after it: a run that waits out another
    # one may wait a whole suite, and a plan made before the wait would deal
    # the tree as it was, leaving a test added meanwhile in no unit at all.
    try:
        with host_lock():
            return discover_and_run()
    except LockUnavailable as refused:
        print(f"shard-harness-suite.py: {refused}. Refusing the run rather "
              f"than running beside one it cannot see.", file=sys.stderr)
        return 2


def discover_and_run():
    """Discover, plan, check the plan covers discovery, then run it."""
    try:
        found = discovered()
    except NothingDiscovered as empty:
        print(f"shard-harness-suite.py: {empty}. Refusing the run rather than "
              f"reporting a pass over nothing.", file=sys.stderr)
        return 2

    units, tail = plan(found)
    phases = [("unit", ids) for ids in units]
    if tail:
        phases.append(("serial", tail))

    missing, extra = unplaced(found, phases)
    if missing or extra:
        print("shard-harness-suite.py: the runs do not cover discovery — "
              f"{len(missing)} test(s) would never run {missing[:5]}, "
              f"{len(extra)} unaccounted for {extra[:5]}", file=sys.stderr)
        return 2

    count = max(1, min(workers(), len(units)))
    print(f"shard-harness-suite.py: {len(found)} tests in {len(units)} units "
          f"over {count} parallel workers, then {len(tail)} serial",
          flush=True)
    return run_all(units, tail, count)


def queue_units(work, units):
    """Lay out a queue in `work`: each unit's ids, in the order they are dealt."""
    for part in ("units", "taken", "done"):
        (work / part).mkdir()
    names = [f"{n:04d}.ids" for n in range(len(units))]
    for name, ids in zip(names, units):
        (work / "units" / name).write_text("\n".join(ids), encoding="utf-8")
    return names


def run_all(units, tail, count):
    """Queue the units, drain the queue with `count` workers, then the tail."""
    work = Path(tempfile.mkdtemp(prefix="harness-shards-"))
    names = queue_units(work, units)

    logs = [(f"worker {n}", work / f"worker-{n}.log") for n in range(count)]
    if tail:
        logs.append((f"serial: {len(tail)} tests", work / "serial.log"))
    began = time.monotonic()
    codes, running, left, times = [], [], [], []
    try:
        with contextlib.ExitStack() as open_logs:
            for _, path in logs[:count]:
                log = open_logs.enter_context(path.open("wb"))
                running.append(spawn(["--queue", str(work)], log))
            for proc in running:
                codes.append(proc.wait())

        # The tail goes last and alone, which is what "serial" means here.
        if tail:
            listing = work / "serial.ids"
            listing.write_text("\n".join(tail), encoding="utf-8")
            with logs[-1][1].open("wb") as log:
                codes.append(spawn(["--ids", str(listing)], log).wait())
        left = unfinished(work, names)
        times = slowest(work)
    finally:
        # A worker still running here is one this process is abandoning, and
        # several of them drive `git worktree` against this checkout.
        for proc in running:
            if proc.poll() is None:
                proc.terminate()
        report(logs)
        shutil.rmtree(str(work), ignore_errors=True)

    print("\nshard-harness-suite.py: every class the queue finished, slowest first, in seconds:")
    for seconds, cls in times:
        print(f"  {seconds:7.1f}  {cls}")
    bad = [title for (title, _), code in zip(logs, codes) if code != 0]
    if len(codes) != len(logs):
        print(f"\nshard-harness-suite.py: {len(codes)} of {len(logs)} runs "
              f"reported at all", file=sys.stderr)
        return 1
    if left:
        print(f"\nshard-harness-suite.py: {len(left)} of {len(names)} units "
              f"never finished, so their tests did not run: {left[:5]}",
              file=sys.stderr)
        return 1
    print(f"\nshard-harness-suite.py: {len(units)} units and "
          f"{1 if tail else 0} serial run in "
          f"{time.monotonic() - began:.0f}s, "
          f"{', '.join(bad) + ' failed' if bad else 'all green'}")
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
