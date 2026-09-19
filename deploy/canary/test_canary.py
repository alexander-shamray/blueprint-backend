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
import re
import tempfile
import unittest
from pathlib import Path

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


def readings(**overrides) -> dict:
    """A healthy step, which the tests then break one field at a time."""
    document = {
        "canary": {"errorRate": 0.0, "latencyP99Seconds": 0.2, "requests": 5000.0},
        "baseline": {"errorRate": 0.0, "latencyP99Seconds": 0.2, "requests": 90000.0},
    }
    for track, values in overrides.items():
        if values is None:
            document.pop(track)
        else:
            document[track].update(values)
    return document


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
        """And 19 + 1 is 20, which is the service charts' maxReplicas
        exactly — not the gateway's, which is 30 because every external request
        passes through it. The 19 is what the weight costs, and only on those
        charts is it also all the chart allows."""
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
        """The gateway and the BFF own no database (§10.1), so their charts
        render no Job and their tags are bounded only by the label."""
        plan = canary.load_plan()

        for workload in ("gateway", "web-bff"):
            with self.subTest(workload=workload):
                self.assertIsNone(canary.migration_prefix(workload, plan))

        canary.validate_tag("a" * 63, canary.migration_prefix("gateway", plan))

    def test_length_and_emptiness(self) -> None:
        with self.assertRaises(canary.PlanError):
            canary.validate_tag("")
        with self.assertRaises(canary.PlanError) as raised:
            canary.validate_tag("a" * 64)

        self.assertIn("63", str(raised.exception))


class VerdictTests(unittest.TestCase):
    def test_a_healthy_step_promotes(self) -> None:
        verdict = canary.analyse(readings(), THRESHOLDS)

        self.assertEqual(verdict["decision"], canary.PROMOTE)

    def test_an_absent_series_rolls_back_on_either_track(self) -> None:
        """§15.1 already says this about the k6 SLO run: it "fails on an absent
        series as well as on a breached one". An empty dashboard reads the same
        whether the system is healthy or nobody scraped it.

        SIX cases, not three. The baseline's `requests` was the one reading
        fetched on every step and validated by nothing — `read` runs each query
        independently, so a single malformed response can null one metric while
        its neighbours succeed. A contract saying "any absent reading is a
        rollback" is only true if the check is over both tracks.
        """
        for track in ("canary", "baseline"):
            for metric in ("errorRate", "latencyP99Seconds", "requests"):
                with self.subTest(track=track, metric=metric):
                    verdict = canary.analyse(readings(**{track: {metric: None}}), THRESHOLDS)

                    self.assertEqual(verdict["decision"], canary.ROLLBACK)
                    self.assertIn(metric, verdict["reason"])
                    self.assertIn(track, verdict["reason"])

    def test_a_missing_canary_track_rolls_back(self) -> None:
        verdict = canary.analyse(readings(canary=None), THRESHOLDS)

        self.assertEqual(verdict["decision"], canary.ROLLBACK)

    def test_too_little_traffic_rolls_back(self) -> None:
        """Five per cent of a quiet ten minutes can be four requests, and four
        requests cannot tell a 1% error rate from a 0% one. Promoting there is
        promoting on no evidence and reporting a green analysis."""
        verdict = canary.analyse(readings(canary={"requests": 40.0}), THRESHOLDS)

        self.assertEqual(verdict["decision"], canary.ROLLBACK)
        self.assertIn("40", verdict["reason"])

    def test_the_minimum_is_the_smallest_sample_that_can_express_the_threshold(self) -> None:
        """minimumRequests is 1/errorRate rather than a number somebody liked:
        below it, one failure is already more than the threshold."""
        self.assertEqual(THRESHOLDS["minimumRequests"], 1 / THRESHOLDS["errorRate"])

    def test_breaching_the_alert_threshold_rolls_back(self) -> None:
        """The absolute check is §13.6's own number. A canary tuned looser
        would promote a release and then page about it."""
        verdict = canary.analyse(readings(canary={"errorRate": 0.02}), THRESHOLDS)

        self.assertEqual(verdict["decision"], canary.ROLLBACK)
        self.assertIn("pages", verdict["reason"])

    def test_breaching_the_latency_threshold_rolls_back(self) -> None:
        verdict = canary.analyse(readings(canary={"latencyP99Seconds": 1.5}), THRESHOLDS)

        self.assertEqual(verdict["decision"], canary.ROLLBACK)

    def test_a_regression_inside_the_threshold_still_rolls_back(self) -> None:
        """§15.5 says "regresses", not "breaches". A canary at four times the
        stable track's error rate is a bad release even while both are under
        the number that pages."""
        verdict = canary.analyse(
            readings(canary={"errorRate": 0.008}, baseline={"errorRate": 0.001}),
            THRESHOLDS,
        )

        self.assertEqual(verdict["decision"], canary.ROLLBACK)
        self.assertIn("stable track", verdict["reason"])

    def test_noise_under_the_floor_does_not_roll_back(self) -> None:
        """Without the floor every quiet service rolls back for ever: a
        baseline of 0.0001 against a canary of 0.0004 is four times worse and
        is two requests."""
        verdict = canary.analyse(
            readings(canary={"errorRate": 0.0004}, baseline={"errorRate": 0.0001}),
            THRESHOLDS,
        )

        self.assertEqual(verdict["decision"], canary.PROMOTE)

    def test_a_missing_baseline_rolls_back(self) -> None:
        """This used to assert PROMOTE, on the argument that the canary's own
        absence means the new version is unobserved while the baseline's only
        means there is nothing to compare against.

        That held while an absent series was ambiguous, and it is not: the
        stable track serves the MAJORITY of traffic at every rung, and since
        the error-rate numerator is coalesced a query only returns nothing when
        the denominator is empty — no requests at all. Skipping the check there
        removed regression detection at exactly the moment the monitoring was
        failing on the larger half of the traffic. The rule is now uniform:
        any absent reading is a rollback.
        """
        verdict = canary.analyse(
            readings(baseline={"errorRate": None, "latencyP99Seconds": None}),
            THRESHOLDS,
        )

        self.assertEqual(verdict["decision"], canary.ROLLBACK)
        self.assertIn("baseline", verdict["reason"])

    def test_the_reason_survives_every_verdict(self) -> None:
        """The rollout prints this and nothing else. A decision with an empty
        reason is a rollback nobody can act on."""
        for document in (readings(), readings(canary={"errorRate": 0.5}), readings(canary=None)):
            self.assertTrue(canary.analyse(document, THRESHOLDS)["reason"].strip())


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

    def test_the_ladder_is_the_chapters(self) -> None:
        """§15.5, verbatim: 5, 25, 50, 100, ten minutes each. Not trimmed to
        what three replicas can express — that is what the refusal is for."""
        self.assertEqual(
            [step["weight"] for step in self.document["steps"]],
            [5, 25, 50, 100],
        )
        for step in self.document["steps"][:-1]:
            self.assertEqual(step["dwellMinutes"], 10)

    def test_every_threshold_analyse_reads_is_present(self) -> None:
        """`analyse` indexes these rather than `.get`-ing them, so a missing
        key is a KeyError mid-rollout with a canary already serving traffic."""
        canary.analyse(readings(), canary.entries(self.document["thresholds"]))

    def test_the_queries_carry_all_three_substitutions(self) -> None:
        """A query that kept `$TRACK` literal matches no series, and an absent
        series rolls back — so the mistake yields a rollout that can only ever
        fail, at the end of a ten-minute wait."""
        for name, expression in canary.entries(self.document["queries"]).items():
            with self.subTest(query=name):
                self.assertIn("$SERVICE", expression)
                self.assertIn("$TRACK", expression)
                self.assertIn("$WINDOW", expression)

    def test_the_error_rate_numerator_is_coalesced(self) -> None:
        """The difference between a canary that can promote and one that cannot.

        A canary serving no 5xx matches no series for the numerator, and PromQL
        carries an empty vector through the division rather than treating it as
        zero — so the query returns no sample, `read_prometheus` returns None,
        and `analyse` reads that as an absent series and rolls back. **A canary
        with a perfect record would have failed every step, for ever**, which
        is the one defect that makes the whole mechanism inoperable rather than
        merely wrong.

        Only the numerator: an empty denominator means no traffic at all, which
        is a real silence and is judged by `requests` against
        `minimumRequests`.
        """
        error_rate = canary.entries(self.document["queries"])["errorRate"]

        self.assertIn("or vector(0)", error_rate)
        self.assertTrue(
            error_rate.startswith("(sum("),
            "the coalesce has to wrap the numerator, not the whole expression",
        )
        for name, expression in canary.entries(self.document["queries"]).items():
            if name != "errorRate":
                with self.subTest(query=name):
                    self.assertNotIn(
                        "or vector(0)", expression,
                        "coalescing a count or a quantile turns 'nobody scraped "
                        "this' into a healthy zero, which is the trap the "
                        "absent-series rule exists for",
                    )

    def test_the_queries_read_the_track_label_and_not_the_version(self) -> None:
        """service_version was the obvious discriminator and is not one:
        BuildInfo.Version strips the source-revision suffix on purpose and
        nothing sets an assembly version, so every build reports 1.0.0. A
        registered name is not a live signal."""
        for expression in canary.entries(self.document["queries"]).values():
            self.assertIn("deployment_track", expression)
            self.assertNotIn("service_version", expression)


class DispatchOptionTests(unittest.TestCase):
    """The `workload:` dispatch input's `options:` against canary.json's keys.

    A workload the plan can roll and this list cannot choose stays invisible
    to a manual rollout while every path-filter check stays green: that check
    covers the trigger, not the menu underneath it.
    """

    WORKFLOW_TEXT = """\
on:
  workflow_dispatch:
    inputs:
      workload:
        description: 'x'
        required: true
        type: choice
        options: [{options}]
"""

    def _failures(self, options: str, workloads: dict) -> list[str]:
        original = canary.WORKFLOW
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "deploy.yml"
            path.write_text(self.WORKFLOW_TEXT.format(options=options), encoding="utf-8")
            canary.WORKFLOW = path
            try:
                return canary._dispatch_options_match_workloads(workloads)
            finally:
                canary.WORKFLOW = original

    def test_a_missing_option_fails(self) -> None:
        failures = self._failures(
            "catalog-api, ordering-api",
            {"catalog-api": {}, "ordering-api": {}, "inventory-api": {}},
        )

        self.assertTrue(any("inventory-api" in f for f in failures), failures)

    def test_an_extra_option_fails(self) -> None:
        failures = self._failures("catalog-api, ordering-api", {"catalog-api": {}})

        self.assertTrue(any("ordering-api" in f for f in failures), failures)

    def test_the_real_repository_passes(self) -> None:
        document = canary.load_plan()

        self.assertEqual(
            canary._dispatch_options_match_workloads(canary.entries(document["workloads"])),
            [],
        )


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
        self.assertIn("$comment", raw["workloads"])


if __name__ == "__main__":
    unittest.main()
