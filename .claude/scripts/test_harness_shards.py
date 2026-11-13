"""The splitter's subject is what it is looking at, not what it found.

The units are compared back against discovery, never against a list written here, and each table's names against
discovery too. Each way of reporting a pass over tests that never ran has a case, and so does the run lock.
"""

import contextlib
import importlib.util
import io
import json
import shutil
import subprocess
import sys
import tempfile
import threading
import time
import types
import unittest
import uuid
from pathlib import Path
from unittest import mock

SCRIPTS = Path(__file__).resolve().parent
ROOT = SCRIPTS.parent.parent
RUNNER = SCRIPTS / "shard-harness-suite.py"


def loaded():
    """The runner, whose filename is not an importable module name."""
    spec = importlib.util.spec_from_file_location("shard_harness_suite", RUNNER)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


SHARD = loaded()
_found = []


def found():
    """Discovery, once per process: it imports every module in every root."""
    if not _found:
        _found.extend(SHARD.discovered())
    return _found


def queued(tmp, units):
    """A queue directory holding <units>, laid out as `run_all` lays one."""
    work = Path(tmp)
    return work, SHARD.queue_units(work, units)


class ThePlacementIsDerivedFromDiscovery(unittest.TestCase):

    def test_every_discovered_test_is_placed_exactly_once(self):
        # The whole claim. Placed twice is a test run twice, which is slow;
        # placed never is a test that stopped being a gate, which is worse.
        self.assertTrue(found(), "discovery returned nothing to place")
        units, tail = SHARD.plan(found())
        placed = [i for unit in units for i in unit] + tail
        self.assertEqual(len(placed), len(set(placed)), "placed twice")
        self.assertEqual(sorted(found()), sorted(placed))

    def test_a_class_no_table_mentions_is_placed_all_the_same(self):
        # What happens when somebody adds a file: the splitter must not need
        # telling, or the tables become the thing that decides coverage.
        stranger = "test_nobody_has_written_yet.AClass.test_it"
        units, tail = SHARD.plan([*found(), stranger])
        self.assertIn([stranger], units)

    def test_the_units_come_longest_first(self):
        # The one guess a queue still pays for in full is a long class dealt
        # last, so the order is the cost: a measured weight, else the test
        # count at UNMEASURED a test, and the name to break a tie.
        ids = ["m.Three.a", "m.Three.b", "m.Three.c", "m.One.a",
               "m.Weighed.a", "m.Tied.a"]
        with mock.patch.object(SHARD, "WEIGHT", {"m.Weighed": 10.0}), \
                mock.patch.object(SHARD, "TOGETHER", {}), \
                mock.patch.object(SHARD, "SERIAL", frozenset()), \
                mock.patch.object(SHARD, "UNMEASURED", 1.0):
            units, tail = SHARD.plan(ids)
        self.assertEqual([["m.Weighed.a"], ["m.Three.a", "m.Three.b", "m.Three.c"],
                          ["m.One.a"], ["m.Tied.a"]], units)
        self.assertEqual([], tail)

    def test_every_root_contributes(self):
        # A root that stops being discovered — a renamed directory, a pattern
        # that stops matching — is otherwise an empty set nobody misses.
        for start, pattern in SHARD.ROOTS:
            with self.subTest(root=start):
                where = str(ROOT / start)
                suite = unittest.TestLoader().discover(where, pattern=pattern,
                                                       top_level_dir=where)
                ids = {test.id() for test in SHARD.walk(suite)}
                self.assertTrue(ids, f"{start} discovered nothing")
                self.assertEqual(set(), ids - set(found()))

    def test_the_only_root_is_the_scripts_directory(self):
        # Every target renders this file, and `.claude/scripts` is the one
        # tree they all hold. A gate suite or a stack root is the target's to
        # run, and a root some target lacks refuses that target's whole run.
        self.assertEqual(((".claude/scripts", "test_*.py"),), SHARD.ROOTS)

    def test_every_name_the_tables_pin_still_exists(self):
        # A renamed class loses its pinning, and a pinning that has stopped
        # applying reads exactly like one that is working.
        classes = {SHARD.owner(i) for i in found()}
        tables = (("SERIAL", SHARD.SERIAL), ("TOGETHER", SHARD.TOGETHER),
                  ("WEIGHT", SHARD.WEIGHT))
        for name, table in tables:
            for pinned in table:
                with self.subTest(table=name, pinned=pinned):
                    self.assertIn(pinned, classes)

    def test_the_classes_that_share_state_share_a_unit(self):
        # One unit runs on one worker, start to finish, so a group in one unit
        # never has two of its classes running at once.
        self.assertTrue(SHARD.TOGETHER, "nothing is pinned together any more")
        units, _ = SHARD.plan(found())
        for group in sorted(set(SHARD.TOGETHER.values())):
            names = {n for n, g in SHARD.TOGETHER.items() if g == group}
            landed = {n for n, unit in enumerate(units)
                      for i in unit if SHARD.owner(i) in names}
            with self.subTest(group=group):
                self.assertEqual(1, len(landed), landed)

    def test_the_serial_classes_reach_no_unit(self):
        units, tail = SHARD.plan(found())
        parallel = {SHARD.owner(i) for unit in units for i in unit}
        self.assertEqual(set(), parallel & set(SHARD.SERIAL))
        self.assertEqual(set(), set(SHARD.SERIAL) -
                         {SHARD.owner(i) for i in tail})

    def test_the_worker_count_follows_the_host_up_to_the_cap(self):
        # Asserting only that the answer sits between one and the cap is an
        # assertion the expression's own shape already guarantees: a
        # `workers()` that ignored the host and always returned 1 would pass
        # it, turning the whole design back into one serial worker while
        # everything stayed green.
        for cores, expected in ((1, 1), (2, 2), (4, 4),
                                (SHARD.CAP, SHARD.CAP),
                                (SHARD.CAP + 1, SHARD.CAP), (64, SHARD.CAP),
                                (None, 1), (0, 1)):
            with self.subTest(cores=cores):
                with mock.patch.object(SHARD.os, "cpu_count",
                                       return_value=cores):
                    self.assertEqual(expected, SHARD.workers())


class TheRunnerRefusesRatherThanReportingAPass(unittest.TestCase):

    def test_a_root_that_discovers_nothing_refuses_the_run(self):
        # A renamed directory or a pattern that stopped matching looks exactly
        # like an empty suite, and `test_every_root_contributes` cannot catch
        # it — that case is among the tests that stopped being discovered. So
        # the runner has to refuse rather than report zero tests and exit 0.
        with mock.patch.object(SHARD, "ROOTS", (("docs", "test_*.py"),)):
            with self.assertRaises(SHARD.NothingDiscovered):
                SHARD.discovered()

    def test_a_run_that_loses_a_unit_is_caught_before_it_starts(self):
        # `plan()` covering everything and the runs covering everything are
        # two claims, and the gate above can only see the first. This is the
        # slip it cannot see: a unit that never becomes a phase.
        units, tail = SHARD.plan(found())
        phases = [("unit", u) for u in units[:-1]]
        if tail:
            phases.append(("serial", tail))
        missing, extra = SHARD.unplaced(found(), phases)
        self.assertTrue(missing, "a dropped unit went unnoticed")
        self.assertEqual([], extra)

    def test_a_run_that_covers_discovery_is_not_flagged(self):
        # The control: the check must be quiet on the plan the runner makes,
        # or it would refuse every run and teach everyone to ignore it.
        units, tail = SHARD.plan(found())
        phases = [("unit", u) for u in units]
        if tail:
            phases.append(("serial", tail))
        self.assertEqual(([], []), SHARD.unplaced(found(), phases))

    def test_a_test_placed_twice_is_caught(self):
        units, tail = SHARD.plan(found())
        phases = [("unit", u) for u in units]
        phases.append(("unit", list(units[0])))
        if tail:
            phases.append(("serial", tail))
        missing, extra = SHARD.unplaced(found(), phases)
        self.assertEqual([], missing)
        self.assertTrue(extra, "a unit run twice went unnoticed")

    def test_a_unit_that_never_reached_done_is_named(self):
        # What came back, not what went out: a unit still queued, one a dead
        # worker left claimed, and one that finished. Only the last ran.
        with tempfile.TemporaryDirectory() as tmp:
            work, names = queued(tmp, [["a.A.x"], ["b.B.x"], ["c.C.x"]])
            (work / "taken" / names[1]).write_bytes(b"")
            (work / "taken" / names[2]).write_bytes(b"")
            (work / "done" / names[2]).write_bytes(b"")
            self.assertEqual(names[:2], SHARD.unfinished(work, names))

    def test_the_queue_is_dealt_once_each_in_order(self):
        # Two workers' views of one queue: the second takes only what the
        # first has not, and neither takes anything twice.
        with tempfile.TemporaryDirectory() as tmp:
            work, names = queued(tmp, [["a.A.x"], ["b.B.x"], ["c.C.x"]])
            first, second = SHARD.claims(work), SHARD.claims(work)
            self.assertEqual(names[0], next(first))
            self.assertEqual(names[1], next(second))
            self.assertEqual([names[2]], list(first))
            self.assertEqual([], list(second))

    def test_racing_workers_finish_every_unit(self):
        # The condition the windows-latest runner failed under: several
        # workers claiming from one queue at once, many small units, every
        # claim a race. Every unit has to come back, and every worker green.
        ids = [i for i in found() if i.startswith("test_dispatch_board.")]
        self.assertGreater(len(ids), 40, "the suite this case borrows shrank")
        # Logs to files, as `run_all` does: a worker writes its whole run to
        # stderr, which would fill a pipe nobody reads until `wait` returns.
        with tempfile.TemporaryDirectory() as tmp:
            work, names = queued(tmp, [[i] for i in ids])
            logs = [work / f"worker-{n}.log" for n in range(4)]
            with contextlib.ExitStack() as open_logs:
                workers = [subprocess.Popen(
                    [sys.executable, str(RUNNER), "--queue", str(work)],
                    cwd=str(ROOT), stdout=open_logs.enter_context(log.open("wb")),
                    stderr=subprocess.STDOUT) for log in logs]
                codes = [worker.wait(timeout=600) for worker in workers]
            text = [log.read_text(encoding="utf-8", errors="replace")
                    for log in logs]
            self.assertEqual([0, 0, 0, 0], codes, [t[-2000:] for t in text])
            self.assertEqual([], SHARD.unfinished(work, names))
            # Finished is not enough: a unit two workers both claimed would
            # finish too. Each test has to have run exactly once.
            ran = "".join(text)
            twice = [i for i in ids if ran.count(f"({i})") != 1]
            self.assertEqual([], twice, "run other than exactly once")

    def test_a_unit_holding_an_id_discovery_does_not_hold_stays_unfinished(self):
        # The queue's half of the runtime coverage rule: the worker refuses
        # the unit, and the unit stays where the parent's check names it.
        with tempfile.TemporaryDirectory() as tmp:
            work, names = queued(tmp, [["test_nothing_at_all.AClass.test_it"]])
            out = subprocess.run(
                [sys.executable, str(RUNNER), "--queue", str(work)],
                cwd=str(ROOT), capture_output=True, text=True, encoding="utf-8",
                timeout=600)
            self.assertEqual(2, out.returncode, out.stdout + out.stderr)
            self.assertIn("discovery does not hold", out.stderr)
            self.assertEqual(names, SHARD.unfinished(work, names))

    def test_a_unit_that_runs_reaches_done_with_its_time(self):
        # The control for the case above, on a real class: the unit moves to
        # `done/` and carries the seconds its class took, which is what the
        # parent prints the slowest classes from.
        ids = [i for i in found() if SHARD.owner(i) == "test_dispatch_board.ParseTests"]
        self.assertTrue(ids, "the class this case borrows is gone")
        with tempfile.TemporaryDirectory() as tmp:
            work, names = queued(tmp, [ids])
            out = subprocess.run(
                [sys.executable, str(RUNNER), "--queue", str(work)],
                cwd=str(ROOT), capture_output=True, text=True, encoding="utf-8",
                timeout=600)
            self.assertEqual(0, out.returncode, out.stdout + out.stderr)
            self.assertEqual([], SHARD.unfinished(work, names))
            self.assertEqual(["test_dispatch_board.ParseTests"],
                             [cls for _, cls in SHARD.slowest(work)])

    def test_an_id_discovery_does_not_hold_stops_the_worker(self):
        # The runtime half of the coverage rule. A worker told to run a name
        # nothing discovers has nothing to run, and reporting that as a pass
        # is the one outcome this file exists to prevent.
        with tempfile.TemporaryDirectory() as tmp:
            listing = Path(tmp) / "ids"
            listing.write_text("test_nothing_at_all.AClass.test_it\n",
                               encoding="utf-8")
            out = subprocess.run(
                [sys.executable, str(RUNNER), "--ids", str(listing)],
                cwd=str(ROOT), capture_output=True, text=True, encoding="utf-8",
                timeout=600)
        self.assertEqual(2, out.returncode, out.stdout + out.stderr)
        self.assertIn("discovery does not hold", out.stderr)

    def test_a_worker_given_no_ids_at_all_refuses(self):
        with tempfile.TemporaryDirectory() as tmp:
            listing = Path(tmp) / "ids"
            listing.write_text("", encoding="utf-8")
            out = subprocess.run(
                [sys.executable, str(RUNNER), "--ids", str(listing)],
                cwd=str(ROOT), capture_output=True, text=True, encoding="utf-8",
                timeout=600)
        self.assertEqual(2, out.returncode, out.stdout + out.stderr)
        self.assertIn("no ids to run", out.stderr)

    def test_it_takes_no_arguments_of_its_own_but_the_selective_switch(self):
        # The shard count is the host's, capped; a caller who could set it
        # would be a second opinion about what the suite is.
        for args in (["--shards", "3"], ["--selective", "x"], ["--selective=yes"]):
            with self.subTest(args=args):
                out = subprocess.run([sys.executable, str(RUNNER), *args],
                                     cwd=str(ROOT), capture_output=True, text=True, encoding="utf-8",
                                     timeout=120)
                self.assertEqual(2, out.returncode, out.stdout)
                self.assertIn("usage: shard-harness-suite.py [--selective]", out.stderr)


class ATargetPinsItsOwnClasses(unittest.TestCase):
    """A class only one target has is pinned from that target's `local` module, never from the managed tables."""

    def declared(self, body):
        """A `local_commands` module holding `body`, on a path of its own, and the runner's read of it."""
        tmp = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, tmp, ignore_errors=True)
        name = "local_pins_fixture_" + uuid.uuid4().hex
        (Path(tmp) / f"{name}.py").write_text(body, encoding="utf-8")
        sys.path.insert(0, tmp)
        self.addCleanup(sys.path.remove, tmp)
        self.addCleanup(sys.modules.pop, name, None)
        return SHARD.local_pins(name)

    def test_a_target_with_no_module_pins_nothing(self):
        self.assertEqual((frozenset(), {}), SHARD.local_pins("no_such_local_module_here"))

    def test_its_pins_reach_the_plan(self):
        serial, together = self.declared(
            'SERIAL_CLASSES = ["t_a.Timed"]\nTOGETHER_CLASSES = {"t_b.One": "g", "t_b.Two": "g"}\n')
        ids = ["t_a.Timed.x", "t_b.One.x", "t_b.Two.x", "t_c.Free.x"]
        with mock.patch.object(SHARD, "SERIAL", frozenset(serial)), mock.patch.object(SHARD, "TOGETHER", together):
            units, tail = SHARD.plan(ids)
        self.assertEqual(["t_a.Timed.x"], tail)
        self.assertIn(["t_b.One.x", "t_b.Two.x"], units)
        self.assertIn(["t_c.Free.x"], units)

    def test_a_module_naming_only_one_table_pins_the_other_empty(self):
        self.assertEqual((frozenset({"t_a.Timed"}), {}), self.declared('SERIAL_CLASSES = {"t_a.Timed"}\n'))

    def test_the_payloads_tables_keep_their_names_beside_the_targets(self):
        serial, together = SHARD.pinned(frozenset({"a.A"}), {"b.B": "g"}, (frozenset({"c.C"}), {"d.D": "g"}))
        self.assertEqual(frozenset({"a.A", "c.C"}), serial)
        self.assertEqual({"b.B": "g", "d.D": "g"}, together)

    def test_a_class_the_payload_already_pins_is_refused(self):
        with self.assertRaises(ValueError):
            SHARD.pinned(frozenset({"a.A"}), {"b.B": "g"}, (frozenset({"a.A"}), {}))
        with self.assertRaises(ValueError):
            SHARD.pinned(frozenset({"a.A"}), {"b.B": "g"}, (frozenset(), {"b.B": "other"}))

    def test_the_tables_the_plan_reads_hold_what_a_fresh_load_reads_from_the_target(self):
        # Reverting the module's own `pinned(...)` line would leave every case above green and a target's pins
        # silently unapplied, so the runner is loaded afresh over a declaration module and its tables read.
        fixture = types.SimpleNamespace(SERIAL_CLASSES=["t_x.Timed"], TOGETHER_CLASSES={"t_x.Shared": "g"})
        with mock.patch("importlib.import_module", return_value=fixture):
            fresh = loaded()
        self.assertIn("t_x.Timed", fresh.SERIAL)
        self.assertEqual("g", fresh.TOGETHER["t_x.Shared"])
        self.assertLessEqual(set(SHARD._SERIAL), set(fresh.SERIAL))

    def test_a_class_pinned_both_ways_is_refused(self):
        with self.assertRaises(ValueError):
            SHARD.pinned(frozenset({"a.A"}), {}, (frozenset(), {"a.A": "g"}))

    def test_a_shape_that_would_read_as_something_else_is_refused(self):
        for body in ('SERIAL_CLASSES = "t_a.Timed"\n', "SERIAL_CLASSES = [1]\n", 'TOGETHER_CLASSES = ["t_a.A"]\n',
                     'TOGETHER_CLASSES = {"t_a.A": 3}\n'):
            with self.subTest(body=body), self.assertRaises(TypeError):
                self.declared(body)

    def test_a_module_whose_own_import_fails_is_raised(self):
        with self.assertRaises(ModuleNotFoundError):
            self.declared("import a_module_nobody_has\n")


class Worker:
    """What `run_all` waits on, standing in for a process, which exits with <code>."""

    def __init__(self, code):
        self.code = code

    def wait(self):
        return self.code

    def poll(self):
        return self.code


class TheRunItselfRefuses(unittest.TestCase):
    """The checks where the plan becomes workers and the workers come back, driven through `run_all` and
    `discover_and_run` rather than called apart from them."""

    def run_all(self, finish, code=0):
        """`run_all` over two units whose workers exit <code>, having finished the units <finish> names."""

        def spawn(args, _log):
            work = Path(args[1])
            for name in finish:
                (work / "done" / f"{name}.ids").write_bytes(b"")
            return Worker(code)

        out, err = io.StringIO(), io.StringIO()
        with mock.patch.object(SHARD, "spawn", spawn), mock.patch.object(SHARD, "report"), \
                contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
            status = SHARD.run_all([["a.A.x"], ["b.B.x"]], [], 1)
        return status, out.getvalue(), err.getvalue()

    def discover_and_run(self):
        err = io.StringIO()
        with contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(err):
            status = SHARD.discover_and_run()
        return status, err.getvalue()

    def test_workers_that_exit_zero_on_an_unfinished_unit_fail_the_run(self):
        status, _, err = self.run_all(["0000"])
        self.assertEqual(1, status)
        self.assertIn("1 of 2 units never finished", err)

    def test_a_worker_that_exits_non_zero_fails_the_run(self):
        status, out, _ = self.run_all(["0000", "0001"], code=1)
        self.assertEqual(1, status)
        self.assertIn("worker 0 failed", out)

    def test_every_unit_finished_by_green_workers_passes(self):
        status, out, err = self.run_all(["0000", "0001"])
        self.assertEqual(0, status, err)
        self.assertIn("all green", out)

    def test_a_plan_that_drops_a_unit_runs_nothing(self):
        units, tail = SHARD.plan(found())
        with mock.patch.object(SHARD, "discovered", return_value=found()), \
                mock.patch.object(SHARD, "plan", return_value=(units[:-1], tail)), \
                mock.patch.object(SHARD, "run_all") as run_all:
            status, err = self.discover_and_run()
        self.assertEqual(2, status)
        self.assertIn("the runs do not cover discovery", err)
        run_all.assert_not_called()

    def test_a_root_that_discovers_nothing_runs_nothing(self):
        with mock.patch.object(SHARD, "ROOTS", (("docs", "test_*.py"),)), \
                mock.patch.object(SHARD, "run_all") as run_all:
            status, err = self.discover_and_run()
        self.assertEqual(2, status)
        self.assertIn("Refusing the run", err)
        run_all.assert_not_called()

    def test_every_class_time_is_there_for_the_refresh(self):
        # `WEIGHT` lists far more classes than a short list of the slowest could hold.
        with tempfile.TemporaryDirectory() as tmp:
            work, _ = queued(tmp, [["a.A.x"]])
            (work / "done" / "0000.ids.times").write_text(
                "".join(f"{n}.0\tm.C{n}\n" for n in range(30)), encoding="utf-8")
            self.assertEqual(30, len(SHARD.slowest(work)))
            self.assertEqual((29.0, "m.C29"), SHARD.slowest(work)[0])


class ASelectiveRunKeepsWhatTheChangeReaches(unittest.TestCase):
    """`--selective` filters discovery and nothing else: every check after it holds over what it kept."""

    def selection(self, paths):
        out = io.StringIO()
        with mock.patch.object(SHARD.suite_select, "changed", return_value=paths), contextlib.redirect_stdout(out):
            kept = SHARD.selection(found())
        return kept, out.getvalue()

    def test_a_helper_keeps_its_own_suite_and_drops_the_unrelated(self):
        kept, said = self.selection([".claude/scripts/git-branch-drop.sh"])
        modules = {test_id.partition(".")[0] for test_id in kept}
        self.assertIn("test_git_branch_drop", modules)
        self.assertNotIn("test_refresh_index", modules)
        self.assertLess(len(kept), len(found()))
        self.assertTrue(set(kept) <= set(found()))
        self.assertIn("CI runs them all", said)

    def test_a_change_it_cannot_read_keeps_the_whole_suite(self):
        kept, said = self.selection(None)
        self.assertEqual(found(), kept)
        self.assertIn("runs the whole suite", said)

    def test_a_selection_of_nothing_runs_nothing_and_says_so(self):
        out = io.StringIO()
        with mock.patch.object(SHARD, "discovered", return_value=found()), \
                mock.patch.object(SHARD, "selection", return_value=[]), \
                mock.patch.object(SHARD, "run_all") as run_all, contextlib.redirect_stdout(out):
            status = SHARD.discover_and_run(selective=True)
        self.assertEqual(0, status)
        self.assertIn("nothing ran", out.getvalue())
        run_all.assert_not_called()

    def test_a_kept_set_the_plan_drops_from_is_still_refused(self):
        kept = [test_id for test_id in found() if test_id.startswith("test_harness_timings.")]
        units, tail = SHARD.plan(kept)
        with mock.patch.object(SHARD, "discovered", return_value=found()), \
                mock.patch.object(SHARD, "selection", return_value=kept), \
                mock.patch.object(SHARD, "plan", return_value=(units[:-1], tail)), \
                mock.patch.object(SHARD, "run_all") as run_all, \
                contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(io.StringIO()):
            self.assertEqual(2, SHARD.discover_and_run(selective=True))
        run_all.assert_not_called()


class EachRunIsRecorded(unittest.TestCase):

    def test_run_all_hands_back_every_class_time(self):
        facts = {}

        def spawn(args, _log):
            work = Path(args[1])
            (work / "done" / "0000.ids").write_bytes(b"")
            (work / "done" / "0000.ids.times").write_text("2.5\ta.A\n", encoding="utf-8")
            return Worker(0)

        with mock.patch.object(SHARD, "spawn", spawn), mock.patch.object(SHARD, "report"), \
                contextlib.redirect_stdout(io.StringIO()):
            self.assertEqual(0, SHARD.run_all([["a.A.x"]], [], 1, facts))
        self.assertEqual({"a.A": 2.5}, facts["classes"])

    def test_main_records_one_line_with_the_mode_the_wait_and_the_exit(self):
        @contextlib.contextmanager
        def held(*_args, **_kwargs):
            yield 7.5

        def ran(selective, facts):
            facts.update(tests=3, workers=2, classes={"a.A": 1.0})
            return 1

        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "t.jsonl"
            with mock.patch.dict("os.environ", {SHARD.harness_timings.ENV: str(path)}), \
                    mock.patch.object(SHARD, "host_lock", held), \
                    mock.patch.object(SHARD, "discover_and_run", side_effect=ran):
                self.assertEqual(1, SHARD.main(["shard-harness-suite.py", "--selective"]))
            line = json.loads(path.read_text(encoding="utf-8"))
        self.assertEqual(("shard-harness-suite.py", "selective", 7.5, 1, 3, {"a.A": 1.0}),
                         (line["script"], line["mode"], line["lock_wait"], line["exit"], line["tests"],
                          line["classes"]))
        self.assertLessEqual({"repository", "branch", "commit", "wall", "at", "host"}, set(line))


class OneRunAtATime(unittest.TestCase):
    """The lock two runs would otherwise interleave without.

    Every case takes a lock file of its own: the real one is held by the run
    this case is part of, and a case that waited on it would wait on itself.
    """

    def test_the_lock_lives_in_the_temp_directory_under_the_users_name(self):
        # Not the checkout: two worktrees are two checkouts and one host. And
        # the user's own file, because opening another user's in a shared
        # `/tmp` would fail rather than wait.
        self.assertEqual(Path(tempfile.gettempdir()), SHARD.LOCK.parent)
        with mock.patch.object(SHARD.getpass, "getuser", return_value="a b/c"):
            self.assertEqual("blueprint-harness-suite-a_b_c.lock",
                             SHARD.lock_path().name)

    def test_a_lock_that_cannot_be_opened_refuses_the_run(self):
        # A run that cannot take the lock cannot know it is alone; carrying on
        # would be the interleaving the lock exists to stop, said nothing of.
        with tempfile.TemporaryDirectory() as tmp:
            missing = Path(tmp) / "no-such-directory" / "lock"
            with self.assertRaises(SHARD.LockUnavailable):
                with SHARD.host_lock(missing):
                    pass

    def test_a_second_holder_is_refused_until_the_first_lets_go(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "lock"
            with SHARD.host_lock(path):
                with open(path, "a+b") as other:
                    self.assertFalse(SHARD.try_lock(other))
            with open(path, "a+b") as other:
                self.assertTrue(SHARD.try_lock(other))
                SHARD.unlock(other)

    def test_a_second_run_waits_and_says_so(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "lock"
            with open(path, "a+b") as first:
                self.assertTrue(SHARD.try_lock(first))
                released = threading.Timer(0.5, SHARD.unlock, (first,))
                released.start()
                began = time.monotonic()
                with mock.patch("builtins.print") as said:
                    with SHARD.host_lock(path, poll=0.05) as reported:
                        waited = time.monotonic() - began
                released.join()
            self.assertGreaterEqual(waited, 0.4)
            # The wait is said as it starts and as it ends, and handed to the record, since no run time includes it.
            lines = [call.args[0] for call in said.call_args_list]
            self.assertIn("waiting for it", lines[0])
            self.assertIn("came free", lines[-1])
            self.assertGreaterEqual(reported, 0.4)

    def test_a_lock_free_at_once_reports_no_wait_worth_naming(self):
        with tempfile.TemporaryDirectory() as tmp:
            with mock.patch("builtins.print") as said:
                with SHARD.host_lock(Path(tmp) / "lock") as reported:
                    pass
        said.assert_not_called()
        self.assertLess(reported, 1.0)

    def test_a_holder_that_dies_leaves_no_lock_behind(self):
        # Why there is no stale-lock recovery: the operating system drops the
        # lock with the process, however the process ended.
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "lock"
            child = (
                "import importlib.util, os, sys\n"
                "spec = importlib.util.spec_from_file_location('r', sys.argv[1])\n"
                "r = importlib.util.module_from_spec(spec); spec.loader.exec_module(r)\n"
                "h = open(sys.argv[2], 'a+b')\n"
                "sys.exit(0 if not r.try_lock(h) else os._exit(7))\n")
            out = subprocess.run([sys.executable, "-c", child, str(RUNNER),
                                  str(path)], timeout=120)
            self.assertEqual(7, out.returncode, "the child never held the lock")
            with open(path, "a+b") as after:
                self.assertTrue(SHARD.try_lock(after))
                SHARD.unlock(after)


if __name__ == "__main__":
    unittest.main()
