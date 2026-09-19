#!/usr/bin/env python3
"""The canary's arithmetic and its verdict, and a gate over the plan itself.

§15.5 specifies the rollout: "route 5% of traffic to the new version, watch
error rate and p99 for ten minutes, then progress to 25%, 50%, 100%. Roll back
automatically if either metric regresses beyond threshold." This file is the
half of that sentence a workflow cannot be trusted with — the weight
arithmetic and the promote/rollback decision — kept out of YAML so it can be
asserted.

**It reaches no cluster and no Prometheus.** `plan` and `analyse` are pure
functions over their arguments; the workflow queries Prometheus and runs
`kubectl scale`, and hands the numbers here. That split is what makes a canary
nobody has run still worth shipping: the part that decides is testable today,
and the part that acts is four commands whose failure is loud.

Stdlib only, on the licence gate's terms — no restore, no SDK, no
dependencies. The plan is JSON rather than YAML for the same reason
`deploy/observability/dashboards` is: there is no stdlib YAML parser, and a
gate that needs a `pip install` is a gate that gets skipped.

    py -3.12 deploy/canary/canary.py check
    py -3.12 deploy/canary/canary.py plan --workload catalog-api --stable 19 --step 0
    py -3.12 deploy/canary/canary.py analyse --readings readings.json
"""

from __future__ import annotations

import argparse
import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
CANARY = Path(__file__).resolve().parent
PLAN_PATH = CANARY / "canary.json"

# EVERY PATH OUTSIDE deploy/canary THAT THIS SCRIPT READS, declared once.
#
# `deploy/helm/smoke.sh` lost count of its own inventory three times and ended
# it by declaring the list beside the reads and asserting the other copy
# matches; `deploy/observability/check.py` adopted that before paying for it
# once. This is the third tree to do it, and check 6 is the assertion.
SOURCE_INPUTS = [
    "src",
    "deploy/helm",
    # Checks 3 and 5 both read platform-alerts.yaml — one to take §13.6's
    # thresholds out of it rather than restate them, the other to establish
    # that a metric this plan queries is one a loaded alert already reads.
    # Retuning ErrorRateService without this entry is a green pull request:
    # observability.yml runs check.py, which does not compare canary
    # thresholds, and the canary gate never runs.
    "deploy/observability",
]

WORKFLOW_PATH = ".github/workflows/deploy.yml"
WORKFLOW = ROOT / WORKFLOW_PATH

# The two verdicts, and there are deliberately only two.
#
# A third — "hold", "inconclusive", "needs a human" — reads as caution and is
# the opposite: an unattended rollout that cannot decide leaves a canary
# serving traffic on nobody's authority. The reason this is affordable is the
# shape of the mechanism rather than optimism about the readings. The canary is
# a SECOND Deployment and the stable one is never touched (ADR-022), so
# rollback costs the canary's own pods and nothing else — no `helm rollback`
# and no image change on the pods serving the other 95%.
#
# NOT "and no schema to undo", which this comment said and ADR-022 denies: the
# canary release runs §7.4's migration hook, because it is the first thing
# carrying the new image, and a rollback removes the pods and LEAVES THE SCHEMA
# MIGRATED. What makes that survivable is §15.5's backward-compatibility
# requirement, which ADR-022 sharpens rather than relaxes — a cheap rollback is
# worth nothing against an incompatible migration. The pods are the cheap half;
# the schema is not a half this mechanism buys at all.
#
# When rollback is cheap, every doubt resolves to it.
PROMOTE = "promote"
ROLLBACK = "rollback"

# Helm's release-name alphabet: a DNS-1123 label, at most 53 characters so the
# suffixed resource names still fit 63. Every workload key is one, and `check`
# holds it to this before a shell reads it as a word or a path.
RELEASE_NAME = re.compile(r"^[a-z0-9]([-a-z0-9]{0,51}[a-z0-9])?$")


class PlanError(Exception):
    """The plan is unusable. Raised by the loader and by `check`."""


def entries(mapping: dict) -> dict:
    """A JSON object's real keys, with the `$comment` ones dropped.

    JSON has no comments and this plan needs them: every number in it is a
    decision somebody has to be able to re-take. `$comment` is the convention
    the tooling around JSON Schema already uses, and it is filtered HERE rather
    than at each call site because forgetting it once turns a comment into a
    workload with no service name.
    """
    return {key: value for key, value in mapping.items() if not key.startswith("$")}


def load_plan(path: Path = PLAN_PATH) -> dict:
    """Read canary.json, or explain what is wrong with it."""
    try:
        text = path.read_text(encoding="utf-8")
    except OSError as error:
        raise PlanError(f"{path} is not readable: {error}") from error

    try:
        return json.loads(text)
    except json.JSONDecodeError as error:
        raise PlanError(f"{path} is not valid JSON: {error}") from error


# --------------------------------------------------------------------------
# The weight arithmetic
# --------------------------------------------------------------------------

def migration_prefix(workload: str, plan_document: dict, root: Path = ROOT) -> str | None:
    """The `<workload>-migrate-` a Job name would start with, where there is one.

    None for a chart that renders no migration Job — the gateway and the BFF
    own no database (§10.1, §15.3), so their tags are bounded only by the label
    length. Derived from the templates on disk rather than listed, because a
    sixth service's chart gains a migrator by having the file.
    """
    workloads = entries(plan_document.get("workloads", {}))
    chart = workloads.get(workload, {}).get("chart")
    if not chart:
        return None
    if not (root / "deploy" / "helm" / chart / "templates" / "migrate-job.yaml").is_file():
        return None
    return f"{workload}-migrate-"


def validate_tag(tag: str, job_prefix: str | None = None) -> None:
    """Refuse a tag Helm's `--set-string` would read as more than a tag.

    **`--set-string image.tag="$TAG"` is not a single assignment**, and that is
    the finding this exists for. Helm parses the right-hand side with `strvals`,
    where a COMMA separates assignments — so
    `deadbeef,image.registry=attacker.example` sets a perfectly valid
    `image.tag` AND overrides the registry, for the canary and the promotion
    alike. `commerce.tag`'s render-time validation passes, because by then the
    tag really is `deadbeef`; the injected key rode in beside it.

    This is docs/lessons.md's own lesson about a value crossing between two
    systems' alphabets, one release later: the tag is validated against Kubernetes'
    alphabet at render time and reaches Helm's parser before that.
    **Validate against the intersection, at the boundary the value enters.**

    The rule is `commerce.tag`'s, deliberately — dot-separated DNS-1123 labels,
    63 characters at most — because a second, looser alphabet here would let
    something through that the chart then rejects mid-`helm upgrade`. It admits
    no comma, no equals and no whitespace, which is what closes the injection;
    that is a consequence of matching the chart rather than a rule of its own,
    and it is the reason this cannot be relaxed independently.
    """
    if not tag:
        raise PlanError("image tag is empty: §15.3 refuses a deploy that cannot name its image")
    if len(tag) > 63:
        raise PlanError(
            f"image tag is {len(tag)} characters. It becomes "
            "app.kubernetes.io/version, and a label value may not exceed 63 (§15.3)"
        )
    for segment in tag.split("."):
        if not re.fullmatch(r"[a-z0-9]([a-z0-9-]*[a-z0-9])?", segment):
            raise PlanError(
                f"image tag {tag!r} is not usable: the segment {segment!r} is not a "
                "DNS-1123 label. Each dot-separated segment must be lowercase "
                "alphanumerics and dashes, starting and ending alphanumeric "
                "(§15.3) — which also excludes the comma and equals that Helm's "
                "--set-string would read as a second assignment"
            )

    # AND THE MIGRATION JOB'S NAME, which is a tighter budget than the label's
    # on any chart that has one. `_migration-job.tpl` derives
    # `<workload>-migrate-<tag>` and refuses it past 63 — correctly, and at
    # RENDER time, which on this path is after the stable track has already
    # been scaled to nineteen. Sixty-three characters is a tag this preflight
    # accepted and the chart then rejected, which is the preflight failing at
    # the one job it has: to move that refusal in front of the scale-up.
    #
    # The prefix is derived from canary.json's workload map rather than
    # written down, so a workload added there brings its own budget with it,
    # and the longest prefix among the workloads sets the tightest one.
    if job_prefix is not None and len(job_prefix) + len(tag) > 63:
        raise PlanError(
            f"image tag {tag!r} is {len(tag)} characters, and the migration Job "
            f"would be named {job_prefix + tag!r} at {len(job_prefix) + len(tag)}. "
            "Kubernetes copies that onto every pod as the `job-name` label, which "
            f"may not exceed 63, so this workload allows {63 - len(job_prefix)} "
            "(§7.4). Refused here rather than by `helm upgrade` after the stable "
            "track has been scaled up"
        )


def _ceil_div(numerator: int, denominator: int) -> int:
    """Integer ceiling division, because `math.ceil` on a float lies here.

    Every quantity in the weight arithmetic is a count of pods or a whole
    percentage, so the exact answer is available and the float route is not
    merely imprecise — it is wrong at the input the ladder starts from.
    """
    return -(-numerator // denominator)


def required_stable(weight_percent: int, overshoot_points: int) -> int:
    """The smallest stable replica count at which a weight is expressible.

    One canary pod is the smallest canary there is, so it serves
    `1 / (stable + 1)` of the traffic and that fraction is the finest weight
    the mechanism has. Inverting it gives the stable count a requested weight
    needs — 19 for §15.5's 5%, which is why the ladder's first rung is a
    scale-up and not a no-op.

    Separate from `plan` and used by it, so the number in the refusal and the
    number the workflow scales to are the same number. Two derivations of one
    figure is how a message ends up naming a count that does not satisfy the
    check that printed it.
    """
    if weight_percent >= 100:
        return 1
    # 100 / (stable + 1) <= weight + overshoot, in integers.
    return max(1, _ceil_div(100, weight_percent + overshoot_points) - 1)


def plan(weight_percent: int, stable_replicas: int, overshoot_points: int) -> dict:
    """How many canary pods a requested weight costs, and what it really buys.

    **A replica-weighted canary cannot hit an arbitrary weight**, and this is
    the function that refuses to pretend otherwise. Traffic reaches these pods
    through a ClusterIP Service, which spreads connections across its endpoints
    — so the share the new version serves is `canary / (stable + canary)` and
    the achievable weights are the fractions that arithmetic can make. With
    §15.3's `replicaCount: 3`, the smallest canary is one pod and the smallest
    weight is 25%, which is five times the 5% §15.5 asks for.

    **The requested weight is a ceiling, not a target to land on.** The canary
    is the LARGEST one whose share stays within it, which is the only direction
    that is safe to be wrong in: undershooting means a smaller blast radius
    than was asked for, and overshooting means more traffic on the new version
    than anybody authorised. A step labelled 5% that serves 25% is the failure
    this whole function exists to prevent, and rounding to the nearest
    expressible weight is how it would have happened.

    One pod is the floor, so where even a single canary exceeds the ceiling
    there is nothing to round down to and this raises. The message names the
    stable replica count that WOULD satisfy the request, because that is the
    decision the operator actually has — scale up and pay for it, or accept a
    coarser step and say so in `tolerance`.

    This rule and `required_stable` are one design read from two ends: that
    function answers "how many stable pods make ONE canary fit", which is
    precisely the boundary at which this stops raising. They disagreed once,
    and the test that pairs them is what said so.
    """
    if not 0 < weight_percent <= 100:
        raise PlanError(f"weight must be in (0, 100]; got {weight_percent}")
    if stable_replicas < 1:
        raise PlanError(f"stable replicas must be at least 1; got {stable_replicas}")

    if weight_percent == 100:
        # The last rung is not a weight, it is the end of the rollout: the
        # canary becomes the release. Expressing it as pods would ask for an
        # infinite canary against a stable track that is about to go away.
        return {
            "requested": 100,
            "canaryReplicas": stable_replicas,
            "stableReplicas": 0,
            "achieved": 100.0,
            "final": True,
        }

    # INTEGER ARITHMETIC THROUGHOUT, and that is a correction rather than a
    # preference. Written with floats this read
    # `ceil(stable * f / (1 - f))`, and at the one input the whole ladder
    # starts from — 5% against 19 replicas — `19 * 0.05 / 0.95` evaluates to
    # 1.0000000000000002, so `ceil` returned two pods and the step served 9.5%
    # instead of 5%. `required_stable` and `plan` then disagreed about the same
    # number: one named 19 as the count that works and the other refused it.
    # Found by the test asserting those two agree.
    #
    #   canary / (stable + canary) <= weight / 100
    #     <=> canary * (100 - weight) <= weight * stable
    #
    # Floor, then a minimum of one pod: the largest canary that stays within
    # the ceiling, or the smallest canary there is when none does.
    canary = max(1, (weight_percent * stable_replicas) // (100 - weight_percent))
    achieved = 100 * canary / (stable_replicas + canary)

    # Compared as integers for the same reason: `achieved` is a ratio and the
    # question is whether 100 * canary exceeds (weight + overshoot) of the
    # total, which has an exact answer.
    if 100 * canary > (weight_percent + overshoot_points) * (stable_replicas + canary):
        needed = required_stable(weight_percent, overshoot_points)
        raise PlanError(
            f"{weight_percent}% is not reachable with {stable_replicas} stable "
            f"replicas: one canary pod already serves {achieved:.1f}%, which "
            f"overshoots by more than {overshoot_points} points. A "
            f"replica-weighted canary quantises the weight (ADR-022). Either "
            f"scale stable to {needed} first, or start the ladder at a weight "
            f"this replica count can express."
        )

    return {
        "requested": weight_percent,
        "canaryReplicas": canary,
        "stableReplicas": stable_replicas,
        "achieved": round(achieved, 2),
        "final": False,
    }


# --------------------------------------------------------------------------
# The verdict
# --------------------------------------------------------------------------

def analyse(readings: dict, thresholds: dict) -> dict:
    """Promote or roll back, from one step's readings.

    Four ways to fail and one way to pass, and the order below is the order
    they are checked in.

    **An absent series is a failure, not a silence.** §15.1 already says this
    about the k6 SLO run — it "fails on an absent series as well as on a
    breached one" — and §13.6 spends a callout on the same shape: an empty
    dashboard reads identically whether the system is healthy or the metric was
    never published. A canary that promotes on `None` promotes on a scrape that
    did not happen.

    **Too little traffic is also a failure**, for the reason above one step on.
    Five per cent of a quiet ten minutes can be four requests, and four
    requests cannot distinguish a 1% error rate from a 0% one. Promoting there
    is promoting on no evidence while reporting a green analysis.

    **Absolute breach is measured against §13.6's own thresholds**, not against
    a looser number chosen for the rollout. A canary tuned to tolerate what
    pages the on-call has bought nothing: it would promote a release and then
    wake somebody at 3 a.m. about the release it promoted.

    **Relative regression has a floor under it**, because a ratio between two
    small numbers is noise. A baseline of 0.0001 and a canary of 0.0004 is
    four times worse and is two requests; without the floor every quiet service
    rolls back for ever.
    """
    verdicts = []

    for track in ("canary", "baseline"):
        if track not in readings:
            return _verdict(ROLLBACK, f"no readings for the {track} track")

    canary = readings["canary"]
    baseline = readings["baseline"]

    # BOTH TRACKS, all three metrics, before any threshold is applied.
    #
    # This checked the canary's three and left the baseline's to the relative
    # check, which reached two of them and never looked at `requests` at all —
    # a reading fetched on every step and validated by nothing. `read` runs
    # each query independently, so one malformed response can null a single
    # metric while its neighbours succeed, and the contract this function
    # states is "any absent reading is a rollback". A loop over both tracks is
    # the only shape in which that sentence is true, and it costs one nesting
    # level to remove the exception.
    for track, values in (("canary", canary), ("baseline", baseline)):
        for name in ("errorRate", "latencyP99Seconds", "requests"):
            if values.get(name) is None:
                return _verdict(
                    ROLLBACK,
                    f"the {track} track reported no {name}: the series is "
                    "absent, which is what a metric nobody publishes and a pod "
                    "nobody scraped look like alike (§13.6). The stable track "
                    "carries the majority of traffic at every rung, so its "
                    "silence is the monitoring failing on the larger half",
                )

    minimum = thresholds["minimumRequests"]
    if canary["requests"] < minimum:
        return _verdict(
            ROLLBACK,
            f"the canary served {canary['requests']:.0f} requests in the step's "
            f"window, below the {minimum} this plan calls enough to judge. "
            "Promoting on that is promoting on no evidence",
        )

    if canary["errorRate"] > thresholds["errorRate"]:
        verdicts.append(
            f"error rate {canary['errorRate']:.3%} is above the "
            f"{thresholds['errorRate']:.3%} that pages (§13.6)"
        )

    if canary["latencyP99Seconds"] > thresholds["latencyP99Seconds"]:
        verdicts.append(
            f"p99 {canary['latencyP99Seconds']:.3f}s is above the "
            f"{thresholds['latencyP99Seconds']:.3f}s that pages (§13.6)"
        )

    factor = thresholds["regressionFactor"]
    verdicts += _regression(
        "error rate",
        canary["errorRate"],
        baseline.get("errorRate"),
        thresholds["errorRateFloor"],
        factor,
        "{:.3%}",
    )
    verdicts += _regression(
        "p99",
        canary["latencyP99Seconds"],
        baseline.get("latencyP99Seconds"),
        thresholds["latencyP99FloorSeconds"],
        factor,
        "{:.3f}s",
    )

    if verdicts:
        return _verdict(ROLLBACK, "; ".join(verdicts))

    return _verdict(
        PROMOTE,
        f"error rate {canary['errorRate']:.3%} and p99 "
        f"{canary['latencyP99Seconds']:.3f}s over {canary['requests']:.0f} "
        "requests, both inside threshold and neither materially worse than the "
        "stable track",
    )


def _regression(
    label: str,
    canary_value: float,
    baseline_value: float | None,
    floor: float,
    factor: float,
    fmt: str,
) -> list[str]:
    """One metric's relative check.

    **Both readings are present by the time this runs**, because `analyse`
    rejects an absent one on either track before applying any threshold. That
    is a change: a missing baseline used to skip this check, on the argument
    that the canary's absence means the new version is unobserved while the
    baseline's only means there is nothing to compare against.

    It did not survive. The stable track serves the MAJORITY of traffic at
    every rung of §15.5's ladder, so its series going missing is the monitoring
    failing on the larger half — and since the error-rate numerator is
    coalesced, a query returns nothing only when the DENOMINATOR is empty, no
    requests at all. Skipping removed regression detection at exactly that
    moment. The rule is now uniform and therefore statable: **any absent
    reading is a rollback**, with no exception to remember and none to check
    for here.
    """
    if canary_value <= floor:
        return []
    if canary_value <= baseline_value * factor:
        return []
    return [
        f"{label} {fmt.format(canary_value)} is more than {factor}x the stable "
        f"track's {fmt.format(baseline_value)}"
    ]


def _verdict(decision: str, reason: str) -> dict:
    return {"decision": decision, "reason": reason}


def _shout(key: str) -> str:
    """`canaryReplicas` -> `CANARY_REPLICAS`. Shell variables are not camel."""
    return re.sub(r"(?<!^)(?=[A-Z])", "_", key).upper()


# --------------------------------------------------------------------------
# The gate over the plan
# --------------------------------------------------------------------------

def check(plan_document: dict, root: Path = ROOT) -> list[str]:
    """Everything that can be wrong with canary.json without a cluster.

    Eight checks. The last three are the ones this repository keeps learning
    it needs: one asserts the gate's own subject is non-empty, one asserts the
    workflow's path filter covers every input the rollout reads, and one
    asserts its dispatch menu covers every workload the rollout can reach.
    """
    failures: list[str] = []

    steps = plan_document.get("steps", [])
    thresholds = entries(plan_document.get("thresholds", {}))
    workloads = entries(plan_document.get("workloads", {}))

    # 1. The ladder climbs, ends at 100, and dwells.
    if not steps:
        failures.append("steps is empty: a rollout with no steps promotes nothing")
    else:
        weights = [step.get("weight") for step in steps]
        if weights != sorted(weights) or len(set(weights)) != len(weights):
            failures.append(
                f"steps must have strictly increasing weights; got {weights}"
            )
        if weights[-1] != 100:
            failures.append(
                f"the last step is {weights[-1]}%, not 100%: a ladder that stops "
                "short leaves the old version serving traffic for ever"
            )
        for step in steps[:-1]:
            if not step.get("dwellMinutes"):
                failures.append(
                    f"the {step.get('weight')}% step has no dwell: §15.5 watches "
                    "each weight for ten minutes, and a step with no window is a "
                    "step whose query has nothing to average over"
                )

    # 2. Every threshold the verdict reads exists. `analyse` indexes these
    #    rather than `.get`-ing them, so a missing key is a KeyError mid-rollout
    #    — which is this check's whole reason for existing.
    for key in (
        "errorRate",
        "latencyP99Seconds",
        "errorRateFloor",
        "latencyP99FloorSeconds",
        "regressionFactor",
        "minimumRequests",
    ):
        if key not in thresholds:
            failures.append(f"thresholds.{key} is missing; analyse() reads it")

    # 3. The absolute thresholds are §13.6's, and that is asserted rather than
    #    intended. A canary tolerating what pages the on-call has bought
    #    nothing, and the two numbers drifting apart is invisible from either
    #    side.
    failures += _thresholds_match_alerts(thresholds, root)

    # 4. Each workload's service_name is a real host assembly.
    #
    #    §13.2 sets the resource's service.name from
    #    `builder.Environment.ApplicationName`, which defaults to the ENTRY
    #    ASSEMBLY name — so the edge emits `Gateway.Api` and the chart's
    #    `workload.name` (`gateway`) never reaches the label.
    #    platform-alerts.yaml carries a nine-line comment about getting this
    #    exact substitution wrong, where the misspelling matched no series and
    #    the alert was silent. The same misspelling here promotes every canary,
    #    because a query that matches nothing returns nothing and an absent
    #    series is the rollback above — so it fails safe and never promotes,
    #    which is a rollout that can only ever roll back.
    hosts = _host_assemblies(root)
    if not workloads:
        failures.append("workloads is empty: the rollout has nothing to deploy")
    for name, workload in sorted(workloads.items()):
        # 4a. The key is a Helm release name, and two shells read it as one.
        #     `deploy.yml` passes it to `helm upgrade` and `realm.yml`'s
        #     scheduled job reads it off `workloads` one line at a time, then
        #     uses it as a file name under RUNNER_TEMP. A key with a space, a
        #     glob character or a slash would be two releases, an expansion
        #     against the checkout, or a path — so the key is held to the
        #     alphabet Helm holds a release to, here, before either job sees it.
        #     `fullmatch`, as `validate_tag` already uses: `match` with a `$`
        #     anchor accepts a key ending in a newline, which the line-oriented
        #     `workloads` output would emit as an extra, empty release.
        if not RELEASE_NAME.fullmatch(name):
            failures.append(
                f"workloads.{name!r} is not a Helm release name: lower-case "
                "letters, digits and hyphens, starting and ending with a letter "
                "or digit, at most 53 characters"
            )
        service_name = workload.get("serviceName")
        if service_name not in hosts:
            failures.append(
                f"workloads.{name}.serviceName is {service_name!r}, which is not "
                f"an entry assembly in this solution. §13.2 takes service.name "
                f"from ApplicationName, so it must be one of: "
                f"{', '.join(sorted(hosts))}"
            )
        if not _chart_exists(workload.get("chart"), root):
            failures.append(
                f"workloads.{name}.chart is {workload.get('chart')!r}, which is "
                "not a chart under deploy/helm"
            )

    # 5. Every metric the queries read is one an alert already reads.
    #
    #    NOT a second copy of check.py's C#-instrument scan. That gate proves a
    #    LOADED alert's metric is published by something; anything this file
    #    reads that a loaded alert also reads inherits the proof. A metric here
    #    and nowhere else is a name nothing has ever vouched for, which is the
    #    typo this catches.
    # 5a. The three queries `analyse` consumes must exist.
    #
    # Check 5 vouches for the metrics a query READS, and says nothing about a
    # query that is not there: deleting `requests` — or the whole `queries`
    # object — passed every check here, after which `read` returns a reading
    # short and `analyse` rolls back for an absent series. An inoperable plan
    # that fails at the end of the first ten-minute dwell rather than in a gate
    # that takes milliseconds.
    queries = entries(plan_document.get("queries", {}))
    for name in ("errorRate", "latencyP99Seconds", "requests"):
        if name not in queries:
            failures.append(
                f"queries.{name} is missing, and analyse() reads it for both "
                "tracks. Without it every rung ends in a rollback for an absent "
                "series, ten minutes at a time"
            )

    failures += _metrics_are_vouched_for(plan_document, root)

    # 6. The gate's own subject. Checks 3, 4 and 5 all compare against
    #    something parsed out of another file, and a parser that quietly
    #    extracted nothing would pass all three vacuously — which is this
    #    repository's most-repeated failure, named in CLAUDE.md as such.
    if not hosts:
        failures.append(
            "found no host assemblies under src/: check 4 would pass vacuously, "
            "so the parser is what is broken rather than the plan"
        )

    # 7. The workflow's triggers cover every input this rollout reads.
    failures += _workflow_covers_inputs()

    # 8. The dispatch menu is exactly the plan's workload set.
    failures += _dispatch_options_match_workloads(workloads)

    return failures


def _thresholds_match_alerts(thresholds: dict, root: Path) -> list[str]:
    """The canary's absolute thresholds against §13.6's loaded rules.

    Read out of the rules file rather than restated, so the two cannot part.
    The alert expressions end in `> 0.01` and `> 1`; those are the numbers, and
    if somebody retunes an alert this goes red naming the canary that no longer
    agrees with it.
    """
    rules = root / "deploy" / "observability" / "alerts" / "platform-alerts.yaml"
    try:
        text = rules.read_text(encoding="utf-8")
    except OSError as error:
        return [f"{rules} is not readable, so the thresholds cannot be checked: {error}"]

    failures = []
    for alert, key, label in (
        ("ErrorRateService", "errorRate", "error rate"),
        ("Latency", "latencyP99Seconds", "p99"),
    ):
        expected = _alert_threshold(text, alert)
        if expected is None:
            failures.append(
                f"could not read the {alert} threshold out of {rules.name}: the "
                f"canary's {label} cannot be checked against an alert it cannot find"
            )
        elif key in thresholds and thresholds[key] != expected:
            failures.append(
                f"thresholds.{key} is {thresholds[key]} and {alert} fires at "
                f"{expected}. A canary that tolerates what pages promotes a "
                "release and then wakes somebody about it"
            )
    return failures


def _alert_threshold(text: str, alert: str) -> float | None:
    """The comparison at the end of one alert's expression.

    The rules are YAML and this is a regex, for the reason check.py one tree
    over gives: there is no stdlib YAML parser, and the alternative to matching
    text is a dependency this gate must not have. The pattern is anchored on
    the alert's own name and stops at the next `- alert:` or `for:`, so it
    cannot drift onto a neighbour's number.
    """
    block = re.search(
        rf"- alert:\s*{re.escape(alert)}\s*\n(.*?)(?=\n\s*(?:- alert:|for:))",
        text,
        re.DOTALL,
    )
    if not block:
        return None
    comparisons = re.findall(r">\s*([0-9]+(?:\.[0-9]+)?)\s*$", block.group(1), re.MULTILINE)
    if len(comparisons) != 1:
        return None
    return float(comparisons[0])


def _host_assemblies(root: Path) -> set[str]:
    """Every project that produces a host, by assembly name.

    A host is a project with a `Program.cs` beside its csproj — which is what
    `Assembly.GetEntryAssembly()` resolves to at run time and therefore what
    `ApplicationName` defaults to. Derived rather than listed, so a sixth
    service's API is a host here the day it exists.
    """
    hosts = set()
    for csproj in (root / "src").rglob("*.csproj"):
        if (csproj.parent / "Program.cs").exists():
            hosts.add(csproj.stem)
    return hosts


def _chart_exists(chart: str | None, root: Path) -> bool:
    if not chart:
        return False
    return (root / "deploy" / "helm" / chart / "Chart.yaml").is_file()


def _metrics_are_vouched_for(plan_document: dict, root: Path) -> list[str]:
    rules = root / "deploy" / "observability" / "alerts" / "platform-alerts.yaml"
    try:
        alert_text = rules.read_text(encoding="utf-8")
    except OSError as error:
        return [f"{rules} is not readable, so no metric here can be vouched for: {error}"]

    failures = []
    for name, query in sorted(entries(plan_document.get("queries", {})).items()):
        for metric in sorted(set(re.findall(r"\b([a-z][a-z0-9_]*_(?:count|bucket|sum|total))\b", query))):
            # The base name, because an alert may read `_count` where a query
            # reads `_bucket` off the same histogram. Vouching is about the
            # instrument, not the suffix a query happens to take.
            base = re.sub(r"_(?:count|bucket|sum|total)$", "", metric)
            if base not in alert_text:
                failures.append(
                    f"queries.{name} reads {metric}, and no loaded alert reads "
                    f"{base}. Nothing has established that this platform "
                    "publishes it, and a query matching no series rolls every "
                    "canary back"
                )
    return failures


def _workflow_covers_inputs() -> list[str]:
    """Both of the deploy workflow's triggers cover every SOURCE_INPUTS entry.

    A merged change that skips the gate on `main` is the same defect one branch
    later, which is why both triggers are checked rather than the first.
    """
    try:
        text = WORKFLOW.read_text(encoding="utf-8")
    except OSError as error:
        return [f"{WORKFLOW_PATH} is not readable: {error}"]

    # `on:` has one `paths:` per trigger. Anything else means the workflow was
    # restructured and this check no longer knows what it is reading.
    blocks = re.findall(r"paths:\s*\n((?:\s*-\s*'[^']+'\s*\n)+)", text)
    if len(blocks) != 2:
        return [
            f"{WORKFLOW_PATH} has {len(blocks)} path lists, expected two (one per "
            "trigger). This check cannot say whether the gate's inputs are "
            "covered, which is not the same as saying they are"
        ]

    failures = []
    for index, block in enumerate(blocks):
        patterns = re.findall(r"-\s*'([^']+)'", block)
        # THE WORKFLOW'S OWN PATH AND THIS GATE'S OWN TREE ARE BOTH REQUIRED,
        # and each was missing in turn. Without the workflow, removing it from
        # both trigger lists means a change to those very lists no longer runs
        # the gate validating them. Without `deploy/canary`, removing THAT
        # means an edit to `canary.py`, `canary.json` or the suite does not run
        # the gate either — the tree that holds the thing being checked, gone
        # from the triggers, with the check still green.
        #
        # `check.py` has required both since it was written
        # (`SOURCE_INPUTS + ["deploy/observability", WORKFLOW_PATH]`); this
        # copy inherited the pattern one piece at a time.
        for entry in SOURCE_INPUTS + ["deploy/canary", WORKFLOW_PATH]:
            if not any(p == entry or p == f"{entry}/**" for p in patterns):
                failures.append(
                    f"{WORKFLOW_PATH} trigger {index + 1} does not cover "
                    f"{entry!r}, which deploy/canary/canary.py reads"
                )
    return failures


def _dispatch_options_match_workloads(workloads: dict) -> list[str]:
    """The `workload:` dispatch input's `options:` against canary.json's keys.

    An exact set, not a subset either way. An option this plan cannot roll
    dispatches a release `chart` and `plan` have never heard of; a workload
    missing from the list is one a manual dispatch cannot choose, invisible
    while `check` on this file's own path filters stays green — nothing else
    compares the two. Parsed as a flow sequence rather than with a YAML
    library, on `_alert_threshold`'s terms: no dependency this gate would
    need to restore.
    """
    try:
        text = WORKFLOW.read_text(encoding="utf-8")
    except OSError as error:
        return [f"{WORKFLOW_PATH} is not readable, so its dispatch options cannot be checked: {error}"]

    match = re.search(r"workload:\n(?:.*\n)*?\s*options:\s*\[([^\]]*)\]", text)
    if not match:
        return [
            f"{WORKFLOW_PATH} has no options list for the workload dispatch "
            "input, so a manual rollout cannot be checked against the plan"
        ]

    options = {item.strip() for item in match.group(1).split(",") if item.strip()}
    expected = set(workloads)

    failures = []
    missing = expected - options
    if missing:
        failures.append(
            f"{WORKFLOW_PATH}'s workload dispatch options omit "
            f"{', '.join(sorted(missing))}: canary.json can roll them and a "
            "manual dispatch cannot choose them"
        )
    extra = options - expected
    if extra:
        failures.append(
            f"{WORKFLOW_PATH}'s workload dispatch options list "
            f"{', '.join(sorted(extra))}, which is not a workload in canary.json"
        )
    return failures


# --------------------------------------------------------------------------
# CLI
# --------------------------------------------------------------------------

def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="command", required=True)

    sub.add_parser("check", help="validate canary.json against the repository")

    # The workflow asks this BEFORE the first step, because §15.5's 5% is not
    # expressible at §15.3's replicaCount of 3 and scaling up is the operator's
    # decision rather than a surprise mid-rollout. One number, on stdout, so a
    # shell can read it without a JSON parser.
    required = sub.add_parser("required", help="stable replicas the first step needs")
    required.add_argument("--step", type=int, default=0)

    sub.add_parser("steps", help="how many rungs the ladder has")

    tag = sub.add_parser("validate-tag", help="refuse a tag Helm would read as two assignments")
    tag.add_argument("--value", required=True)
    tag.add_argument("--workload", help="apply that workload's migration Job name budget too")

    # Separate from `plan` on purpose: the chart a workload deploys is a fact
    # about the plan, and `plan` REFUSES when the step's weight is not
    # expressible at the current replica count. Asking it for a chart would
    # make resolving the chart depend on the arithmetic succeeding.
    chart = sub.add_parser("chart", help="the chart directory a workload deploys")
    chart.add_argument("--workload", required=True)

    # One name per line, for a shell loop. `realm.yml`'s scheduled job reads
    # every release's authority between rollouts (ADR-043), and the set of
    # releases is this plan's — a list restated in that workflow would agree
    # with this one until a fifth workload joined here and not there, which is
    # `deploy.yml`'s `options:` list one artefact over, and that one at least
    # is a dispatch menu rather than a subject.
    sub.add_parser("workloads", help="every workload in the plan, one per line")

    planner = sub.add_parser("plan", help="canary replicas for one step")
    planner.add_argument("--workload", required=True)
    planner.add_argument("--stable", type=int, required=True)
    planner.add_argument("--step", type=int, required=True)

    analyser = sub.add_parser("analyse", help="promote or roll back")
    analyser.add_argument("--readings", required=True, help="path to a readings JSON file")

    args = parser.parse_args(argv[1:])

    try:
        document = load_plan()
    except PlanError as error:
        print(f"canary: {error}", file=sys.stderr)
        return 1

    if args.command == "check":
        failures = check(document)
        if failures:
            print(f"canary: {len(failures)} problem(s) with the rollout plan:\n", file=sys.stderr)
            for failure in failures:
                print(f"  - {failure}", file=sys.stderr)
            return 1
        print(f"canary: the plan is consistent - {len(document['steps'])} steps, "
              f"{len(entries(document['workloads']))} workloads.")
        return 0

    if args.command == "steps":
        print(len(document["steps"]))
        return 0

    if args.command == "validate-tag":
        prefix = migration_prefix(args.workload, document) if args.workload else None
        try:
            validate_tag(args.value, prefix)
        except PlanError as error:
            print(f"canary: {error}", file=sys.stderr)
            return 1
        print(args.value)
        return 0

    if args.command == "chart":
        workloads = entries(document["workloads"])
        if args.workload not in workloads:
            print(f"canary: no workload {args.workload!r} in the plan", file=sys.stderr)
            return 1
        print(workloads[args.workload]["chart"])
        return 0

    if args.command == "workloads":
        for name in entries(document["workloads"]):
            print(name)
        return 0

    if args.command == "required":
        try:
            step = document["steps"][args.step]
        except IndexError:
            print(f"canary: no step {args.step} in the plan", file=sys.stderr)
            return 1
        print(required_stable(
            step["weight"],
            document["tolerance"]["weightOvershootPoints"],
        ))
        return 0

    if args.command == "plan":
        if args.workload not in entries(document["workloads"]):
            print(f"canary: no workload {args.workload!r} in the plan", file=sys.stderr)
            return 1
        try:
            step = document["steps"][args.step]
        except IndexError:
            print(f"canary: no step {args.step} in the plan", file=sys.stderr)
            return 1
        try:
            result = plan(
                step["weight"],
                args.stable,
                document["tolerance"]["weightOvershootPoints"],
            )
        except PlanError as error:
            print(f"canary: {error}", file=sys.stderr)
            return 1
        workload = entries(document["workloads"])[args.workload]
        result["dwellMinutes"] = step.get("dwellMinutes", 0)
        result["serviceName"] = workload["serviceName"]
        result["chart"] = workload["chart"]

        # `KEY=value` rather than JSON, so the caller is `eval`-able from a
        # shell and needs no parser. The workflow that drives this is bash on a
        # runner; handing it JSON would put five inline `python -c` invocations
        # in a YAML string, and every one of them is a place for a quoting bug
        # in the one file nothing here can test.
        for key, value in result.items():
            print(f"CANARY_{_shout(key)}={value}")
        return 0

    readings = json.loads(Path(args.readings).read_text(encoding="utf-8"))
    verdict = analyse(readings, entries(document["thresholds"]))
    print(json.dumps(verdict, indent=2))
    return 0 if verdict["decision"] == PROMOTE else 2


if __name__ == "__main__":
    sys.exit(main(sys.argv))
