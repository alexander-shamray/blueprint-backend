#!/usr/bin/env python3
"""The canary's arithmetic and its verdict, which are the only parts testable.

Nothing in this repository has ever run a canary — there is no cluster, and
`deploy/canary/README.md` says so in its first paragraph. What that makes this
suite is the whole of the gate: the workflow is four commands whose failure is
loud, and every decision it takes comes from here.

The verdict tests are weighted toward the ways an analysis can pass when it
should not. A canary that rolls back wrongly costs a deploy; one that promotes
wrongly ships the release it was meant to catch, and every path to that runs
through a reading nobody took.

    py -3.12 -m unittest discover -s deploy/canary
"""
from __future__ import annotations

import contextlib
import io
import json
import os
import re
import tempfile
import unittest
from pathlib import Path
from unittest import mock

import canary
import read_prometheus

THRESHOLDS = {
    "errorRate": 0.01,
    "latencyP99Seconds": 1.0,
    "errorRateFloor": 0.001,
    "latencyP99FloorSeconds": 0.05,
    "regressionFactor": 2.0,
    "minimumRequests": 100,
}


# The signal definitions, restated only as far as a verdict reads them: which
# absolute thresholds each signal is judged against.
SIGNALS = {
    "http": {"absolute": ["errorRate", "latencyP99Seconds"]},
    "consume": {"absolute": ["errorRate"]},
}
HTTP = {"http": SIGNALS["http"]}
WITH_SAGA = {**SIGNALS, "saga": {"absolute": ["errorRate"]}}


def readings(signals=("http",), **overrides) -> dict:
    """A healthy step, which the tests then break one field at a time."""
    healthy = {
        "canary": {"errorRate": 0.0, "latencyP99Seconds": 0.2, "requests": 5000.0},
        "baseline": {"errorRate": 0.0, "latencyP99Seconds": 0.2, "requests": 90000.0},
    }
    document = {
        track: {signal: dict(values) for signal in signals}
        for track, values in healthy.items()
    }
    for track, per_signal in overrides.items():
        if per_signal is None:
            document.pop(track)
            continue
        for signal, values in per_signal.items():
            if values is None:
                document[track].pop(signal, None)
            else:
                document[track].setdefault(signal, {}).update(values)
    return document


def service_tree(files: dict[str, str]) -> Path:
    """A throwaway src/ holding one service, `Svc.Api`, and the given sources.

    Keys are paths under src/Services/Svc; the host project and Program.cs
    are always written, because the scans find a service by its host.
    """
    root = Path(tempfile.mkdtemp())
    service = root / "src" / "Services" / "Svc"
    host = service / "Svc.Api"
    host.mkdir(parents=True)
    (host / "Svc.Api.csproj").write_text("<Project />\n", encoding="utf-8")
    (host / "Program.cs").write_text("// host\n", encoding="utf-8")
    for relative, text in files.items():
        path = service / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text, encoding="utf-8")
    return root


class WeightTests(unittest.TestCase):
    def test_five_percent_is_not_expressible_at_the_chart_default(self) -> None:
        """§15.3's replicaCount is 3, so one canary pod already serves 25% —
        five times §15.5's first rung. The refusal is the deliverable: rounding
        would have written 5% and shipped five times the blast radius."""
        with self.assertRaises(canary.PlanError) as raised:
            canary.plan(5, stable_replicas=3, overshoot_points=0)

        message = str(raised.exception)
        self.assertIn("19", message, "the refusal must name the count that would work")
        self.assertIn("25.0%", message)

    def test_five_percent_is_expressible_at_nineteen(self) -> None:
        """And 19 + 1 is 20, which is the maxReplicas of every chart that
        autoscales but one — the gateway's is 30, because every external
        request passes through it, and a worker's sets a replica count
        instead of autoscaling (§15.3). The 19 is what the weight costs, and only on
        those charts is it also all the chart allows."""
        result = canary.plan(5, stable_replicas=19, overshoot_points=0)

        self.assertEqual(result["canaryReplicas"], 1)
        self.assertEqual(result["achieved"], 5.0)

    def test_the_needed_count_actually_satisfies_the_check_that_named_it(self) -> None:
        """`plan` and `required_stable` are one derivation used twice, and this
        is why. Two derivations of one figure is how a message ends up naming a
        replica count that the check printing it would still reject."""
        for weight in (1, 2, 5, 10, 20, 25, 33, 50, 75, 99):
            needed = canary.required_stable(weight, overshoot_points=0)
            result = canary.plan(weight, stable_replicas=needed, overshoot_points=0)
            self.assertLessEqual(
                result["achieved"],
                weight,
                f"required_stable({weight}) returned {needed}, which plan() rejects",
            )

    def test_the_achieved_weight_never_exceeds_the_request(self) -> None:
        """The direction that matters. Overshooting is more traffic on the new
        version than anyone asked for; undershooting is a smaller canary."""
        for weight in (5, 10, 25, 50):
            for stable in range(1, 60):
                try:
                    result = canary.plan(weight, stable, overshoot_points=0)
                except canary.PlanError:
                    continue
                self.assertLessEqual(result["achieved"], weight)

    def test_a_canary_is_never_zero_pods(self) -> None:
        """`ceil` of a fraction under one is one, and rounding down is not
        available: zero canary pods is not a canary, it is a step that reports
        a weight and serves none of it."""
        result = canary.plan(1, stable_replicas=99, overshoot_points=0)

        self.assertGreaterEqual(result["canaryReplicas"], 1)

    def test_the_last_rung_retires_the_stable_track(self) -> None:
        """100% is the end of the rollout rather than a weight. Expressed as
        pods it would ask for an infinite canary against a track that is about
        to go away."""
        result = canary.plan(100, stable_replicas=19, overshoot_points=0)

        self.assertTrue(result["final"])
        self.assertEqual(result["stableReplicas"], 0)

    def test_a_tolerance_buys_a_coarser_first_step(self) -> None:
        """The knob exists so the refusal is a decision and not a wall — and
        canary.json sets it to zero, so taking that decision is an edit
        somebody signs."""
        result = canary.plan(5, stable_replicas=3, overshoot_points=20)

        self.assertEqual(result["achieved"], 25.0)

    def test_nonsense_weights_are_refused(self) -> None:
        for weight in (0, -5, 101):
            with self.assertRaises(canary.PlanError):
                canary.plan(weight, stable_replicas=19, overshoot_points=0)


class TagTests(unittest.TestCase):
    """The tag as Helm's parser would read it, not as Kubernetes would.

    `--set-string image.tag="$TAG"` is parsed by `strvals`, where a comma
    separates assignments — so the injection below sets a valid `image.tag`
    AND a registry, and the chart's render-time validation passes because by
    then the tag really is `deadbeef`.
    """

    def test_ordinary_tags_are_accepted(self) -> None:
        for tag in ("deadbeef", "1.2.3", "v1-2-3", "a" * 63,
                    "0123456789abcdef0123456789abcdef01234567"):
            with self.subTest(tag=tag):
                canary.validate_tag(tag)

    def test_a_comma_is_a_second_assignment_and_is_refused(self) -> None:
        """The finding, exactly as reported."""
        with self.assertRaises(canary.PlanError) as raised:
            canary.validate_tag("deadbeef,image.registry=attacker.example")

        self.assertIn("--set-string", str(raised.exception))

    def test_the_other_strvals_metacharacters_are_refused(self) -> None:
        for tag in ("a=b", "a b", "a{b}", "a[0]", "a\\,b", "a\nb"):
            with self.subTest(tag=tag):
                with self.assertRaises(canary.PlanError):
                    canary.validate_tag(tag)

    def test_it_matches_the_chart_rather_than_inventing_an_alphabet(self) -> None:
        """`commerce.tag` refuses these three by name, and a looser rule here
        would pass something the chart then rejects mid-`helm upgrade` — after
        the release has started, which is the failure that validation exists to
        move earlier."""
        for tag in ("Release_1", "release..1", "release.-1"):
            with self.subTest(tag=tag):
                with self.assertRaises(canary.PlanError):
                    canary.validate_tag(tag)

    def test_a_migrator_workload_has_a_tighter_budget(self) -> None:
        """63 is the LABEL's budget and not the Job name's.

        `_migration-job.tpl` derives `<workload>-migrate-<tag>` and refuses it
        past 63 — correctly, and at render time, which on this path is after
        the stable track has been scaled to nineteen. A 63-character tag was
        accepted here and rejected there, which is this preflight failing at
        the one job it has.
        """
        plan = canary.load_plan()
        prefix = canary.migration_prefix("catalog-api", plan)
        self.assertEqual(prefix, "catalog-api-migrate-")

        canary.validate_tag("a" * (63 - len(prefix)), prefix)
        with self.assertRaises(canary.PlanError) as raised:
            canary.validate_tag("a" * (64 - len(prefix)), prefix)

        self.assertIn("job-name", str(raised.exception))

    def test_a_databaseless_workload_has_no_migration_budget(self) -> None:
        """The gateway owns no database (§10.1), so its chart renders no Job
        and its tags are bounded only by the label."""
        plan = canary.load_plan()

        self.assertIsNone(canary.migration_prefix("gateway", plan))
        canary.validate_tag("a" * 63, canary.migration_prefix("gateway", plan))

    def test_the_bff_spends_its_tag_budget_on_a_migration_job(self) -> None:
        """ADR-051's projection gives the BFF a schema, so its chart renders
        the hook and its tag is bounded by the Job's name."""
        plan = canary.load_plan()

        self.assertEqual(canary.migration_prefix("web-bff", plan), "web-bff-migrate-")

    def test_length_and_emptiness(self) -> None:
        with self.assertRaises(canary.PlanError):
            canary.validate_tag("")
        with self.assertRaises(canary.PlanError) as raised:
            canary.validate_tag("a" * 64)

        self.assertIn("63", str(raised.exception))


class VerdictTests(unittest.TestCase):
    def test_a_healthy_step_promotes(self) -> None:
        verdict = canary.analyse(readings(), THRESHOLDS, HTTP)

        self.assertEqual(verdict["decision"], canary.PROMOTE)

    def test_an_absent_series_rolls_back_on_either_track(self) -> None:
        """§15.1 already says this about the k6 SLO run: it "fails on an absent
        series as well as on a breached one". An empty dashboard reads the same
        whether the system is healthy or nobody scraped it.

        Six cases, not three. The baseline's `requests` was the one reading
        fetched on every step and validated by nothing — `read` runs each query
        independently, so a single malformed response can null one metric while
        its neighbours succeed. A contract saying "any absent reading is a
        rollback" is only true if the check is over both tracks.
        """
        for track in ("canary", "baseline"):
            for metric in ("errorRate", "latencyP99Seconds", "requests"):
                with self.subTest(track=track, metric=metric):
                    document = readings(**{track: {"http": {metric: None}}})
                    verdict = canary.analyse(document, THRESHOLDS, HTTP)

                    self.assertEqual(verdict["decision"], canary.ROLLBACK)
                    self.assertIn(metric, verdict["reason"])
                    self.assertIn(track, verdict["reason"])

    def test_an_absent_consume_series_rolls_back(self) -> None:
        """The defect this signal exists for, from the other end: a release
        whose consumers are broken publishes no consume series, and the
        verdict has to read that as a rollback rather than as quiet."""
        document = readings(signals=("http", "consume"))
        document["canary"]["consume"]["requests"] = None

        verdict = canary.analyse(document, THRESHOLDS, SIGNALS)

        self.assertEqual(verdict["decision"], canary.ROLLBACK)
        self.assertIn("consume", verdict["reason"])

    def test_a_whole_signal_missing_from_the_readings_rolls_back(self) -> None:
        """A declared signal nobody fetched is not a signal that passed."""
        document = readings(signals=("http", "consume"), canary={"consume": None})

        verdict = canary.analyse(document, THRESHOLDS, SIGNALS)

        self.assertEqual(verdict["decision"], canary.ROLLBACK)
        self.assertIn("consume", verdict["reason"])

    def test_readings_carrying_an_undeclared_signal_roll_back(self) -> None:
        """`read_prometheus` fetches what the plan declares, so a reading for
        a signal this workload does not declare means the two disagree about
        what is being judged — which is the question, not a detail."""
        document = readings(signals=("http", "consume"))

        verdict = canary.analyse(document, THRESHOLDS, HTTP)

        self.assertEqual(verdict["decision"], canary.ROLLBACK)
        self.assertIn("consume", verdict["reason"])

    def test_a_workload_declaring_no_signal_rolls_back(self) -> None:
        """There is no third verdict, so "nothing to judge" resolves to the
        rollback like every other doubt."""
        verdict = canary.analyse(readings(), THRESHOLDS, {})

        self.assertEqual(verdict["decision"], canary.ROLLBACK)

    def test_a_missing_canary_track_rolls_back(self) -> None:
        verdict = canary.analyse(readings(canary=None), THRESHOLDS, HTTP)

        self.assertEqual(verdict["decision"], canary.ROLLBACK)

    def test_too_little_traffic_rolls_back(self) -> None:
        """Five per cent of a quiet ten minutes can be four requests, and four
        requests cannot tell a 1% error rate from a 0% one. Promoting there is
        promoting on no evidence and reporting a green analysis."""
        verdict = canary.analyse(
            readings(canary={"http": {"requests": 40.0}}), THRESHOLDS, HTTP)

        self.assertEqual(verdict["decision"], canary.ROLLBACK)
        self.assertIn("40", verdict["reason"])

    def test_a_quiet_consume_signal_rolls_back(self) -> None:
        """A declared signal below its minimum is the same silence as an HTTP
        series nobody published, and it is declared precisely so that it
        cannot pass by being empty."""
        document = readings(signals=("http", "consume"))
        document["canary"]["consume"]["requests"] = 3.0

        verdict = canary.analyse(document, THRESHOLDS, SIGNALS)

        self.assertEqual(verdict["decision"], canary.ROLLBACK)
        self.assertIn("consume", verdict["reason"])

    def test_the_minimum_is_the_smallest_sample_that_can_express_the_threshold(self) -> None:
        """minimumRequests is 1/errorRate rather than a number somebody liked:
        below it, one failure is already more than the threshold."""
        self.assertEqual(THRESHOLDS["minimumRequests"], 1 / THRESHOLDS["errorRate"])

    def test_breaching_the_alert_threshold_rolls_back(self) -> None:
        """The absolute check is §13.6's own number. A canary tuned looser
        would promote a release and then page about it."""
        verdict = canary.analyse(
            readings(canary={"http": {"errorRate": 0.02}}), THRESHOLDS, HTTP)

        self.assertEqual(verdict["decision"], canary.ROLLBACK)
        self.assertIn("pages", verdict["reason"])

    def test_a_consume_fault_rate_breach_rolls_back(self) -> None:
        """The fault rate is the one absolute threshold consume keeps, and it
        is the same §13.6 number the HTTP signal is held to."""
        document = readings(signals=("http", "consume"))
        document["canary"]["consume"]["errorRate"] = 0.02

        verdict = canary.analyse(document, THRESHOLDS, SIGNALS)

        self.assertEqual(verdict["decision"], canary.ROLLBACK)
        self.assertIn("consume", verdict["reason"])
        # §13.6's alerts page on the HTTP series only, so a message signal's
        # breach names the threshold without claiming an alert fires.
        self.assertNotIn("pages", verdict["reason"])

    def test_a_failing_saga_is_not_carried_by_healthy_consumers(self) -> None:
        """MassTransit counts a saga's messages on its own instruments, so a
        state machine faulting on every message leaves the consume series
        clean. Judged together, the healthy signal would promote it."""
        document = readings(signals=("http", "consume", "saga"))
        document["canary"]["saga"]["errorRate"] = 0.5

        verdict = canary.analyse(document, THRESHOLDS, WITH_SAGA)

        self.assertEqual(verdict["decision"], canary.ROLLBACK)
        self.assertIn("saga", verdict["reason"])
        self.assertNotIn("consume", verdict["reason"])

    def test_a_quiet_saga_signal_rolls_back(self) -> None:
        document = readings(signals=("http", "consume", "saga"))
        document["canary"]["saga"]["requests"] = 3.0

        verdict = canary.analyse(document, THRESHOLDS, WITH_SAGA)

        self.assertEqual(verdict["decision"], canary.ROLLBACK)
        self.assertIn("saga", verdict["reason"])

    def test_breaching_the_latency_threshold_rolls_back(self) -> None:
        verdict = canary.analyse(
            readings(canary={"http": {"latencyP99Seconds": 1.5}}), THRESHOLDS, HTTP)

        self.assertEqual(verdict["decision"], canary.ROLLBACK)

    def test_consume_is_not_held_to_the_http_latency_threshold(self) -> None:
        """§13.6's Latency alert is about what a user waits for, and no alert
        owns a consume-duration number. A consumer slower than a second is
        judged against the stable track and against nothing else (ADR-047)."""
        document = readings(signals=("http", "consume"))
        for track in ("canary", "baseline"):
            document[track]["consume"]["latencyP99Seconds"] = 4.0

        verdict = canary.analyse(document, THRESHOLDS, SIGNALS)

        self.assertEqual(verdict["decision"], canary.PROMOTE)

    def test_a_consume_duration_regression_still_rolls_back(self) -> None:
        """Dropping the absolute number does not drop the comparison: the
        stable track needs no alert to own it."""
        document = readings(signals=("http", "consume"))
        document["canary"]["consume"]["latencyP99Seconds"] = 4.0
        document["baseline"]["consume"]["latencyP99Seconds"] = 0.5

        verdict = canary.analyse(document, THRESHOLDS, SIGNALS)

        self.assertEqual(verdict["decision"], canary.ROLLBACK)
        self.assertIn("stable track", verdict["reason"])

    def test_a_regression_inside_the_threshold_still_rolls_back(self) -> None:
        """§15.5 says "regresses", not "breaches". A canary at four times the
        stable track's error rate is a bad release even while both are under
        the number that pages."""
        verdict = canary.analyse(
            readings(canary={"http": {"errorRate": 0.008}},
                     baseline={"http": {"errorRate": 0.001}}),
            THRESHOLDS,
            HTTP,
        )

        self.assertEqual(verdict["decision"], canary.ROLLBACK)
        self.assertIn("stable track", verdict["reason"])

    def test_noise_under_the_floor_does_not_roll_back(self) -> None:
        """Without the floor every quiet service rolls back for ever: a
        baseline of 0.0001 against a canary of 0.0004 is four times worse and
        is two requests."""
        verdict = canary.analyse(
            readings(canary={"http": {"errorRate": 0.0004}},
                     baseline={"http": {"errorRate": 0.0001}}),
            THRESHOLDS,
            HTTP,
        )

        self.assertEqual(verdict["decision"], canary.PROMOTE)

    def test_a_missing_baseline_rolls_back(self) -> None:
        """The stable track serves the majority of traffic at every rung, and
        since the error-rate numerator is coalesced a query returns nothing
        only when the denominator is empty — no requests at all. Skipping the
        regression check there would remove it at exactly the moment the
        monitoring was failing on the larger half of the traffic. The rule is
        uniform: any absent reading is a rollback."""
        verdict = canary.analyse(
            readings(baseline={"http": {"errorRate": None, "latencyP99Seconds": None}}),
            THRESHOLDS,
            HTTP,
        )

        self.assertEqual(verdict["decision"], canary.ROLLBACK)
        self.assertIn("baseline", verdict["reason"])

    def test_the_reason_survives_every_verdict(self) -> None:
        """The rollout prints this and nothing else. A decision with an empty
        reason is a rollback nobody can act on."""
        documents = (
            readings(),
            readings(canary={"http": {"errorRate": 0.5}}),
            readings(canary=None),
        )
        for document in documents:
            self.assertTrue(
                canary.analyse(document, THRESHOLDS, HTTP)["reason"].strip())


class PlanDocumentTests(unittest.TestCase):
    """The shipped canary.json, against the repository it deploys."""

    def setUp(self) -> None:
        self.document = canary.load_plan()

    def test_the_shipped_plan_is_consistent(self) -> None:
        self.assertEqual(canary.check(self.document), [])

    def test_a_workload_key_that_is_not_a_release_name_fails_the_plan(self) -> None:
        """Two shells read the key as one word — `deploy.yml`'s `helm upgrade`
        and `realm.yml`'s loop, which also makes a file name of it. A space is
        two releases, a glob expands against the checkout, a slash is a path
        (review round eleven). Held here, before either job sees it; the
        shipped keys are the positive control."""
        # The trailing newline is the case `match` plus `$` lets through and
        # `fullmatch` does not (review round twelve).
        for bad in ("two words", "cat*", "../etc", "Catalog", "-lead", "a" * 54, "", "catalog-api\n"):
            with self.subTest(key=bad):
                document = json.loads(json.dumps(self.document))
                document["workloads"][bad] = dict(document["workloads"]["catalog-api"])
                failures = canary.check(document)
                self.assertTrue(
                    any("is not a Helm release name" in f and repr(bad) in f for f in failures),
                    failures,
                )
        for good in ("catalog-api", "a", "x" * 53, "a-1"):
            with self.subTest(key=good):
                self.assertIsNotNone(canary.RELEASE_NAME.fullmatch(good))

    def test_workloads_lists_the_plan_and_not_its_comments(self) -> None:
        """`realm.yml`'s scheduled job loops over this output (ADR-043).

        One name per line and nothing else on stdout, because a shell reads
        it; and no `$comment` key, because that would be a release nothing
        installed handed to `helm get values`.
        """
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            code = canary.main(["canary", "workloads"])
        self.assertEqual(code, 0)
        self.assertEqual(
            out.getvalue().splitlines(),
            list(canary.entries(self.document["workloads"])),
        )
        self.assertNotIn("$comment", out.getvalue())
        self.assertIn("catalog-api", out.getvalue().splitlines())

    def test_the_ladder_is_the_one_the_chapter_cites(self) -> None:
        """§15.5's numbers: 5, 25, 50, 100, ten minutes before each step up. Not trimmed to
        what three replicas can express — that is what the refusal is for."""
        self.assertEqual(
            [step["weight"] for step in self.document["steps"]],
            [5, 25, 50, 100],
        )
        for step in self.document["steps"][:-1]:
            self.assertEqual(step["dwellMinutes"], 10)

    def test_every_threshold_analyse_reads_is_present(self) -> None:
        """`analyse` indexes these rather than `.get`-ing them, so a missing
        key is a KeyError mid-rollout with a canary already serving traffic.

        Every signal canary.py defines is read, and the verdict has to be a
        promotion: an early rollback returns before any threshold is indexed,
        and would pass this vacuously."""
        verdict = canary.analyse(
            readings(signals=tuple(canary.SIGNALS)),
            canary.entries(self.document["thresholds"]),
            canary.SIGNALS,
        )

        self.assertEqual(verdict["decision"], canary.PROMOTE, verdict["reason"])

    def test_the_plan_holds_no_query_text(self) -> None:
        """The queries are code; a plan that carries some again is refused
        rather than silently ignored beside the templates that run."""
        self.assertNotIn("signals", self.document)
        for key in ("signals", "queries"):
            with self.subTest(key=key):
                document = json.loads(json.dumps(self.document))
                document[key] = {}

                failures = canary.check(document)

                self.assertTrue(any(repr(key) in f for f in failures), failures)


def selectors(expression: str) -> list[tuple[str, list[tuple[str, str, str]]]]:
    """Each series selector in a rendered query, with its matchers."""
    return [
        (series, re.findall(r'([a-z_]+)(=~|!~|!=|=)"([^"]*)"', braces))
        for series, braces in re.findall(r"([a-z_]+)\{([^}]*)\}", expression)
    ]


class TemplateTests(unittest.TestCase):
    """The PromQL canary.py generates, one template per signal and role.

    The golden strings are the point: each is the whole meaning of its role,
    so any change to what a query measures is a change to one of them.
    """

    SERVICE, TRACK, WINDOW = "Catalog.Api", "canary", "10m"
    WHERE = 'service_name="Catalog.Api", deployment_track="canary"'
    PROBES = 'http_route!~"/health/live|/health/ready|/health/startup"'
    HTTP = "http_server_request_duration_seconds"

    def rendered(self, signal: str) -> dict[str, str]:
        return {
            role: template
            .replace("$SERVICE", self.SERVICE)
            .replace("$TRACK", self.TRACK)
            .replace("$WINDOW", self.WINDOW)
            for role, template in canary.queries(signal).items()
        }

    def test_the_http_queries(self) -> None:
        where = f"{self.WHERE}, {self.PROBES}"
        self.assertEqual(self.rendered("http"), {
            "errorRate":
                f"(sum(rate({self.HTTP}_count{{{where}, "
                'http_response_status_code=~"5.."}[10m])) or vector(0)) '
                f"/ sum(rate({self.HTTP}_count{{{where}}}[10m]))",
            "latencyP99Seconds":
                f"histogram_quantile(0.99, sum by (le) "
                f"(rate({self.HTTP}_bucket{{{where}}}[10m])))",
            "requests": f"sum(increase({self.HTTP}_count{{{where}}}[10m]))",
        })

    def test_the_message_queries(self) -> None:
        for signal in ("consume", "saga"):
            with self.subTest(signal=signal):
                series = f"messaging_masstransit_{signal}"
                self.assertEqual(self.rendered(signal), {
                    "errorRate":
                        f"(sum(rate({series}_errors_ea_total{{{self.WHERE}}}[10m])) "
                        f"or vector(0)) / sum(rate({series}_ea_total{{{self.WHERE}}}[10m]))",
                    "latencyP99Seconds":
                        f"histogram_quantile(0.99, sum by (le) (rate("
                        f"{series}_duration_milliseconds_bucket{{{self.WHERE}}}[10m]))) / 1000",
                    "requests": f"sum(increase({series}_ea_total{{{self.WHERE}}}[10m]))",
                })

    def test_every_selector_carries_exactly_the_workload_and_track(self) -> None:
        """Equality on both, once each, and nothing else but the probe
        exclusion on http and the server-error class on its numerator."""
        allowed = {
            ("service_name", "=", "$SERVICE"),
            ("deployment_track", "=", "$TRACK"),
        }
        probes = ("http_route", "!~", canary.probe_exclusion())
        server_errors = ("http_response_status_code", "=~", "5..")
        for signal in canary.SIGNALS:
            for role, template in canary.queries(signal).items():
                for series, matchers in selectors(template):
                    with self.subTest(signal=signal, role=role, series=series):
                        self.assertEqual(len(matchers), len(set(matchers)))
                        self.assertTrue(allowed <= set(matchers))
                        extra = set(matchers) - allowed
                        if signal == "http":
                            self.assertIn(probes, extra)
                            extra.discard(probes)
                            if role == "errorRate" and server_errors in extra:
                                extra.discard(server_errors)
                        self.assertEqual(extra, set())

    def test_only_the_http_numerator_selects_server_errors(self) -> None:
        template = canary.queries("http")["errorRate"]
        numerator, denominator = template.split(" / ", 1)

        self.assertIn('http_response_status_code=~"5.."', numerator)
        self.assertNotIn("http_response_status_code", denominator)

    def test_the_templates_carry_all_three_substitutions(self) -> None:
        """A template that kept `$TRACK` literal matches no series, and an
        absent series rolls back — a rollout that can only ever fail."""
        for signal in canary.SIGNALS:
            for role, template in canary.queries(signal).items():
                with self.subTest(signal=signal, role=role):
                    for placeholder in ("$SERVICE", "$TRACK", "$WINDOW"):
                        self.assertIn(placeholder, template)

    def test_only_the_error_rate_numerator_is_coalesced(self) -> None:
        """A clean canary matches no numerator series, and PromQL carries the
        empty vector through the division; coalescing a count or a quantile
        would turn nobody-scraped-this into a healthy zero."""
        for signal in canary.SIGNALS:
            for role, template in canary.queries(signal).items():
                with self.subTest(signal=signal, role=role):
                    if role == "errorRate":
                        self.assertTrue(template.startswith("(sum("))
                        self.assertIn(") or vector(0)) / ", template)
                    else:
                        self.assertNotIn("or vector(0)", template)

    def test_the_templates_read_the_track_label_and_not_the_version(self) -> None:
        """service_version is not a discriminator: every build reports the
        same one, so a query on it compares a release against itself."""
        for signal in canary.SIGNALS:
            for template in canary.queries(signal).values():
                self.assertIn("deployment_track", template)
                self.assertNotIn("service_version", template)

    def test_the_message_templates_carry_no_route_selector(self) -> None:
        """A message has no route, so a selector on one would match nothing
        and roll every consume- or saga-judged canary back."""
        for signal in ("consume", "saga"):
            for template in canary.queries(signal).values():
                self.assertNotIn("http_route", template)

    def test_the_signals_hold_adr_047s_thresholds(self) -> None:
        """http is held to both of §13.6's numbers; a message signal's
        duration is judged against the stable track only."""
        self.assertEqual(
            {name: set(signal["absolute"]) for name, signal in canary.SIGNALS.items()},
            {"http": {"errorRate", "latencyP99Seconds"},
             "consume": {"errorRate"}, "saga": {"errorRate"}},
        )
        for signal in canary.SIGNALS.values():
            self.assertTrue(set(signal["absolute"]) <= set(canary.ABSOLUTE))

    def test_every_series_the_templates_read_is_vouched_for(self) -> None:
        self.assertEqual(
            canary._metrics_are_vouched_for(
                sorted(canary.series_read()), canary.ROOT, canary.ROOT), [])
        self.assertIn("messaging_masstransit_saga_errors_ea_total", canary.series_read())


class SignalTests(unittest.TestCase):
    """What each workload is judged on, and what the gate holds that to."""

    def setUp(self) -> None:
        self.document = canary.load_plan()

    def workload(self, name: str) -> dict:
        return canary.entries(self.document["workloads"])[name]

    def test_every_workload_declares_a_signal_it_actually_receives(self) -> None:
        """Inventory is the sharp case: its HTTP surface is the admin route,
        and its commands and events arrive on the broker, so HTTP alone judges
        none of its work. The edge consumes nothing."""
        declared = {
            name: set(entry.get("signals", []))
            for name, entry in canary.entries(self.document["workloads"]).items()
        }

        self.assertEqual(declared["inventory-api"], {"consume"})
        self.assertEqual(declared["payments-api"], {"consume"})
        self.assertEqual(declared["ordering-api"], {"http", "consume", "saga"})
        self.assertEqual(declared["gateway"], {"http"})
        self.assertEqual(declared["web-bff"], {"http", "consume"})
        self.assertEqual(declared["catalog-api"], {"http"})

    def test_a_workload_with_no_signal_fails_the_plan(self) -> None:
        document = json.loads(json.dumps(self.document))
        document["workloads"]["gateway"]["signals"] = []

        failures = canary.check(document)

        self.assertTrue(any("declares no signal" in f for f in failures), failures)

    def test_a_workload_declaring_an_unknown_signal_fails(self) -> None:
        document = json.loads(json.dumps(self.document))
        document["workloads"]["gateway"]["signals"] = ["grpc"]

        failures = canary.check(document)

        self.assertTrue(any("'grpc'" in f for f in failures), failures)

class ConsumerScanTests(unittest.TestCase):
    """The scan that decides which workloads owe a consume signal."""

    def setUp(self) -> None:
        self.document = canary.load_plan()

    def test_the_scan_finds_the_services_it_judges(self) -> None:
        """The subject before the assertion. A scan that found no consumer
        would let every workload off, which is the state this closes."""
        found = {
            name: canary.has_consumers(entry["serviceName"])
            for name, entry in canary.entries(self.document["workloads"]).items()
        }

        self.assertTrue(found["inventory-api"])
        self.assertTrue(found["ordering-api"])
        self.assertTrue(found["catalog-api"])
        self.assertTrue(found["payments-api"])
        self.assertFalse(found["gateway"])
        self.assertTrue(found["web-bff"])

    def test_a_consumer_service_declaring_no_consume_signal_is_named(self) -> None:
        document = json.loads(json.dumps(self.document))
        document["workloads"]["inventory-api"]["signals"] = ["http"]

        failures = canary.check(document)

        self.assertTrue(
            any("inventory-api" in f and "consume" in f for f in failures), failures)

    def test_an_argued_exemption_is_accepted(self) -> None:
        """Catalog's is the shipped one, and the plan passing is the proof."""
        self.assertIn("consumeExemption", self.workload_catalog())
        self.assertEqual(canary.check(self.document), [])

    def workload_catalog(self) -> dict:
        return canary.entries(self.document["workloads"])["catalog-api"]

    def test_an_empty_exemption_does_not_count_as_an_argument(self) -> None:
        document = json.loads(json.dumps(self.document))
        document["workloads"]["catalog-api"]["consumeExemption"] = "  "

        failures = canary.check(document)

        self.assertTrue(any("catalog-api" in f for f in failures), failures)

    def test_an_exemption_on_a_workload_with_no_consumers_fails(self) -> None:
        """Both directions, as the outbox exemption one tree over: a stale
        exemption is a claim nobody rechecks."""
        document = json.loads(json.dumps(self.document))
        document["workloads"]["gateway"]["consumeExemption"] = "no reason at all"

        failures = canary.check(document)

        self.assertTrue(any("gateway" in f for f in failures), failures)

    def test_a_message_signal_on_a_service_that_registers_nothing_fails(self) -> None:
        """The other direction: a declared consume or saga signal on a
        service with nothing to measure reads series that cannot exist, so
        every rung rolls back on a release that did nothing wrong."""
        for signal in ("consume", "saga"):
            with self.subTest(signal=signal):
                document = json.loads(json.dumps(self.document))
                document["workloads"]["gateway"]["signals"].append(signal)

                failures = canary.check(document)

                self.assertTrue(
                    any("gateway" in f and signal in f for f in failures), failures)

    def test_an_exemption_beside_a_declared_consume_signal_fails(self) -> None:
        document = json.loads(json.dumps(self.document))
        document["workloads"]["inventory-api"]["consumeExemption"] = "unneeded"

        failures = canary.check(document)

        self.assertTrue(any("inventory-api" in f for f in failures), failures)


class HttpExemptionTests(unittest.TestCase):
    """Every workload is an ASP.NET Core host, so leaving its HTTP traffic
    unjudged is a decision the plan argues rather than an omission."""

    def setUp(self) -> None:
        self.document = canary.load_plan()

    def test_a_workload_without_http_and_without_an_argument_fails(self) -> None:
        document = json.loads(json.dumps(self.document))
        document["workloads"]["inventory-api"].pop("httpExemption", None)

        failures = canary.check(document)

        self.assertTrue(
            any("inventory-api" in f and "httpExemption" in f for f in failures), failures)

    def test_an_empty_http_exemption_fails(self) -> None:
        document = json.loads(json.dumps(self.document))
        document["workloads"]["inventory-api"]["httpExemption"] = "  "

        failures = canary.check(document)

        self.assertTrue(any("inventory-api" in f for f in failures), failures)

    def test_an_http_exemption_beside_a_declared_http_signal_fails(self) -> None:
        document = json.loads(json.dumps(self.document))
        document["workloads"]["gateway"]["httpExemption"] = "unneeded"

        failures = canary.check(document)

        self.assertTrue(any("gateway" in f for f in failures), failures)


class BulkRegistrationTests(unittest.TestCase):
    """Every MassTransit 8.5.3 registration form, singly and in bulk.

    A service that registers its consumers by namespace owes the same signal
    as one that names them, and a scan that knew only the generic single
    form would let it off.
    """

    CONSUMER_FORMS = (
        "x.AddConsumer<OrderConsumer>();",
        "x.AddConsumer(typeof(OrderConsumer));",
        "x.AddConsumers(typeof(OrderConsumer).Assembly);",
        "x.AddConsumersFromNamespaceContaining<OrderConsumer>();",
        "x.AddFutureRequestConsumer<OrderFuture, OrderRequest, Order>();",
    )
    SAGA_FORMS = (
        "x.AddSaga<OrderSaga>();",
        "x.AddSagas(typeof(OrderSaga).Assembly);",
        "x.AddSagasFromNamespaceContaining<OrderSaga>();",
        "x.AddSagaStateMachine<OrderStateMachine, OrderState>();",
        "x.AddSagaStateMachines(typeof(OrderStateMachine).Assembly);",
        "x.AddSagaStateMachinesFromNamespaceContaining<OrderStateMachine>();",
        "x.AddJobSagaStateMachines();",
        "x.AddFuture<OrderFuture>();",
        "x.AddFutures(typeof(OrderFuture).Assembly);",
        "x.AddFuturesFromNamespaceContaining<OrderFuture>();",
    )

    def test_every_consumer_form_owes_consume_and_not_saga(self) -> None:
        for form in self.CONSUMER_FORMS:
            with self.subTest(form=form):
                root = service_tree({"Svc.Infrastructure/Bus.cs": form + "\n"})

                self.assertTrue(canary.has_consumers("Svc.Api", root))
                self.assertFalse(canary.has_sagas("Svc.Api", root))

    def test_every_saga_form_owes_saga_and_not_consume(self) -> None:
        for form in self.SAGA_FORMS:
            with self.subTest(form=form):
                root = service_tree({"Svc.Infrastructure/Bus.cs": form + "\n"})

                self.assertTrue(canary.has_sagas("Svc.Api", root))
                self.assertFalse(canary.has_consumers("Svc.Api", root))

    def test_a_saga_repository_or_a_commented_registration_owes_nothing(self) -> None:
        """The negative control: a repository is not a saga, and a comment
        is not a registration."""
        root = service_tree({"Svc.Infrastructure/Bus.cs": (
            "x.AddSagaRepository<OrderState>();\n"
            "// x.AddConsumersFromNamespaceContaining<OrderConsumer>();\n"
        )})

        self.assertFalse(canary.has_consumers("Svc.Api", root))
        self.assertFalse(canary.has_sagas("Svc.Api", root))

    def test_a_comment_marker_inside_a_string_hides_nothing(self) -> None:
        """A URL is a string holding `//`, and a scan that read it as a
        comment would lose the rest of the line."""
        root = service_tree({"Svc.Infrastructure/Bus.cs": (
            'var host = "rabbitmq://broker"; x.AddConsumer<OrderConsumer>();\n'
            'var glob = @"/*"; x.AddSaga<OrderState>(); var end = "*/";\n'
        )})

        self.assertTrue(canary.has_consumers("Svc.Api", root))
        self.assertTrue(canary.has_sagas("Svc.Api", root))

    def test_a_registration_spelled_inside_a_string_is_not_one(self) -> None:
        """A string is text, not a call: a log message or a raw literal that
        names a registration owes no signal."""
        root = service_tree({"Svc.Infrastructure/Bus.cs": (
            'log.Info("calling x.AddConsumer<OrderConsumer>() next");\n'
            'var doc = """\n    x.AddSaga<OrderState>();\n    """;\n'
        )})

        self.assertFalse(canary.has_consumers("Svc.Api", root))
        self.assertFalse(canary.has_sagas("Svc.Api", root))

    def test_a_raw_string_closes_on_its_own_delimiter(self) -> None:
        """A raw literal opened with four quotes may hold three, so it ends
        at the matching run of four, and nothing inside it is a call."""
        for prefix in ("", "$$"):
            with self.subTest(prefix=prefix):
                root = service_tree({"Svc.Infrastructure/Bus.cs": (
                    f'var doc = {prefix}""""\n    a """ b x.AddConsumer<OrderConsumer>();\n'
                    '    app.MapHealthChecks("/orders");\n    """";\n'
                    'app.MapHealthChecks("/health/live");\n'
                )})

                self.assertFalse(canary.has_consumers("Svc.Api", root))
                self.assertEqual(canary.health_routes(root), {"/health/live"})


class SagaScanTests(unittest.TestCase):
    """The scan that decides which workloads owe a saga signal."""

    def setUp(self) -> None:
        self.document = canary.load_plan()

    def test_the_scan_finds_the_services_it_judges(self) -> None:
        """Ordering's fulfilment saga is the one state machine, and a scan
        that found none would let it off."""
        found = {
            name: canary.has_sagas(entry["serviceName"])
            for name, entry in canary.entries(self.document["workloads"]).items()
        }

        self.assertTrue(found["ordering-api"])
        for name in ("catalog-api", "inventory-api", "payments-api", "gateway", "web-bff"):
            self.assertFalse(found[name], name)

    def test_a_saga_is_not_a_consumer_to_the_consume_scan(self) -> None:
        """The two scans are split because the two signals are: a service
        whose only registration is a saga owes saga, not consume."""
        self.assertNotRegex(".AddSagaStateMachine<S, T>()", canary.CONSUMER_REGISTRATION)
        self.assertRegex(".AddSagaStateMachine<S, T>()", canary.SAGA_REGISTRATION)
        self.assertRegex(".AddConsumer<C>()", canary.CONSUMER_REGISTRATION)
        self.assertNotRegex(".AddConsumer<C>()", canary.SAGA_REGISTRATION)

    def test_a_saga_service_declaring_no_saga_signal_is_named(self) -> None:
        document = json.loads(json.dumps(self.document))
        document["workloads"]["ordering-api"]["signals"] = ["http", "consume"]

        failures = canary.check(document)

        self.assertTrue(
            any("ordering-api" in f and "saga" in f for f in failures), failures)

    def test_an_argued_saga_exemption_is_accepted(self) -> None:
        document = json.loads(json.dumps(self.document))
        document["workloads"]["ordering-api"]["signals"] = ["http", "consume"]
        document["workloads"]["ordering-api"]["sagaExemption"] = "an argument"

        self.assertEqual(canary.check(document), [])

    def test_an_empty_saga_exemption_does_not_count_as_an_argument(self) -> None:
        document = json.loads(json.dumps(self.document))
        document["workloads"]["ordering-api"]["signals"] = ["http", "consume"]
        document["workloads"]["ordering-api"]["sagaExemption"] = "  "

        failures = canary.check(document)

        self.assertTrue(any("ordering-api" in f for f in failures), failures)

    def test_a_saga_exemption_on_a_workload_with_no_saga_fails(self) -> None:
        document = json.loads(json.dumps(self.document))
        document["workloads"]["inventory-api"]["sagaExemption"] = "no saga here"

        failures = canary.check(document)

        self.assertTrue(any("inventory-api" in f for f in failures), failures)

    def test_a_saga_exemption_beside_a_declared_saga_signal_fails(self) -> None:
        document = json.loads(json.dumps(self.document))
        document["workloads"]["ordering-api"]["sagaExemption"] = "unneeded"

        failures = canary.check(document)

        self.assertTrue(any("ordering-api" in f for f in failures), failures)


class HealthRouteTests(unittest.TestCase):
    """The probe exclusion, against the routes Common.Web actually maps."""

    def setUp(self) -> None:
        self.document = canary.load_plan()

    def test_the_scan_finds_the_routes_it_is_checking(self) -> None:
        """A scan that found nothing would certify any selector at all."""
        routes = canary.health_routes()

        self.assertEqual(routes, {"/health/live", "/health/ready", "/health/startup"})

    def test_a_route_that_is_not_a_literal_fails_the_plan_by_location(self) -> None:
        """A route read through a constant, built by concatenation or spelled
        with an escape is one the scan cannot see as the host maps it, and a
        probe it cannot see is traffic again. The XML doc mention is the
        negative control: a comment is not a call site; and a URL before a
        call on its line does not hide the call."""
        root = service_tree({"Svc.Api/Health.cs": (
            "/// Maps <c>MapHealthChecks</c> for the probes.\n"
            'app.MapHealthChecks("/health/live");\n'
            "app.MapHealthChecks(ReadyPath, options);\n"
            'app.MapHealthChecks("/health/" + "startup", options);\n'
            'app.MapHealthChecks("/health/\\u0072eady");\n'
            'var u = "http://docs"; app.MapHealthChecks("/probe");\n'
        )})

        failures = canary._probe_routes_are_readable(root)

        self.assertEqual(canary.health_routes(root), {"/health/live", "/probe"})
        self.assertEqual(len(failures), 3, failures)
        self.assertTrue(any("Health.cs:3" in f for f in failures), failures)
        self.assertTrue(any("Health.cs:4" in f for f in failures), failures)
        self.assertTrue(any("Health.cs:5" in f for f in failures), failures)
        self.assertFalse(any("Health.cs:1" in f for f in failures), failures)

    def test_a_call_spelled_inside_a_string_maps_no_route(self) -> None:
        """Excluding a route nobody maps as a probe would hide real traffic
        on it, so text inside a string is not a call site."""
        root = service_tree({"Svc.Api/Health.cs": (
            'app.MapHealthChecks("/health/live");\n'
            'var hint = "try app.MapHealthChecks(\\"/orders\\")";\n'
        )})

        self.assertEqual(canary.health_routes(root), {"/health/live"})
        self.assertEqual(canary.unresolved_health_routes(root), [])

    def test_the_real_call_sites_are_all_literals(self) -> None:
        self.assertEqual(canary.unresolved_health_routes(canary.ROOT), [])

    def test_the_exclusion_covers_every_mapped_probe_route(self) -> None:
        """The library chart probes every five and ten seconds, which alone
        passes minimumRequests inside one dwell — so unfiltered, a canary that
        served no real request is judged healthy on its own probes. PromQL
        anchors the regex at both ends, as `fullmatch` does."""
        exclusion = canary.probe_exclusion()

        for route in canary.health_routes():
            with self.subTest(route=route):
                self.assertIsNotNone(re.fullmatch(exclusion, route))
        self.assertIsNone(re.fullmatch(exclusion, "/api/products"))

    def test_a_route_mapped_later_is_excluded_without_an_edit(self) -> None:
        root = service_tree({"Svc.Api/Health.cs": (
            'app.MapHealthChecks("/health/live");\n'
            'app.MapHealthChecks("/probe/deep");\n'
        )})

        exclusion = canary.probe_exclusion(root)

        self.assertIsNotNone(re.fullmatch(exclusion, "/probe/deep"))
        self.assertIn(f'http_route!~"{exclusion}"', canary.queries("http", root)["requests"])

    def test_no_http_query_is_rendered_over_an_unreadable_route(self) -> None:
        """A partial exclusion counts the probes it misses as traffic, so the
        template is refused rather than rendered short."""
        root = service_tree({"Svc.Api/Health.cs": (
            'app.MapHealthChecks("/health/live");\n'
            "app.MapHealthChecks(ReadyPath);\n"
        )})

        with self.assertRaises(canary.PlanError) as raised:
            canary.queries("http", root)

        self.assertIn("Health.cs:2", str(raised.exception))

    def test_no_http_query_is_rendered_when_the_scan_finds_no_route(self) -> None:
        with self.assertRaises(canary.PlanError):
            canary.queries("http", service_tree({}))


class VouchingTests(unittest.TestCase):
    """Check 5, over a series no loaded alert reads."""

    ALERTS = "  - alert: X\n    expr: http_server_request_duration_seconds_count\n"

    def root_with(self, registration: str) -> Path:
        """A throwaway tree holding just the two files check 5 reads."""
        tmp = Path(tempfile.mkdtemp())
        alerts = tmp / "deploy" / "observability" / "alerts"
        alerts.mkdir(parents=True)
        (alerts / "platform-alerts.yaml").write_text(self.ALERTS, encoding="utf-8")
        web = tmp / "src" / "BuildingBlocks" / "Common.Web"
        web.mkdir(parents=True)
        (web / "ObservabilityExtensions.cs").write_text(registration, encoding="utf-8")
        return tmp

    def test_a_meter_registration_vouches_for_a_declared_series(self) -> None:
        root = self.root_with('.AddMeter("MassTransit")\n')

        failures = canary._metrics_are_vouched_for(
            ["messaging_masstransit_consume_ea_total"], root, root)

        self.assertEqual(failures, [])

    def test_removing_the_registration_unvouches_it(self) -> None:
        """The declaration is not a free pass: what makes the series real is
        the meter this platform collects, so deleting that line goes red."""
        root = self.root_with('.AddMeter("Commerce.Messaging")\n')

        failures = canary._metrics_are_vouched_for(
            ["messaging_masstransit_consume_ea_total"], root, root)

        self.assertTrue(any("MassTransit" in f for f in failures), failures)

    def test_a_registration_left_in_a_comment_or_a_string_vouches_for_nothing(self) -> None:
        """What collects the series is a live call, so the text of one that
        was commented out, or quoted, is not a registration."""
        for registration in (
            '// .AddMeter("MassTransit")\n',
            '/* .AddMeter("MassTransit") */\n',
            'var note = ".AddMeter(\\"MassTransit\\")";\n',
        ):
            with self.subTest(registration=registration):
                root = self.root_with(registration)

                failures = canary._metrics_are_vouched_for(
                    ["messaging_masstransit_consume_ea_total"], root, root)

                self.assertTrue(any("MassTransit" in f for f in failures), failures)

    def test_a_series_the_instrument_does_not_export_is_refused(self) -> None:
        """Each spelling is the instrument's own exported name or nothing: a
        counter exports no `_count`, and a millisecond histogram no
        `_seconds` bucket, however near the prefix."""
        root = self.root_with('.AddMeter("MassTransit")\n')
        for metric in (
            "messaging_masstransit_consume_count_total",
            "messaging_masstransit_consume_duration_seconds_bucket",
            "messaging_masstransit_consume_total",
        ):
            with self.subTest(metric=metric):
                failures = canary._metrics_are_vouched_for([metric], root, root)

                self.assertTrue(failures)

    def test_every_table_entry_is_its_instruments_exported_spelling(self) -> None:
        """The table is data a reviewer reads, so each key is derived again
        here from its instrument, kind and unit, and each instrument is one
        the pinned version declares."""
        for series, (meter, instrument, kind, unit) in canary.EXPORTED_SERIES.items():
            with self.subTest(series=series):
                self.assertIn(instrument, canary.METER_INSTRUMENTS[meter])
                self.assertIn(series, canary.exported_series(instrument, kind, unit))

    def test_an_undeclared_series_no_alert_reads_still_fails(self) -> None:
        root = self.root_with('.AddMeter("MassTransit")\n')

        failures = canary._metrics_are_vouched_for(
            ["messaging_masstransit_invented_total"], root, root)

        self.assertTrue(failures)

    def test_an_alert_vouches_for_the_series_it_reads(self) -> None:
        root = self.root_with('.AddMeter("MassTransit")\n')

        failures = canary._metrics_are_vouched_for(
            ["http_server_request_duration_seconds_bucket"], root, root)

        self.assertEqual(failures, [])


class VerifiedVersionTests(unittest.TestCase):
    """The MassTransit pin against the version the series table was read from."""

    def root_with(self, version: str) -> Path:
        tmp = Path(tempfile.mkdtemp())
        (tmp / "Directory.Packages.props").write_text(
            f'<Project>\n  <ItemGroup>\n    <PackageVersion Include="MassTransit" '
            f'Version="{version}" />\n  </ItemGroup>\n</Project>\n',
            encoding="utf-8",
        )
        return tmp

    def test_the_verified_version_passes(self) -> None:
        root = self.root_with(canary.MASSTRANSIT_VERIFIED)

        self.assertEqual(canary._masstransit_pin_is_verified(root), [])

    def test_an_upgrade_fails_until_the_table_is_reverified(self) -> None:
        """An upgrade can rename or re-unit an instrument, and every
        consume-judged rung would then read an absent series."""
        root = self.root_with("99.0.0")

        failures = canary._masstransit_pin_is_verified(root)

        self.assertTrue(
            any("99.0.0" in f and canary.MASSTRANSIT_VERIFIED in f for f in failures),
            failures)

    def test_a_missing_pin_fails(self) -> None:
        tmp = Path(tempfile.mkdtemp())
        (tmp / "Directory.Packages.props").write_text("<Project />\n", encoding="utf-8")

        self.assertTrue(canary._masstransit_pin_is_verified(tmp))

    def test_the_real_repository_passes(self) -> None:
        self.assertEqual(canary._masstransit_pin_is_verified(canary.ROOT), [])

    def test_the_pin_is_a_declared_input(self) -> None:
        """So deploy.yml's triggers run the gate when the pin moves."""
        self.assertIn("Directory.Packages.props", canary.SOURCE_INPUTS)


class DescriptorReadTests(unittest.TestCase):
    """Every descriptor the workflow reads, the canary and the smoke run read.

    The subject is the text deploy.yml and smoke.sh run.
    """

    def setUp(self) -> None:
        self.workloads = canary.load_plan()["workloads"]

    def _read(self, workflow: str | None = None, smoke: str | None = None,
              workloads: dict | None = None) -> tuple[dict, list[str]]:
        """What each reader takes in, and check 8's verdict, over these."""
        workloads = self.workloads if workloads is None else workloads
        with tempfile.TemporaryDirectory() as tmp:
            paths = []
            for name, text, real in (("deploy.yml", workflow, canary.WORKFLOW), ("smoke.sh", smoke, canary.SMOKE)):
                path = Path(tmp) / name
                path.write_text(real.read_text(encoding="utf-8") if text is None else text, encoding="utf-8")
                paths.append(path)
            return canary.descriptors_read(workloads, *paths), canary._descriptors_agree(workloads, *paths)

    def test_every_descriptor_the_workflow_reads_the_canary_and_the_smoke_run_read(self) -> None:
        read = canary.descriptors_read(self.workloads)

        self.assertTrue(read["workflow"], "deploy.yml reads no descriptor, so the comparison below is vacuous")
        self.assertLessEqual(
            read["workflow"], read["smoke"],
            f"deploy.yml reads {sorted(read['workflow'] - read['smoke'])}, which smoke.sh does not render")
        self.assertEqual(canary._descriptors_agree(self.workloads), [])

    def test_a_descriptor_without_its_smoke_block_is_one_the_smoke_run_cannot_read(self) -> None:
        workloads = json.loads(json.dumps(self.workloads))
        del workloads["gateway"]["smoke"]

        read, failures = self._read(workloads=workloads)

        self.assertIn("gateway", read["workflow"])
        self.assertNotIn("gateway", read["smoke"])
        self.assertTrue(any("gateway" in f and "does not render" in f for f in failures), failures)

    def test_a_workflow_only_name_is_refused(self) -> None:
        text = canary.WORKFLOW.read_text(encoding="utf-8").replace(
            "        type: string\n",
            "        type: choice\n        options: [catalog-api, no-such-release]\n", 1)

        read, failures = self._read(workflow=text)

        self.assertIn("no-such-release", read["workflow"])
        self.assertTrue(any("by hand" in f for f in failures), failures)
        self.assertTrue(any("no-such-release" in f and "no descriptor describes" in f for f in failures), failures)

    def test_a_menu_written_one_option_per_line_is_refused(self) -> None:
        text = canary.WORKFLOW.read_text(encoding="utf-8").replace(
            "        type: string\n",
            "        type: choice\n        options:\n          - catalog-api\n          - no-such-release\n", 1)

        read, failures = self._read(workflow=text)

        self.assertIn("no-such-release", read["workflow"])
        self.assertTrue(any("by hand (catalog-api, no-such-release)" in f for f in failures), failures)
        self.assertTrue(any("no-such-release" in f and "no descriptor describes" in f for f in failures), failures)

    def test_a_choice_input_with_no_options_is_refused(self) -> None:
        text = canary.WORKFLOW.read_text(encoding="utf-8").replace(
            "        type: string\n", "        type: choice\n", 1)

        _, failures = self._read(workflow=text)

        self.assertEqual(canary._dispatch_options(canary._live(text)), set())
        self.assertTrue(any("by hand:" in f and "`type: choice`" in f for f in failures), failures)

    MENU = "        type: choice\n        options: [catalog-api, no-such-release]\n"

    def _assert_menu(self, workflow: str) -> None:
        read, failures = self._read(workflow=workflow)

        self.assertIn("no-such-release", read["workflow"])
        self.assertTrue(any("by hand (catalog-api, no-such-release)" in f for f in failures), failures)

    def test_a_menu_past_a_blank_line_in_its_input_is_refused(self) -> None:
        self._assert_menu(canary.WORKFLOW.read_text(encoding="utf-8").replace(
            "        type: string\n", "\n" + self.MENU, 1))

    def test_a_menu_past_a_blank_line_after_the_key_is_refused(self) -> None:
        text = self._replaced(canary.WORKFLOW, "      workload:\n", "      workload:\n\n")
        self._assert_menu(text.replace("        type: string\n", self.MENU, 1))

    def test_a_menu_in_a_flow_mapping_on_the_key_line_is_refused(self) -> None:
        self._assert_menu(re.sub(
            r"(?m)^      workload:\n(?:        .*\n)*",
            "      workload: {type: choice, options: [catalog-api, 'no-such-release']}\n",
            canary.WORKFLOW.read_text(encoding="utf-8"), count=1))

    CALLED = "  workflow_call:\n    inputs:\n      workload:\n        type: string\n"

    def test_a_menu_behind_a_called_workflows_workload_input_is_refused(self) -> None:
        text = canary.WORKFLOW.read_text(encoding="utf-8").replace("        type: string\n", self.MENU, 1)
        self._assert_menu(text.replace("  workflow_dispatch:\n", self.CALLED + "  workflow_dispatch:\n", 1))

    def test_a_flow_menu_behind_an_earlier_flow_workload_key_is_read(self) -> None:
        text = """\
on:
  workflow_call:
    inputs:
      workload: {type: string}
  workflow_dispatch:
    inputs:
      workload: {type: choice, options: [catalog-api]}
"""
        self.assertEqual(canary._dispatch_options(text), {"catalog-api"})

    def test_a_quoted_description_in_a_flow_mapping_is_not_a_menu(self) -> None:
        for description in ("'one of the options: see README'", '"type: choice, options: [x]"'):
            with self.subTest(description=description):
                text = f"      workload: {{description: {description}, type: string}}\n"
                self.assertIsNone(canary._dispatch_options(text))

    GUARD ='if ! python deploy/canary/canary.py chart --workload="$WORKLOAD" >/dev/null 2>&1; then'
    CASES = r'''$PYTHON "$ROOT/deploy/canary/canary.py" smoke-cases | tr -d '\r' >"$CASES"'''

    def _replaced(self, real: Path, old: str, new: str) -> str:
        """The real reader's text with `old`, which it must hold once, made `new`."""
        text = real.read_text(encoding="utf-8")
        self.assertEqual(text.count(old), 1, f"{real.name} does not hold {old!r} once")
        return text.replace(old, new)

    def _assert_no_guard(self, workflow: str) -> None:
        read, failures = self._read(workflow=workflow)

        self.assertEqual(read["workflow"], set())
        self.assertTrue(any("holds no live guard" in f for f in failures), failures)

    def test_a_workflow_without_the_guard_is_refused(self) -> None:
        self._assert_no_guard(self._replaced(canary.WORKFLOW, self.GUARD, "if false; then"))

    def test_a_guard_named_only_in_a_trailing_comment_is_refused(self) -> None:
        self._assert_no_guard(self._replaced(canary.WORKFLOW, self.GUARD, f"if false; then  # {self.GUARD}"))

    def test_a_guard_weakened_to_a_substring_match_is_refused(self) -> None:
        self._assert_no_guard(self._replaced(
            canary.WORKFLOW, self.GUARD,
            'WORKLOADS=$(python deploy/canary/canary.py workloads)\n'
            '          if ! grep -qF -- "$WORKLOAD" <<< "$WORKLOADS"; then'))

    def test_a_guard_that_cannot_fail_is_refused(self) -> None:
        self._assert_no_guard(self._replaced(
            canary.WORKFLOW, self.GUARD,
            'if ! (python deploy/canary/canary.py chart --workload="$WORKLOAD" >/dev/null 2>&1 || true); then'))

    def test_a_guard_that_lets_argparse_read_the_name_as_an_option_is_refused(self) -> None:
        self._assert_no_guard(self._replaced(
            canary.WORKFLOW, self.GUARD,
            'if ! python deploy/canary/canary.py chart --workload "$WORKLOAD" >/dev/null 2>&1; then'))

    def test_a_guard_whose_errors_reach_the_log_is_refused(self) -> None:
        self._assert_no_guard(self._replaced(
            canary.WORKFLOW, self.GUARD,
            'if ! python deploy/canary/canary.py chart --workload="$WORKLOAD" >/dev/null; then'))

    def test_a_guard_whose_annotation_echoes_the_input_is_refused(self) -> None:
        self._assert_no_guard(self._replaced(
            canary.WORKFLOW, 'echo "::error::The dispatched workload',
            'echo "::error::$WORKLOAD, the dispatched workload'))

    def test_a_guard_that_does_not_stop_is_refused(self) -> None:
        self._assert_no_guard(self._replaced(
            canary.WORKFLOW, 'under deploy/canary/deployables."\n            exit 1\n',
            'under deploy/canary/deployables."\n            true\n'))

    def test_a_trailing_comment_on_the_guard_leaves_it_read(self) -> None:
        read, failures = self._read(workflow=self._replaced(canary.WORKFLOW, self.GUARD, f"{self.GUARD}  # a key"))

        self.assertEqual(read["workflow"], set(self.workloads))
        self.assertEqual(failures, [])

    def test_a_hash_inside_quotes_or_an_expansion_is_not_a_comment(self) -> None:
        self.assertEqual(
            canary._live('echo "a # b" \'c # d\' ${#x} $# x#y  # tail\n  # whole\nkey: v # c'),
            'echo "a # b" \'c # d\' ${#x} $# x#y\nkey: v')

    def test_a_smoke_run_that_reads_no_cases_renders_nothing(self) -> None:
        text = canary.SMOKE.read_text(encoding="utf-8").replace("smoke-cases", "workloads")

        read, failures = self._read(smoke=text)

        self.assertEqual(read["smoke"], set())
        self.assertTrue(any("takes no live cases" in f for f in failures), failures)
        self.assertTrue(any("does not render" in f for f in failures), failures)

    def test_a_case_read_named_only_in_a_trailing_comment_is_refused(self) -> None:
        read, failures = self._read(smoke=self._replaced(canary.SMOKE, self.CASES, f': >"$CASES"  # {self.CASES}'))

        self.assertEqual(read["smoke"], set())
        self.assertTrue(any("takes no live cases" in f for f in failures), failures)

    def _assert_hand_list(self, line: str) -> None:
        read, failures = self._read(smoke=canary.SMOKE.read_text(encoding="utf-8") + line + "\n")

        self.assertEqual(read["smoke"], set())
        self.assertTrue(any("lists charts by hand" in f and "catalog" in f for f in failures), failures)

    def test_a_chart_list_assigned_by_hand_is_refused(self) -> None:
        self._assert_hand_list('SERVICE_CHARTS="catalog gateway"')

    def test_a_readonly_chart_list_is_refused(self) -> None:
        self._assert_hand_list('readonly SERVICE_CHARTS="catalog gateway"')

    def test_a_chart_array_is_refused(self) -> None:
        self._assert_hand_list("SERVICE_CHARTS=(catalog gateway)")

    def test_a_chart_appended_by_hand_is_refused(self) -> None:
        self._assert_hand_list('SERVICE_CHARTS="$SERVICE_CHARTS catalog"')

    def test_a_loop_over_charts_named_by_hand_is_refused(self) -> None:
        self._assert_hand_list("for chart in catalog gateway; do :; done")

    def test_a_loop_over_charts_named_in_a_brace_list_is_refused(self) -> None:
        self._assert_hand_list("for chart in {catalog,gateway}; do :; done")

    def test_a_sibling_inputs_options_are_not_the_workload_inputs(self) -> None:
        text = """\
on:
  workflow_dispatch:
    inputs:
      workload:
        description: 'options: [wrong, values]'
        type: string
      region:
        type: choice
        options: [catalog-api]
"""
        self.assertIsNone(canary._dispatch_options(text))

    def test_the_workload_inputs_own_options_are_read_after_a_sibling(self) -> None:
        text = """\
on:
  workflow_dispatch:
    inputs:
      region:
        type: choice
        options: [north, south]
      workload:
        description: 'options: [wrong, values]'
        type: choice
        options: [catalog-api]
"""
        self.assertEqual(canary._dispatch_options(text), {"catalog-api"})

    def test_a_list_at_the_options_keys_own_indentation_is_read(self) -> None:
        text = """on:
  workflow_dispatch:
    inputs:
      workload:
        type: string
        options:
        - catalog-api
        - 'gateway'
      region:
        type: string
"""
        self.assertEqual(canary._dispatch_options(text), {"catalog-api", "gateway"})


class DescriptorTests(unittest.TestCase):
    """A deployable's descriptor: one file, named for its release."""

    def _plan(self, plan: dict, descriptors: dict[str, dict]) -> dict:
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "canary.json"
            path.write_text(json.dumps(plan), encoding="utf-8")
            folder = Path(tmp) / "deployables"
            folder.mkdir()
            for name, descriptor in descriptors.items():
                (folder / f"{name}.json").write_text(json.dumps(descriptor), encoding="utf-8")
            return canary.load_plan(path, folder)

    def test_each_descriptor_is_the_workload_its_file_names(self) -> None:
        plan = self._plan({"steps": []}, {"a-api": {"chart": "a"}, "b": {"chart": "b"}})

        self.assertEqual(plan["workloads"], {"a-api": {"chart": "a"}, "b": {"chart": "b"}})

    def test_a_plan_that_still_carries_workloads_is_refused(self) -> None:
        with self.assertRaisesRegex(canary.PlanError, "its own file"):
            self._plan({"workloads": {"a": {}}}, {})

    def test_smoke_cases_prints_what_smoke_sh_reads(self) -> None:
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            code = canary.main(["canary", "smoke-cases"])

        self.assertEqual(code, 0)
        self.assertEqual(out.getvalue().splitlines(), canary.smoke_cases(canary.load_plan()["workloads"]))
        self.assertIn("payments overlay paymentProvider.baseUrl=https://psp.example.invalid/",
                      out.getvalue().splitlines())
        self.assertIn("notifications host Notifications.Worker", out.getvalue().splitlines())

    def test_a_host_name_smoke_sh_cannot_find_settings_by_is_refused(self) -> None:
        """smoke.sh finds a chart's listeners beside its host's project, so the name must be one word it can
        hand to find; a service with two hosts under one source is why the source alone is not enough."""
        workloads = json.loads(json.dumps(canary.load_plan()["workloads"]))
        workloads["payments-api"]["serviceName"] = "Payments Api"

        failures = canary.check({**canary.load_plan(), "workloads": workloads})

        self.assertTrue(
            any("payments-api.serviceName" in f and "one project name" in f for f in failures), failures)

    def test_a_case_smoke_sh_would_split_into_two_words_is_refused(self) -> None:
        workloads = json.loads(json.dumps(canary.load_plan()["workloads"]))
        workloads["payments-api"]["smoke"]["overlay"] = ["paymentProvider.baseUrl=https://a b/"]

        failures = canary.check({**canary.load_plan(), "workloads": workloads})

        self.assertTrue(any("payments-api.smoke.overlay" in f for f in failures), failures)

    def test_an_address_and_a_one_item_list_are_one_word_each(self) -> None:
        workloads = json.loads(json.dumps(canary.load_plan()["workloads"]))
        workloads["payments-api"]["smoke"]["overlay"] = [
            "paymentProvider.baseUrl=https://psp.example.invalid/",
            "mail.from=no-reply@commerce.example.invalid",
            "jurisdiction.languages={en}",
        ]

        failures = canary.check({**canary.load_plan(), "workloads": workloads})

        self.assertFalse(any("payments-api.smoke.overlay" in f for f in failures), failures)

    def test_a_comma_is_refused_even_inside_a_list(self) -> None:
        workloads = json.loads(json.dumps(canary.load_plan()["workloads"]))
        workloads["payments-api"]["smoke"]["overlay"] = ["jurisdiction.languages={en,kk}"]

        failures = canary.check({**canary.load_plan(), "workloads": workloads})

        self.assertTrue(any("payments-api.smoke.overlay" in f for f in failures), failures)

    def test_a_source_that_holds_another_host_is_refused(self) -> None:
        workloads = json.loads(json.dumps(canary.load_plan()["workloads"]))
        workloads["catalog-api"]["source"] = "src/Services/Ordering"

        failures = canary.check({**canary.load_plan(), "workloads": workloads})

        self.assertTrue(any("catalog-api.source" in f and "Catalog.Api.csproj" in f for f in failures), failures)

    def test_two_descriptors_naming_one_chart_are_refused(self) -> None:
        workloads = json.loads(json.dumps(canary.load_plan()["workloads"]))
        workloads["ordering-api"]["chart"] = "catalog"

        failures = canary.check({**canary.load_plan(), "workloads": workloads})

        self.assertTrue(any("two descriptors name the chart 'catalog'" in f for f in failures), failures)

    def test_a_descriptor_that_does_not_say_whether_it_has_a_database_is_refused(self) -> None:
        workloads = json.loads(json.dumps(canary.load_plan()["workloads"]))
        del workloads["catalog-api"]["smoke"]["database"]

        failures = canary.check({**canary.load_plan(), "workloads": workloads})

        self.assertTrue(any("catalog-api.smoke.database" in f for f in failures), failures)

    def test_a_chart_that_migrates_a_database_it_lacks_is_refused(self) -> None:
        workloads = json.loads(json.dumps(canary.load_plan()["workloads"]))
        workloads["catalog-api"]["smoke"]["database"] = False

        failures = canary.check({**canary.load_plan(), "workloads": workloads})

        self.assertTrue(any("migrates a database it does not have" in f for f in failures), failures)

    def test_a_database_with_no_job_must_name_the_release_that_migrates_it(self) -> None:
        """ADR-118: without the name, nothing orders the rollout behind the schema."""
        workloads = json.loads(json.dumps(canary.load_plan()["workloads"]))
        del workloads["notifications-api"]["migratedBy"]

        failures = canary.check({**canary.load_plan(), "workloads": workloads})

        self.assertTrue(
            any("notifications-api" in f and "names no migratedBy" in f for f in failures), failures)

    def test_a_chart_that_migrates_itself_names_no_other(self) -> None:
        workloads = json.loads(json.dumps(canary.load_plan()["workloads"]))
        workloads["catalog-api"]["migratedBy"] = "notifications"

        failures = canary.check({**canary.load_plan(), "workloads": workloads})

        self.assertTrue(
            any("catalog-api.migratedBy" in f and "no schema" in f for f in failures), failures)

    def test_migrated_by_must_name_a_workload_whose_chart_migrates(self) -> None:
        for named in ("gateway", "notifications-api", "no-such-workload"):
            with self.subTest(named=named):
                workloads = json.loads(json.dumps(canary.load_plan()["workloads"]))
                workloads["notifications-api"]["migratedBy"] = named

                failures = canary.check({**canary.load_plan(), "workloads": workloads})

                self.assertTrue(
                    any("notifications-api.migratedBy" in f and "runs a migration Job" in f
                        for f in failures), failures)

    def test_the_shipped_plan_names_the_worker_as_the_api_host_s_migrator(self) -> None:
        cases = canary.smoke_cases(canary.load_plan()["workloads"])

        self.assertIn("notifications-api migratedBy notifications", cases)
        self.assertIn("notifications-api database yes", cases)
        self.assertIn("notifications-api migrator no", cases)

    def test_migrated_by_prints_the_release_or_nothing(self) -> None:
        for workload, printed in (("notifications-api", "notifications\n"), ("catalog-api", "\n")):
            with self.subTest(workload=workload):
                out = io.StringIO()
                with contextlib.redirect_stdout(out):
                    code = canary.main(["canary", "migrated-by", "--workload", workload])

                self.assertEqual((0, printed), (code, out.getvalue()))

    def test_a_migrated_by_that_is_not_a_name_is_refused_rather_than_raised(self) -> None:
        """Check 4 refuses it, smoke-cases prints no line for it, and the rollout's question fails closed."""
        workloads = json.loads(json.dumps(canary.load_plan()["workloads"]))
        workloads["notifications-api"]["migratedBy"] = ["notifications"]

        cases = canary.smoke_cases(workloads)
        failures = canary.check({**canary.load_plan(), "workloads": workloads})

        self.assertFalse(any(" migratedBy " in case for case in cases), cases)
        self.assertTrue(any("notifications-api.migratedBy" in f for f in failures), failures)
        with self.assertRaisesRegex(canary.PlanError, "names no workload"):
            canary.migrated_by("notifications-api", {"workloads": workloads})

    def test_migrated_by_refuses_a_workload_the_plan_does_not_describe(self) -> None:
        err = io.StringIO()
        with contextlib.redirect_stderr(err):
            code = canary.main(["canary", "migrated-by", "--workload", "no-such-workload"])

        self.assertEqual(1, code)
        self.assertIn("no workload 'no-such-workload'", err.getvalue())

    def test_migrates_prints_each_host_whose_schema_the_release_migrates(self) -> None:
        for workload, printed in (("notifications", "notifications-api\n"), ("catalog-api", "")):
            with self.subTest(workload=workload):
                out = io.StringIO()
                with contextlib.redirect_stdout(out):
                    code = canary.main(["canary", "migrates", "--workload", workload])

                self.assertEqual((0, printed), (code, out.getvalue()))

    def test_migrates_refuses_a_workload_the_plan_does_not_describe(self) -> None:
        err = io.StringIO()
        with contextlib.redirect_stderr(err):
            code = canary.main(["canary", "migrates", "--workload", "no-such-workload"])

        self.assertEqual(1, code)
        self.assertIn("no workload 'no-such-workload'", err.getvalue())


class RolloutOrderTests(unittest.TestCase):
    """Check 13: a host another release migrates rolls in lockstep with it (ADR-118, ADR-122)."""

    def _workflow(self, text: str) -> Path:
        path = Path(tempfile.mkdtemp()) / "deploy.yml"
        path.write_text(text, encoding="utf-8")
        return path

    @property
    def shipped(self) -> str:
        return canary.WORKFLOW.read_text(encoding="utf-8")

    @property
    def workloads(self) -> dict:
        return canary.entries(canary.load_plan()["workloads"])

    def test_the_shipped_workflow_orders_the_rollout(self) -> None:
        self.assertEqual([], canary._rollout_follows_the_migrator(self.workloads))

    def test_a_workflow_that_never_asks_is_refused(self) -> None:
        without = "\n".join(
            line for line in self.shipped.splitlines() if "canary.py migrated-by" not in line)

        failures = canary._rollout_follows_the_migrator(self.workloads, self._workflow(without))

        self.assertEqual(1, len(failures), failures)
        self.assertIn("runs no `canary.py migrated-by`", failures[0])

    def test_an_answer_that_refuses_nothing_is_refused(self) -> None:
        without = "\n".join(
            line for line in self.shipped.splitlines() if "merge-base --is-ancestor" not in line
            or "MIGRATED_REVISION" not in line)

        failures = canary._rollout_follows_the_migrator(self.workloads, self._workflow(without))

        self.assertEqual(1, len(failures), failures)
        self.assertIn("refuses nothing", failures[0])

    def test_a_host_held_from_above_only_is_refused(self) -> None:
        """ADR-122: a host may not roll back behind a schema the migrating release has moved."""
        without = "\n".join(
            line for line in self.shipped.splitlines()
            if 'merge-base --is-ancestor "$MIGRATED_REVISION" "$REVISION"' not in line)

        failures = canary._rollout_follows_the_migrator(self.workloads, self._workflow(without))

        self.assertEqual(1, len(failures), failures)
        self.assertIn("older than the schema", failures[0])

    def test_a_migrating_release_that_never_asks_what_it_migrates_is_refused(self) -> None:
        without = "\n".join(
            line for line in self.shipped.splitlines() if "canary.py migrates " not in line)

        failures = canary._rollout_follows_the_migrator(self.workloads, self._workflow(without))

        self.assertEqual(1, len(failures), failures)
        self.assertIn("runs no `canary.py migrates`", failures[0])

    def test_a_migrating_release_held_to_no_host_is_refused(self) -> None:
        without = "\n".join(
            line for line in self.shipped.splitlines() if "merge-base --is-ancestor" not in line
            or "HOST_REVISION" not in line)

        failures = canary._rollout_follows_the_migrator(self.workloads, self._workflow(without))

        self.assertEqual(1, len(failures), failures)
        self.assertIn("never holds", failures[0])
        self.assertIn("$HOST_REVISION", failures[0])

    def test_a_migrating_release_held_from_below_only_is_refused(self) -> None:
        """ADR-122: a worker rolled back leaves the host ahead of it, and the next migration need not support that."""
        without = "\n".join(
            line for line in self.shipped.splitlines()
            if 'merge-base --is-ancestor "$HOST_REVISION" "$STABLE_REVISION"' not in line)

        failures = canary._rollout_follows_the_migrator(self.workloads, self._workflow(without))

        self.assertEqual(1, len(failures), failures)
        self.assertIn("newer than", failures[0])

    def test_a_commented_out_question_is_no_question(self) -> None:
        commented = self.shipped.replace(
            "MIGRATED_BY=$(python deploy/canary/canary.py migrated-by",
            "# MIGRATED_BY=$(python deploy/canary/canary.py migrated-by")

        failures = canary._rollout_follows_the_migrator(self.workloads, self._workflow(commented))

        self.assertTrue(any("runs no `canary.py migrated-by`" in f for f in failures), failures)

    def test_with_no_host_migrated_by_another_there_is_nothing_to_order(self) -> None:
        workloads = {name: entry for name, entry in self.workloads.items() if "migratedBy" not in entry}

        self.assertEqual([], canary._rollout_follows_the_migrator(workloads, self._workflow("")))


class SharedValuesTests(unittest.TestCase):
    """ADR-125: a value two releases are each given is held equal before either rolls; check 14 holds it."""

    @property
    def workloads(self) -> dict:
        return json.loads(json.dumps(canary.entries(canary.load_plan()["workloads"])))

    def _checked(self, workloads: dict) -> list[str]:
        return canary.check({**canary.load_plan(), "workloads": workloads})

    def _workflow(self, text: str) -> Path:
        path = Path(tempfile.mkdtemp()) / "deploy.yml"
        path.write_text(text, encoding="utf-8")
        return path

    @property
    def shipped(self) -> str:
        return canary.WORKFLOW.read_text(encoding="utf-8")

    def test_the_shipped_pairs_are_read_from_both_ends(self) -> None:
        plan = canary.load_plan()

        self.assertEqual(
            [("notifications-api", "jurisdiction.required"), ("web-bff", "push.apps")],
            canary.shared_values("notifications", plan))
        self.assertEqual([("notifications", "jurisdiction.required")], canary.shared_values("notifications-api", plan))
        self.assertEqual([("notifications", "push.apps")], canary.shared_values("web-bff", plan))
        self.assertEqual([], canary.shared_values("catalog-api", plan))

    def test_the_cli_prints_one_pair_a_line_and_refuses_an_unknown_workload(self) -> None:
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            code = canary.main(["canary", "shared-values", "--workload", "web-bff"])
        self.assertEqual((0, "notifications push.apps\n"), (code, out.getvalue()))

        with contextlib.redirect_stderr(io.StringIO()):
            self.assertEqual(1, canary.main(["canary", "shared-values", "--workload", "nobody"]))

    def test_the_shipped_descriptors_pass_check_4(self) -> None:
        self.assertEqual([], [f for f in self._checked(self.workloads) if "sharesValues" in f])

    def test_a_declaration_check_4_refuses(self) -> None:
        cases = {
            "names no peer and no key": {},
            "which is not another workload": {"nobody": ["push.apps"]},
            "which names no key": {"web-bff": []},
            "which is not a dotted values path": {"web-bff": ["push..apps"]},
        }
        for message, declared in cases.items():
            with self.subTest(message):
                workloads = self.workloads
                workloads["notifications"]["sharesValues"] = declared

                self.assertTrue(any(message in f for f in self._checked(workloads)), self._checked(workloads))

    def test_a_malformed_declaration_is_refused_and_not_a_crash(self) -> None:
        for declared in [["web-bff"], {"web-bff": 1}]:
            with self.subTest(declared=declared):
                plan = canary.load_plan()
                plan["workloads"]["notifications"]["sharesValues"] = declared

                failures = canary.check(plan)

                self.assertTrue(any("sharesValues" in failure for failure in failures), failures)

    def test_a_pair_declared_from_both_ends_is_refused(self) -> None:
        workloads = self.workloads
        workloads["web-bff"]["sharesValues"] = {"notifications": ["push.apps"]}

        self.assertTrue(any("one declaration is the pair's owner" in f for f in self._checked(workloads)))

    def test_equal_values_agree_whatever_their_order(self) -> None:
        mine = {"push": {"apps": [{"appId": "a", "platform": "fcm"}, {"appId": "b", "platform": "apns"}]}}
        theirs = {"push": {"apps": [{"platform": "apns", "appId": "b"}, {"appId": "a", "platform": "fcm"}]}}

        self.assertEqual([], canary.values_agree("push.apps", mine, theirs, "web-bff"))

    def test_a_member_named_twice_is_the_same_member(self) -> None:
        mine, theirs = {"push": {"apps": ["a", "a", "b"]}}, {"push": {"apps": ["b", "a"]}}

        self.assertEqual([], canary.values_agree("push.apps", mine, theirs, "web-bff"))

    def test_a_member_only_one_release_names_is_a_difference_that_says_which(self) -> None:
        mine = {"jurisdiction": {"required": ["order-placed/email", "order-confirmed/email"]}}
        theirs = {"jurisdiction": {"required": ["order-placed/email"]}}

        differences = canary.values_agree("jurisdiction.required", mine, theirs, "notifications-api")

        self.assertEqual(1, len(differences), differences)
        self.assertIn('only this release names "order-confirmed/email"', differences[0])
        self.assertIn("only notifications-api names nothing", differences[0])

    def test_absent_null_and_empty_are_three_answers(self) -> None:
        """A chart refuses null as unstated and takes [] as none (Appendix E), so neither stands in for the other."""
        absent, null, empty = {}, {"jurisdiction": {"required": None}}, {"jurisdiction": {"required": []}}

        for a, b in [(absent, null), (null, empty), (absent, empty)]:
            with self.subTest(a=a, b=b):
                self.assertTrue(canary.values_agree("jurisdiction.required", a, b, "peer"))
        self.assertEqual([], canary.values_agree("jurisdiction.required", null, null, "peer"))

    def test_a_path_neither_release_holds_is_refused_and_not_agreed(self) -> None:
        """A misspelt key would otherwise pass every rollout, since `--all` carries every default."""
        differences = canary.values_agree("push.app", {"push": {"apps": []}}, {"push": {"apps": []}}, "web-bff")

        self.assertEqual(1, len(differences), differences)
        self.assertIn("neither this release's values nor web-bff's hold", differences[0])

    def test_the_cli_exits_by_the_answer(self) -> None:
        folder = Path(tempfile.mkdtemp())
        same, other = folder / "same.json", folder / "other.json"
        same.write_text(json.dumps({"push": {"apps": []}}), encoding="utf-8")
        other.write_text(json.dumps({"push": {"apps": [{"appId": "a", "platform": "fcm"}]}}), encoding="utf-8")
        argv = ["canary", "values-agree", "--key", "push.apps", "--peer-name", "web-bff"]

        broken = folder / "broken.json"
        broken.write_text("{", encoding="utf-8")

        with contextlib.redirect_stderr(io.StringIO()) as err:
            self.assertEqual(0, canary.main([*argv, "--values", str(same), "--peer", str(same)]))
            self.assertEqual(1, canary.main([*argv, "--values", str(same), "--peer", str(other)]))
            self.assertEqual(2, canary.main([*argv, "--values", str(same), "--peer", str(broken)]))
            self.assertEqual(2, canary.main(
                ["canary", "values-agree", "--key", "push.app", "--peer-name", "web-bff",
                 "--values", str(same), "--peer", str(same)]))
            self.assertEqual(2, canary.main(
                ["canary", "values-agree", "--key", "push..apps", "--peer-name", "web-bff",
                 "--values", str(same), "--peer", str(same)]))
        self.assertIn("push.apps differs from web-bff's", err.getvalue())

    def test_the_shipped_workflow_compares_the_pairs(self) -> None:
        self.assertEqual([], canary._rollout_holds_shared_values(self.workloads))

    def test_a_workflow_that_never_asks_is_refused(self) -> None:
        without = "\n".join(line for line in self.shipped.splitlines() if "canary.py shared-values" not in line)

        failures = canary._rollout_holds_shared_values(self.workloads, self._workflow(without))

        self.assertEqual(1, len(failures), failures)
        self.assertIn("runs no `canary.py shared-values`", failures[0])

    def test_an_answer_that_compares_nothing_is_refused(self) -> None:
        without = "\n".join(line for line in self.shipped.splitlines() if "canary.py values-agree" not in line)

        failures = canary._rollout_holds_shared_values(self.workloads, self._workflow(without))

        self.assertEqual(1, len(failures), failures)
        self.assertIn("refuses nothing", failures[0])

    def test_a_key_the_rollout_sets_itself_is_refused(self) -> None:
        """Read from the workflow's own `--set` flags, so a newly pinned key is covered without a list to keep."""
        self.assertIn('identity.authority="$CHECKED_AUTHORITY"', self.shipped)
        for key in ["identity.authority", "image", "image.tag"]:
            with self.subTest(key=key):
                workloads = self.workloads
                workloads["notifications"]["sharesValues"]["web-bff"].append(key)

                failures = canary._rollout_holds_shared_values(workloads, self._workflow(self.shipped))

                self.assertEqual(1, len(failures), failures)
                self.assertIn(f"names {key}, which", failures[0])
                self.assertIn("own command line", failures[0])

    def test_a_key_pinned_quoted_literal_or_after_a_comma_is_refused(self) -> None:
        for flag, key in [
            ("--set-string 'jurisdiction.required={a,b}'", "jurisdiction.required"),
            ("--set image.pullPolicy=Always,push.apps={a}", "push.apps"),
            ('--set-literal "push.apps=a"', "push.apps"),
            ("--set-json=push.apps='[1, 2]'", "push.apps"),
            ("--set push.apps[0]=a", "push.apps"),
            ("--set-string jurisdiction.required[1].topic=x", "jurisdiction.required"),
        ]:
            with self.subTest(flag=flag):
                pinned = self.shipped + f"\n          helm upgrade x deploy/helm/x {flag}\n"

                failures = canary._rollout_holds_shared_values(self.workloads, self._workflow(pinned))

                self.assertEqual(1, len(failures), failures)
                self.assertIn(f"names {key}, which", failures[0])

    def test_no_spelling_of_a_set_flag_hides_a_key_it_pins(self) -> None:
        """A superset: a list member may read as a key, which refuses; a pinned key never reads as none."""
        for argument in [
            "a=x{y,push.apps=z", "a=x{y,push.apps=z}", "a=}x,push.apps=z,c={", "a=b={x,push.apps=y}",
            "'x=a" + chr(92) + ",push.apps=c'", "x={a,b},push.apps=1", '"push.apps"=x', 'push."apps"=x',
        ]:
            with self.subTest(argument=argument):
                self.assertIn("push.apps", canary._pinned_keys([f"helm upgrade r c --set {argument}"]))
        continued = ["helm upgrade r c --set " + chr(92), "  push.apps=x --wait"]
        self.assertIn("push.apps", canary._pinned_keys(continued))
        folded = ["helm upgrade r c --set", "  push.apps=x", "  --wait"]
        self.assertIn("push.apps", canary._pinned_keys(folded))
        self.assertIn("push.apps", canary._pinned_keys(["helm upgrade r c --set a=x--y,push.apps=z"]))
        self.assertEqual(set(), canary._pinned_keys(["helm upgrade r c -f values.yaml"]))
        self.assertEqual({"image.tag"}, canary._pinned_keys(['helm upgrade r c --set-string image.tag="$TAG"']))

    def test_a_comparison_of_the_running_values_is_refused(self) -> None:
        """The running values carry the old chart's defaults, so a changed default would pass unseen."""
        running = self.shipped.replace('--values "$RUNNER_TEMP/candidate.json"', '--values "$RUNNER_TEMP/stable.json"')

        failures = canary._rollout_holds_shared_values(self.workloads, self._workflow(running))

        self.assertEqual(1, len(failures), failures)
        self.assertIn("will be installed with", failures[0])

    def test_overrides_read_with_their_defaults_are_refused(self) -> None:
        coalesced = self.shipped.replace(
            '-n "$NAMESPACE" -o yaml > "$RUNNER_TEMP/overrides.yaml"',
            '-n "$NAMESPACE" --all -o yaml > "$RUNNER_TEMP/overrides.yaml"')

        failures = canary._rollout_holds_shared_values(self.workloads, self._workflow(coalesced))

        self.assertEqual(1, len(failures), failures)
        self.assertIn("own overrides", failures[0])

    def test_candidate_values_not_drawn_from_the_checked_out_chart_are_refused(self) -> None:
        for mutation in [
            ('cp "deploy/helm/$CHART/values.yaml"', 'touch "deploy/helm/$CHART/values.yaml"'),
            ('-f "$RUNNER_TEMP/overrides.yaml" ' + chr(92), chr(92)),
        ]:
            with self.subTest(mutation=mutation[0]):
                self.assertIn(mutation[0], self.shipped)
                failures = canary._rollout_holds_shared_values(
                    self.workloads, self._workflow(self.shipped.replace(*mutation)))

                self.assertEqual(1, len(failures), failures)
                self.assertIn("checked-out chart's", failures[0])

    def test_a_commented_out_question_is_no_question(self) -> None:
        commented = self.shipped.replace(
            "python deploy/canary/canary.py shared-values", "# python deploy/canary/canary.py shared-values")

        failures = canary._rollout_holds_shared_values(self.workloads, self._workflow(commented))

        self.assertEqual(1, len(failures), failures)

    def test_with_no_pair_declared_there_is_nothing_to_compare(self) -> None:
        workloads = self.workloads
        del workloads["notifications"]["sharesValues"]

        self.assertEqual([], canary._rollout_holds_shared_values(workloads, self._workflow("")))


class EmptyUntilTests(unittest.TestCase):
    """ADR-126: a value earlier revisions misread stays empty while either track serves one; check 15 holds it."""

    PUSH_DELIVERIES = "19ff224ff02274f4c9b33b2685f197f3005ca7f1"

    @property
    def workloads(self) -> dict:
        return json.loads(json.dumps(canary.entries(canary.load_plan()["workloads"])))

    def _checked(self, workloads: dict) -> list[str]:
        return canary.check({**canary.load_plan(), "workloads": workloads})

    def _workflow(self, text: str) -> Path:
        path = Path(tempfile.mkdtemp()) / "deploy.yml"
        path.write_text(text, encoding="utf-8")
        return path

    @property
    def shipped(self) -> str:
        return canary.WORKFLOW.read_text(encoding="utf-8")

    def test_the_shipped_key_waits_on_the_last_commit_of_push_deliveries(self) -> None:
        plan = canary.load_plan()

        self.assertEqual([("push.apps", self.PUSH_DELIVERIES)], canary.empty_until("notifications", plan))
        self.assertEqual([], canary.empty_until("web-bff", plan))

    def test_the_cli_prints_one_key_a_line_and_refuses_an_unknown_workload(self) -> None:
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            code = canary.main(["canary", "empty-until", "--workload", "notifications"])
        self.assertEqual((0, f"push.apps {self.PUSH_DELIVERIES}\n"), (code, out.getvalue()))

        with contextlib.redirect_stderr(io.StringIO()):
            self.assertEqual(1, canary.main(["canary", "empty-until", "--workload", "nobody"]))

    def test_the_shipped_descriptors_pass_check_4(self) -> None:
        self.assertEqual([], [f for f in self._checked(self.workloads) if "emptyUntil" in f])

    def test_a_declaration_check_4_refuses(self) -> None:
        cases = {
            "which names no key": {},
            "which is not a dotted values path": {"push..apps": self.PUSH_DELIVERIES},
            "which is not a whole commit object name": {"push.apps": self.PUSH_DELIVERIES[:12]},
        }
        for message, declared in cases.items():
            with self.subTest(message):
                workloads = self.workloads
                workloads["notifications"]["emptyUntil"] = declared

                self.assertTrue(any(message in f for f in self._checked(workloads)), self._checked(workloads))

    def test_a_malformed_declaration_is_refused_and_not_a_crash(self) -> None:
        for declared in [["push.apps"], {"push.apps": 1}]:
            with self.subTest(declared=declared):
                plan = canary.load_plan()
                plan["workloads"]["notifications"]["emptyUntil"] = declared

                self.assertTrue(any("emptyUntil" in failure for failure in canary.check(plan)))

    def test_empty_names_nothing_and_a_member_is_named(self) -> None:
        for empty in [[], None, "", {}]:
            with self.subTest(empty=empty):
                self.assertEqual([], canary.values_named("push.apps", {"push": {"apps": empty}}))
        named = canary.values_named("push.apps", {"push": {"apps": [{"appId": "a", "platform": "fcm"}]}})

        self.assertEqual(['{"appId": "a", "platform": "fcm"}'], named)

    def test_a_path_the_values_do_not_hold_is_refused_and_not_empty(self) -> None:
        """The values carry every chart default, so a misspelt key would otherwise let every rollout through."""
        with self.assertRaisesRegex(canary.PlanError, "push.app is a path the values do not hold"):
            canary.values_named("push.app", {"push": {"apps": []}})

    def test_the_cli_exits_by_the_answer(self) -> None:
        folder = Path(tempfile.mkdtemp())
        empty, named, broken = folder / "empty.json", folder / "named.json", folder / "broken.json"
        empty.write_text(json.dumps({"push": {"apps": []}}), encoding="utf-8")
        named.write_text(json.dumps({"push": {"apps": [{"appId": "a", "platform": "fcm"}]}}), encoding="utf-8")
        broken.write_text("{", encoding="utf-8")
        argv = ["canary", "values-empty", "--key"]

        with contextlib.redirect_stderr(io.StringIO()) as err:
            self.assertEqual(0, canary.main([*argv, "push.apps", "--values", str(empty)]))
            self.assertEqual(1, canary.main([*argv, "push.apps", "--values", str(named)]))
            self.assertEqual(2, canary.main([*argv, "push.apps", "--values", str(broken)]))
            self.assertEqual(2, canary.main([*argv, "push.app", "--values", str(empty)]))
            self.assertEqual(2, canary.main([*argv, "push..apps", "--values", str(empty)]))
        self.assertIn('push.apps names {"appId": "a", "platform": "fcm"} (ADR-126)', err.getvalue())

    def test_the_shipped_workflow_holds_the_keys_empty(self) -> None:
        self.assertEqual([], canary._rollout_keeps_values_empty(self.workloads))

    def test_check_runs_check_15(self) -> None:
        with mock.patch.object(canary, "_rollout_keeps_values_empty", return_value=["check 15 refused"]):
            self.assertIn("check 15 refused", canary.check(canary.load_plan()))

    def test_a_workflow_that_never_asks_is_refused(self) -> None:
        without = "\n".join(line for line in self.shipped.splitlines() if "canary.py empty-until" not in line)

        failures = canary._rollout_keeps_values_empty(self.workloads, self._workflow(without))

        self.assertEqual(1, len(failures), failures)
        self.assertIn("runs no `canary.py empty-until`", failures[0])

    def test_an_answer_that_holds_nothing_empty_is_refused(self) -> None:
        without = "\n".join(line for line in self.shipped.splitlines() if "canary.py values-empty" not in line)

        failures = canary._rollout_keeps_values_empty(self.workloads, self._workflow(without))

        self.assertEqual(1, len(failures), failures)
        self.assertIn("refuses nothing", failures[0])

    def test_holding_the_running_values_empty_is_refused(self) -> None:
        """The running values are the stable track's, not the ones the candidate is installed with."""
        running = self.shipped.replace(
            'values-empty --key "$KEY" ' + chr(92) + '\n              --values "$RUNNER_TEMP/candidate.json"',
            'values-empty --key "$KEY" ' + chr(92) + '\n              --values "$RUNNER_TEMP/stable.json"')
        self.assertNotEqual(self.shipped, running)

        failures = canary._rollout_keeps_values_empty(self.workloads, self._workflow(running))

        self.assertEqual(1, len(failures), failures)
        self.assertIn("will be installed with", failures[0])

    def test_candidate_values_are_held_to_their_source_without_check_14(self) -> None:
        """Check 14 is silent with no `sharesValues` declared, so check 15 cannot lean on it."""
        workloads = self.workloads
        del workloads["notifications"]["sharesValues"]
        self.assertEqual([], canary._rollout_holds_shared_values(workloads, self._workflow("")))
        for mutation, message in [
            (('-n "$NAMESPACE" -o yaml > "$RUNNER_TEMP/overrides.yaml"',
              '-n "$NAMESPACE" --all -o yaml > "$RUNNER_TEMP/overrides.yaml"'), "own overrides"),
            (('cp "deploy/helm/$CHART/values.yaml"', 'touch "deploy/helm/$CHART/values.yaml"'), "checked-out chart's"),
        ]:
            with self.subTest(mutation=mutation[0]):
                self.assertIn(mutation[0], self.shipped)
                failures = canary._rollout_keeps_values_empty(
                    workloads, self._workflow(self.shipped.replace(*mutation)))

                self.assertEqual(1, len(failures), failures)
                self.assertIn(message, failures[0])
                self.assertIn("(ADR-126)", failures[0])

    def test_a_step_moved_above_the_candidate_values_is_refused(self) -> None:
        lines = self.shipped.splitlines()
        asked = next(i for i, line in enumerate(lines) if "canary.py values-empty" in line)
        moved = lines[asked:asked + 2] + lines[:asked] + lines[asked + 2:]

        failures = canary._rollout_keeps_values_empty(self.workloads, self._workflow("\n".join(moved)))

        self.assertEqual(1, len(failures), failures)
        self.assertIn("once written", failures[0])

    def test_a_track_never_compared_with_the_revision_is_refused(self) -> None:
        """An older candidate misreads the stable track's rows, so each track is asked, not only the stable one."""
        for track in ["STABLE_REVISION", "REVISION"]:
            with self.subTest(track=track):
                asked = f'git merge-base --is-ancestor "$SINCE" "${track}"'
                self.assertIn(asked, self.shipped)
                without = self.shipped.replace(asked, "true")

                failures = canary._rollout_keeps_values_empty(self.workloads, self._workflow(without))

                self.assertEqual(1, len(failures), failures)
                self.assertIn(f'"${track}"', failures[0])

    def test_a_key_skipped_on_either_track_alone_is_refused(self) -> None:
        conjunction = '"$STABLE_REVISION" && git merge-base'
        for mutation in [
            (conjunction, '"$STABLE_REVISION" || git merge-base'),
            ("            if git merge-base --is-ancestor \"$SINCE\" \"$STABLE_REVISION\"",
             "            if true || git merge-base --is-ancestor \"$SINCE\" \"$STABLE_REVISION\""),
        ]:
            with self.subTest(mutation=mutation[1]):
                self.assertIn(mutation[0], self.shipped)
                failures = canary._rollout_keeps_values_empty(
                    self.workloads, self._workflow(self.shipped.replace(*mutation)))

                self.assertEqual(1, len(failures), failures)
                self.assertIn("without both tracks", failures[0])

    def test_a_workflow_that_rolls_back_across_the_revision_is_refused(self) -> None:
        """ADR-127: the rows a stable track left are not in the values, so the refusal reads revisions alone."""
        back = '            if git merge-base --is-ancestor "$SINCE" "$STABLE_REVISION"; then\n'
        self.assertIn(back, self.shipped)
        start = self.shipped.index(back)
        refusal = self.shipped[start:self.shipped.index("            fi\n", start) + len("            fi\n")]
        done = '          done < "$RUNNER_TEMP/empty-until.txt"\n'
        skip = '            if git merge-base --is-ancestor "$SINCE" "$STABLE_REVISION" && '
        self.assertIn(done, self.shipped)
        self.assertEqual(1, self.shipped.count(skip))
        without = self.shipped.replace(refusal, "")
        refuses = "              exit 1\n"
        self.assertEqual(1, refusal.count(refuses))
        for name, mutated in {
            "removed": without,
            "exit 1 made true": self.shipped.replace(refusal, refusal.replace("exit 1", "true")),
            "exit 1 commented out": self.shipped.replace(refusal, refusal.replace("exit 1", "# exit 1")),
            # Past the loop, an empty $SINCE is no commit's ancestor, so the refusal never fires.
            "past the loop": without.replace(done, done + refusal),
            # Above the skip, it refuses every rollout once the stable track carries the revision.
            "above the skip": without.replace(skip, refusal + skip),
            # The branch that runs when the stable track carries the revision must be the one that exits.
            "exit 1 under else": self.shipped.replace(
                refusal, refusal.replace(refuses, "              true\n            else\n" + refuses)),
            "exit 1 in a nested block": self.shipped.replace(
                refusal, refusal.replace(refuses, "              if false; then\n  " + refuses + "              fi\n")),
        }.items():
            with self.subTest(name):
                self.assertNotEqual(self.shipped, mutated)
                failures = canary._rollout_keeps_values_empty(self.workloads, self._workflow(mutated))

                self.assertEqual(1, len(failures), failures)
                self.assertIn("(ADR-127)", failures[0])

    def test_a_key_the_rollout_sets_itself_is_refused(self) -> None:
        workloads = self.workloads
        workloads["notifications"]["emptyUntil"]["image.tag"] = self.PUSH_DELIVERIES

        failures = canary._rollout_keeps_values_empty(workloads, self._workflow(self.shipped))

        self.assertEqual(1, len(failures), failures)
        self.assertIn("names image.tag, which", failures[0])

    def test_a_commented_out_question_is_no_question(self) -> None:
        commented = self.shipped.replace(
            "python deploy/canary/canary.py empty-until", "# python deploy/canary/canary.py empty-until")

        failures = canary._rollout_keeps_values_empty(self.workloads, self._workflow(commented))

        self.assertEqual(1, len(failures), failures)

    def test_with_no_key_declared_there_is_nothing_to_hold(self) -> None:
        workloads = self.workloads
        del workloads["notifications"]["emptyUntil"]

        self.assertEqual([], canary._rollout_keeps_values_empty(workloads, self._workflow("")))


class SourceInputTests(unittest.TestCase):
    """SOURCE_INPUTS against the reads it claims to enumerate.

    The list shipped incomplete: it declared `src` and `deploy/helm` and
    omitted `deploy/observability`, which checks 3 and 5 both open. Check 7
    stayed green throughout, because a list can only be compared against the
    workflow for the entries it contains — **a gate cannot see a read it was
    never told about.**

    That is the same shape as the empty-parser tests one directory over, and
    the reason this is a test rather than a careful re-reading: the comment
    above the list says "EVERY PATH OUTSIDE deploy/canary THAT THIS SCRIPT
    READS", and nothing was checking the word *every*.
    """

    # `root / "deploy" / "observability"` and `(root / "src")`, as written.
    READ = re.compile(r'root\s*/\s*"([a-z]+)"(?:\s*/\s*"([a-z-]+)")?')

    def paths_read(self) -> set[str]:
        source = Path(canary.__file__).read_text(encoding="utf-8")
        found = set()
        for first, second in self.READ.findall(source):
            # Two segments where there are two, because the declarable unit is
            # not always the top level: `deploy` is too wide to be correct,
            # since deploy/compose must not trigger this gate. The assertion
            # below accepts a declared entry that is a prefix, so a
            # one-segment declaration still covers a two-segment read where
            # that is what somebody meant. Same rule as check.py's copy.
            found.add(f"{first}/{second}" if second else first)
        return found

    def test_the_scan_finds_the_reads_it_is_checking(self) -> None:
        """The subject, before the assertion. A regex that matched nothing
        would pass the test below against any list at all."""
        self.assertIn("deploy/observability", self.paths_read())
        self.assertIn("src", self.paths_read())

    def test_every_path_the_script_reads_is_declared(self) -> None:
        for path in sorted(self.paths_read()):
            with self.subTest(path=path):
                self.assertTrue(
                    any(path == entry or path.startswith(f"{entry}/")
                        for entry in canary.SOURCE_INPUTS),
                    f"canary.py opens {path!r} and SOURCE_INPUTS does not declare it, "
                    f"so deploy.yml's triggers do not watch it: {canary.SOURCE_INPUTS}",
                )


class ReadingTests(unittest.TestCase):
    """`read_prometheus.query`'s silences, which decide promote or roll back.

    Every case here returns `None`, and `analyse` reads `None` as a rollback —
    so the test is really that a reading which cannot be trusted never reaches
    the verdict as a number. Two of the four were promoted as healthy before
    this pass.
    """

    def response(self, payload: bytes):
        """A stand-in for `urlopen`'s context manager over a fixed body."""
        class Response:
            def read(self):
                return payload

            def __enter__(self):
                return self

            def __exit__(self, *_):
                return False

        return lambda *_args, **_kwargs: Response()

    def query(self, payload: bytes):
        original = read_prometheus.urllib.request.urlopen
        read_prometheus.urllib.request.urlopen = self.response(payload)
        try:
            return read_prometheus.query("http://prometheus.invalid", "up")
        finally:
            read_prometheus.urllib.request.urlopen = original

    def test_a_healthy_sample_is_a_number(self) -> None:
        body = b'{"status":"success","data":{"result":[{"value":[0,"0.25"]}]}}'

        self.assertEqual(self.query(body), 0.25)

    def test_a_body_that_does_not_parse_is_absent(self) -> None:
        """The docstring promised this and the code did not do it: `json.loads`
        raised straight past the function. A proxy returning an HTML error page
        with a 200 is the ordinary way it happens."""
        self.assertIsNone(self.query(b"<html>502 Bad Gateway</html>"))

    def test_nan_is_absent(self) -> None:
        """`histogram_quantile` over a histogram with no observations. NaN
        compares false against every threshold, so read as a number it passes
        the absolute and relative checks alike."""
        body = b'{"status":"success","data":{"result":[{"value":[0,"NaN"]}]}}'

        self.assertIsNone(self.query(body))

    def test_negative_infinity_is_absent(self) -> None:
        """The dangerous one, and the one `!= self` missed. `-Inf` is below
        every absolute threshold and below any multiple of the baseline — not
        merely admitted, but excellent-looking."""
        body = b'{"status":"success","data":{"result":[{"value":[0,"-Inf"]}]}}'

        self.assertIsNone(self.query(body))

    def test_positive_infinity_is_absent(self) -> None:
        body = b'{"status":"success","data":{"result":[{"value":[0,"+Inf"]}]}}'

        self.assertIsNone(self.query(body))

    def test_an_empty_result_is_absent(self) -> None:
        self.assertIsNone(self.query(b'{"status":"success","data":{"result":[]}}'))

    def test_a_refusal_is_not_a_silence(self) -> None:
        """Prometheus answering `error` means the monitoring stack is
        reachable and disagreeing, which is a stopped rollout rather than an
        unobserved canary."""
        body = b'{"status":"error","error":"parse error"}'

        with self.assertRaises(RuntimeError):
            self.query(body)


class FetchTests(unittest.TestCase):
    """`read_prometheus.read` fetches what the workload declares and no more.

    A run that fetched every signal would hand `analyse` a consume reading for
    the gateway, which consumes nothing — an absent series, read as a rollback
    on a release that is behaving perfectly.
    """

    def read(self, workload: str) -> dict:
        asked = []

        def record(_base, expression):
            asked.append(expression)
            return 1.0

        original = read_prometheus.query
        read_prometheus.query = record
        try:
            return read_prometheus.read(
                "http://prometheus.invalid", workload, "10m", canary.load_plan())
        finally:
            read_prometheus.query = original

    def test_an_http_only_workload_reads_only_the_http_signal(self) -> None:
        readings = self.read("gateway")

        self.assertEqual(set(readings["canary"]), {"http"})
        self.assertEqual(set(readings["baseline"]), {"http"})

    def test_a_consume_only_workload_reads_only_the_consume_signal(self) -> None:
        readings = self.read("inventory-api")

        self.assertEqual(set(readings["canary"]), {"consume"})

    def test_both_tracks_and_every_signal_where_several_are_declared(self) -> None:
        readings = self.read("ordering-api")

        for track in ("canary", "baseline"):
            self.assertEqual(set(readings[track]), {"http", "consume", "saga"})
            for signal in ("http", "consume", "saga"):
                self.assertEqual(
                    set(readings[track][signal]),
                    {"errorRate", "latencyP99Seconds", "requests"},
                )

    def test_the_service_name_comes_from_the_plan(self) -> None:
        """One source for it. A name passed beside the workload key is two
        spellings of one fact, and a rollout that can only ever fail when
        they part."""
        asked = []

        def record(_base, expression):
            asked.append(expression)
            return 1.0

        original = read_prometheus.query
        read_prometheus.query = record
        try:
            read_prometheus.read(
                "http://x", "inventory-api", "10m", canary.load_plan())
        finally:
            read_prometheus.query = original

        self.assertTrue(all("Inventory.Api" in e for e in asked), asked)
        self.assertFalse(any("$SERVICE" in e for e in asked), asked)

    def test_an_unknown_workload_is_refused(self) -> None:
        """The name is deliberately not a service's. Spelled as one a service
        could take, this test passes until that service joins the plan and
        then reaches the network instead of the refusal it asserts — which is
        what `payments-api` did here. The absence is asserted first, so the
        fixture cannot go stale silently a second time."""
        unknown = "no-such-workload"
        self.assertNotIn(unknown, canary.entries(canary.load_plan()["workloads"]))

        with self.assertRaises(KeyError):
            read_prometheus.read("http://x", unknown, "10m", canary.load_plan())


class CommentTests(unittest.TestCase):
    def test_comment_keys_are_not_data(self) -> None:
        """JSON has no comments and every number in the plan is a decision
        somebody has to be able to re-take. Filtered in one place, because
        forgetting it once turns a comment into a workload with no service
        name."""
        self.assertEqual(
            canary.entries({"$comment": ["why"], "gateway": {}}),
            {"gateway": {}},
        )

    def test_the_shipped_plan_actually_uses_them(self) -> None:
        """If the comments were ever stripped out, the filter above would stop
        being exercised by anything real."""
        raw = json.loads(Path(canary.PLAN_PATH).read_text(encoding="utf-8"))

        self.assertIn("$comment", raw)


class ImageRevisionTests(unittest.TestCase):
    """An image tag names a commit, because §15.2 builds it that way."""

    def test_a_commit_sha_is_the_revision(self) -> None:
        self.assertEqual("a" * 40, canary.image_revision("a" * 40))

    def test_a_version_tag_names_no_revision(self) -> None:
        with self.assertRaises(canary.PlanError) as raised:
            canary.image_revision("1.4.2")

        self.assertIn("§15.2", str(raised.exception))

    def test_an_abbreviation_is_refused(self) -> None:
        """It resolves against the history like the full name does, and then
        the rollout reports a revision spelled differently from the tag it
        handed to Helm — two strings for the one fact ADR-050 is about."""
        with self.assertRaises(canary.PlanError):
            canary.image_revision("a" * 12)

    def test_upper_case_is_refused(self) -> None:
        """git prints lower case, and the comparison is textual."""
        with self.assertRaises(canary.PlanError):
            canary.image_revision("A" * 40)

    def test_the_tag_alphabet_admits_what_names_no_revision(self) -> None:
        """validate_tag is the Helm-injection guard, and a version tag and an
        abbreviated commit both pass it — which is why ADR-050's rule is a
        second check rather than a tightening of that one. Upper case is the
        one refusal the two share, and for the unrelated reason that a tag is
        a DNS-1123 label."""
        for tag in ("1.4.2", "a" * 12):
            with self.subTest(tag=tag):
                canary.validate_tag(tag)


SHA = "sha256:" + "a" * 64


def verification(*digests: str) -> list[dict]:
    """The document `cosign verify -o json` prints: one entry per signature."""
    return [
        {"critical": {"image": {"docker-manifest-digest": digest}, "type": "cosign container image signature"}}
        for digest in digests
    ]


class DigestTests(unittest.TestCase):
    """The digest as Helm's parser would read it, as the tag is above."""

    def test_a_registry_digest_is_accepted(self) -> None:
        canary.validate_digest(SHA)

    def test_what_strvals_would_split_is_refused(self) -> None:
        for digest in (SHA + ",image.registry=attacker.example", SHA + "\n", "sha256:" + "A" * 64,
                       "sha256:" + "a" * 63, "sha512:" + "a" * 64, "", "latest"):
            with self.subTest(digest=digest):
                with self.assertRaises(canary.PlanError):
                    canary.validate_digest(digest)

    def test_it_is_the_chart_s_rule(self) -> None:
        """`commerce.digest` refuses what this does not, and the rollout would
        fail inside `helm upgrade` instead of in front of it."""
        helpers = (canary.ROOT / "deploy/helm/common/templates/_helpers.tpl").read_text(encoding="utf-8")

        self.assertIn(r'regexMatch "^sha256:[a-f0-9]{64}$"', helpers)

    def test_the_digest_cosign_verified_is_the_one_read(self) -> None:
        self.assertEqual(SHA, canary.verified_digest(verification(SHA, SHA)))

    def test_real_cosign_output_is_read(self) -> None:
        """What cosign v3.1.3 printed for the first image the pipeline signed.

        A hand-built document shows the reader agrees with its author, and
        this one shows it agrees with cosign.
        """
        path = canary.CANARY / "fixtures" / "cosign-verify-catalog-api.json"
        document = json.loads(path.read_text(encoding="utf-8"))

        self.assertEqual(
            "sha256:d07220003b94411646a922059a69eaa79f5a7ec395357e11520224d229936bfd",
            canary.verified_digest(document))
        self.assertEqual(3, len(document))

    def test_real_cosign_output_with_one_entry_over_another_digest_is_refused(self) -> None:
        path = canary.CANARY / "fixtures" / "cosign-verify-catalog-api.json"
        document = json.loads(path.read_text(encoding="utf-8"))
        document[1]["critical"]["image"]["docker-manifest-digest"] = "sha256:" + "b" * 64

        with self.assertRaises(canary.PlanError):
            canary.verified_digest(document)

    def test_nothing_verified_is_no_digest(self) -> None:
        for document in ([], {}, None, "sha256"):
            with self.subTest(document=document):
                with self.assertRaises(canary.PlanError):
                    canary.verified_digest(document)

    def test_signatures_over_two_digests_pin_neither(self) -> None:
        with self.assertRaises(canary.PlanError) as raised:
            canary.verified_digest(verification(SHA, "sha256:" + "b" * 64))

        self.assertIn("2 different digests", str(raised.exception))

    def test_an_entry_without_the_digest_is_refused_rather_than_skipped(self) -> None:
        with self.assertRaises(canary.PlanError):
            canary.verified_digest(verification(SHA) + [{"critical": {}}])

    def test_a_verified_digest_that_is_not_one_is_refused(self) -> None:
        with self.assertRaises(canary.PlanError):
            canary.verified_digest(verification("sha256:short"))


class WorkloadImageTests(unittest.TestCase):
    """The rollout verifies the images the chart pulls, so the chart is the list."""

    def test_a_service_pulls_its_host_and_its_migrator(self) -> None:
        plan = canary.load_plan()

        self.assertEqual(
            {"api": "catalog-api", "migrator": "catalog-migrator"},
            canary.workload_images("catalog-api", plan))
        self.assertEqual(
            {"api": "shipping-worker", "migrator": "shipping-migrator"},
            canary.workload_images("shipping", plan))

    def test_the_bff_s_migrator_is_not_named_for_its_host(self) -> None:
        self.assertEqual(
            {"api": "web-bff", "migrator": "bff-migrator"},
            canary.workload_images("web-bff", canary.load_plan()))

    def test_the_gateway_has_no_migrator(self) -> None:
        self.assertEqual({"api": "gateway"}, canary.workload_images("gateway", canary.load_plan()))

    def test_a_migrator_is_exactly_where_the_descriptor_says_there_is_one(self) -> None:
        """Two owners of one fact: the descriptor feeds smoke.sh and the chart
        feeds the rollout, and a workload one of them gives a migrator and the
        other does not is an image verified for nobody or pulled unverified."""
        plan = canary.load_plan()
        for name, descriptor in canary.entries(plan["workloads"]).items():
            with self.subTest(workload=name):
                self.assertEqual(
                    descriptor["smoke"]["migrator"],
                    "migrator" in canary.workload_images(name, plan))

    def test_an_unknown_workload_is_refused(self) -> None:
        with self.assertRaises(canary.PlanError):
            canary.workload_images("nonesuch", canary.load_plan())

    def test_a_chart_with_no_api_image_is_refused_rather_than_read_as_empty(self) -> None:
        root = Path(tempfile.mkdtemp())
        (root / "deploy" / "helm" / "x").mkdir(parents=True)
        (root / "deploy" / "helm" / "x" / "values.yaml").write_text(
            "image:\n  # api: commented-out\n  tag: \"\"\n", encoding="utf-8")

        with self.assertRaises(canary.PlanError):
            canary.workload_images("x", {"workloads": {"x": {"chart": "x"}}}, root)

    def test_a_key_outside_the_image_block_is_not_an_image(self) -> None:
        root = Path(tempfile.mkdtemp())
        (root / "deploy" / "helm" / "x").mkdir(parents=True)
        (root / "deploy" / "helm" / "x" / "values.yaml").write_text(
            "image:\n  api: x-api\nworker:\n  migrator: not-an-image\n", encoding="utf-8")

        self.assertEqual(
            {"api": "x-api"},
            canary.workload_images("x", {"workloads": {"x": {"chart": "x"}}}, root))


class ImageSourceTests(unittest.TestCase):
    """The facts that describe the image are read from the tree given."""

    @staticmethod
    def _tree(route: str) -> Path:
        return service_tree({"Svc.Api/Health.cs": f'app.MapHealthChecks("{route}");\n'})

    def test_the_exclusion_is_the_given_trees_routes(self) -> None:
        self.assertEqual("/healthz/live", canary.probe_exclusion(self._tree("/healthz/live")))

    def test_two_revisions_give_two_exclusions(self) -> None:
        """ADR-050's defect in one assertion. The same code over two trees
        produces two different exclusions, so which tree it reads decides
        which requests the rung counts as traffic."""
        self.assertNotEqual(
            canary.probe_exclusion(self._tree("/healthz/live")),
            canary.probe_exclusion(self._tree("/health/live")),
        )

    def test_the_queries_carry_the_given_trees_exclusion(self) -> None:
        """Through `queries`, because that is the call read_prometheus makes
        and an exclusion nothing interpolates is not applied to anything."""
        self.assertIn(
            "/health/live",
            canary.queries("http", self._tree("/health/live"))["errorRate"],
        )

    def test_check_reads_the_rolled_workloads_hosts_from_the_source(self) -> None:
        """Check 4 asks whether each serviceName is an entry assembly. Of the
        image, for the workload being rolled: one renamed in the image and not
        in the checkout selects no series, and an absent series promotes
        nothing and rolls every rung back."""
        failures = canary.check(
            canary.load_plan(), source=self._tree("/healthz/live"),
            workload="catalog-api")

        self.assertEqual(
            ["catalog-api"],
            [f.split(".")[1].split(".serviceName")[0]
             for f in failures if "not an entry assembly" in f])

    def test_the_other_workloads_are_not_judged_against_this_image(self) -> None:
        """A tag names one workload's image. CI builds an image only for a
        service a commit changed, so an Ordering image legitimately predates a
        Catalog-only change — and judging Catalog's plan entry against it
        would refuse a rollout that is fine."""
        failures = canary.check(
            canary.load_plan(), source=self._tree("/healthz/live"),
            workload="ordering-api")

        self.assertEqual(
            [], [f for f in failures if "catalog-api" in f], failures)

    def test_a_source_without_a_workload_is_refused(self) -> None:
        """Because the alternative is one image's revision answering for every
        workload, which is the defect rather than a stricter reading of it."""
        failures = canary.check(
            canary.load_plan(), source=self._tree("/healthz/live"))

        self.assertTrue(
            any("not a workload in this plan" in f for f in failures), failures)

    def test_a_source_for_an_unknown_workload_is_refused(self) -> None:
        """A name no key matches scopes nothing: every tree would come from
        the checkout and the image would answer for none of them, which is
        the scoping absent rather than refused."""
        for name in ("", "catalog", "catalog-api "):
            with self.subTest(workload=name):
                failures = canary.check(
                    canary.load_plan(), source=self._tree("/healthz/live"),
                    workload=name)

                self.assertTrue(
                    any("not a workload in this plan" in f for f in failures),
                    failures)

    def test_an_http_only_workload_is_not_held_to_message_series(self) -> None:
        """series_read() spans every signal the plan defines, so holding one
        image to all of them refuses a gateway image for lacking a MassTransit
        meter it has no reason to carry. The tree registers a meter, because
        one with no registration file at all fails check 5 for being
        unreadable and would answer this question without scoping anything."""
        tree = self._tree("/healthz/live")
        web = tree / "src" / "BuildingBlocks" / "Common.Web"
        web.mkdir(parents=True)
        (web / "ObservabilityExtensions.cs").write_text(
            '.AddMeter("Commerce.Messaging")\n', encoding="utf-8")

        failures = canary.check(canary.load_plan(), source=tree, workload="gateway")

        self.assertEqual([], [f for f in failures if "MassTransit" in f], failures)

    def test_the_installed_image_has_to_build_the_judged_assembly(self) -> None:
        """§13.2 takes service.name from each image's entry assembly, and
        the baseline is the installed image. One from before a rename emits
        the old label, so the baseline query matches nothing and a healthy
        release rolls back at every rung."""
        candidate = self._tree("/healthz/live")
        installed = service_tree({"Svc.Api/Health.cs": 'app.MapHealthChecks("/x");\n'})

        failures = canary.check(
            canary.load_plan(), source=candidate, workload="catalog-api",
            installed=installed)

        self.assertTrue(
            any("installed release's image does not build" in f for f in failures),
            failures)

    def test_check_reads_its_charts_from_the_root(self) -> None:
        """The other half of the split. The chart is installed from the
        checkout, so it is judged there — a source tree with no deploy/ at
        all raises nothing about charts."""
        failures = canary.check(
            canary.load_plan(), source=self._tree("/healthz/live"),
            workload="catalog-api")

        self.assertEqual([], [f for f in failures if "not a chart under" in f])

    def test_the_default_source_is_the_checkout(self) -> None:
        """A pull request has no image and no tag, so `check` with one tree
        is the run the gate job makes."""
        self.assertEqual([], canary.check(canary.load_plan()))


class RolloutBindingTests(unittest.TestCase):
    """Check 12: the workflow hands the image's tree to what reads it."""

    def _workflow(self, text: str) -> Path:
        path = Path(tempfile.mkdtemp()) / "deploy.yml"
        path.write_text(text, encoding="utf-8")
        return path

    @property
    def shipped(self) -> str:
        return canary.WORKFLOW.read_text(encoding="utf-8")

    def test_the_shipped_workflow_is_bound(self) -> None:
        self.assertEqual([], canary._rollout_reads_the_image_source())

    def test_a_dropped_reading_source_fails_on_its_own(self) -> None:
        """The mutation ADR-050 is defended against: the commands still run
        and every other check still passes, so nothing but this one notices
        that the readings went back to the checkout's routes. The reading's
        flag is dropped alone, so this failure is its own rather than the
        gate's standing in for it."""
        without = "\n".join(
            line for line in self.shipped.splitlines()
            if line.strip() != '--source "$IMAGE_SOURCE" \\'
        )

        failures = canary._rollout_reads_the_image_source(self._workflow(without))

        self.assertEqual(1, len(failures), failures)
        self.assertIn("read_prometheus.py", failures[0])

    def test_a_dropped_gate_source_fails_on_its_own(self) -> None:
        """The other half, and the reason each is mutated separately: a run
        that removed both would report one diagnostic and leave the branch
        that produced the other unproved."""
        without = self.shipped.replace(
            '--source "$IMAGE_SOURCE" --installed-source', "--installed-source")

        failures = canary._rollout_reads_the_image_source(self._workflow(without))

        self.assertEqual(1, len(failures), failures)
        self.assertIn("canary.py check", failures[0])

    def test_a_dropped_export_fails(self) -> None:
        without = "\n".join(
            line for line in self.shipped.splitlines() if "IMAGE_SOURCE=" not in line
        )

        failures = canary._rollout_reads_the_image_source(self._workflow(without))

        self.assertTrue(any("GITHUB_ENV" in f for f in failures), failures)

    def test_a_reading_that_is_not_run_at_all_fails(self) -> None:
        """The gate's own subject, on checks 6's terms: a workflow this could
        find nothing in would report a bound rollout it never looked at."""
        without = "\n".join(
            line for line in self.shipped.splitlines() if "read_prometheus.py" not in line
        )

        failures = canary._rollout_reads_the_image_source(self._workflow(without))

        self.assertTrue(any("nowhere" in f for f in failures), failures)

    def test_a_commented_out_binding_binds_nothing(self) -> None:
        """This file argues at length about the commands it runs, so a check
        reading its prose would pass on the argument for the thing."""
        commented = "\n".join(
            "# " + line if "read_prometheus.py" in line or "IMAGE_SOURCE=" in line else line
            for line in self.shipped.splitlines()
        )

        self.assertTrue(canary._rollout_reads_the_image_source(self._workflow(commented)))

    def test_a_near_miss_variable_is_not_the_binding(self) -> None:
        """The near miss, and the exports are left correct on purpose: with
        them renamed too the export check reports the fault and this one
        never has to. `--source "$OLD_IMAGE_SOURCE"` passes a different tree
        while containing the flag and the name, so a substring test reads it
        as bound. The whole argument is matched instead."""
        near = self.shipped.replace('"$IMAGE_SOURCE"', '"$OLD_IMAGE_SOURCE"')

        failures = canary._rollout_reads_the_image_source(self._workflow(near))

        self.assertTrue(
            any("read_prometheus.py" in f for f in failures), failures)

    def test_a_dropped_installed_source_fails_on_its_own(self) -> None:
        """The stable track's tree is a second argument and goes missing the
        same way the first one can."""
        without = "\n".join(
            line for line in self.shipped.splitlines()
            if line.strip() != '--installed-source "$INSTALLED_SOURCE" \\'
        )

        failures = canary._rollout_reads_the_image_source(self._workflow(without))

        self.assertEqual(1, len(failures), failures)
        self.assertIn("--installed-source", failures[0])

    def test_an_export_that_no_archive_wrote_fails(self) -> None:
        """The near miss with the right name on it. Exporting the checkout
        satisfies every name and argument check while the rollout reads
        exactly the tree the binding exists to stop it reading."""
        swapped = self.shipped.replace(
            'IMAGE_SOURCE=$RUNNER_TEMP/image-source',
            'IMAGE_SOURCE=$GITHUB_WORKSPACE')

        failures = canary._rollout_reads_the_image_source(self._workflow(swapped))

        self.assertTrue(any("no `git archive` writes" in f for f in failures),
                        failures)

    def test_a_tree_archived_from_another_revision_fails(self) -> None:
        """The other half: the path is the archive's and the archive is of
        something else. The export is then correct about the wrong tree.

        The comparison is positive, so the cases are not a list of spellings
        that are wrong: an unrelated variable is refused exactly as `HEAD`
        is, which a denylist of checkout spellings could not do."""
        for revision in ("HEAD", "$GITHUB_SHA", "$OTHER_REVISION", "v1.2.3"):
            with self.subTest(revision=revision):
                swapped = self.shipped.replace(
                    'git archive "$REVISION" src', f"git archive {revision} src")

                failures = canary._rollout_reads_the_image_source(
                    self._workflow(swapped))

                self.assertTrue(
                    any(f"rather than {canary.RESOLVED_REVISION}" in f
                        for f in failures), failures)

    def test_the_right_name_with_the_wrong_value_fails(self) -> None:
        """One level deeper, and the last place the binding can be true in
        name only: the archive is of the expected variable, and the step
        sets that variable from the checkout rather than from the tag."""
        swapped = self.shipped.replace(
            'REVISION=$(python deploy/canary/canary.py revision --value "$TAG")',
            "REVISION=$GITHUB_SHA", 1)

        failures = canary._rollout_reads_the_image_source(self._workflow(swapped))

        self.assertTrue(
            any("does not set from `canary.py revision`" in f for f in failures),
            failures)

    def test_a_gate_run_missing_either_companion_fails(self) -> None:
        """A tree handed over without the workload it answers for is a
        refusal inside check(), and one handed over without the release it
        replaces leaves the baseline half unjudged. Both are the same run,
        not merely the same file."""
        for dropped in (' --workload "$WORKLOAD"',
                        ' --installed-source "$INSTALLED_SOURCE"'):
            with self.subTest(dropped=dropped.strip()):
                without = self.shipped.replace(dropped.strip() + " " + chr(92), "")
                without = without.replace(dropped, "")

                failures = canary._rollout_reads_the_image_source(
                    self._workflow(without))

                self.assertTrue(
                    any(dropped.split()[0] in f for f in failures), failures)

    def test_a_revision_resolved_from_the_wrong_value_fails(self) -> None:
        """One level below the archive again: `canary.py revision` is
        invoked, and handed something that is not the tag."""
        swapped = self.shipped.replace(
            'revision --value "$TAG"', 'revision --value "$GITHUB_SHA"', 1)

        failures = canary._rollout_reads_the_image_source(self._workflow(swapped))

        self.assertTrue(
            any("does not set from `canary.py revision`" in f for f in failures),
            failures)

    def test_a_tag_from_anywhere_but_the_release_fails(self) -> None:
        """And below that: the stable track's tag is the running
        release's or it is a name somebody put there."""
        swapped = self.shipped.replace(
            'TAG=$(python deploy/canary/canary.py installed-tag',
            'TAG=$(echo installed-tag', 1)

        failures = canary._rollout_reads_the_image_source(self._workflow(swapped))

        self.assertTrue(
            any("other than `canary.py installed-tag`" in f for f in failures),
            failures)

    def test_a_continuation_is_read_as_one_invocation(self) -> None:
        """The flag is never on the line the command is named on."""
        lines = ["  run: |", "    python read_prometheus.py \\", '      --source "$IMAGE_SOURCE"']

        self.assertEqual(
            ['python read_prometheus.py --source "$IMAGE_SOURCE"'],
            canary._invocations(lines, "read_prometheus.py"),
        )


class InstalledTagTests(unittest.TestCase):
    """The stable track's tag is read from the release, not assumed."""

    def test_the_tag_is_read(self) -> None:
        self.assertEqual("a" * 40, canary.installed_tag({"image": {"tag": "a" * 40}}))

    def test_a_release_with_no_tag_is_refused(self) -> None:
        """Every chart requires the tag, so a release without one is not a
        default to fall back on — it is a release whose revision cannot be
        read, and the baseline's probe routes come from that revision."""
        for values in ({}, {"image": {}}, {"image": {"tag": "   "}}, {"image": {"tag": 7}}):
            with self.subTest(values=values):
                with self.assertRaises(canary.PlanError):
                    canary.installed_tag(values)


class TrackSourceTests(unittest.TestCase):
    """Each track's probe exclusion is scanned from its own image (ADR-050)."""

    @staticmethod
    def _tree(route: str) -> Path:
        return service_tree({"Svc.Api/Health.cs": f'app.MapHealthChecks("{route}");\n'})

    def _expressions(self, **trees) -> list[str]:
        """Every PromQL `read` emits, with the fetch replaced."""
        seen: list[str] = []
        original = read_prometheus.query
        read_prometheus.query = lambda _base, expression: seen.append(expression) or 1.0
        try:
            read_prometheus.read(
                "http://prometheus.invalid", "catalog-api", "10m",
                canary.load_plan(), **trees)
        finally:
            read_prometheus.query = original
        return seen

    def test_each_track_reads_the_routes_of_the_image_it_runs(self) -> None:
        """One exclusion for both tracks filters the stable track's probes
        by the candidate's routes, so a probe the stable image serves and
        the candidate does not is counted as its business traffic."""
        seen = self._expressions(
            candidate=self._tree("/healthz/live"), installed=self._tree("/health/ready"))

        canary_side = [e for e in seen if 'deployment_track="canary"' in e and "http_route" in e]
        stable_side = [e for e in seen if 'deployment_track="stable"' in e and "http_route" in e]

        self.assertTrue(canary_side and stable_side, seen)
        self.assertTrue(all("/healthz/live" in e for e in canary_side), canary_side)
        self.assertTrue(all("/health/ready" in e for e in stable_side), stable_side)
        self.assertFalse(any("/health/ready" in e for e in canary_side), canary_side)

    def test_an_absent_installed_tree_falls_back_to_the_candidate(self) -> None:
        """Stated rather than assumed: the gate path has one tree, and the
        rollout is held to supplying both by check 12."""
        seen = self._expressions(candidate=self._tree("/healthz/live"))

        self.assertTrue(all("/healthz/live" in e for e in seen if "http_route" in e), seen)

    def test_main_carries_both_trees_into_read(self) -> None:
        """Without this, dropping either argument from main() leaves every
        other test green and silently restores the checkout's routes."""
        seen: dict[str, Path] = {}

        def spy(_base, _workload, _window, _plan, candidate=None, installed=None):
            seen.update(candidate=candidate, installed=installed)
            return {}

        original_read, original_url = read_prometheus.read, os.environ.get("PROMETHEUS_URL")
        read_prometheus.read = spy
        os.environ["PROMETHEUS_URL"] = "http://prometheus.invalid"
        try:
            with tempfile.TemporaryDirectory() as out,                     contextlib.redirect_stdout(io.StringIO()):
                read_prometheus.main([
                    "read_prometheus.py", "--workload", "catalog-api", "--window", "10m",
                    "--source", "candidate-tree", "--installed-source", "stable-tree",
                    "--out", str(Path(out) / "readings.json"),
                ])
        finally:
            read_prometheus.read = original_read
            if original_url is None:
                del os.environ["PROMETHEUS_URL"]
            else:
                os.environ["PROMETHEUS_URL"] = original_url

        self.assertEqual(Path("candidate-tree"), seen["candidate"])
        self.assertEqual(Path("stable-tree"), seen["installed"])


if __name__ == "__main__":
    unittest.main()
