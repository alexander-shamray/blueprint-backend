"""The canary's weight arithmetic and its verdict, which are the ones that can promote a
release wrongly, and the readings the verdict is given."""

from __future__ import annotations

import re
import unittest

import analysis
import csharp_sources
import descriptor_model
import read_prometheus
import repo_gate
from canary_testing import readings


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


def selectors(expression: str) -> list[tuple[str, list[tuple[str, str, str]]]]:
    """Each series selector in a rendered query, with its matchers."""
    return [
        (series, re.findall(r'([a-z_]+)(=~|!~|!=|=)"([^"]*)"', braces))
        for series, braces in re.findall(r"([a-z_]+)\{([^}]*)\}", expression)
    ]


class WeightTests(unittest.TestCase):
    def test_five_percent_is_not_expressible_at_the_chart_default(self) -> None:
        """§15.3's replicaCount is 3, so one canary pod already serves 25% —
        five times §15.5's first rung. The refusal is the deliverable: rounding
        would have written 5% and shipped five times the blast radius."""
        with self.assertRaises(descriptor_model.PlanError) as raised:
            analysis.plan(5, stable_replicas=3, overshoot_points=0)

        message = str(raised.exception)
        self.assertIn("19", message, "the refusal must name the count that would work")
        self.assertIn("25.0%", message)

    def test_five_percent_is_expressible_at_nineteen(self) -> None:
        """And 19 + 1 is 20, which is the maxReplicas of every chart that
        autoscales but one — the gateway's is 30, because every external
        request passes through it, and a worker's sets a replica count
        instead of autoscaling (§15.3). The 19 is what the weight costs, and only on
        those charts is it also all the chart allows."""
        result = analysis.plan(5, stable_replicas=19, overshoot_points=0)

        self.assertEqual(result["canaryReplicas"], 1)
        self.assertEqual(result["achieved"], 5.0)

    def test_the_needed_count_actually_satisfies_the_check_that_named_it(self) -> None:
        """`plan` and `required_stable` are one derivation used twice, and this
        is why. Two derivations of one figure is how a message ends up naming a
        replica count that the check printing it would still reject."""
        for weight in (1, 2, 5, 10, 20, 25, 33, 50, 75, 99):
            needed = analysis.required_stable(weight, overshoot_points=0)
            result = analysis.plan(weight, stable_replicas=needed, overshoot_points=0)
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
                    result = analysis.plan(weight, stable, overshoot_points=0)
                except descriptor_model.PlanError:
                    continue
                self.assertLessEqual(result["achieved"], weight)

    def test_a_canary_is_never_zero_pods(self) -> None:
        """`ceil` of a fraction under one is one, and rounding down is not
        available: zero canary pods is not a canary, it is a step that reports
        a weight and serves none of it."""
        result = analysis.plan(1, stable_replicas=99, overshoot_points=0)

        self.assertGreaterEqual(result["canaryReplicas"], 1)

    def test_the_last_rung_retires_the_stable_track(self) -> None:
        """100% is the end of the rollout rather than a weight. Expressed as
        pods it would ask for an infinite canary against a track that is about
        to go away."""
        result = analysis.plan(100, stable_replicas=19, overshoot_points=0)

        self.assertTrue(result["final"])
        self.assertEqual(result["stableReplicas"], 0)

    def test_a_tolerance_buys_a_coarser_first_step(self) -> None:
        """The knob exists so the refusal is a decision and not a wall — and
        canary.json sets it to zero, so taking that decision is an edit
        somebody signs."""
        result = analysis.plan(5, stable_replicas=3, overshoot_points=20)

        self.assertEqual(result["achieved"], 25.0)

    def test_nonsense_weights_are_refused(self) -> None:
        for weight in (0, -5, 101):
            with self.assertRaises(descriptor_model.PlanError):
                analysis.plan(weight, stable_replicas=19, overshoot_points=0)


class VerdictTests(unittest.TestCase):
    def test_a_healthy_step_promotes(self) -> None:
        verdict = analysis.analyse(readings(), THRESHOLDS, HTTP)

        self.assertEqual(verdict["decision"], analysis.PROMOTE)

    def test_an_absent_series_rolls_back_on_either_track(self) -> None:
        """§15.1 says the same of the k6 SLO run: it "fails on an absent series as
        well as on a breached one". Six cases, not three: `read` runs each query
        independently, so one malformed response can null one metric.
        """
        for track in ("canary", "baseline"):
            for metric in ("errorRate", "latencyP99Seconds", "requests"):
                with self.subTest(track=track, metric=metric):
                    document = readings(**{track: {"http": {metric: None}}})
                    verdict = analysis.analyse(document, THRESHOLDS, HTTP)

                    self.assertEqual(verdict["decision"], analysis.ROLLBACK)
                    self.assertIn(metric, verdict["reason"])
                    self.assertIn(track, verdict["reason"])

    def test_an_absent_consume_series_rolls_back(self) -> None:
        """The defect this signal exists for, from the other end: a release
        whose consumers are broken publishes no consume series, and the
        verdict has to read that as a rollback rather than as quiet."""
        document = readings(signals=("http", "consume"))
        document["canary"]["consume"]["requests"] = None

        verdict = analysis.analyse(document, THRESHOLDS, SIGNALS)

        self.assertEqual(verdict["decision"], analysis.ROLLBACK)
        self.assertIn("consume", verdict["reason"])

    def test_a_whole_signal_missing_from_the_readings_rolls_back(self) -> None:
        """A declared signal nobody fetched is not a signal that passed."""
        document = readings(signals=("http", "consume"), canary={"consume": None})

        verdict = analysis.analyse(document, THRESHOLDS, SIGNALS)

        self.assertEqual(verdict["decision"], analysis.ROLLBACK)
        self.assertIn("consume", verdict["reason"])

    def test_readings_carrying_an_undeclared_signal_roll_back(self) -> None:
        """`read_prometheus` fetches what the plan declares, so a reading for
        a signal this workload does not declare means the two disagree about
        what is being judged — which is the question, not a detail."""
        document = readings(signals=("http", "consume"))

        verdict = analysis.analyse(document, THRESHOLDS, HTTP)

        self.assertEqual(verdict["decision"], analysis.ROLLBACK)
        self.assertIn("consume", verdict["reason"])

    def test_a_workload_declaring_no_signal_rolls_back(self) -> None:
        """There is no third verdict, so "nothing to judge" resolves to the
        rollback like every other doubt."""
        verdict = analysis.analyse(readings(), THRESHOLDS, {})

        self.assertEqual(verdict["decision"], analysis.ROLLBACK)

    def test_a_missing_canary_track_rolls_back(self) -> None:
        verdict = analysis.analyse(readings(canary=None), THRESHOLDS, HTTP)

        self.assertEqual(verdict["decision"], analysis.ROLLBACK)

    def test_too_little_traffic_rolls_back(self) -> None:
        """Five per cent of a quiet ten minutes can be four requests, and four
        requests cannot tell a 1% error rate from a 0% one. Promoting there is
        promoting on no evidence and reporting a green analysis."""
        verdict = analysis.analyse(
            readings(canary={"http": {"requests": 40.0}}), THRESHOLDS, HTTP)

        self.assertEqual(verdict["decision"], analysis.ROLLBACK)
        self.assertIn("40", verdict["reason"])

    def test_a_quiet_consume_signal_rolls_back(self) -> None:
        """A declared signal below its minimum is the same silence as an HTTP
        series nobody published, and it is declared precisely so that it
        cannot pass by being empty."""
        document = readings(signals=("http", "consume"))
        document["canary"]["consume"]["requests"] = 3.0

        verdict = analysis.analyse(document, THRESHOLDS, SIGNALS)

        self.assertEqual(verdict["decision"], analysis.ROLLBACK)
        self.assertIn("consume", verdict["reason"])

    def test_the_minimum_is_the_smallest_sample_that_can_express_the_threshold(self) -> None:
        """minimumRequests is 1/errorRate rather than a number somebody liked:
        below it, one failure is already more than the threshold."""
        self.assertEqual(THRESHOLDS["minimumRequests"], 1 / THRESHOLDS["errorRate"])

    def test_breaching_the_alert_threshold_rolls_back(self) -> None:
        """The absolute check is §13.6's own number. A canary tuned looser
        would promote a release and then page about it."""
        verdict = analysis.analyse(
            readings(canary={"http": {"errorRate": 0.02}}), THRESHOLDS, HTTP)

        self.assertEqual(verdict["decision"], analysis.ROLLBACK)
        self.assertIn("pages", verdict["reason"])

    def test_a_consume_fault_rate_breach_rolls_back(self) -> None:
        """The fault rate is the one absolute threshold consume keeps, and it
        is the same §13.6 number the HTTP signal is held to."""
        document = readings(signals=("http", "consume"))
        document["canary"]["consume"]["errorRate"] = 0.02

        verdict = analysis.analyse(document, THRESHOLDS, SIGNALS)

        self.assertEqual(verdict["decision"], analysis.ROLLBACK)
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

        verdict = analysis.analyse(document, THRESHOLDS, WITH_SAGA)

        self.assertEqual(verdict["decision"], analysis.ROLLBACK)
        self.assertIn("saga", verdict["reason"])
        self.assertNotIn("consume", verdict["reason"])

    def test_a_quiet_saga_signal_rolls_back(self) -> None:
        document = readings(signals=("http", "consume", "saga"))
        document["canary"]["saga"]["requests"] = 3.0

        verdict = analysis.analyse(document, THRESHOLDS, WITH_SAGA)

        self.assertEqual(verdict["decision"], analysis.ROLLBACK)
        self.assertIn("saga", verdict["reason"])

    def test_breaching_the_latency_threshold_rolls_back(self) -> None:
        verdict = analysis.analyse(
            readings(canary={"http": {"latencyP99Seconds": 1.5}}), THRESHOLDS, HTTP)

        self.assertEqual(verdict["decision"], analysis.ROLLBACK)

    def test_consume_is_not_held_to_the_http_latency_threshold(self) -> None:
        """§13.6's Latency alert is about what a user waits for, and no alert
        owns a consume-duration number. A consumer slower than a second is
        judged against the stable track and against nothing else (ADR-047)."""
        document = readings(signals=("http", "consume"))
        for track in ("canary", "baseline"):
            document[track]["consume"]["latencyP99Seconds"] = 4.0

        verdict = analysis.analyse(document, THRESHOLDS, SIGNALS)

        self.assertEqual(verdict["decision"], analysis.PROMOTE)

    def test_a_consume_duration_regression_still_rolls_back(self) -> None:
        """Dropping the absolute number does not drop the comparison: the
        stable track needs no alert to own it."""
        document = readings(signals=("http", "consume"))
        document["canary"]["consume"]["latencyP99Seconds"] = 4.0
        document["baseline"]["consume"]["latencyP99Seconds"] = 0.5

        verdict = analysis.analyse(document, THRESHOLDS, SIGNALS)

        self.assertEqual(verdict["decision"], analysis.ROLLBACK)
        self.assertIn("stable track", verdict["reason"])

    def test_a_regression_inside_the_threshold_still_rolls_back(self) -> None:
        """§15.5 says "regresses", not "breaches". A canary at four times the
        stable track's error rate is a bad release even while both are under
        the number that pages."""
        verdict = analysis.analyse(
            readings(canary={"http": {"errorRate": 0.008}},
                     baseline={"http": {"errorRate": 0.001}}),
            THRESHOLDS,
            HTTP,
        )

        self.assertEqual(verdict["decision"], analysis.ROLLBACK)
        self.assertIn("stable track", verdict["reason"])

    def test_noise_under_the_floor_does_not_roll_back(self) -> None:
        """Without the floor every quiet service rolls back for ever: a
        baseline of 0.0001 against a canary of 0.0004 is four times worse and
        is two requests."""
        verdict = analysis.analyse(
            readings(canary={"http": {"errorRate": 0.0004}},
                     baseline={"http": {"errorRate": 0.0001}}),
            THRESHOLDS,
            HTTP,
        )

        self.assertEqual(verdict["decision"], analysis.PROMOTE)

    def test_a_missing_baseline_rolls_back(self) -> None:
        """The stable track serves most traffic at every rung, and the
        error-rate numerator is coalesced, so a query returns nothing only
        when no requests were counted. Skipping the regression check there
        would drop it just when monitoring failed on the larger half. The rule
        is uniform: any absent reading is a rollback."""
        verdict = analysis.analyse(
            readings(baseline={"http": {"errorRate": None, "latencyP99Seconds": None}}),
            THRESHOLDS,
            HTTP,
        )

        self.assertEqual(verdict["decision"], analysis.ROLLBACK)
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
                analysis.analyse(document, THRESHOLDS, HTTP)["reason"].strip())


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
            for role, template in analysis.queries(signal).items()
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
        probes = ("http_route", "!~", csharp_sources.probe_exclusion())
        server_errors = ("http_response_status_code", "=~", "5..")
        for signal in analysis.SIGNALS:
            for role, template in analysis.queries(signal).items():
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
        template = analysis.queries("http")["errorRate"]
        numerator, denominator = template.split(" / ", 1)

        self.assertIn('http_response_status_code=~"5.."', numerator)
        self.assertNotIn("http_response_status_code", denominator)

    def test_the_templates_carry_all_three_substitutions(self) -> None:
        """A template that kept `$TRACK` literal matches no series, and an
        absent series rolls back — a rollout that can only ever fail."""
        for signal in analysis.SIGNALS:
            for role, template in analysis.queries(signal).items():
                with self.subTest(signal=signal, role=role):
                    for placeholder in ("$SERVICE", "$TRACK", "$WINDOW"):
                        self.assertIn(placeholder, template)

    def test_only_the_error_rate_numerator_is_coalesced(self) -> None:
        """A clean canary matches no numerator series, and PromQL carries the
        empty vector through the division; coalescing a count or a quantile
        would turn nobody-scraped-this into a healthy zero."""
        for signal in analysis.SIGNALS:
            for role, template in analysis.queries(signal).items():
                with self.subTest(signal=signal, role=role):
                    if role == "errorRate":
                        self.assertTrue(template.startswith("(sum("))
                        self.assertIn(") or vector(0)) / ", template)
                    else:
                        self.assertNotIn("or vector(0)", template)

    def test_the_templates_read_the_track_label_and_not_the_version(self) -> None:
        """service_version is not a discriminator: every build reports the
        same one, so a query on it compares a release against itself."""
        for signal in analysis.SIGNALS:
            for template in analysis.queries(signal).values():
                self.assertIn("deployment_track", template)
                self.assertNotIn("service_version", template)

    def test_the_message_templates_carry_no_route_selector(self) -> None:
        """A message has no route, so a selector on one would match nothing
        and roll every consume- or saga-judged canary back."""
        for signal in ("consume", "saga"):
            for template in analysis.queries(signal).values():
                self.assertNotIn("http_route", template)

    def test_the_signals_hold_adr_047s_thresholds(self) -> None:
        """http is held to both of §13.6's numbers; a message signal's
        duration is judged against the stable track only."""
        self.assertEqual(
            {name: set(signal["absolute"]) for name, signal in analysis.SIGNALS.items()},
            {"http": {"errorRate", "latencyP99Seconds"},
             "consume": {"errorRate"}, "saga": {"errorRate"}},
        )
        for signal in analysis.SIGNALS.values():
            self.assertTrue(set(signal["absolute"]) <= set(analysis.ABSOLUTE))

    def test_every_series_the_templates_read_is_vouched_for(self) -> None:
        self.assertEqual(
            repo_gate._metrics_are_vouched_for(
                sorted(analysis.series_read()), descriptor_model.ROOT, descriptor_model.ROOT), [])
        self.assertIn("messaging_masstransit_saga_errors_ea_total", analysis.series_read())


class ReadingTests(unittest.TestCase):
    """`read_prometheus.query`'s silences, which decide promote or roll back.
    Every case returns `None`, which `analyse` reads as a rollback; two of the
    four were promoted as healthy before this pass.
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
    the gateway, which consumes nothing: an absent series, read as a rollback.
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
                "http://prometheus.invalid", workload, "10m", descriptor_model.load_plan())
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
                "http://x", "inventory-api", "10m", descriptor_model.load_plan())
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
        self.assertNotIn(unknown, descriptor_model.entries(descriptor_model.load_plan()["workloads"]))

        with self.assertRaises(KeyError):
            read_prometheus.read("http://x", unknown, "10m", descriptor_model.load_plan())


if __name__ == "__main__":
    unittest.main()
