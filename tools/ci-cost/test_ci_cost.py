"""The measurement's suite, over a fake Actions API.

    cd tools/ci-cost && python -m unittest
"""

import contextlib
import http.client
import io
import json
import re
import unittest
import urllib.error
from datetime import date, datetime, timedelta, timezone
from unittest import mock
from urllib.parse import unquote

import ci_cost
from ci_cost import Window

REPO = "owner/repo"


def job(started, completed, labels=("ubuntu-latest",), name="build"):
    return {"name": name, "started_at": started, "completed_at": completed, "labels": list(labels)}


def run(run_id, created, name="CI", event="pull_request", sha="a", conclusion="success"):
    return {"id": run_id, "created_at": created, "name": name, "event": event, "head_sha": sha,
            "conclusion": conclusion}


class FakeApi:
    """Answers the paths ci_cost asks for from in-memory runs, jobs and events, paging as GitHub does."""

    def __init__(self, runs, jobs, events=(), prs=(), packages=None, cap=ci_cost.SEARCH_CAP, artifacts=None):
        self.runs, self.jobs, self.events = runs, jobs, list(events)
        self.prs = [{"id": number, "created_at": created} for number, created in enumerate(prs)]
        self.packages, self.cap, self.paths, self.artifacts = packages, cap, [], artifacts

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
        if "actions/artifacts" in path or "actions/cache/usage" in path:
            if self.artifacts is None:
                raise LookupError(f"{path}: HTTP 403")
            if "cache/usage" in path:
                return {"active_caches_size_in_bytes": 7_560_000_000}
            return {"artifacts": window(self.artifacts)}
        if self.packages is None:
            raise LookupError(f"{path}: HTTP 403")
        if path.startswith("users/") and "/packages" not in path:
            return {"type": "User"}
        if "/versions" in path:
            name = unquote(path.split("/container/")[1].split("/")[0])
            return window([{"name": f"{name}@{n}"} for n in range(self.packages[name])])
        return window([{"name": name, "repository": {"full_name": REPO}} for name in self.packages])


def no_manifests(package, reference):
    return {}


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

    def test_the_main_workflow_is_broken_down_by_head_conclusion_and_job(self):
        started, ended = "2026-10-10T05:00:00Z", "2026-10-10T05:02:00Z"
        api = FakeApi([run(1, started, sha="a"), run(2, started, sha="a", conclusion="cancelled"),
                       run(3, started, sha="b", event="push"), run(4, started, name="Helm", sha="c")], {
            1: [job(started, ended, name="integration (1)"), job(started, ended, name="integration (2)")],
            2: [job(started, ended, ["macos-latest"], name="edit-target-guard (macos-latest)")],
            3: [job(started, ended, name="build")],
            4: [job(started, ended, name="template")]})
        usage = ci_cost.measure(api, REPO, A, workers=1)
        self.assertEqual((usage.main_pr_runs, usage.main_pr_heads), (2, {"a"}))
        self.assertEqual(usage.main_conclusions, {"success": 2, "cancelled": 1})
        self.assertEqual(usage.main_minutes_by_job["integration"], 4)
        self.assertNotIn("template", usage.main_minutes_by_job)
        self.assertAlmostEqual(usage.main_minutes_by_conclusion["cancelled"], 2 * 0.062 / 0.006)
        text = ci_cost.table([usage], "Free")
        self.assertIn("| CI's `pull_request` runs per head commit | 2.00 |", text)
        self.assertIn("| integration | 4 | 1 |", text)

    def test_a_job_counts_its_runs_once_however_many_legs_or_attempts_and_a_skipped_one_not_at_all(self):
        started, ended = "2026-10-10T05:00:00Z", "2026-10-10T05:02:00Z"
        api = FakeApi([run(1, started), run(2, started)], {
            1: [job(started, ended, name="guard (windows)"), job(started, ended, name="guard (macos)")],
            2: [job(started, started, name="guard (windows)")]})
        self.assertEqual(ci_cost.measure(api, REPO, A, workers=1).main_runs_by_job, {"guard": 1})

    def test_minutes_are_summed_by_the_day_a_job_started(self):
        api = FakeApi([run(1, "2026-10-10T23:59:00Z"), run(2, "2026-10-11T01:00:00Z")], {
            1: [job("2026-10-10T23:59:00Z", "2026-10-11T00:01:00Z")],
            2: [job("2026-10-11T01:00:00Z", "2026-10-11T01:03:00Z"),
                job("2026-10-11T01:00:00Z", "2026-10-11T01:01:00Z", ["windows-latest"])]})
        usage = ci_cost.measure(api, REPO, A, workers=1)
        self.assertEqual(usage.linux_minutes_by_day, {"2026-10-10": 2, "2026-10-11": 3})
        self.assertAlmostEqual(usage.equivalent_by_day["2026-10-11"], 3 + 0.010 / 0.006)
        self.assertIn("| 2026-10-10 | 2 | 2 |", ci_cost.table([usage], "Free"))

    def test_a_row_that_a_page_shift_repeats_is_counted_once(self):
        rows = [{"id": n, "created_at": "2026-10-10T08:00:00Z"} for n in range(ci_cost.PAGE + 5)]

        # Five PRs open between the first page and the second, so the second starts five rows back.
        def shifted(path):
            if path.endswith("&page=1"):
                return rows[:ci_cost.PAGE]
            return rows[ci_cost.PAGE - 5:]

        self.assertEqual(ci_cost.count_prs(shifted, REPO, A), ci_cost.PAGE + 5)

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

    def test_a_full_page_the_server_keeps_failing_is_read_again_in_smaller_pages(self):
        started = "2026-10-10T01:00:00Z"
        api = FakeApi([run(1, started)], {1: [job(started, "2026-10-10T01:01:00Z")] * 123})

        def failing_full_pages(path):
            if f"jobs?filter=all&per_page={ci_cost.PAGE}&" in path:
                raise LookupError(f"{path}: gh: Server Error (HTTP 502)")
            return api(path)

        self.assertEqual(ci_cost.measure(failing_full_pages, REPO, A, workers=1).jobs, 123)

    def test_a_refusal_on_a_full_page_is_not_read_again(self):
        paths = []

        def refused(path):
            paths.append(path)
            raise LookupError(f"{path}: gh: Not Found (HTTP 404)")

        with self.assertRaisesRegex(LookupError, "Not Found"):
            list(ci_cost.paged(refused, "repos/owner/repo/pulls"))
        self.assertEqual(paths, [f"repos/owner/repo/pulls?per_page={ci_cost.PAGE}&page=1"])

    def test_a_package_name_with_a_slash_is_one_path_segment(self):
        api = FakeApi([], {}, packages={"blueprint-backend/gateway": 3})
        self.assertEqual(ci_cost.measure(api, REPO, A, current=True, workers=1, manifest=no_manifests).packages,
                         {"blueprint-backend/gateway": 3})
        self.assertTrue(any("/container/blueprint-backend%2Fgateway/versions" in path for path in api.paths))

    def test_registry_bytes_counts_a_blob_two_images_share_once_and_owns_up_to_a_manifest_unread(self):
        bodies = {"a@0": {"config": {"digest": "c1", "size": 1}, "layers": [{"digest": "shared", "size": 10}]},
                  "b@0": {"config": {"digest": "c2", "size": 2}, "layers": [{"digest": "shared", "size": 10}]},
                  "b@1": {"manifests": [{"digest": "child", "size": 99}]}}

        def manifest(package, reference):
            if reference == "b@2":
                raise LookupError("timed out")
            return bodies[reference]

        api = FakeApi([], {}, packages={"a": 1, "b": 3})
        usage = ci_cost.measure(api, REPO, A, current=True, workers=1, manifest=manifest)
        self.assertEqual((usage.package_bytes, usage.package_unread), (13, 1))
        self.assertIn("0.00 GB, a floor: 1 manifests unread", ci_cost.table([usage], "Free"))

    def test_actions_storage_sums_live_artifacts_and_reads_the_caches(self):
        artifacts = [{"id": 1, "size_in_bytes": 3_000_000_000, "expired": False},
                     {"id": 2, "size_in_bytes": 960_000_000, "expired": False},
                     {"id": 3, "size_in_bytes": 5_000_000_000, "expired": True}]
        usage = ci_cost.measure(FakeApi([], {}, artifacts=artifacts), REPO, A, current=True, workers=1)
        self.assertEqual(usage.actions_storage,
                         {"artifacts": 2, "artifact_bytes": 3_960_000_000, "cache_bytes": 7_560_000_000})
        text = ci_cost.table([usage], "Free")
        self.assertIn("| Actions artifacts live, at the window's end | 2 holding 3.96 GB |", text)
        self.assertIn("| Actions caches, at the window's end | 7.56 GB |", text)

    def test_label_events_are_counted_inside_the_window_only(self):
        events = [{"created_at": "2026-10-12T00:00:00Z", "event": "labeled"},
                  {"created_at": "2026-10-11T10:00:00Z", "event": "unlabeled"},
                  {"created_at": "2026-10-10T10:00:00Z", "event": "closed"},
                  {"created_at": "2026-10-09T10:00:00Z", "event": "labeled"}]
        self.assertEqual(ci_cost.measure(FakeApi([], {}, events), REPO, A, workers=1).label_events, 1)

    def test_packages_are_read_only_for_a_window_ending_now(self):
        api = FakeApi([], {}, packages={"catalog": 3, "ordering": 2})
        self.assertIsNone(ci_cost.measure(api, REPO, A, workers=1).packages)
        self.assertEqual(ci_cost.measure(api, REPO, A, current=True, workers=1, manifest=no_manifests).packages,
                         {"catalog": 3, "ordering": 2})

    def test_the_repository_is_matched_whatever_its_case_and_a_listing_with_none_of_its_packages_is_a_note(self):
        api = FakeApi([], {}, packages={"catalog": 3})
        upper = ci_cost.measure(api, REPO.upper(), A, current=True, workers=1, manifest=no_manifests)
        self.assertEqual(upper.packages, {"catalog": 3})
        other = ci_cost.measure(api, "owner/other", A, current=True, workers=1, manifest=no_manifests)
        self.assertIsNone(other.packages)
        self.assertTrue(any(note.startswith("GHCR packages") for note in other.unavailable))

    def test_a_refused_read_is_reported_rather_than_fatal(self):
        usage = ci_cost.measure(FakeApi([], {}), REPO, A, current=True, workers=1)
        self.assertIsNone(usage.packages)
        self.assertIsNone(usage.actions_storage)
        self.assertTrue(any(note.startswith("GHCR packages") for note in usage.unavailable))
        self.assertTrue(any(note.startswith("Actions storage") for note in usage.unavailable))


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

    def test_a_delete_answers_no_body_and_names_its_method(self):
        with mock.patch("subprocess.run", return_value=mock.Mock(returncode=0, stdout="", stderr="")) as called:
            self.assertIsNone(ci_cost.gh("repos/owner/repo/thing", "DELETE"))
        self.assertEqual(called.call_args.args[0][:4], ["gh", "api", "--method", "DELETE"])

    def test_a_rate_limit_read_that_fails_waits_a_minute_rather_than_crashing(self):
        replies = self.answers((1, "", "gh: API rate limit exceeded for user ID 1. (HTTP 403)"),
                               (1, "", "dial tcp: i/o timeout"),
                               (0, "{}", ""))
        with mock.patch("subprocess.run", side_effect=replies), mock.patch("time.sleep") as slept:
            ci_cost.gh("repos/owner/repo")
        slept.assert_called_once_with(ci_cost.SECONDARY_WAIT)

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


class Registry(unittest.TestCase):
    def read(self, *answers, env=None):
        """One manifest read, each urlopen answering in turn; a token request always succeeds."""
        queue = list(answers)
        asked = []

        def urlopen(request, timeout):
            asked.append(request)
            if "/token?" in request.full_url:
                return io.BytesIO(b'{"token": "t"}')
            answer = queue.pop(0)
            if isinstance(answer, Exception):
                raise answer
            return io.BytesIO(answer)

        with mock.patch("urllib.request.urlopen", urlopen), mock.patch("time.sleep"), \
                mock.patch.dict("os.environ", env or {}, clear=True):
            try:
                return ci_cost.registry("Owner/repo")("repo/image", "sha256:a"), asked
            except LookupError as error:
                return error, asked

    def refused(self, code):
        return urllib.error.HTTPError("url", code, "refused", {}, None)

    def test_the_owner_is_lower_cased_and_the_read_accepts_an_index(self):
        body, asked = self.read(b'{"manifests": []}')
        self.assertEqual(body, {"manifests": []})
        self.assertEqual(asked[-1].full_url, "https://ghcr.io/v2/owner/repo/image/manifests/sha256:a")
        self.assertIn("image.index", asked[-1].get_header("Accept"))

    def test_gh_token_signs_the_token_request_and_no_token_is_anonymous(self):
        self.assertIsNone(self.read(b"{}")[1][0].get_header("Authorization"))
        self.assertTrue(self.read(b"{}", env={"GH_TOKEN": "x"})[1][0].get_header("Authorization").startswith("Basic "))

    def test_an_expired_registry_token_is_asked_for_again(self):
        body, asked = self.read(self.refused(401), b"{}")
        self.assertEqual(body, {})
        self.assertEqual(sum("/token?" in request.full_url for request in asked), 2)

    def test_a_401_on_a_fresh_token_ends_the_read_rather_than_waiting_out_every_try(self):
        error, asked = self.read(*[self.refused(401)] * ci_cost.TRANSIENT_TRIES)
        self.assertIsInstance(error, LookupError)
        self.assertEqual(sum("/manifests/" in request.full_url for request in asked), 2)

    def test_a_first_401_after_every_transient_try_still_gets_its_fresh_token(self):
        transient = [self.refused(502)] * (ci_cost.TRANSIENT_TRIES - 1)
        body, _ = self.read(*transient, self.refused(401), b"{}")
        self.assertEqual(body, {})

    def test_a_cut_short_body_or_a_dropped_connection_is_retried(self):
        body, _ = self.read(http.client.IncompleteRead(b""), OSError("reset"), self.refused(502), b"{}")
        self.assertEqual(body, {})

    def test_a_refusal_ends_the_read_as_a_lookup_error(self):
        error, asked = self.read(self.refused(404))
        self.assertIsInstance(error, LookupError)
        self.assertEqual(sum("/manifests/" in request.full_url for request in asked), 1)

    def test_a_read_that_keeps_failing_ends_as_a_lookup_error(self):
        error, _ = self.read(*[http.client.IncompleteRead(b"")] * ci_cost.TRANSIENT_TRIES)
        self.assertIsInstance(error, LookupError)


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

    def test_a_well_shaped_day_the_calendar_lacks_is_a_usage_error(self):
        stderr = io.StringIO()
        with self.assertRaises(SystemExit) as ended, contextlib.redirect_stderr(stderr):
            self.output("--window", "A=2026-02-01:2026-02-30")
        self.assertEqual(ended.exception.code, 2)
        self.assertIn("calendar", stderr.getvalue())


if __name__ == "__main__":
    unittest.main()
