"""The measurement's suite, over a fake Actions API.

    cd tools/ci-cost && python -m unittest
"""

import contextlib
import io
import json
import re
import unittest
from datetime import date, datetime, timedelta, timezone
from unittest import mock

import ci_cost
from ci_cost import Window

REPO = "owner/repo"


def job(started, completed, labels=("ubuntu-latest",)):
    return {"started_at": started, "completed_at": completed, "labels": list(labels)}


def run(run_id, created, name="CI", event="pull_request", sha="a"):
    return {"id": run_id, "created_at": created, "name": name, "event": event, "head_sha": sha}


class FakeApi:
    """Answers the paths ci_cost asks for from in-memory runs, jobs and events, paging as GitHub does."""

    def __init__(self, runs, jobs, events=(), prs=(), packages=None, cap=ci_cost.SEARCH_CAP):
        self.runs, self.jobs, self.events = runs, jobs, list(events)
        self.prs = [{"created_at": created} for created in prs]
        self.packages, self.cap, self.paths = packages, cap, []

    def __call__(self, path):
        self.paths.append(path)
        numbered = re.search(r"[?&]page=(\d+)", path)
        page = int(numbered[1]) if numbered else 1
        sized = re.search(r"per_page=(\d+)", path)
        size = int(sized[1]) if sized else 30

        def window(items):
            return items[(page - 1) * size:page * size]

        if match := re.search(r"actions/runs\?created=([^.]+)\.\.([^&]+)", path):
            inside = [r for r in self.runs if match[1] <= r["created_at"] <= match[2]]
            return {"total_count": len(inside), "workflow_runs": window(inside[:self.cap])}
        if match := re.search(r"actions/runs/(\d+)/jobs", path):
            return {"jobs": window(self.jobs.get(int(match[1]), []))}
        if "/pulls?" in path:
            return window(self.prs)
        if "issues/events" in path:
            return window(self.events)
        if self.packages is None:
            raise LookupError(f"{path}: HTTP 403")
        if path.startswith("users/") and "/packages" not in path:
            return {"type": "User"}
        if "/versions" in path:
            return window([{}] * self.packages[path.split("/container/")[1].split("/")[0]])
        return window([{"name": name, "repository": {"full_name": REPO}} for name in self.packages])


A = Window("A", date(2026, 10, 10), date(2026, 10, 11))


class BilledMinutes(unittest.TestCase):
    def test_a_job_is_rounded_up_to_the_whole_minute(self):
        self.assertEqual(ci_cost.billed_minutes(job("2026-10-10T00:00:00Z", "2026-10-10T00:01:01Z")), 2)

    def test_an_exact_minute_is_not_rounded(self):
        self.assertEqual(ci_cost.billed_minutes(job("2026-10-10T00:00:00Z", "2026-10-10T00:03:00Z")), 3)

    def test_a_skipped_or_unfinished_job_bills_nothing(self):
        self.assertEqual(ci_cost.billed_minutes(job("2026-10-10T00:00:00Z", "2026-10-10T00:00:00Z")), 0)
        self.assertEqual(ci_cost.billed_minutes(job("2026-10-10T00:00:00Z", None)), 0)


class RunnerOs(unittest.TestCase):
    def test_labels_name_the_os(self):
        self.assertEqual(ci_cost.runner_os(job(None, None, ["windows-latest"])), "Windows")
        self.assertEqual(ci_cost.runner_os(job(None, None, ["macos-15"])), "macOS")
        self.assertEqual(ci_cost.runner_os(job(None, None, ["ubuntu-24.04"])), "Linux")

    def test_self_hosted_wins_over_the_os_it_runs(self):
        self.assertEqual(ci_cost.runner_os(job(None, None, ["self-hosted", "windows"])), "self-hosted")


class Measure(unittest.TestCase):
    def test_minutes_are_split_by_os_and_priced_at_each_rate(self):
        api = FakeApi([run(1, "2026-10-10T05:00:00Z"), run(2, "2026-10-11T23:59:59Z", event="push", sha="b")], {
            1: [job("2026-10-10T05:00:00Z", "2026-10-10T05:04:30Z"),
                job("2026-10-10T05:00:00Z", "2026-10-10T05:02:00Z", ["windows-latest"])],
            2: [job("2026-10-11T23:59:59Z", "2026-10-12T00:00:59Z", ["macos-latest"])]},
            prs=["2026-10-12T00:00:00Z", "2026-10-11T08:00:00Z", "2026-10-10T08:00:00Z", "2026-10-09T08:00:00Z"])
        usage = ci_cost.measure(api, REPO, A, workers=1)
        self.assertEqual((usage.runs, usage.jobs, usage.pushes, usage.prs), (2, 3, 1, 2))
        self.assertEqual(usage.minutes, {"Linux": 5, "Windows": 2, "macOS": 1})
        self.assertAlmostEqual(usage.dollars, 5 * 0.006 + 2 * 0.010 + 0.062)
        self.assertAlmostEqual(usage.minutes_by_workflow["CI"], usage.linux_equivalent)

    def test_runs_outside_the_window_are_not_counted(self):
        api = FakeApi([run(1, "2026-10-09T23:59:59Z"), run(2, "2026-10-12T00:00:00Z")], {})
        self.assertEqual(ci_cost.measure(api, REPO, A, workers=1).runs, 0)

    def test_a_span_over_the_search_cap_is_split_until_every_run_is_read(self):
        runs = [run(i, f"2026-10-10T{i // 60:02d}:{i % 60:02d}:00Z") for i in range(250)]
        with mock.patch.object(ci_cost, "SEARCH_CAP", 100):
            self.assertEqual(ci_cost.measure(FakeApi(runs, {}, cap=100), REPO, A, workers=1).runs, 250)

    def test_pages_are_requested_by_number_until_a_short_page(self):
        runs = [run(i, "2026-10-10T01:00:00Z") for i in range(ci_cost.PAGE + 1)]
        api = FakeApi(runs, {})
        self.assertEqual(ci_cost.measure(api, REPO, A, workers=1).runs, ci_cost.PAGE + 1)
        self.assertTrue(any("page=2" in path for path in api.paths))

    def test_label_events_are_counted_inside_the_window_only(self):
        events = [{"created_at": "2026-10-12T00:00:00Z", "event": "labeled"},
                  {"created_at": "2026-10-11T10:00:00Z", "event": "unlabeled"},
                  {"created_at": "2026-10-10T10:00:00Z", "event": "closed"},
                  {"created_at": "2026-10-09T10:00:00Z", "event": "labeled"}]
        self.assertEqual(ci_cost.measure(FakeApi([], {}, events), REPO, A, workers=1).label_events, 1)

    def test_packages_are_read_only_for_a_window_ending_now(self):
        api = FakeApi([], {}, packages={"catalog": 3, "ordering": 2})
        self.assertIsNone(ci_cost.measure(api, REPO, A, workers=1).packages)
        self.assertEqual(ci_cost.measure(api, REPO, A, current=True, workers=1).packages,
                         {"catalog": 3, "ordering": 2})

    def test_a_refused_read_is_reported_rather_than_fatal(self):
        usage = ci_cost.measure(FakeApi([], {}), REPO, A, current=True, workers=1)
        self.assertIsNone(usage.packages)
        self.assertTrue(any(note.startswith("GHCR packages") for note in usage.unavailable))


class Gh(unittest.TestCase):
    def test_gh_output_is_decoded_as_utf8_whatever_the_locale(self):
        done = mock.Mock(returncode=0, stdout='{"name": "Observability — smoke"}', stderr="")
        with mock.patch("subprocess.run", return_value=done) as called:
            ci_cost.gh("repos/owner/repo")
        self.assertEqual(called.call_args.kwargs["encoding"], "utf-8")
        self.assertNotIn("text", called.call_args.kwargs)

    def answers(self, *bodies):
        return [mock.Mock(returncode=code, stdout=out, stderr=err) for code, out, err in bodies]

    def test_an_exhausted_rate_limit_waits_for_its_reset_then_retries(self):
        replies = self.answers((1, "", "gh: API rate limit exceeded for user ID 1. (HTTP 403)"),
                               (0, '{"resources": {"core": {"remaining": 0, "reset": 1300}}}', ""),
                               (0, '{"ok": true}', ""))
        with mock.patch("subprocess.run", side_effect=replies), mock.patch("time.time", return_value=1000), \
                mock.patch("time.sleep") as slept:
            self.assertEqual(ci_cost.gh("repos/owner/repo"), {"ok": True})
        slept.assert_called_once_with(301)

    def test_a_secondary_limit_waits_a_minute_while_the_hour_has_requests_left(self):
        replies = self.answers((1, "", "gh: You have exceeded a secondary rate limit. (HTTP 403)"),
                               (0, '{"resources": {"core": {"remaining": 4000, "reset": 3000}}}', ""),
                               (0, "{}", ""))
        with mock.patch("subprocess.run", side_effect=replies), mock.patch("time.time", return_value=1000), \
                mock.patch("time.sleep") as slept:
            ci_cost.gh("repos/owner/repo")
        slept.assert_called_once_with(60)

    def test_a_dropped_connection_or_a_server_error_is_retried(self):
        replies = self.answers((1, "", 'Get "https://api.github.com/x": dial tcp 140.82.121.6:443: connectex'),
                               (1, "", "gh: Server Error (HTTP 502)"),
                               (0, "{}", ""))
        with mock.patch("subprocess.run", side_effect=replies), mock.patch("time.sleep") as slept:
            self.assertEqual(ci_cost.gh("repos/owner/repo"), {})
        self.assertEqual(slept.call_count, 2)

    def test_a_connection_that_keeps_failing_ends_the_call(self):
        replies = self.answers(*[(1, "", "dial tcp: i/o timeout")] * ci_cost.TRANSIENT_TRIES)
        with mock.patch("subprocess.run", side_effect=replies), mock.patch("time.sleep"), \
                self.assertRaisesRegex(LookupError, "i/o timeout"):
            ci_cost.gh("repos/owner/repo")

    def test_any_other_refusal_ends_the_call_without_waiting(self):
        replies = self.answers((1, "", "gh: Not Found (HTTP 404)"))
        with mock.patch("subprocess.run", side_effect=replies), mock.patch("time.sleep") as slept, \
                self.assertRaisesRegex(LookupError, "Not Found"):
            ci_cost.gh("repos/owner/repo")
        slept.assert_not_called()


class RunsBetween(unittest.TestCase):
    def test_a_minute_still_over_the_cap_is_refused_not_undercounted(self):
        start = datetime(2026, 10, 10, tzinfo=timezone.utc)

        def crowded(path):
            return {"total_count": ci_cost.SEARCH_CAP + 1, "workflow_runs": []}

        with self.assertRaisesRegex(LookupError, "short"):
            ci_cost.runs_between(crowded, REPO, start, start + timedelta(minutes=1))


class Cli(unittest.TestCase):
    def output(self, *argv, api=None):
        stdout = io.StringIO()
        with contextlib.redirect_stdout(stdout):
            ci_cost.main(["--repo", REPO, *argv], fetch=api or FakeApi([], {}), today=date(2026, 10, 24))
        return stdout.getvalue()

    def test_the_default_window_is_the_last_fourteen_full_days(self):
        window = json.loads(self.output("--json"))["windows"][0]
        self.assertEqual((window["first"], window["last"]), ("2026-10-10", "2026-10-23"))

    def test_each_window_is_a_column(self):
        text = self.output("--window", "A=2026-10-01:2026-10-14", "--window", "B=2026-11-01:2026-11-14")
        self.assertIn("A (2026-10-01..2026-10-14) | B (2026-11-01..2026-11-14)", text)

    def test_a_malformed_window_is_refused(self):
        with self.assertRaises(SystemExit), contextlib.redirect_stderr(io.StringIO()):
            self.output("--window", "A=2026-10-14:2026-10-01")


if __name__ == "__main__":
    unittest.main()
