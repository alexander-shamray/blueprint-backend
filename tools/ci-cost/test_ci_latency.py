"""The latency report's suite, over a fake Actions API.

    cd tools/ci-cost && python -m unittest
"""

import contextlib
import io
import json
import re
import unittest
from datetime import date, datetime, timezone
from unittest import mock

import ci_cost
import ci_latency
from ci_cost import Window
from test_ci_cost import REPO, FakeApi

DAY = date(2026, 11, 10)
WINDOW = Window("A", date(2026, 11, 2), date(2026, 11, 15))


def moment(hour, minute=0, second=0, day=10):
    return f"2026-11-{day:02d}T{hour:02d}:{minute:02d}:{second:02d}Z"


def job(created, started, completed, name="build", conclusion="success", steps=()):
    return {"name": name, "created_at": created, "started_at": started, "completed_at": completed,
            "conclusion": conclusion, "labels": ["ubuntu-latest"], "steps": list(steps)}


def step(name, started, completed, conclusion="success"):
    return {"name": name, "started_at": started, "completed_at": completed, "conclusion": conclusion}


def run(run_id, created, started, updated, workflow=1, name="CI", conclusion="success"):
    return {"id": run_id, "created_at": created, "run_started_at": started, "updated_at": updated, "name": name,
            "event": "pull_request", "head_sha": "a", "conclusion": conclusion, "workflow_id": workflow}


class WorkflowApi(FakeApi):
    """FakeApi that also lists workflows and a workflow's own runs, each listing capped on its own."""

    def __init__(self, runs, jobs, workflows=((1, "CI"),), **kwargs):
        super().__init__(runs, jobs, **kwargs)
        self.workflows = [{"id": number, "name": name, "path": f".github/workflows/{number}.yml"}
                          for number, name in workflows]

    def __call__(self, path):
        if "actions/workflows?" in path:
            self.paths.append(path)
            page = int(re.search(r"[?&]page=(\d+)", path)[1])
            return {"workflows": self.workflows if page == 1 else []}
        if match := re.search(r"actions/workflows/(\d+)/runs\?(.*)", path):
            self.paths.append(path)
            everything = self.runs
            self.runs = [r for r in everything if r["workflow_id"] == int(match[1])]
            try:
                return super().__call__(f"repos/{REPO}/actions/runs?{match[2]}")
            finally:
                self.runs = everything
        return super().__call__(path)


class Percentile(unittest.TestCase):
    def test_nearest_rank_returns_a_measured_value(self):
        values = list(range(1, 11))
        self.assertEqual((ci_latency.percentile(values, 0.5), ci_latency.percentile(values, 0.9)), (5, 9))

    def test_nothing_measured_is_none(self):
        self.assertIsNone(ci_latency.percentile([], 0.5))
        self.assertEqual(ci_latency.summary([]), {"n": 0, "p50": None, "p90": None})

    def test_durations_read_in_minutes_and_seconds(self):
        self.assertEqual([ci_latency.duration(v) for v in (None, 7, 62, 600)], ["n/a", "7s", "1m 02s", "10m 00s"])


class Weekly(unittest.TestCase):
    def test_a_successful_run_counts_in_the_week_it_was_created(self):
        runs = [run(1, moment(1, day=4), moment(1, day=4), moment(1, 10, day=4)),
                run(2, moment(1, day=8), moment(1, day=8), moment(1, 20, day=8)),
                run(3, moment(1, day=3), moment(1, day=3), moment(1, 5, day=3), conclusion="failure")]
        self.assertEqual(ci_latency.weekly(runs), {"2026-11-02": [600.0, 1200.0]})

    def test_a_week_starts_on_monday_in_utc(self):
        self.assertEqual(ci_latency.week_of("2026-11-08T23:59:59Z"), "2026-11-02")
        self.assertEqual(ci_latency.week_of("2026-11-09T00:00:00Z"), "2026-11-09")


class CriticalPath(unittest.TestCase):
    def test_time_goes_to_the_first_job_across_the_jobs_and_beyond_the_longest(self):
        jobs = [job(moment(1), moment(1, 1), moment(1, 4), "a"), job(moment(1), moment(1, 2), moment(1, 11), "b"),
                job(moment(1), moment(1, 3), moment(1, 3), "skipped", conclusion="skipped")]
        path = ci_latency.critical_path(run(1, moment(1), moment(1), moment(1, 11)), jobs)
        self.assertEqual(path, {"lag": 60.0, "span": 600.0, "longest": 540.0, "beyond": 60.0})

    def test_a_run_with_no_job_that_ran_has_no_path(self):
        self.assertIsNone(ci_latency.critical_path(run(1, moment(1), moment(1), moment(1)), []))


class Sampled(unittest.TestCase):
    def test_queue_wait_and_the_slowest_steps_are_read_per_job_without_the_matrix_leg(self):
        slow = [step("Restore", moment(1, 1), moment(1, 2)), step("Test", moment(1, 2), moment(1, 9)),
                step("Skipped", moment(1, 9), moment(1, 9), "skipped")]
        runs = [run(1, moment(1), moment(1), moment(1, 9)), run(2, moment(2), moment(2), moment(2, 9))]
        api = WorkflowApi(runs, {1: [job(moment(1), moment(1, 1), moment(1, 9), "unit (1)", steps=slow)],
                                 2: [job(moment(2), moment(2, 3), moment(2, 9), "unit (2)", steps=slow)]})
        result = ci_latency.sampled(api, REPO, runs, 40, 1, 1)
        self.assertEqual(result["sampled"], 2)
        self.assertEqual(result["jobs"]["unit"]["wait"], {"n": 2, "p50": 60.0, "p90": 180.0})
        self.assertEqual([row["step"] for row in result["jobs"]["unit"]["steps"]], ["Test"])

    def test_only_the_newest_successful_runs_are_read_and_the_latest_attempt_of_each(self):
        runs = [run(n, moment(n), moment(n), moment(n, 5)) for n in range(1, 6)]
        runs.append(run(9, moment(9), moment(9), moment(9, 5), conclusion="failure"))
        api = WorkflowApi(runs, {n: [job(moment(n), moment(n, 1), moment(n, 5))] for n in range(1, 10)})
        result = ci_latency.sampled(api, REPO, runs, 2, 1, 1)
        self.assertEqual(result["sampled"], 2)
        read = [path for path in api.paths if "/jobs" in path]
        self.assertEqual(len(read), 2)
        self.assertTrue(all("filter=latest" in path for path in read))
        self.assertTrue(all("/runs/5/" in path or "/runs/4/" in path for path in read))


class RepoLatency(unittest.TestCase):
    def test_each_workflow_is_listed_on_its_own_and_one_without_runs_is_left_out(self):
        runs = [run(1, moment(1), moment(1), moment(1, 9)), run(2, moment(2), moment(2), moment(2, 4), 2, "Helm")]
        api = WorkflowApi(runs, {1: [job(moment(1), moment(1, 1), moment(1, 9))],
                                 2: [job(moment(2), moment(2, 1), moment(2, 4))]},
                          workflows=((1, "CI"), (2, "Helm"), (3, "Idle")))
        result = ci_latency.repo_latency(api, REPO, WINDOW, workers=1)
        self.assertEqual(sorted(result["workflows"]), ["CI", "Helm"])
        self.assertEqual(result["workflows"]["CI"]["weekly"]["2026-11-09"], {"n": 1, "p50": 540.0, "p90": 540.0})
        self.assertTrue(any("workflows/1/runs?" in path for path in api.paths))

    def test_two_workflows_sharing_a_name_are_both_reported(self):
        runs = [run(1, moment(1), moment(1), moment(1, 9)), run(2, moment(2), moment(2), moment(2, 4), 2)]
        api = WorkflowApi(runs, {}, workflows=((1, "CI"), (2, "CI")))
        result = ci_latency.repo_latency(api, REPO, WINDOW, workers=1)
        self.assertEqual(sorted(result["workflows"]), ["CI", "CI (.github/workflows/2.yml)"])

    def test_a_workflow_past_the_search_cap_is_halved_like_the_whole_listing(self):
        runs = [run(n, moment(n % 24, n % 60, day=3 + n % 7), moment(1), moment(2)) for n in range(1, 31)]
        api = WorkflowApi(runs, {}, cap=12)
        with mock.patch.object(ci_cost, "SEARCH_CAP", 12):
            found = ci_cost.runs_between(api, REPO, WINDOW.start, WINDOW.end, workflow=1)
        self.assertEqual(sorted(r["id"] for r in found), list(range(1, 31)))


def slot(created, started, completed):
    stamp = lambda second: datetime(2026, 11, 10, 0, 0, tzinfo=timezone.utc).replace(
        hour=second // 3600, minute=second % 3600 // 60, second=second % 60)
    return {"created": stamp(created), "started": stamp(started), "completed": stamp(completed)}


ORIGIN = datetime(2026, 11, 10, tzinfo=timezone.utc)


class Concurrency(unittest.TestCase):
    def test_a_job_counts_in_every_minute_it_touches(self):
        counts = ci_latency.running_by_minute([slot(0, 30, 150), slot(0, 90, 100)], ORIGIN)
        self.assertEqual(counts[:4], [1, 2, 1, 0])

    def test_no_jobs_run_nowhere(self):
        self.assertEqual(max(ci_latency.running_by_minute([], ORIGIN)), 0)


class Replay(unittest.TestCase):
    def test_under_a_cap_of_one_jobs_run_in_turn(self):
        slots = [slot(0, 0, 100), slot(0, 0, 100), slot(10, 10, 110)]
        self.assertEqual(ci_latency.replay(slots, 1), [0.0, 100.0, 190.0])

    def test_a_cap_above_the_peak_makes_nobody_wait(self):
        slots = [slot(0, 0, 100), slot(0, 0, 100), slot(10, 10, 110)]
        self.assertEqual(ci_latency.replay(slots, 3), [0.0, 0.0, 0.0])

    def test_a_job_queued_behind_a_finished_one_starts_when_it_ends(self):
        self.assertEqual(ci_latency.replay([slot(0, 0, 50), slot(60, 60, 90)], 1), [0.0, 0.0])


class Day(unittest.TestCase):
    def test_the_day_counts_jobs_peak_and_waits_actual_and_replayed(self):
        runs = [run(1, moment(1), moment(1), moment(1, 30)), run(2, moment(1, 1), moment(1, 1), moment(1, 30)),
                run(3, moment(1, day=11), moment(1, day=11), moment(1, 30, day=11))]
        wait = [job(moment(1), moment(1, 5), moment(1, 15)), job(moment(1), moment(1), moment(1, 10))]
        api = WorkflowApi(runs, {1: wait[:1], 2: wait[1:], 3: wait[:1]})
        result = ci_latency.day_latency(api, [REPO], DAY, caps=(1, 5), workers=1)
        self.assertEqual((result["jobs"], result["peak"]), (2, 2))
        self.assertEqual(result["actual"]["queued"], 1)
        self.assertEqual(result["replay"]["1"]["queued"], 1)
        self.assertEqual(result["replay"]["1"]["max"], 600.0)
        self.assertEqual(result["replay"]["5"]["max"], 0.0)
        self.assertEqual(result["running_when_queued"]["n"], 1)
        self.assertEqual(result["hours"][1]["started"], 2)

    def test_a_job_that_never_ran_is_not_a_slot(self):
        runs = [run(1, moment(1), moment(1), moment(1, 30))]
        api = WorkflowApi(runs, {1: [job(moment(1), None, None, conclusion=None),
                                     job(moment(1), moment(1), moment(1), conclusion="skipped")]})
        self.assertEqual(ci_latency.day_latency(api, [REPO], DAY, workers=1)["jobs"], 0)


class Report(unittest.TestCase):
    def api(self):
        slow = [step("Test", moment(1, 2), moment(1, 9))]
        return WorkflowApi([run(1, moment(1), moment(1), moment(1, 9))],
                           {1: [job(moment(1), moment(1, 1), moment(1, 9), steps=slow)]})

    def main(self, *argv):
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            code = ci_cost.main(["--latency", "--repo", REPO, *argv], fetch=self.api(), today=date(2026, 11, 12))
        self.assertEqual(code, 0)
        return out.getvalue()

    def test_the_tables_name_the_week_the_path_the_wait_and_the_step(self):
        text = self.main("--window", "A=2026-11-02:2026-11-12")
        for expected in ("| CI | 2026-11-09 | 1 | 9m 00s / 9m 00s |", "| Run start to first job",
                         "| CI | build | 1 | 1m 00s / 1m 00s |", "| CI | build | Test | 1 | 7m 00s / 7m 00s |"):
            self.assertIn(expected, text)

    def test_a_day_alone_prints_the_replay_and_no_window(self):
        text = self.main("--day", "2026-11-10", "--cap", "1")
        self.assertIn("| Concurrency cap |", text)
        self.assertNotIn("Critical path", text)

    def test_json_holds_both_parts(self):
        body = json.loads(self.main("--window", "A=2026-11-02:2026-11-12", "--day", "2026-11-10", "--json"))
        self.assertEqual(body["day"]["jobs"], 1)
        self.assertIn("CI", body["windows"][0]["workflows"])

    def test_a_cap_below_one_is_refused(self):
        with contextlib.redirect_stderr(io.StringIO()), self.assertRaises(SystemExit):
            ci_cost.main(["--latency", "--repo", REPO, "--day", "2026-11-10", "--cap", "0"], fetch=self.api(),
                         today=date(2026, 11, 12))

    def test_the_latency_flags_are_refused_without_latency(self):
        with contextlib.redirect_stderr(io.StringIO()), self.assertRaises(SystemExit):
            ci_cost.main(["--day", "2026-11-10"], fetch=self.api(), today=date(2026, 11, 12))


if __name__ == "__main__":
    unittest.main()
